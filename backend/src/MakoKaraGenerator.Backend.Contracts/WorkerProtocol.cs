using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>受管理 Worker 的私有协议。它不复用工程保存格式。</summary>
public static class WorkerProtocol
{
    /// <summary>与 Worker 进程匹配的私有协议版本。</summary>
    public const string Version = "worker-1";

    /// <summary>单条消息（JSON 及长度前缀）的最大字节数。</summary>
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    /// <summary>单条输入的保守上限，避免 JSON base64 放大后越过消息上限。</summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>计算 Worker 输入合同中的固定 SHA-256。</summary>
    public static string ComputeSha256(ReadOnlySpan<byte> input) =>
        Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

    /// <summary>校验消息边界。请求哈希对应输入；帧回执独立校验输出哈希。</summary>
    public static void Validate(WorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.ProtocolVersion, Version, StringComparison.Ordinal))
            throw new WorkerProtocolException($"Worker 协议版本不匹配：{message.ProtocolVersion}。");
        if (string.IsNullOrWhiteSpace(message.Kind) || string.IsNullOrWhiteSpace(message.RequestId))
            throw new WorkerProtocolException("Worker 消息缺少 kind 或 requestId。");
        if (message.Payload is null || message.Payload.Length > MaxPayloadBytes)
            throw new WorkerProtocolException("Worker payload 为空或超过单条上限。");
        bool isInput = message.Kind is "render" or "render-frame-v1" or StaticRenderWorkerContract.OpenKind
            or StaticRenderWorkerContract.RenderKind or StaticRenderWorkerContract.CloseKind or StaticRenderWorkerContract.CapabilitiesKind
            or WorkerSceneMeasurementContract.CapabilitiesKind or WorkerSceneMeasurementContract.RequestKind
            or WorkerValidationContract.RequestKind
            or ProjectImageValidationWorkerContract.CapabilitiesKind or ProjectImageValidationWorkerContract.RequestKind
            or RuntimeValidationWorkerContract.CapabilitiesKind or RuntimeValidationWorkerContract.RequestKind
            or ProjectImageExportWorkerContract.CapabilitiesKind or ProjectImageExportWorkerContract.RequestKind
            or WorkerExportContract.RequestKind or WorkerExportContract.CapabilitiesKind
            or WorkerAnalysisContract.RequestKind or WorkerAnalysisContract.CapabilitiesKind
            or WorkerAudioPreparationContract.RequestKind or WorkerAudioPreparationContract.CapabilitiesKind
            or FontWorkerContract.ListKind or FontWorkerContract.InspectKind
            or PackageRenderWorkerContract.CapabilitiesKind or PackageRenderWorkerContract.OpenKind
            or PackageRenderWorkerContract.ValidationCapabilitiesKind
            or PackageRenderWorkerContract.RenderKind or PackageRenderWorkerContract.CloseKind
            or PackageResourceWorkerContract.CapabilitiesKind or PackageResourceWorkerContract.ValidateKind
            or ProjectImagePreviewWorkerContract.CapabilitiesKind or ProjectImagePreviewWorkerContract.OpenKind
            or ProjectRuntimeInputWorkerContract.CapabilitiesKind or ProjectRuntimeInputWorkerContract.OpenKind;
        if (isInput && message.InputSha256 is null)
            throw new WorkerProtocolException("Worker 输入缺少 SHA-256。");
        // 回执的 inputSha256 是请求关联值，不能与经过渲染的输出像素比较。
        // 旧 opaque render 回执仍没有 outputSha256，保留其原有回显校验。
        bool isEcho = message.Kind == "result" && message.OutputSha256 is null && !message.Cancelled;
        if ((isInput || isEcho) && message.InputSha256 is not null &&
            !string.Equals(message.InputSha256, ComputeSha256(message.Payload), StringComparison.OrdinalIgnoreCase))
            throw new WorkerProtocolException("Worker 输入 SHA-256 不匹配。");
        if (message.OutputSha256 is not null &&
            !string.Equals(message.OutputSha256, ComputeSha256(message.Payload), StringComparison.OrdinalIgnoreCase))
            throw new WorkerProtocolException("Worker 输出 SHA-256 不匹配。");
        if (message.DelayMilliseconds is < 0 or > 60_000)
            throw new WorkerProtocolException("Worker 延迟参数超出范围。");
        if (message.GraphPixelChecks is not null && message.Kind is not (PackageRenderWorkerContract.OpenKind
                or ProjectImagePreviewWorkerContract.OpenKind or ProjectRuntimeInputWorkerContract.OpenKind))
            throw new WorkerProtocolException("图像素检查开关只能属于会话打开请求。");
        if (message.FrameInspection is not null && message.Kind is not (StaticRenderWorkerContract.FrameResultKind or PackageRenderWorkerContract.FrameResultKind))
            throw new WorkerProtocolException("帧检查投影只能属于帧回执。", "WorkerInspectionInvalid");
        if (message.AnalysisProgress is not null && message.Kind != WorkerAnalysisContract.ProgressKind)
            throw new WorkerProtocolException("分析进度不能混入其他回执。", "WorkerAnalysisProtocolInvalid");
        if (message.PackageDiagnostics is not null)
        {
            if ((message.Kind != PackageRenderWorkerContract.FrameResultKind && message.Kind != "error")
                || message.SessionId is null || message.Cancelled || message.TaskId is not null || message.ExportFormat is not null
                || message.AnalysisProgress is not null || message.ExportDiagnostics is not null)
                throw new WorkerProtocolException("包诊断混入未协商消息。", "WorkerPackageDiagnosticsInvalid");
            WorkerPackageDiagnosticsContract.Validate(message.PackageDiagnostics);
            var package = message.PackageDiagnostics;
            bool compilation = message.ErrorCode == "WorkerPackageCompilationFailed" && package.Source.Length != 0
                && package.Image.Length == 0 && package.Material.Length == 0 && message.Diagnostics is null;
            bool runtime = message.ErrorCode is not null && message.ErrorCode != "WorkerPackageCompilationFailed"
                && message.Diagnostics is { HasError: true } && package.Source.Length == 0;
            if (message.Kind == "error" && !compilation && !runtime
                || message.Kind == PackageRenderWorkerContract.FrameResultKind && package.Source.Length != 0)
                throw new WorkerProtocolException("包诊断不属于编译失败或实际Runtime帧。", "WorkerPackageDiagnosticsInvalid");
        }
        if (message.Diagnostics is not null) WorkerDiagnosticsSnapshotDto.Validate(message.Diagnostics);
        if (message.ExportDiagnostics is not null) WorkerExportContract.ValidateDiagnostics(message.ExportDiagnostics);
        if (message.ExportSourceMaterialFailure is not null)
        {
            BackendSourceMaterialFailureContract.Validate(message.ExportSourceMaterialFailure);
            if (message.ErrorCode != message.ExportSourceMaterialFailure.Code) throw new WorkerProtocolException(
                "源材质诊断与导出主因不一致。", "WorkerExportProtocolInvalid");
        }
        if ((message.ExportDiagnostics is not null || message.ExportDiagnosticsFailureCode is not null || message.ExportSourceMaterialFailure is not null)
            && (message.Kind != "error" || message.ExportFormat is null || message.Diagnostics is not null
                || message.ExportDiagnosticsFailureCode is not null && (message.ExportDiagnostics is not null || message.ExportSourceMaterialFailure is not null)
                || message.ExportDiagnosticsFailureCode is not null and not "WorkerExportReportBudgetExceeded"))
            throw new WorkerProtocolException("导出失败诊断字段只能属于独立导出错误回执。", "WorkerExportProtocolInvalid");
    }

    /// <summary>写入 4 字节 little-endian 长度前缀和 JSON 消息。</summary>
    public static async ValueTask WriteAsync(Stream stream, WorkerMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate(message);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is <= 0 or > MaxMessageBytes)
            throw new WorkerProtocolException("Worker 消息长度超出上限。");
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取并验证一条有界消息；EOF 返回 null。</summary>
    public static async ValueTask<WorkerMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(int)];
        int read = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        if (read != prefix.Length) throw new WorkerProtocolException("Worker 长度前缀不完整。");
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxMessageBytes)
            throw new WorkerProtocolException("Worker 消息长度前缀超出上限。");
        byte[] body = new byte[length];
        if (await ReadAtMostAsync(stream, body, cancellationToken).ConfigureAwait(false) != length)
            throw new WorkerProtocolException("Worker 消息正文不完整。");
        WorkerMessage message;
        try
        {
            using (JsonDocument document = JsonDocument.Parse(body))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new WorkerProtocolException("Worker 消息根必须为对象。");
                    var packages = document.RootElement.EnumerateObject().Where(p => p.Name.Equals("packageDiagnostics", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (packages.Length > 1) throw new WorkerProtocolException("重复包诊断容器。", "WorkerPackageDiagnosticsInvalid");
                    if (packages.Length == 1) WorkerPackageDiagnosticsContract.ValidateWire(packages[0].Value);
                }
            message = JsonSerializer.Deserialize<WorkerMessage>(body, JsonOptions)
                ?? throw new WorkerProtocolException("Worker 消息不是有效 JSON。");
        }
        catch (JsonException error)
        {
            throw new WorkerProtocolException($"Worker 消息不是有效 JSON：{error.Message}");
        }
        Validate(message);
        return message;
    }

    private static async ValueTask<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        return offset;
    }
}

