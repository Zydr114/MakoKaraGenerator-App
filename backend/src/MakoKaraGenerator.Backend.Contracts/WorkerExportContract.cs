using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>静态下载导出的私有有界合同，媒体仅存在Host授权的候选目录。</summary>
public static class WorkerExportContract
{
    public const string ExecutionProfile = "static-export-pcmwav-v1";
    /// <summary>新增压缩来源必须显式采用此执行能力；旧WAV和纯视频保持原profile。</summary>
    public const string CompressedExecutionProfile = "static-export-compressed-audio-v1";
    /// <summary>精确覆盖判断来自完整解码采样帧，报告微秒是整数下取整。</summary>
    public const string CompressedAudioDurationBasis = "strict-source-rate-decoded-sample-frames";
    public const string RequestKind = "task-export-v1";
    public const string CapabilitiesKind = "task-export-capabilities-v1";
    public const string ProgressKind = "task-export-progress-v1";
    public const string ResultKind = "task-export-result-v1";
    public const string CapabilitiesResultKind = "task-export-capabilities-result-v1";
    public const int HeaderBytes = 32;
    public const int MaximumMetadataBytes = 32 * 1024;
    public const int MaximumReportBytes = 256 * 1024;
    public const int MaximumOccurrences = 512;
    // Compatibility values retained for old capability DTO readers and test fixtures.
    // Actual export limits come from WorkerExportResourcePolicy and may be null.
    public const long MaximumMediaBytes = 64 * 1024 * 1024;
    public const long MaximumAudioBytes = 16 * 1024 * 1024;
    public const int MaximumFrames = 600;
    public const long MaximumDurationUs = 10_000_000;
    public const int MaximumFrameRate = 60;
    public const long MaximumCompressedAudioDurationUs = 300_000_000;
    public const int MaximumWarnings = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] EncodeInput(Guid taskId, WorkerExportMetadata metadata, ReadOnlySpan<byte> snapshot)
    {
        if (taskId == Guid.Empty) throw Invalid("导出任务身份无效。");
        ValidateMetadata(metadata, snapshot.Length == 0);
        byte[] meta = EncodeJson(metadata, MaximumMetadataBytes, "WorkerSnapshotBudgetExceeded");
        if (snapshot.Length > WorkerProtocol.MaxPayloadBytes - HeaderBytes - meta.Length)
            throw new WorkerProtocolException("快照超过扣除导出头和实际metadata后的输入预算。", "WorkerSnapshotBudgetExceeded");
        byte[] bytes = new byte[HeaderBytes + meta.Length + snapshot.Length];
        "KEX1"u8.CopyTo(bytes); bytes[4] = 1; taskId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), meta.Length);
        meta.CopyTo(bytes.AsSpan(HeaderBytes)); snapshot.CopyTo(bytes.AsSpan(HeaderBytes + meta.Length));
        return bytes;
    }
    public static WorkerExportInput DecodeInput(ReadOnlySpan<byte> bytes, bool capabilities)
    {
        if (bytes.Length <= HeaderBytes || bytes.Length > WorkerProtocol.MaxPayloadBytes || !bytes[..4].SequenceEqual("KEX1"u8)
            || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0)
            throw Invalid("导出封包header无效。");
        Guid id = new(bytes.Slice(8, 16)); int metaLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));
        if (id == Guid.Empty || metaLength is <= 0 or > MaximumMetadataBytes || metaLength > bytes.Length - HeaderBytes
            || capabilities != (bytes.Length == HeaderBytes + metaLength)) throw Invalid("导出metadata或snapshot边界无效。");
        var metadata = DecodeJson<WorkerExportMetadata>(bytes.Slice(HeaderBytes, metaLength), MaximumMetadataBytes);
        ValidateMetadata(metadata, capabilities);
        byte[] snapshot = bytes[(HeaderBytes + metaLength)..].ToArray();
        return new(id, metadata, snapshot, WorkerProtocol.ComputeSha256(snapshot));
    }
    public static void ValidateMetadata(WorkerExportMetadata metadata, bool capabilities)
    {
        if (metadata is null || metadata.Quality is not ("draft" or "standard" or "high") || !BackendSnapshotProfiles.IsExportSupported(metadata.CodecProfile)
            || !PathValue(metadata.FfmpegPath) || !PathValue(metadata.FfprobePath) || metadata.Format is not ("opaqueMp4" or "transparentProRes4444")
            || capabilities && (metadata.CandidatePath is not null || metadata.Audio is not null || metadata.IncludeAudio || metadata.ExecutionProfile != ExecutionProfile)
            || !capabilities && (!PathValue(metadata.CandidatePath) || metadata.IncludeAudio != (metadata.Audio is not null))
            || !BackendAudioInputProfiles.TryResolveExportAudioProfile(metadata.Audio?.AudioProfile, out string audioProfile)
            || metadata.ExecutionProfile != GetExecutionProfile(metadata.Audio is null ? null : audioProfile)
            || metadata.Audio is { } audio && (!PathValue(audio.Path) || (audio.Size <= 0 || audio.Size > metadata.ResourcePolicy?.MaxAudioBytes) || !Hash(audio.Sha256)))
            throw Invalid("导出固定选项或Host文件能力无效。");
        metadata.ResourcePolicy?.Validate();
    }
    /// <summary>从受信来源格式唯一派生执行profile，不增加可由客户端选择的策略字段。</summary>
    public static string GetExecutionProfile(string? audioProfile) => audioProfile switch
    {
        null or BackendAudioInputProfiles.Pcm16Wave => ExecutionProfile,
        BackendAudioInputProfiles.Mp3 or BackendAudioInputProfiles.Flac or BackendAudioInputProfiles.Aac
            or BackendAudioInputProfiles.Mov or BackendAudioInputProfiles.Ogg => CompressedExecutionProfile,
        _ => throw new ArgumentException("导出音频来源能力不受支持。", nameof(audioProfile)),
    };
    public static void ValidateCorrelation(WorkerMessage request, WorkerMessage response)
    {
        if (response.RequestId != request.RequestId || response.TaskId != request.TaskId || response.InputSha256 != request.InputSha256
            || response.SourceSha256 != request.SourceSha256 || response.ExportFormat != request.ExportFormat || response.ExportIncludeAudio != request.ExportIncludeAudio
            || response.SessionId is not null || response.FrameSequence is not null || response.ValidationProgress is not null
            || response.ValidationFramesPerOutput is not null || response.FrameWidth is not null || response.FrameHeight is not null
            || response.FrameStride is not null || response.FrameFormat is not null || response.FrameQuality is not null
            || response.FrameTransparentBackground is not null || response.FrameTimeUs is not null || response.ProcessGroupId is not null
            || response.MediaFileSizeLimitBytes is not null || response.DelayMilliseconds != 0)
            throw Invalid("导出回执不属于当前固定来源及选项。");
    }
    public static byte[] EncodeReport(ExportReportDto report, WorkerExportReportExpectation expected)
    { ValidateReport(report, expected); return EncodeJson(report, MaximumReportBytes); }
    public static ExportReportDto DecodeReport(ReadOnlySpan<byte> bytes, WorkerExportReportExpectation expected)
    { var report = DecodeJson<ExportReportDto>(bytes, MaximumReportBytes); ValidateReport(report, expected); return report; }
    /// <summary>导出失败完整诊断独立于预览64KiB／256项合同，不截断真实发生。</summary>
    public static void ValidateDiagnostics(WorkerDiagnosticsSnapshotDto snapshot)
    {
        if (snapshot?.Occurrences is null) throw Invalid("导出实际诊断数组缺失。");
        if (snapshot.Occurrences.Length > MaximumOccurrences)
            throw new WorkerProtocolException("导出实际诊断次数超过完整报告预算。", "WorkerExportReportBudgetExceeded");
        HashSet<Guid> epochs = []; Guid epoch = Guid.Empty; long sequence = 0; bool hasError = false;
        foreach (var item in snapshot.Occurrences)
        {
            if (item is null || item.Epoch == Guid.Empty || item.Diagnostic is null) throw Invalid("导出实际发生身份缺失。");
            if (item.Epoch != epoch)
            { if (!epochs.Add(item.Epoch)) throw Invalid("导出实际发生代次重复。"); epoch = item.Epoch; sequence = 0; }
            var value = item.Diagnostic;
            if (item.Sequence != ++sequence || string.IsNullOrWhiteSpace(value.InstanceId) || value.ArtifactHash is null
                || string.IsNullOrWhiteSpace(value.Stage) || value.TimeUs < 0 || value.Severity is not ("Info" or "Warning" or "Error")
                || string.IsNullOrWhiteSpace(value.Code) || string.IsNullOrWhiteSpace(value.Message)
                || value.Location is { } location && (string.IsNullOrWhiteSpace(location.Path) || location.Line < 1 || location.Column < 1)
                || value.Package is { } package && (string.IsNullOrWhiteSpace(package.PackageId) || string.IsNullOrWhiteSpace(package.ExportId)
                    || package.Revision is null || package.Revision.Length != 71 || !package.Revision.StartsWith("sha256:", StringComparison.Ordinal)
                    || !Hash(package.Revision[7..]))) throw Invalid("导出实际发生序号或来源字段无效。");
            hasError |= value.Severity == "Error";
        }
        if (snapshot.HasError != hasError) throw Invalid("导出实际Error标记不一致。");
        _ = EncodeJson(snapshot, MaximumReportBytes);
    }
    public static void ValidateReport(ExportReportDto report, WorkerExportReportExpectation expected)
    {
        if (expected is null || expected.Quality is not ("draft" or "standard" or "high")) throw Invalid("导出报告缺少固定计划或质量无效。");
        if (!BackendAudioInputProfiles.TryResolveExportAudioProfile(expected.AudioProfile, out string audioProfile)
            || !expected.IncludeAudio && audioProfile != BackendAudioInputProfiles.Pcm16Wave)
            throw Invalid("导出报告来源格式与固定音轨计划不匹配。");
        ValidatePlanLimits(expected.Width, expected.Height, expected.FrameRateNumerator, expected.FrameRateDenominator,
            expected.TotalFrames, expected.VideoDurationUs, expected.Format, expected.ResourcePolicy);
        if (report is null || expected is null || report.RenderBackend is not (null or "cpu" or "gpu")
            || report.Format != expected.Format || report.AudioIncluded != expected.IncludeAudio
            || report.RequestedQuality != expected.Quality || report.ActualQuality != expected.Quality || !report.StrictlyVerified
            || report.ExpectedFrames != expected.TotalFrames || report.DecodedFrames != expected.TotalFrames || report.FramesWritten != expected.TotalFrames
            || report.DroppedFrames != 0 || report.FailedFrames != 0 || report.Width != expected.Width || report.Height != expected.Height
            || report.FrameRateNumerator != expected.FrameRateNumerator || report.FrameRateDenominator != expected.FrameRateDenominator
            || !Number(report.DerivedVideoDurationUs, out long duration) || duration != expected.VideoDurationUs
            || !Number(report.ActualVideoDurationUs, out long actual) || Math.Abs(actual - duration) > 200_000
            || report.MediaSize <= 0 || expected.ResourcePolicy?.MaxMediaBytes is { } mediaLimit && report.MediaSize > mediaLimit || !Hash(report.Sha256)
            || report.Warnings is null || report.Diagnostics is null || report.Warnings.Length > MaximumWarnings || report.Diagnostics.Length > MaximumOccurrences
            || report.Warnings.Any(value => value is null) || report.Diagnostics.Any(value => value is null)
            || string.IsNullOrWhiteSpace(report.VideoCodec) || string.IsNullOrWhiteSpace(report.PixelFormat))
            throw Invalid("导出报告计数、来源、实测媒体或预算无效。");
        bool alpha = expected.Format == "transparentProRes4444";
        if (report.HasAlpha != alpha || report.VideoCodec != (alpha ? "prores" : "h264")
            || (alpha ? !report.PixelFormat.StartsWith("yuva444", StringComparison.Ordinal) : report.PixelFormat != "yuv420p")
            || expected.IncludeAudio && (report.AudioCodec != (alpha ? "pcm_s16le" : "aac")
                || !Number(report.AudioDurationUs, out long audio) || audio < expected.VideoDurationUs
                || expected.ResourcePolicy?.MaxCompressedAudioDurationUs is { } compressedLimit
                    && expected.AudioProfile is not null && expected.AudioProfile != BackendAudioInputProfiles.Pcm16Wave && audio > compressedLimit)
            || !expected.IncludeAudio && (report.AudioCodec is not null || report.AudioDurationUs is not null)) throw Invalid("实际格式、Alpha或音轨不符合固定导出选项。");
        if (expected.IncludeAudio ? !Number(report.ActualAudioDurationUs, out long actualAudio) || Math.Abs(actualAudio - duration) > 50_000
            : report.ActualAudioDurationUs is not null) throw Invalid("实际产物音轨时长不符合明示的50ms量化容差。");
        Dictionary<Guid, long> sequences = [];
        foreach (var diagnostic in report.Diagnostics)
        {
            var occurrence = diagnostic.Occurrence;
            if (diagnostic.OriginKind != "runtime" || diagnostic.Severity is not ("info" or "warning") || string.IsNullOrWhiteSpace(diagnostic.Code)
                || string.IsNullOrWhiteSpace(diagnostic.Message) || occurrence is null || occurrence.Epoch == Guid.Empty
                || !Number(occurrence.Sequence, out long sequence) || sequence <= 0 || !Number(occurrence.TimeUs, out long time, positive: false) || time >= duration
                || string.IsNullOrWhiteSpace(occurrence.InstanceId) || occurrence.ArtifactHash is null || string.IsNullOrWhiteSpace(occurrence.Stage)
                || occurrence.Location is { } location && (string.IsNullOrWhiteSpace(location.Path) || location.Line < 1 || location.Column < 1)
                || occurrence.Package is { } package && (string.IsNullOrWhiteSpace(package.PackageId) || string.IsNullOrWhiteSpace(package.ExportId)
                    || package.Revision is null || package.Revision.Length != 71 || !package.Revision.StartsWith("sha256:", StringComparison.Ordinal)
                    || !Hash(package.Revision[7..])))
                throw Invalid("导出实际诊断无效或含Error。");
            sequences.TryGetValue(occurrence.Epoch, out long previous);
            if (sequence != previous + 1) throw Invalid("导出实际发生序号不连续。");
            sequences[occurrence.Epoch] = sequence;
        }
    }
    public static void ValidatePlanLimits(int width, int height, int numerator, int denominator, int frames, long videoDuration, string format, WorkerExportResourcePolicy? policy = null)
    {
        policy?.Validate();
        if (width < 1 || height < 1 || (long)width * height * 4 > int.MaxValue
            || numerator < 1 || denominator < 1 || frames < 1 || videoDuration <= 0
            || width > policy?.MaxCanvasWidth || height > policy?.MaxCanvasHeight
            || (long)width * height * 4 > policy?.MaxRawFrameBytes
            || numerator > (long?)policy?.MaxFrameRate * denominator || frames > policy?.MaxFrames || videoDuration > policy?.MaxDurationUs
            || format is not ("opaqueMp4" or "transparentProRes4444") || format == "opaqueMp4" && (width % 2 != 0 || height % 2 != 0))
            throw new WorkerProtocolException("派生正式导出计划超过静态能力。", "WorkerExportInputRejected");
    }
    public static byte[] EncodeJson<T>(T value, int maximumBytes)
        => EncodeJson(value, maximumBytes, "WorkerExportReportBudgetExceeded");
    private static byte[] EncodeJson<T>(T value, int maximumBytes, string budgetFailureCode)
    { using var stream = new BoundedStream(maximumBytes, budgetFailureCode); JsonSerializer.Serialize(stream, value, Json); return stream.ToArray(); }
    public static T DecodeJson<T>(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximumBytes) throw Invalid("导出JSON超出预算。");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 16 });
            RejectDuplicate(doc.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid("导出JSON为空。");
        }
        catch (JsonException) { throw Invalid("导出JSON格式无效。"); }
    }
    private static void RejectDuplicate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> fields = [];
            foreach (var field in element.EnumerateObject()) { if (!fields.Add(field.Name)) throw Invalid("导出JSON字段重复。"); RejectDuplicate(field.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicate(item);
    }
    private static bool PathValue(string? path) => path is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl);
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Number(string? text, out long value, bool positive = true) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && (positive ? value > 0 : value >= 0) && text == value.ToString(CultureInfo.InvariantCulture);
    private static WorkerProtocolException Invalid(string message) => new(message, "WorkerExportProtocolInvalid");
    private sealed class BoundedStream(int maximum, string budgetFailureCode) : Stream
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
        { if (buffer.Length > maximum - Length) throw new WorkerProtocolException("导出完整JSON超过预算。", budgetFailureCode); _inner.Write(buffer); }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
public sealed record WorkerExportMetadata(string ExecutionProfile, string CodecProfile, string Format, bool IncludeAudio,
    string? CandidatePath, WorkerExportAudioGrant? Audio, string FfmpegPath, string FfprobePath, string Quality = "standard",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkerExportResourcePolicy? ResourcePolicy = null);
public sealed record WorkerExportAudioGrant(string Path, long Size, string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AudioProfile = null);
public sealed record WorkerExportInput(Guid TaskId, WorkerExportMetadata Metadata, byte[] SnapshotBytes, string SourceSha256);
public sealed record WorkerExportReportExpectation(string Format, bool IncludeAudio, int Width, int Height,
    int FrameRateNumerator, int FrameRateDenominator, int TotalFrames, long VideoDurationUs, string? AudioProfile = null,
    string Quality = "standard", WorkerExportResourcePolicy? ResourcePolicy = null);
public sealed record WorkerExportCapabilitiesDto(bool GuardAvailable, bool OpaqueMp4, bool TransparentProRes4444, string? FailureCode = null,
    string? EngineIdentity = null, string[]? SourceProfiles = null, string[]? AudioProfiles = null, string[]? ExecutionProfiles = null);
