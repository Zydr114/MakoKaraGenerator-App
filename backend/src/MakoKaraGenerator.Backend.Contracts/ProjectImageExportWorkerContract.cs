using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>已保存工程图片导出的独立能力与 KIE1 任务封包合同。</summary>
public static class ProjectImageExportWorkerContract
{
    public const string CapabilitiesKind = "project-image-export-capabilities-v1";
    public const string CapabilitiesResultKind = "project-image-export-capabilities-result-v1";
    public const string RequestKind = "task-project-image-export-v1";
    public const string ProgressKind = "task-project-image-export-progress-v1";
    public const string ResultKind = "task-project-image-export-result-v1";
    public const string ExportProfile = BackendSnapshotProfiles.ProjectImageExport;
    public const int HeaderBytes = WorkerExportContract.HeaderBytes;
    public const int MaximumInputBytes = WorkerProtocol.MaxPayloadBytes - HeaderBytes;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public static byte[] EncodeCapabilities(ProjectImageExportWorkerCapabilities value)
    {
        ValidateCapabilities(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 8192) throw Invalid("图片导出能力超过预算。");
        return bytes;
    }

    public static ProjectImageExportWorkerCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 8192) throw Invalid("图片导出能力为空或超出预算。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            ProjectImageExportWorkerCapabilities value = JsonSerializer.Deserialize<ProjectImageExportWorkerCapabilities>(bytes, Json)
                ?? throw Invalid("图片导出能力为空。");
            ValidateCapabilities(value);
            return value;
        }
        catch (JsonException) { throw Invalid("图片导出能力JSON无效。"); }
    }

    public static void ValidateCapabilities(ProjectImageExportWorkerCapabilities value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.EngineIdentity)
            || value.TaskProfile != ExportProfile
            || value.ImageDecoderProfile != ProjectImagePreviewWorkerContract.ImageDecoderProfile
            || value.BaseProfiles is not { Length: 3 }
            || !value.BaseProfiles.ToHashSet(StringComparer.Ordinal).SetEquals([
                BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                BackendSnapshotProfiles.PackageLyricsScene])
            || value.MaximumInputBytes != MaximumInputBytes
            || value.MaximumImageBytes != ProjectImageValidationWorkerContract.MaximumImageBytes
            || value.MaximumDimension != ProjectImageValidationWorkerContract.MaximumDimension
            || value.MaximumDecodedBytes != ProjectImageValidationWorkerContract.MaximumDecodedBytes
            || value.MaximumBaseSnapshotBytes != ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes
            || value.MaximumSourceValues != ProjectImagePreviewWorkerContract.MaximumSourceValues
            || value.MaximumReportBytes != WorkerExportContract.MaximumReportBytes
            || value.MaximumMediaBytes is <= 0
            || value.MaximumAudioBytes is <= 0
            || value.PackageDiagnosticsVersion != WorkerPackageDiagnosticsContract.Version
            || value.PackageCapabilities is null)
            throw Invalid("图片导出 Worker 能力与固定合同不匹配。");
        PackageRenderWorkerContract.ValidateCapabilities(value.PackageCapabilities);
        if (value.PackageCapabilities.EngineIdentity != value.EngineIdentity) throw Invalid("图片导出 Worker 引擎身份不一致。");
        if (value.ExportCapabilities is not { } export || export.EngineIdentity != value.EngineIdentity
            || !export.GuardAvailable || !HasRequiredExportSourceProfiles(export.SourceProfiles)
            || export.AudioProfiles is not { Length: 6 }
            || !export.AudioProfiles.ToHashSet(StringComparer.Ordinal).SetEquals([
                BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg])
            || export.ExecutionProfiles is not { Length: 2 }
            || !export.ExecutionProfiles.ToHashSet(StringComparer.Ordinal).SetEquals([
                WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile]))
            throw Invalid("图片导出 Worker 缺少导出能力。");
    }

    private static bool HasRequiredExportSourceProfiles(string[]? profiles)
    {
        if (profiles is not { Length: >= 4 and <= 6 }) return false;
        var supported = profiles.ToHashSet(StringComparer.Ordinal);
        // 嵌套能力描述通用导出器；新增通用来源不扩大上方固定的图片 BaseProfiles。
        return supported.Count == profiles.Length && supported.IsSubsetOf([
                BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                BackendSnapshotProfiles.StaticLyricsScenePresets, BackendSnapshotProfiles.PackageLyricsScene,
                BackendSnapshotProfiles.PackageLyricsScenePresets, BackendSnapshotProfiles.RuntimeProjectInput])
            && supported.IsSupersetOf([
                BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                BackendSnapshotProfiles.StaticLyricsScenePresets, BackendSnapshotProfiles.PackageLyricsScene]);
    }

    public static byte[] EncodeInput(Guid taskId, ProjectImageExportWorkerMetadata metadata, ReadOnlySpan<byte> snapshot,
        bool capabilities = false)
    {
        if (taskId == Guid.Empty) throw InvalidInput("图片导出任务身份无效。");
        ValidateMetadata(metadata, capabilities);
        if (capabilities && snapshot.Length != 0 || !capabilities && snapshot.Length == 0)
            throw InvalidInput("图片导出能力请求与任务来源不匹配。");
        if (snapshot.Length > MaximumInputBytes)
            throw Budget("图片导出完整封包超过输入预算。");
        byte[] json = EncodeJson(metadata, WorkerExportContract.MaximumMetadataBytes);
        if (json.Length > MaximumInputBytes - snapshot.Length)
            throw Budget("图片导出metadata与完整图片输入超过预算。");
        byte[] bytes = new byte[HeaderBytes + json.Length + snapshot.Length];
        "KIE1"u8.CopyTo(bytes);
        bytes[4] = 1;
        taskId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), json.Length);
        json.CopyTo(bytes.AsSpan(HeaderBytes));
        snapshot.CopyTo(bytes.AsSpan(HeaderBytes + json.Length));
        return bytes;
    }

    public static ProjectImageExportWorkerInput DecodeInput(ReadOnlySpan<byte> bytes, bool capabilities = false)
    {
        if (bytes.Length > WorkerProtocol.MaxPayloadBytes) throw Budget("图片导出完整封包超过输入预算。");
        if (bytes.Length <= HeaderBytes
            || !bytes[..4].SequenceEqual("KIE1"u8) || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0)
            throw InvalidInput("图片导出封包头无效。");
        Guid taskId = new(bytes.Slice(8, 16));
        int metadataBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));
        if (taskId == Guid.Empty || metadataBytes is <= 0 or > WorkerExportContract.MaximumMetadataBytes
            || metadataBytes > bytes.Length - HeaderBytes || capabilities != (bytes.Length == HeaderBytes + metadataBytes))
            throw InvalidInput("图片导出metadata边界无效。");
        ProjectImageExportWorkerMetadata metadata = DecodeJson<ProjectImageExportWorkerMetadata>(bytes.Slice(HeaderBytes, metadataBytes), WorkerExportContract.MaximumMetadataBytes);
        ValidateMetadata(metadata, capabilities);
        byte[] snapshot = bytes[(HeaderBytes + metadataBytes)..].ToArray();
        if (!capabilities && snapshot.Length == 0) throw InvalidInput("图片导出缺少完整输入。");
        return new(taskId, metadata, snapshot, WorkerProtocol.ComputeSha256(snapshot));
    }

    public static void ValidateMetadata(ProjectImageExportWorkerMetadata metadata, bool capabilities)
    {
        if (metadata is null || metadata.Quality is not ("draft" or "standard" or "high") || metadata.BaseProfile is not (BackendSnapshotProfiles.StaticLyricsScene
                or BackendSnapshotProfiles.StaticLyricsSceneAnalysis or BackendSnapshotProfiles.PackageLyricsScene)
            || metadata.ImageProfile != ExportProfile
            || !metadata.FfmpegPathValue() || !metadata.FfprobePathValue()
            || metadata.Format is not ("opaqueMp4" or "transparentProRes4444")
            || capabilities && (metadata.CandidatePath is not null || metadata.Audio is not null || metadata.IncludeAudio
                || metadata.ExecutionProfile != WorkerExportContract.ExecutionProfile)
            || !capabilities && (!metadata.CandidatePathValue() || metadata.IncludeAudio != (metadata.Audio is not null))
            || !BackendAudioInputProfiles.TryResolveExportAudioProfile(metadata.Audio?.AudioProfile, out string profile)
            || metadata.ExecutionProfile != WorkerExportContract.GetExecutionProfile(metadata.Audio is null ? null : profile)
            || metadata.Audio is { } audio && (!audio.Path.PathValue() || (audio.Size <= 0 || audio.Size > metadata.ResourcePolicy?.MaxAudioBytes)
                || !Hash(audio.Sha256)))
            throw InvalidInput("图片导出固定选项或Host文件能力无效。");
        metadata.ResourcePolicy?.Validate();
    }

    private static byte[] EncodeJson<T>(T value, int maximum)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximum) throw Budget("图片导出metadata超过预算。");
        return bytes;
    }

    private static T DecodeJson<T>(ReadOnlySpan<byte> bytes, int maximum)
    {
        if (bytes.Length <= 0 || bytes.Length > maximum) throw InvalidInput("图片导出JSON超过预算。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw InvalidInput("图片导出JSON为空。");
        }
        catch (JsonException) { throw InvalidInput("图片导出JSON无效。"); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name)) throw Invalid("图片导出能力字段重复。");
                RejectDuplicates(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(IsHex);
    private static WorkerProtocolException Invalid(string message) => new(message, "WorkerImageExportCapabilitiesInvalid");
    private static WorkerProtocolException Budget(string message) => new(message, "WorkerSnapshotBudgetExceeded");
    private static WorkerProtocolException InvalidInput(string message) => new(message, "WorkerImageExportInputRejected");

    private static bool PathValue(this string? value) => value is { Length: > 0 and <= 4096 }
        && Path.IsPathFullyQualified(value) && !value.Any(char.IsControl);
    private static bool FfmpegPathValue(this ProjectImageExportWorkerMetadata value) => value.FfmpegPath.PathValue();
    private static bool FfprobePathValue(this ProjectImageExportWorkerMetadata value) => value.FfprobePath.PathValue();
    private static bool CandidatePathValue(this ProjectImageExportWorkerMetadata value) => value.CandidatePath.PathValue();
    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

public sealed record ProjectImageExportWorkerMetadata(
    [property: JsonRequired] string ImageProfile, [property: JsonRequired] string BaseProfile,
    [property: JsonRequired] string ExecutionProfile, [property: JsonRequired] string Format,
    [property: JsonRequired] bool IncludeAudio, [property: JsonRequired] string? CandidatePath,
    [property: JsonRequired] WorkerExportAudioGrant? Audio, [property: JsonRequired] string FfmpegPath,
    [property: JsonRequired] string FfprobePath, string Quality = "standard",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkerExportResourcePolicy? ResourcePolicy = null);

public sealed record ProjectImageExportWorkerInput(Guid TaskId, ProjectImageExportWorkerMetadata Metadata,
    byte[] SnapshotBytes, string SourceSha256);

public sealed record ProjectImageExportWorkerCapabilities(
    [property: JsonRequired] string EngineIdentity, [property: JsonRequired] string TaskProfile,
    [property: JsonRequired] string[] BaseProfiles, [property: JsonRequired] string ImageDecoderProfile,
    [property: JsonRequired] int MaximumInputBytes, [property: JsonRequired] int MaximumImageBytes,
    [property: JsonRequired] int MaximumDimension, [property: JsonRequired] long MaximumDecodedBytes,
    [property: JsonRequired] int MaximumBaseSnapshotBytes, [property: JsonRequired] int MaximumSourceValues,
    [property: JsonRequired] int MaximumReportBytes, [property: JsonRequired] long? MaximumMediaBytes,
    [property: JsonRequired] long? MaximumAudioBytes, [property: JsonRequired] int PackageDiagnosticsVersion,
    [property: JsonRequired] PackageRenderWorkerCapabilities PackageCapabilities,
    [property: JsonRequired] WorkerExportCapabilitiesDto ExportCapabilities);
