using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>离线音频分析的私有有界输入；源是固定授权音频，不是工程快照。</summary>
public static class WorkerAnalysisContract
{
    public const string Profile = "analysis-pcmwav-v1";
    public const string CompressedProfile = "analysis-compressed-audio-v1";
    public const string RequestKind = "task-audio-analysis-v1";
    public const string CapabilitiesKind = "task-audio-analysis-capabilities-v1";
    public const string ProgressKind = "task-audio-analysis-progress-v1";
    public const string ResultKind = "task-audio-analysis-result-v1";
    public const string CapabilitiesResultKind = "task-audio-analysis-capabilities-result-v1";
    public const int HeaderBytes = 32;
    public const int MaximumMetadataBytes = 32 * 1024;
    public const int MaximumResultBytes = 4 * 1024 * 1024;
    public const long MaximumAudioBytes = BackendAudioInputProfiles.MaximumAudioBytes;
    public const long MaximumSourceDurationUs = 300_000_000;
    public const long MaximumSamples = 14_400_000;
    public const int MaximumPoints = 15_000;
    public const long ProgressSampleStep = 4800;
    public const long MaximumProgressSequence = MaximumSamples / ProgressSampleStep + 16;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] EncodeInput(Guid taskId, WorkerAnalysisMetadata metadata, bool capabilities = false)
    {
        if (taskId == Guid.Empty) throw Invalid("分析任务身份为空。");
        ValidateMetadata(metadata, capabilities);
        byte[] json = EncodeJson(metadata, MaximumMetadataBytes);
        byte[] bytes = new byte[HeaderBytes + json.Length];
        "KAN1"u8.CopyTo(bytes); bytes[4] = 1; taskId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), json.Length); json.CopyTo(bytes.AsSpan(HeaderBytes));
        return bytes;
    }
    public static WorkerAnalysisInput DecodeInput(ReadOnlySpan<byte> bytes, bool capabilities = false)
    {
        if (bytes.Length <= HeaderBytes || bytes.Length > HeaderBytes + MaximumMetadataBytes || !bytes[..4].SequenceEqual("KAN1"u8)
            || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)) != bytes.Length - HeaderBytes) throw Invalid("分析封包header无效。");
        Guid task = new(bytes.Slice(8, 16)); if (task == Guid.Empty) throw Invalid("分析任务身份为空。");
        var metadata = DecodeJson<WorkerAnalysisMetadata>(bytes[HeaderBytes..], MaximumMetadataBytes); ValidateMetadata(metadata, capabilities);
        return new(task, metadata);
    }
    public static void ValidateMetadata(WorkerAnalysisMetadata metadata, bool capabilities)
    {
        if (metadata is null || !PathValue(metadata.FfmpegPath) || !PathValue(metadata.FfprobePath)
            || capabilities != (metadata.Audio is null)) throw Invalid("分析授权或受信工具位置无效。");
        if (capabilities)
        {
            if (metadata.Profile != Profile) throw Invalid("分析能力profile无效。");
            return;
        }
        var audio = metadata.Audio!;
        if (!PathValue(audio.Path) || audio.Size < (audio.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? 44 : 4) || audio.Size > MaximumAudioBytes || !Hash(audio.Sha256)
            || !BackendAudioInputProfiles.IsSupported(audio.AudioProfile)
            || metadata.Profile != (audio.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? Profile : CompressedProfile))
            throw Invalid("分析音频格式与执行profile不匹配。");
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
            || response.MediaFileSizeLimitBytes is not null || response.DelayMilliseconds != 0) throw Invalid("分析回执不属于本次任务及固定授权。");
    }
    public static int ValidateProgress(WorkerMessage message, long previousSequence, long previousSamples, int previousStage)
    {
        var progress = message.AnalysisProgress;
        int stage = progress?.Stage switch { "validating-source" => 0, "probing" => 1, "decoding" => 2,
            "finalizing-analysis" => 3, "prepared" => 4, _ => -1 };
        if (progress is null || stage < previousStage || stage < 0 || message.ProgressSequence is null
            || message.ProgressSequence <= previousSequence || message.ProgressSequence > MaximumProgressSequence
            || !Number(progress.CompletedSamples, out long samples) || samples < previousSamples || samples > MaximumSamples
            || stage < 2 && samples != 0 || stage >= 3 && samples == 0 || !Number(progress.TimeUs, out long time)
            || time != (stage == 4 ? DurationUs(samples) : samples * 1_000_000 / 48000)
            || (stage == 4 ? progress.TotalSamples != progress.CompletedSamples : progress.TotalSamples is not null)
            || message.Payload.Length != 0 || message.OutputSha256 is not null || message.Error is not null || message.ErrorCode is not null || message.Cancelled)
            throw Invalid("分析实际进度、采样数、时刻或序号无效。");
        return stage;
    }
    public static long DurationUs(long samples) => (samples * 1_000_000 + 47999) / 48000;
    public static bool Number(string? text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value >= 0 && text == value.ToString(CultureInfo.InvariantCulture);
    public static byte[] EncodeJson<T>(T value, int maximumBytes)
    { using var stream = new BoundedStream(maximumBytes); JsonSerializer.Serialize(stream, value, Json); return stream.ToArray(); }
    public static T DecodeJson<T>(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximumBytes) throw Invalid("分析JSON超出预算。");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 8 }); RejectDuplicate(doc.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid("分析JSON为空。");
        }
        catch (JsonException) { throw Invalid("分析JSON格式无效。"); }
    }
    private static void RejectDuplicate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { HashSet<string> keys = []; foreach (var field in value.EnumerateObject()) { if (!keys.Add(field.Name)) throw Invalid("分析JSON字段重复。"); RejectDuplicate(field.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicate(item);
    }
    private static bool PathValue(string? path) => path is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl);
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static WorkerProtocolException Invalid(string message) => new(message, "WorkerAnalysisProtocolInvalid");
    private sealed class BoundedStream(int maximum) : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public byte[] ToArray() => _inner.ToArray(); public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (buffer.Length > maximum - Length) throw Invalid("分析JSON超过完整字节预算。"); _inner.Write(buffer); }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
public sealed record WorkerAnalysisMetadata(string Profile, WorkerAnalysisAudioGrant? Audio, string FfmpegPath, string FfprobePath);
public sealed record WorkerAnalysisAudioGrant(string Path, long Size, string Sha256,
    string AudioProfile = BackendAudioInputProfiles.Pcm16Wave);
public sealed record WorkerAnalysisInput(Guid TaskId, WorkerAnalysisMetadata Metadata);
public sealed record WorkerAnalysisCapabilitiesDto(bool GuardAvailable, bool ToolStartupVerified, string? FailureCode = null,
    string? EngineIdentity = null, string[]? AudioProfiles = null, string[]? SourceProfiles = null);
