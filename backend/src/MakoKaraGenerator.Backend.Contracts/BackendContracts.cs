using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>首版 loopback 后端协议常量。</summary>
public static class BackendProtocol
{
    /// <summary>当前传输协议主版本。</summary>
    public const string Version = "1";
    /// <summary>统一 JSON 响应媒体类型。</summary>
    public const string JsonContentType = "application/json; charset=utf-8";
}

/// <summary>服务可观测、可稳定处理的协议错误码。</summary>
public enum BackendErrorCode
{
    /// <summary>请求字段缺失或值不合法。</summary>
    InvalidRequest,
    /// <summary>缺少或不合法的客户端身份。</summary>
    UnauthorizedClient,
    /// <summary>上传 MIME 不在允许范围。</summary>
    UnsupportedMediaType,
    /// <summary>上传内容为空。</summary>
    ResourceEmpty,
    /// <summary>单资源超过大小上限。</summary>
    ResourceTooLarge,
    /// <summary>资源总配额不足。</summary>
    ResourceQuotaExceeded,
    /// <summary>文件名包含路径或越界片段。</summary>
    ResourcePathNotAllowed,
    /// <summary>资源 ID 不存在。</summary>
    ResourceNotFound,
    /// <summary>资源属于其他客户端。</summary>
    ResourceUnauthorized,
    /// <summary>歌词无法识别或解析。</summary>
    LyricsImportFailed,
    /// <summary>歌词没有可导入行。</summary>
    EmptyLyrics,
    /// <summary>音频不含有效音轨。</summary>
    NoAudioStream,
    /// <summary>音频探测或解码失败。</summary>
    AudioPreparationFailed,
    /// <summary>工程会话不存在。</summary>
    SessionNotFound,
    /// <summary>服务重启导致旧会话失效。</summary>
    HostRestarted,
    /// <summary>会话注册冲突。</summary>
    SessionConflict,
    /// <summary>客户端提交的工程修订不是当前修订。</summary>
    RevisionConflict,
    /// <summary>请求的具名命令当前未开放。</summary>
    CommandNotSupported,
    /// <summary>请求的目标或历史步骤当前不可用。</summary>
    CommandRejected,
    /// <summary>请求截止时间缺失或已经过去。</summary>
    RequestExpired,
    /// <summary>请求 ID 已经用于不同的语义载荷。</summary>
    RequestIdReuse,
    /// <summary>会话有效命令回执已经达到公布上限。</summary>
    RequestQuotaExceeded,
    /// <summary>客户端数量达到上限。</summary>
    ClientQuotaExceeded,
    /// <summary>客户端令牌已过期。</summary>
    ClientTokenExpired,
    /// <summary>客户端会话配额达到上限。</summary>
    SessionQuotaExceeded,
    /// <summary>资源已到期。</summary>
    ResourceExpired,
    /// <summary>目录授权或相对路径不合法。</summary>
    PathNotAllowed,
    /// <summary>工程目录已有写者。</summary>
    WriteLeaseConflict,
    /// <summary>只读会话不能执行修改。</summary>
    ReadOnlySession,
    /// <summary>会话正在关闭。</summary>
    SessionClosing,
    /// <summary>未保存工程不能按当前策略关闭。</summary>
    DirtySession,
    /// <summary>任务不存在或无权读取。</summary>
    JobNotFound,
    /// <summary>当前仍有未完成任务。</summary>
    ActiveJobs,
    /// <summary>工程加载或闭包校验失败。</summary>
    ProjectLoadFailed,
    /// <summary>已进入发布阶段的任务不可取消。</summary>
    OperationNotCancellable,
    /// <summary>未分类的服务错误。</summary>
    InternalError,
    /// <summary>预览会话不存在或不属于当前客户端。</summary>
    PreviewSessionNotFound,
    /// <summary>预览代次不是当前绑定的代次。</summary>
    PreviewGenerationConflict,
    /// <summary>预览请求序号重复或倒退。</summary>
    PreviewRequestOutOfOrder,
    /// <summary>预览帧不存在或不属于当前预览会话。</summary>
    PreviewFrameNotFound,
    /// <summary>预览帧租约已经到期，客户端应请求新帧。</summary>
    PreviewFrameExpired,
    /// <summary>诊断分页游标已经失效，客户端应重新读取。</summary>
    DiagnosticCursorExpired,
    /// <summary>诊断分页参数超过服务公布的上限。</summary>
    DiagnosticLimitExceeded,
    /// <summary>主机未部署可用的预览Worker；不会退回同进程执行。</summary>
    PreviewUnavailable,
    /// <summary>固定来源和帧计划的正式验证未通过。</summary>
    ValidationFailed,
    /// <summary>任务执行失败；内部稳定编号由任务FailureCode表达。</summary>
    TaskExecutionFailed,
    TaskUnavailable,
    ExportInputRejected,
    ExportAudioUnsupported,
    ExportArtifactNotFound,
    ExportArtifactExpired,
    /// <summary>预览帧存在，但请求时没有开启检查，或检查投影没有生成。</summary>
    PreviewInspectionUnavailable,
}

