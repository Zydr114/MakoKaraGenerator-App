using System.Buffers.Binary;
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定静态工程双输出验证的私有任务合同；报告传输不包含像素。</summary>
public static class WorkerValidationContract
{
    private static readonly JsonSerializerOptions ReportOptions = new(WorkerProtocol.JsonOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    public const string RequestKind = "task-validation-v1";
    public const string ProgressKind = "task-validation-progress-v1";
    public const string ResultKind = "task-validation-result-v1";
    public const int HeaderBytes = 32;
    public const int MaximumSnapshotBytes = WorkerProtocol.MaxPayloadBytes - HeaderBytes;
    public const int MaximumFramesPerOutput = 600;
    public const int MaximumCanvasWidth = 1920;
    public const int MaximumCanvasHeight = 1080;
    public const int MaximumRawFrameBytes = 8 * 1024 * 1024;
    public const int MaximumReportBytes = 256 * 1024;
    public const int MaximumIssues = 256;
    public const int MaximumOccurrences = 512;

    public static byte[] EncodeInput(Guid taskId, int framesPerOutput, ReadOnlySpan<byte> snapshot, bool packageProfile = false)
    {
        ValidatePlan(framesPerOutput);
        if (taskId == Guid.Empty || snapshot.Length is <= 0 or > MaximumSnapshotBytes)
            throw new WorkerProtocolException("验证任务身份或快照预算无效。", "WorkerValidationInputRejected");
        byte[] bytes = new byte[HeaderBytes + snapshot.Length];
        "KTV1"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = packageProfile ? (byte)1 : (byte)0;
        taskId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), framesPerOutput);
        snapshot.CopyTo(bytes.AsSpan(HeaderBytes));
        return bytes;
    }

    public static WorkerValidationInput DecodeInput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= HeaderBytes || bytes.Length > WorkerProtocol.MaxPayloadBytes
            || !bytes[..4].SequenceEqual("KTV1"u8) || bytes[4] != 1
            || bytes[5] > 1 || bytes[6] != 0 || bytes[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0)
            throw new WorkerProtocolException("验证任务封包无效。", "WorkerValidationInputRejected");
        Guid id = new(bytes.Slice(8, 16));
        int frames = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));
        ValidatePlan(frames);
        if (id == Guid.Empty) throw new WorkerProtocolException("验证任务缺少固定身份。", "WorkerValidationInputRejected");
        byte[] snapshot = bytes[HeaderBytes..].ToArray();
        return new(id, frames, snapshot, WorkerProtocol.ComputeSha256(snapshot), bytes[5] == 1 ? "package-lyrics-scene-v1" : "static");
    }

    public static void ValidatePlan(int framesPerOutput)
    {
        if (framesPerOutput is < 1 or > MaximumFramesPerOutput)
            throw new WorkerProtocolException("验证帧计划超出首批能力。", "WorkerValidationInputRejected");
    }

    public static void ValidateGeometryAndTiming(int width, int height, long? durationUs, int numerator, int denominator, int framesPerOutput)
    {
        ValidatePlan(framesPerOutput);
        if (width < 1 || height < 1 || width > MaximumCanvasWidth || height > MaximumCanvasHeight
            || (long)width * height * 4 > MaximumRawFrameBytes || numerator < 1 || denominator < 1
            || durationUs is null or <= 0 || checked((long)(framesPerOutput - 1) * 1_000_000 * denominator / numerator) > durationUs)
            throw new WorkerProtocolException("验证画布、时长或末帧超出固定静态任务能力。", "WorkerValidationInputRejected");
    }

    public static void ValidateCorrelation(WorkerMessage request, WorkerMessage response)
    {
        if (response.RequestId != request.RequestId || response.TaskId != request.TaskId
            || response.SourceSha256 != request.SourceSha256 || response.InputSha256 != request.InputSha256
            || response.ValidationFramesPerOutput != request.ValidationFramesPerOutput
            || response.SessionId is not null || response.FrameSequence is not null || response.FrameWidth is not null
            || response.FrameHeight is not null || response.FrameStride is not null || response.FrameTimeUs is not null
            || response.FrameFormat is not null || response.FrameQuality is not null || response.FrameTransparentBackground is not null)
            throw new WorkerProtocolException("验证任务回执不属于固定来源及帧计划。");
    }

    public static void ValidateProgress(WorkerMessage request, WorkerMessage response, long previousSequence,
        int previousCompleted, int frameRateNumerator, int frameRateDenominator)
    {
        ValidateCorrelation(request, response);
        int frames = request.ValidationFramesPerOutput ?? 0;
        BackendJobProgressDto? progress = response.ValidationProgress;
        if (response.Kind != ProgressKind || response.ProgressSequence is null || response.ProgressSequence <= previousSequence
            || response.ProgressSequence > MaximumProgressSequence(frames)
            || response.Payload.Length != 0 || response.OutputSha256 is not null || response.Cancelled || response.Error is not null
            || progress is null || progress.Stage != "rendering" || progress.TotalFrames != frames * 2
            || progress.CompletedFrames <= previousCompleted || progress.CompletedFrames > frames * 2
            || progress.Transparent != (progress.CompletedFrames > frames)
            || !Number(progress.TimeUs, out long time) || frameRateNumerator <= 0 || frameRateDenominator <= 0)
            throw new WorkerProtocolException("验证任务进度无效或已过期。");
        long expectedTime = (long)((progress.CompletedFrames - 1) % frames) * 1_000_000 * frameRateDenominator / frameRateNumerator;
        if (time != expectedTime) throw new WorkerProtocolException("验证进度时间不属于实际帧计划。");
    }

    public static ValidationReportDto DecodeReport(ReadOnlySpan<byte> bytes, int framesPerOutput)
    {
        if (bytes.Length is <= 0 or > MaximumReportBytes) throw BudgetFailure();
        ValidationReportDto report;
        try { report = JsonSerializer.Deserialize<ValidationReportDto>(bytes, ReportOptions)
                ?? throw new WorkerProtocolException("验证报告为空。"); }
        catch (JsonException error) { throw new WorkerProtocolException("验证报告 JSON 无效：" + error.Message); }
        ValidateReport(report, framesPerOutput);
        return report;
    }

    public static byte[] EncodeReport(ValidationReportDto report, int framesPerOutput)
    {
        ValidateReport(report, framesPerOutput);
        return SerializeBounded(report);
    }

    /// <summary>逐项累计检查使用同一有界序列化器，巨条不会先分配无界输出数组。</summary>
    public static int MeasureItem<T>(T value) => SerializeBounded(value).Length;

    public static void ValidateReport(ValidationReportDto report, int framesPerOutput)
    {
        ValidatePlan(framesPerOutput);
        if (report is null || report.Issues is null || report.Diagnostics is null || report.RenderBackend is not (null or "cpu" or "gpu"))
            throw new WorkerProtocolException("验证报告缺少完整集合。");
        if (report.Issues.Length > MaximumIssues || report.Diagnostics.Length > MaximumOccurrences) throw BudgetFailure();
        if (report.OpaqueFrames < 0 || report.OpaqueFrames > framesPerOutput || report.TransparentFrames < 0
            || report.TransparentFrames > framesPerOutput || report.TransparentFrames > 0 && report.OpaqueFrames != framesPerOutput)
            throw new WorkerProtocolException("验证报告完成帧计数无效。");
        bool hasError = false;
        foreach (ValidationIssueDto issue in report.Issues)
        {
            if (issue is null || !Number(issue.TimeUs, out _) || string.IsNullOrWhiteSpace(issue.Code)
                || !ValidLocation(issue.Location) || !ValidDiagnostic(issue.Diagnostic)) throw new WorkerProtocolException("验证汇总问题字段无效。");
            hasError |= issue.IsError;
        }
        Dictionary<(bool Mode, Guid Epoch), long> sequences = [];
        foreach (ValidationDiagnosticDto value in report.Diagnostics)
        {
            BackendDiagnosticDto? diagnostic = value?.Diagnostic;
            BackendDiagnosticOccurrenceDto? occurrence = diagnostic?.Occurrence;
            if (value is null || diagnostic is null || diagnostic.OriginKind != "runtime" || occurrence is null
                || occurrence.Epoch == Guid.Empty || !Number(occurrence.Sequence, out long sequence) || sequence < 1
                || !Number(occurrence.TimeUs, out _) || string.IsNullOrWhiteSpace(occurrence.InstanceId)
                || occurrence.ArtifactHash is null || string.IsNullOrWhiteSpace(occurrence.Stage)
                || diagnostic.Severity is not ("info" or "warning" or "error") || string.IsNullOrWhiteSpace(diagnostic.Code)
                || string.IsNullOrWhiteSpace(diagnostic.Message) || !ValidLocation(occurrence.Location)
                || occurrence.Package is { } package && (string.IsNullOrWhiteSpace(package.PackageId)
                    || package.Revision is null || package.Revision.Length != 71 || !package.Revision.StartsWith("sha256:", StringComparison.Ordinal)
                    || package.Revision.AsSpan(7).ContainsAnyExcept(LowerHex) || string.IsNullOrWhiteSpace(package.ExportId)))
                throw new WorkerProtocolException("验证报告缺少实际 Runtime 发生来源。");
            var key = (value.Transparent, occurrence.Epoch);
            sequences.TryGetValue(key, out long previous);
            if (sequence != previous + 1) throw new WorkerProtocolException("验证实际发生序号不连续。");
            sequences[key] = sequence; hasError |= diagnostic.Severity == "error";
        }
        if (report.Succeeded ? hasError || report.OpaqueFrames != framesPerOutput || report.TransparentFrames != framesPerOutput : !hasError)
            throw new WorkerProtocolException("验证结果与完整帧数或 Error 报告不一致。");
    }

    public static int MaximumProgressSequence(int framesPerOutput)
    {
        ValidatePlan(framesPerOutput);
        int last = framesPerOutput - 1;
        return 2 * (last / 10 + 1 + (last % 10 == 0 ? 0 : 1));
    }

    public static void ValidateReportTimes(ValidationReportDto report, long lastFrameTimeUs)
    {
        foreach (var issue in report.Issues)
            if (!Number(issue.TimeUs, out long time) || time > lastFrameTimeUs)
                throw new WorkerProtocolException("验证汇总时间超出固定帧计划。");
        foreach (var diagnostic in report.Diagnostics)
            if (!Number(diagnostic.Diagnostic.Occurrence!.TimeUs, out long time) || time > lastFrameTimeUs)
                throw new WorkerProtocolException("验证实际发生时间超出固定帧计划。");
    }

    private static bool ValidLocation(BackendDiagnosticLocationDto? value) => value is null
        || !string.IsNullOrWhiteSpace(value.Path) && value.Line > 0 && value.Column > 0;
    private static bool ValidDiagnostic(BackendDiagnosticDto? value)
    {
        if (value is null) return true;
        bool source = value.Source is not null, image = value.ImageEffect is not null, material = value.Material is not null;
        if ((source ? 1 : 0) + (image ? 1 : 0) + (material ? 1 : 0) != 1) return false;
        if (value.OriginKind != (source ? "compileSource" : image ? "imageEffect" : "material")
            || value.Occurrence is not null) return false;
        if (source)
        {
            var d = value.Source!;
            return !string.IsNullOrWhiteSpace(d.PackageId) && !string.IsNullOrWhiteSpace(d.Revision)
                && d.Revision.StartsWith("sha256:", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(d.Code) && d.Line >= 0 && d.Column >= 0;
        }
        if (image)
        {
            var d = value.ImageEffect!;
            return !string.IsNullOrWhiteSpace(d.InstanceId) && !string.IsNullOrWhiteSpace(d.ArtifactHash)
                && d.Graph is not null && !string.IsNullOrWhiteSpace(d.Graph.PackageId)
                && !string.IsNullOrWhiteSpace(d.Graph.Revision) && !string.IsNullOrWhiteSpace(d.Graph.ExportId)
                && !string.IsNullOrWhiteSpace(d.InputId) && !string.IsNullOrWhiteSpace(d.Code);
        }
        var m = value.Material!;
        return !string.IsNullOrWhiteSpace(m.InputId) && !string.IsNullOrWhiteSpace(m.Code)
            && m.Material is not null && !string.IsNullOrWhiteSpace(m.Material.PackageId)
            && !string.IsNullOrWhiteSpace(m.Material.Revision) && !string.IsNullOrWhiteSpace(m.Material.ExportId)
            && m.Owner is not null && m.Owner.Kind is "script" or "source";
    }
    private static bool Number(string? value, out long number) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= 0 && value == number.ToString(CultureInfo.InvariantCulture);
    private static WorkerProtocolException BudgetFailure() => new("完整验证报告超过预算。", "WorkerValidationReportBudgetExceeded");
    private static byte[] SerializeBounded<T>(T value)
    {
        using var stream = new ReportStream();
        JsonSerializer.Serialize(stream, value, ReportOptions);
        return stream.ToArray();
    }
    private sealed class ReportStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public byte[] ToArray() => _inner.ToArray();
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > MaximumReportBytes) throw BudgetFailure();
            _inner.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing)
        { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}

public sealed record WorkerValidationInput(Guid TaskId, int FramesPerOutput, byte[] SnapshotBytes, string SourceSha256, string Profile = "static");