/// <summary>Worker 控制消息；请求 payload 是固定输入，帧回执 payload 是有界原始像素。</summary>
public sealed record WorkerMessage
{
    public WorkerMessage(string kind, string requestId, string? inputSha256 = null, byte[]? payload = null,
        int delayMilliseconds = 0, string? error = null, bool cancelled = false,
        string protocolVersion = WorkerProtocol.Version, string? outputSha256 = null,
        int? frameWidth = null, int? frameHeight = null, int? frameStride = null,
        long? frameTimeUs = null, string? frameFormat = null, string? sessionId = null,
        string? sourceSha256 = null, long? frameSequence = null, bool? frameTransparentBackground = null,
        WorkerDiagnosticsSnapshotDto? diagnostics = null, string? errorCode = null, int? frameQuality = null,
        string? taskId = null, int? validationFramesPerOutput = null, long? progressSequence = null,
        BackendJobProgressDto? validationProgress = null, string? exportFormat = null, bool? exportIncludeAudio = null,
        BackendJobProgressDto? exportProgress = null, int? processGroupId = null, long? mediaFileSizeLimitBytes = null,
        WorkerDiagnosticsSnapshotDto? exportDiagnostics = null, string? exportDiagnosticsFailureCode = null,
        BackendAnalysisProgressDto? analysisProgress = null, WorkerPackageDiagnosticsDto? packageDiagnostics = null,
        PreviewInspectionDto? frameInspection = null, BackendSourceMaterialFailureDto? exportSourceMaterialFailure = null)
    {
        Kind = kind;
        RequestId = requestId;
        InputSha256 = inputSha256;
        Payload = payload ?? [];
        DelayMilliseconds = delayMilliseconds;
        Error = error;
        Cancelled = cancelled;
        ProtocolVersion = protocolVersion;
        OutputSha256 = outputSha256;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        FrameStride = frameStride;
        FrameTimeUs = frameTimeUs;
        FrameFormat = frameFormat;
        SessionId = sessionId; SourceSha256 = sourceSha256; FrameSequence = frameSequence;
        FrameTransparentBackground = frameTransparentBackground; Diagnostics = diagnostics; ErrorCode = errorCode;
        FrameQuality = frameQuality;
        TaskId = taskId; ValidationFramesPerOutput = validationFramesPerOutput;
        ProgressSequence = progressSequence; ValidationProgress = validationProgress;
        ExportFormat = exportFormat; ExportIncludeAudio = exportIncludeAudio; ExportProgress = exportProgress;
        ProcessGroupId = processGroupId; MediaFileSizeLimitBytes = mediaFileSizeLimitBytes;
        ExportDiagnostics = exportDiagnostics; ExportDiagnosticsFailureCode = exportDiagnosticsFailureCode;
        AnalysisProgress = analysisProgress; PackageDiagnostics = packageDiagnostics; FrameInspection = frameInspection;
        ExportSourceMaterialFailure = exportSourceMaterialFailure;
    }

