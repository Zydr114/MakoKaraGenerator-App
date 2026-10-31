using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>Private bounded transport for the original self-contained native waveform preparation.</summary>
public static class WorkerAudioPreparationContract
{
    public const string Profile = "self-contained-native-waveform-v1";
    public const string RequestKind = "task-audio-prepare-v1";
    public const string CapabilitiesKind = "task-audio-prepare-capabilities-v1";
    public const string ResultKind = "task-audio-prepare-result-v1";
    public const string CapabilitiesResultKind = "task-audio-prepare-capabilities-result-v1";
    public const int MaximumMetadataBytes = 32 * 1024;
    public const int MaximumResultBytes = 4 * 1024 * 1024;
    public const int MaximumBaseBuckets = 500_000;
    public const int BaseBucketSamples = 64;
    public const long MaximumAudioBytes = BackendAudioInputProfiles.MaximumAudioBytes;
    public const int InputHeaderBytes = 32;
    public const int ResultHeaderBytes = 64;
    private const int MaximumInfoBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string[] SupportedFamilies => ["wav", "mp3", "flac", "aac", "mov", "ogg"];

    public static byte[] EncodeInput(Guid invocationId, WorkerAudioPreparationMetadata metadata, bool capabilities = false)
    {
        if (invocationId == Guid.Empty) throw Invalid("准备调用身份为空。");
        ValidateMetadata(metadata, capabilities);
        byte[] json = EncodeJson(metadata, MaximumMetadataBytes);
        byte[] bytes = new byte[InputHeaderBytes + json.Length];
        "KAP1"u8.CopyTo(bytes); bytes[4] = 1; invocationId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), json.Length);
        json.CopyTo(bytes.AsSpan(InputHeaderBytes));
        return bytes;
    }

    public static WorkerAudioPreparationInput DecodeInput(ReadOnlySpan<byte> bytes, bool capabilities = false)
    {
        if (bytes.Length <= InputHeaderBytes || bytes.Length > InputHeaderBytes + MaximumMetadataBytes
            || !bytes[..4].SequenceEqual("KAP1"u8) || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)) != bytes.Length - InputHeaderBytes)
            throw Invalid("准备输入封包无效。");
        Guid id = new(bytes.Slice(8, 16));
        if (id == Guid.Empty) throw Invalid("准备调用身份为空。");
        var metadata = DecodeJson<WorkerAudioPreparationMetadata>(bytes[InputHeaderBytes..], MaximumMetadataBytes);
        ValidateMetadata(metadata, capabilities);
        return new(id, metadata);
    }

    public static void ValidateMetadata(WorkerAudioPreparationMetadata metadata, bool capabilities)
    {
        if (metadata is null || metadata.Profile != Profile || !PathValue(metadata.FfmpegPath) || !PathValue(metadata.FfprobePath)
            || capabilities != (metadata.Audio is null)) throw Invalid("准备profile、工具位置或授权无效。");
        if (metadata.Audio is { } audio && (!PathValue(audio.Path) || !Hash(audio.Sha256)
            || audio.Size is < 0 or > MaximumAudioBytes)) throw Invalid("准备固定输入无效。");
    }

    public static void ValidateCorrelation(WorkerMessage request, WorkerMessage response)
    {
        if (response.RequestId != request.RequestId || response.TaskId != request.TaskId || response.SourceSha256 != request.SourceSha256
            || response.InputSha256 != request.InputSha256 || response.SessionId is not null || response.FrameSequence is not null
            || response.FrameWidth is not null || response.FrameHeight is not null || response.FrameStride is not null || response.FrameFormat is not null
            || response.FrameTimeUs is not null || response.FrameTransparentBackground is not null || response.FrameQuality is not null
            || response.ValidationFramesPerOutput is not null || response.ValidationProgress is not null || response.ExportFormat is not null
            || response.ExportIncludeAudio is not null || response.ExportProgress is not null || response.Diagnostics is not null
            || response.ExportDiagnostics is not null || response.ExportDiagnosticsFailureCode is not null || response.ProcessGroupId is not null
            || response.MediaFileSizeLimitBytes is not null || response.AnalysisProgress is not null || response.ProgressSequence is not null
            || response.DelayMilliseconds != 0) throw Invalid("准备回执与本次固定输入不一致。");
    }

    public static byte[] EncodeCapabilities(WorkerAudioPreparationCapabilities capabilities) => EncodeJson(capabilities, 8192);
    public static WorkerAudioPreparationCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
        => DecodeJson<WorkerAudioPreparationCapabilities>(bytes, 8192);

    public static byte[] EncodeResult(WorkerAudioPreparationResult result)
    {
        ValidateResult(result);
        byte[] info = EncodeJson(result.Info, MaximumInfoBytes);
        int length = checked(ResultHeaderBytes + info.Length + result.Minimums.Length * 8);
        if (length > MaximumResultBytes) throw Budget();
        byte[] bytes = new byte[length];
        "KAP2"u8.CopyTo(bytes); bytes[4] = 1;
        Convert.FromHexString(result.SourceSha256).CopyTo(bytes.AsSpan(8, 32));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40, 8), result.SampleCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48, 4), result.BaseBucketSamples);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52, 4), result.Minimums.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56, 4), info.Length);
        info.CopyTo(bytes.AsSpan(ResultHeaderBytes));
        int offset = ResultHeaderBytes + info.Length;
        for (int index = 0; index < result.Minimums.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), result.Minimums[index]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4, 4), result.Maximums[index]);
            offset += 8;
        }
        return bytes;
    }

    public static WorkerAudioPreparationResult DecodeResult(ReadOnlySpan<byte> bytes, string expectedSourceSha256)
    {
        if (!Hash(expectedSourceSha256) || bytes.Length <= ResultHeaderBytes || bytes.Length > MaximumResultBytes
            || !bytes[..4].SequenceEqual("KAP2"u8) || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(60, 4)) != 0)
            throw Invalid("准备结果封包无效。");
        string source = Convert.ToHexStringLower(bytes.Slice(8, 32));
        if (source != expectedSourceSha256) throw Invalid("准备结果不属于固定来源。");
        long samples = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(40, 8));
        int bucketSamples = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(48, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(52, 4));
        int infoLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(56, 4));
        if (bucketSamples != BaseBucketSamples || samples is <= 0 or > (long)MaximumBaseBuckets * BaseBucketSamples
            || count is < 1 or > MaximumBaseBuckets || count != (samples + BaseBucketSamples - 1) / BaseBucketSamples
            || infoLength is < 1 or > MaximumInfoBytes || ResultHeaderBytes + infoLength + (long)count * 8 != bytes.Length)
            throw Invalid("准备结果计数或容量无效。");
        var info = DecodeJson<WorkerPreparedAudioInfo>(bytes.Slice(ResultHeaderBytes, infoLength), MaximumInfoBytes);
        float[] minimums = new float[count], maximums = new float[count];
        int offset = ResultHeaderBytes + infoLength;
        for (int index = 0; index < count; index++)
        {
            minimums[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, 4));
            maximums[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 4, 4)); offset += 8;
        }
        var result = new WorkerAudioPreparationResult(source, info, samples, bucketSamples, minimums, maximums);
        ValidateResult(result);
        return result;
    }

    private static void ValidateResult(WorkerAudioPreparationResult result)
    {
        if (result is null || !Hash(result.SourceSha256) || result.Info is not { } info || info.DurationUs < 0
            || info.SampleRate <= 0 || info.Channels <= 0 || !TextValue(info.CodecName) || !TextValue(info.ContainerName)
            || !Family(info.ContainerName) || result.BaseBucketSamples != BaseBucketSamples
            || result.SampleCount <= 0 || result.Minimums is null || result.Maximums is null
            || result.Minimums.Length != result.Maximums.Length) throw Invalid("准备结果属性无效。");
        if (result.SampleCount > (long)MaximumBaseBuckets * BaseBucketSamples || result.Minimums.Length > MaximumBaseBuckets)
            throw Budget();
        if (result.Minimums.Length != (result.SampleCount + BaseBucketSamples - 1) / BaseBucketSamples)
            throw Invalid("准备结果缺少完整尾桶。");
        for (int index = 0; index < result.Minimums.Length; index++)
            if (!float.IsFinite(result.Minimums[index]) || !float.IsFinite(result.Maximums[index]) || result.Minimums[index] > result.Maximums[index])
                throw Invalid("准备结果峰值无效。");
    }

    private static bool Family(string container) => SupportedFamilies.Contains(container, StringComparer.Ordinal)
        || container.Split(',').All(value => value is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2");
    private static bool TextValue(string? value) => value is { Length: > 0 and <= 1024 } && !value.Any(char.IsControl);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool PathValue(string? value) => value is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(value)
        && !value.Any(char.IsControl) && Path.GetFullPath(value) == value;
    private static WorkerProtocolException Invalid(string message) => new(message, "WorkerAudioPreparationProtocolInvalid");
    private static WorkerProtocolException Budget() => new("完整波形超过准备结果容量。", "WorkerAudioPreparationBudgetExceeded");
    private static byte[] EncodeJson<T>(T value, int maximum)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is <= 0 || bytes.Length > maximum) throw Invalid("准备JSON超过容量。");
        return bytes;
    }
    private static T DecodeJson<T>(ReadOnlySpan<byte> bytes, int maximum)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximum) throw Invalid("准备JSON超过容量。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid("准备JSON为空。");
        }
        catch (JsonException) { throw Invalid("准备JSON格式无效。"); }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!keys.Add(property.Name)) throw Invalid("准备JSON字段重复。"); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}

public sealed record WorkerAudioPreparationGrant(string Path, long Size, string Sha256);
public sealed record WorkerAudioPreparationMetadata(string Profile, WorkerAudioPreparationGrant? Audio, string FfmpegPath, string FfprobePath);
public sealed record WorkerAudioPreparationInput(Guid TaskId, WorkerAudioPreparationMetadata Metadata);
public sealed record WorkerPreparedAudioInfo(long DurationUs, int SampleRate, int Channels, string CodecName, string ContainerName);
public sealed record WorkerAudioPreparationResult(string SourceSha256, WorkerPreparedAudioInfo Info, long SampleCount,
    int BaseBucketSamples, float[] Minimums, float[] Maximums);
public sealed record WorkerAudioPreparationCapabilities(bool GuardAvailable, bool ToolStartupVerified, string? FailureCode = null,
    string? EngineIdentity = null, string? Profile = null, string[]? Families = null, int? MaximumBaseBuckets = null,
    int? BaseBucketSamples = null, int? MaximumResultBytes = null);