/// <summary>统一错误响应；Detail 只用于安全的字段级诊断。</summary>
public sealed record BackendErrorResponse(
    BackendErrorCode Code,
    string Message,
    string? Field = null,
    string ProtocolVersion = BackendProtocol.Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureCode = null);

/// <summary>健康探针返回值，不包含工程名或本机路径。</summary>
public sealed record HealthResponse(
    string Status,
    string HostInstanceId,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>当前首批能力与资源上限。</summary>
public sealed record BackendCapabilitiesResponse(
    string ProtocolVersion,
    string HostInstanceId,
    string ProductVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MaxAssetBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MaxAssetQuotaBytes,
    string[] AssetMimeTypes,
    string[] Features,
    DateTimeOffset ServerTimeUtc,
    int MaxCommandRetryWindowSeconds,
    int MaxCommandReceiptsPerSession,
    int ClientTokenLifetimeSeconds = 120,
    int ClientLeaseLifetimeSeconds = 300,
    int MaxClients = 16,
    int MaxSessionsPerClient = 16,
    int UnreferencedAssetLifetimeSeconds = 600,
    int MaxEventsPerClient = 256,
    int MaxCommandReceiptsPerClient = 1024,
    int MaxReceiptBytes = 65536,
    long MaxReceiptBytesPerClient = 67108864,
    int MaxUploadPreparations = 4,
    int MaxUploadPreparationsPerClient = 2,
    int MaxEventConnections = 4,
    int MaxEventConnectionsPerClient = 2,
    int MaxAssets = 4096,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackendAudioPreparationCapabilitiesDto? AudioPreparation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GpuPreviewCapabilitiesDto? GpuPreview = null);

/// <summary>GPU 流的固定预算，不代表具体尺寸已通过分配探测。</summary>
public sealed record GpuPreviewCapabilitiesDto(int MaxWidth, int MaxHeight, int SlotsPerPool,
    long MaxAllocationBytesPerPool, int MaxStreamsPerHost, bool PerformanceValidated);

/// <summary>已授权的不可变上传资源摘要。</summary>
public sealed record AssetResponse(
    string AssetId,
    string Sha256,
    long Size,
    string ContentType,
    string FileName,
    DateTimeOffset? ExpiresAtUtc = null);

/// <summary>HTTP 创建工程请求。音频和歌词均可省略；提供资源时只能通过已上传 assetId 引用。</summary>
public sealed record CreateProjectSessionRequest(
    string Name,
    string? AudioAssetId = null,
    string? LyricsAssetId = null,
    CompositionDto? Composition = null,
    string? ImporterId = null,
    string? EncodingName = null,
    int SplitRule = 0,
    bool EstimateSplitTimes = false,
    RequestMetadataDto? Metadata = null);

/// <summary>不依赖 Core 的传输合成设置。</summary>
public sealed record CompositionDto(
    int CanvasWidth = 1920,
    int CanvasHeight = 1080,
    int FrameRateNumerator = 30,
    int FrameRateDenominator = 1,
    byte BackgroundRed = 0,
    byte BackgroundGreen = 0,
    byte BackgroundBlue = 0,
    byte BackgroundAlpha = 255,
    long? OutputDurationUs = null);

/// <summary>创建后的最小工程会话摘要。</summary>
public sealed record ProjectSessionSummaryResponse(
    string ProjectSessionId,
    string HostInstanceId,
    string ProjectRevision,
    string Name,
    string AudioAssetId,
    int LyricsLineCount,
    long? AudioDurationUs,
    bool IsDirty,
    bool CanUndo,
    bool CanRedo,
    string? EventCursor = null,
    string ProtocolVersion = BackendProtocol.Version,
    bool HasPath = false,
    string? SavedProjectRevision = null,
    bool IsReadOnly = false,
    ProjectAudioAnalysisSummaryDto? AudioAnalysis = null);

/// <summary>稳定歌词时间目标；TokenId 为空表示整行。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LyricTimingTargetDto(
    string BranchId,
    string LineId,
    string? TokenId = null);