    public string Kind { get; init; }
    public string RequestId { get; init; }
    public string? InputSha256 { get; init; }
    /// <summary>避免 null payload 在协议边界产生两套语义。</summary>
    public byte[] Payload { get; init; }
    public int DelayMilliseconds { get; init; }
    public string? Error { get; init; }
    public bool Cancelled { get; init; }
    public string ProtocolVersion { get; init; }
    /// <summary>render-frame-v1 输出像素的 SHA-256；普通 opaque 回执为空。</summary>
    public string? OutputSha256 { get; init; }
    /// <summary>帧像素宽度。</summary>
    public int? FrameWidth { get; init; }
    /// <summary>帧像素高度。</summary>
    public int? FrameHeight { get; init; }
    /// <summary>帧每行字节数。</summary>
    public int? FrameStride { get; init; }
    /// <summary>帧对应的整数微秒时间。</summary>
    public long? FrameTimeUs { get; init; }
    /// <summary>帧像素布局与预乘语义。</summary>
    public string? FrameFormat { get; init; }
    /// <summary>固定静态 Runtime 会话身份。</summary>
    public string? SessionId { get; init; }
    /// <summary>该会话固定的 snapshot-v1 净字节 SHA。</summary>
    public string? SourceSha256 { get; init; }
    /// <summary>会话内单调帧／关闭请求序号。</summary>
    public long? FrameSequence { get; init; }
    /// <summary>帧请求透明模式的关联回执。</summary>
    public bool? FrameTransparentBackground { get; init; }
    /// <summary>真实 Runtime 完整发生快照；成功静态帧不可省略。</summary>
    public WorkerDiagnosticsSnapshotDto? Diagnostics { get; init; }
    /// <summary>私有 Worker 的稳定失败编号。</summary>
    public string? ErrorCode { get; init; }
    /// <summary>静态帧质量：0 Draft、1 Standard、2 Final，必须与受保护请求一致。</summary>
    public int? FrameQuality { get; init; }
    /// <summary>固定验证任务身份，不复用像素会话身份。</summary>
    public string? TaskId { get; init; }
    /// <summary>每种输出的固定验证帧数。</summary>
    public int? ValidationFramesPerOutput { get; init; }
    /// <summary>验证任务的单调进度序号。</summary>
    public long? ProgressSequence { get; init; }
    /// <summary>正式验证器的真实已完成帧进度。</summary>
    public BackendJobProgressDto? ValidationProgress { get; init; }
    public string? ExportFormat { get; init; }
    public bool? ExportIncludeAudio { get; init; }
    public BackendJobProgressDto? ExportProgress { get; init; }
    public int? ProcessGroupId { get; init; }
    public long? MediaFileSizeLimitBytes { get; init; }
    /// <summary>导出失败的实际完整发生快照，独立遵守256KiB／512项预算。</summary>
    public WorkerDiagnosticsSnapshotDto? ExportDiagnostics { get; init; }
    /// <summary>完整失败快照超预算的次要失败；ErrorCode保留原主因。</summary>
    public string? ExportDiagnosticsFailureCode { get; init; }
    /// <summary>导出实际源材质失败；独立于必须有脚本实例的 Runtime 发生。</summary>
    public BackendSourceMaterialFailureDto? ExportSourceMaterialFailure { get; init; }
    /// <summary>真实实际解码样本进度；不是渲染帧或最终特征。</summary>
    public BackendAnalysisProgressDto? AnalysisProgress { get; init; }
    public WorkerPackageDiagnosticsDto? PackageDiagnostics { get; init; }
    /// <summary>请求检查的帧回执附带的只读投影；Host 另行核对字节预算。</summary>
    public PreviewInspectionDto? FrameInspection { get; init; }
    /// <summary>
    /// 仅包／图片／运行输入会话打开请求可带：true 时会话内图与材质执行逐像素检查（作者草稿）；
    /// 缺省或 false 为正式使用，不为检查读回。验证任务始终检查，不读此字段。
    /// </summary>
    public bool? GraphPixelChecks { get; init; }
}

/// <summary>Worker 协议违反版本、长度或哈希约束时抛出。</summary>
public sealed class WorkerProtocolException(string message, string code = "WorkerProtocolInvalid",
    WorkerDiagnosticsSnapshotDto? diagnostics = null, WorkerPackageDiagnosticsDto? packageDiagnostics = null,
    BackendSourceMaterialFailureDto? sourceMaterialFailure = null) : Exception(message)
{
    /// <summary>稳定的协议、预算或 Runtime 失败编号。</summary>
    public string Code { get; } = code;
    /// <summary>失败时真实保留的 Runtime 发生快照；没有实际快照时为 null。</summary>
    public WorkerDiagnosticsSnapshotDto? Diagnostics { get; } = diagnostics;
    public WorkerPackageDiagnosticsDto? PackageDiagnostics { get; } = packageDiagnostics;
    /// <summary>实际源材质失败；宿主还须核对固定工程来源。</summary>
    public BackendSourceMaterialFailureDto? SourceMaterialFailure { get; } = sourceMaterialFailure;
}
