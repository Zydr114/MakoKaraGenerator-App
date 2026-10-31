using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>已保存工程图片正式验证的独立私有能力与任务封包合同。</summary>
public static class ProjectImageValidationWorkerContract
{
    public const string CapabilitiesKind = "project-image-validation-capabilities-v1";
    public const string CapabilitiesResultKind = "project-image-validation-capabilities-result-v1";
    public const string RequestKind = "task-project-image-validation-v1";
    public const string ProgressKind = "task-project-image-validation-progress-v1";
    public const string ResultKind = "task-project-image-validation-result-v1";
    public const string ValidationProfile = BackendSnapshotProfiles.ProjectImageValidation;
    public const string ImageDecoderProfile = ProjectImagePreviewWorkerContract.ImageDecoderProfile;
    public const int HeaderBytes = WorkerValidationContract.HeaderBytes;
    public const int MaximumInputBytes = WorkerProtocol.MaxPayloadBytes - HeaderBytes;
    public const int MaximumImageBytes = ProjectImagePreviewWorkerContract.MaximumImageBytes;
    public const int MaximumDimension = ProjectImagePreviewWorkerContract.MaximumDimension;
    public const long MaximumDecodedBytes = ProjectImagePreviewWorkerContract.MaximumDecodedBytes;
    public const int MaximumBaseSnapshotBytes = ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes;
    public const int MaximumSourceValues = ProjectImagePreviewWorkerContract.MaximumSourceValues;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public static byte[] EncodeCapabilitiesRequest() => [];

    public static byte[] EncodeCapabilities(ProjectImageValidationWorkerCapabilities value)
    {
        ValidateCapabilities(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > 8192) throw Invalid();
        return bytes;
    }

    public static ProjectImageValidationWorkerCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 8192) throw Invalid();
        try
        {
            using (JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }))
                RejectDuplicates(document.RootElement);
            ProjectImageValidationWorkerCapabilities value = JsonSerializer.Deserialize<ProjectImageValidationWorkerCapabilities>(bytes, Options) ?? throw Invalid();
            ValidateCapabilities(value);
            return value;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static void ValidateCapabilities(ProjectImageValidationWorkerCapabilities value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.EngineIdentity)
            || value.CodecProfile != ValidationProfile || value.ImageDecoderProfile != ImageDecoderProfile
            || value.MaximumInputBytes != MaximumInputBytes || value.MaximumImageBytes != MaximumImageBytes
            || value.MaximumDimension != MaximumDimension || value.MaximumDecodedBytes != MaximumDecodedBytes
            || value.MaximumBaseSnapshotBytes != MaximumBaseSnapshotBytes || value.MaximumSourceValues != MaximumSourceValues
            || value.MaximumReportBytes != WorkerValidationContract.MaximumReportBytes
            || value.PackageDiagnosticsVersion != WorkerPackageDiagnosticsContract.Version
            || value.BaseProfiles is null || value.BaseProfiles.Length != 3
            || !value.BaseProfiles.ToHashSet(StringComparer.Ordinal).SetEquals([
                BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                BackendSnapshotProfiles.PackageLyricsScene]) || value.PackageCapabilities is null)
            throw Invalid();
        PackageRenderWorkerContract.ValidateCapabilities(value.PackageCapabilities);
        if (value.PackageCapabilities.EngineIdentity != value.EngineIdentity) throw Invalid();
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static WorkerProtocolException Invalid() => new("图片验证 Worker 能力与固定合同不匹配。", "WorkerImageValidationCapabilitiesInvalid");

    /// <summary>固定验证头中独立的 image discriminator；payload 是完整 image envelope。</summary>
    public static byte[] EncodeInput(Guid taskId, int framesPerOutput, ReadOnlySpan<byte> snapshot)
    {
        WorkerValidationContract.ValidatePlan(framesPerOutput);
        if (taskId == Guid.Empty || snapshot.Length is <= 0 or > MaximumInputBytes)
            throw new WorkerProtocolException("图片验证任务身份或封包预算无效。", "WorkerImageValidationInputRejected");
        byte[] bytes = new byte[HeaderBytes + snapshot.Length];
        "KTI1"u8.CopyTo(bytes);
        bytes[4] = 1; bytes[5] = 1;
        taskId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), framesPerOutput);
        snapshot.CopyTo(bytes.AsSpan(HeaderBytes));
        return bytes;
    }

    public static WorkerValidationInput DecodeInput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= HeaderBytes || bytes.Length > WorkerProtocol.MaxPayloadBytes
            || !bytes[..4].SequenceEqual("KTI1"u8) || bytes[4] != 1 || bytes[5] != 1
            || bytes[6] != 0 || bytes[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0)
            throw new WorkerProtocolException("图片验证任务封包无效。", "WorkerImageValidationInputRejected");
        Guid id = new(bytes.Slice(8, 16));
        int frames = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));
        WorkerValidationContract.ValidatePlan(frames);
        if (id == Guid.Empty) throw new WorkerProtocolException("图片验证任务缺少固定身份。", "WorkerImageValidationInputRejected");
        byte[] snapshot = bytes[HeaderBytes..].ToArray();
        return new(id, frames, snapshot, WorkerProtocol.ComputeSha256(snapshot), ValidationProfile);
    }
}

public sealed record ProjectImageValidationWorkerCapabilities(
    [property: JsonRequired] string EngineIdentity,
    [property: JsonRequired] string CodecProfile,
    [property: JsonRequired] string[] BaseProfiles,
    [property: JsonRequired] string ImageDecoderProfile,
    [property: JsonRequired] int MaximumInputBytes,
    [property: JsonRequired] int MaximumImageBytes,
    [property: JsonRequired] int MaximumDimension,
    [property: JsonRequired] long MaximumDecodedBytes,
    [property: JsonRequired] int MaximumBaseSnapshotBytes,
    [property: JsonRequired] int MaximumSourceValues,
    [property: JsonRequired] int MaximumReportBytes,
    [property: JsonRequired] int PackageDiagnosticsVersion,
    [property: JsonRequired] PackageRenderWorkerCapabilities PackageCapabilities);