/// <summary>首期 HTTP 只接受具名命令，不接受任意对象补丁。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public record ProjectCommandDto(
    string Type,
    LyricTimingTargetDto? Target = null,
    string? StartUs = null,
    string? EndUs = null,
    string? AnalysisJobId = null);

/// <summary>带工程修订门禁的命令请求。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectCommandRequest(
    string ExpectedRevision,
    ProjectCommandDto Command,
    string? RequestId = null,
    string? HostInstanceId = null,
    DateTimeOffset? ValidUntilUtc = null);

/// <summary>撤销／重做读模型。</summary>
public sealed record ProjectHistoryResponse(
    string ProjectRevision,
    bool CanUndo,
    bool CanRedo,
    string? UndoName,
    string? RedoName,
    int UndoCount,
    int RedoCount,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>按稳定目标返回的时间范围。</summary>
public sealed record LyricTimeDto(string? StartUs, string? EndUs);

/// <summary>歌词元素的最小只读投影。</summary>
public sealed record LyricTokenDto(
    string Id,
    string Text,
    LyricTimeDto Time);

/// <summary>歌词行的最小只读投影。</summary>
public sealed record LyricLineDto(
    string BranchId,
    string Id,
    string Text,
    LyricTimeDto Time,
    LyricTokenDto[] Tokens);

/// <summary>当前工程歌词查询结果，绑定一个精确工程修订。</summary>
public sealed record LyricsLinesResponse(
    string ProjectSessionId,
    string ProjectRevision,
    string BranchId,
    LyricLineDto[] Lines,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>命令影响范围；未知变化要求客户端完整刷新。</summary>
public sealed record ProjectChangeSetDto(
    string[] Areas,
    string[] BranchIds,
    string[] LineIds,
    string[] TokenIds,
    bool RequiresFullRefresh);

/// <summary>预设应用命令受影响的稳定身份；无变化命令仍可返回实际目标身份。</summary>
public sealed record PresetApplicationCommandResultDto(string[] ApplicationIds);

/// <summary>命令提交后的当前读模型与历史状态。</summary>
public sealed record ProjectCommandResponse(
    string ProjectSessionId,
    string PreviousRevision,
    string ProjectRevision,
    bool Changed,
    bool IsDirty,
    bool CanUndo,
    bool CanRedo,
    string? UndoName,
    string? RedoName,
    ProjectChangeSetDto ChangeSet,
    string? EventCursor = null,
    string ProtocolVersion = BackendProtocol.Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricTimingCommandResultDto? TimingResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricContentCommandFeedbackDto? Feedback = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricBranchCommandResultDto? BranchResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricTokenCommandResultDto? TokenResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricsImportCommandResultDto? ImportResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StyleCommandResultDto? StyleResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresetApplicationCommandResultDto? PresetApplicationResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationCommandResultDto? OrchestrationResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationLayoutCommandResultDto? LayoutResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationStructureCommandResultDto? StructureResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BasicLayoutCommandResultDto? BasicLayoutResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationClipCommandResultDto? ClipResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationLeadInCommandResultDto? LeadInResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LineLocalTransformCommandResultDto? LineLocalTransformResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationPropertiesCommandResultDto? PropertiesResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LeadInGenerationCommandResultDto? LeadInGenerationResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationClipMembersCommandResultDto? ClipMembersResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RubySegmentCommandResultDto? RubyResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectEditResultDto[]? EditResults = null);
