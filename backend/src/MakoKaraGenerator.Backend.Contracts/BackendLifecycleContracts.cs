using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>所有有副作用的请求共同使用的有界重试元信息。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RequestMetadataDto(string? RequestId, string? HostInstanceId, DateTimeOffset? ValidUntilUtc);

/// <summary>以启动凭证注册，或凭恢复凭证重新取得同一客户端身份。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClientRegistrationRequest(string ProtocolVersion = BackendProtocol.Version,
    string? ResumeClientId = null, string? ResumeToken = null);

/// <summary>随机访问与恢复凭证；恢复凭证不会由客户端状态查询暴露。</summary>
public sealed record ClientRegistrationResponse(string ClientId, string HostInstanceId, string AccessToken,
    string ResumeToken, DateTimeOffset TokenExpiresAtUtc, DateTimeOffset LeaseExpiresAtUtc,
    DateTimeOffset ServerTimeUtc);

/// <summary>心跳只续客户端租约，不修改工程。</summary>
public sealed record ClientHeartbeatResponse(string ClientId, string HostInstanceId,
    DateTimeOffset TokenExpiresAtUtc, DateTimeOffset LeaseExpiresAtUtc, DateTimeOffset ServerTimeUtc);

/// <summary>客户端所有权威工程摘要与同一次读取捕获的事件游标。</summary>
public sealed record ClientStateResponse(string ClientId, string HostInstanceId, bool Dormant,
    DateTimeOffset LeaseExpiresAtUtc, string EventCursor, ProjectSessionSummaryResponse[] Sessions, JobSummaryDto[]? Jobs = null);

/// <summary>小型事件投影；不携带二进制或整个领域工程。</summary>
public sealed record BackendEventResponse(string EventSeq, string EventCursor, string Type,
    string HostInstanceId, DateTimeOffset OccurredAtUtc, ProjectSessionSummaryResponse? Summary = null,
    ProjectChangeSetDto? ChangeSet = null, JobSummaryDto? Job = null, PreviewEventDto? Preview = null);

/// <summary>有界重放结果；缺口必须由客户端重新读取 state。</summary>
public sealed record ClientEventsResponse(string EventCursor, BackendEventResponse[] Events, bool ResyncRequired);

/// <summary>创建受根授权约束的目录句柄。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileHandleRequest(string RootGrantId, string RelativePath, string Access = "readWrite",
    RequestMetadataDto? Metadata = null);

/// <summary>目录句柄从不返回服务端绝对路径。</summary>
public sealed record FileHandleResponse(string HandleId, string RootGrantId, string RelativePath, bool CanWrite);

/// <summary>从已授权的工程目录打开。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OpenProjectSessionRequest(string ProjectDirectoryHandleId, string Access = "readWrite",
    RequestMetadataDto? Metadata = null);

/// <summary>从服务实际保留的恢复快照打开。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoverProjectSessionRequest(string RecoveryId, RequestMetadataDto? Metadata = null);

/// <summary>捕获指定修订保存，后续编辑不被保存结果覆盖。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveProjectRequest(string ExpectedRevision, string? TargetHandleId = null,
    RequestMetadataDto? Metadata = null);

/// <summary>显式关闭策略，不因网页断连隐式丢弃未保存内容。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseProjectRequest(string ExpectedRevision, string Policy = "requireClean",
    string? TargetHandleId = null, string ActiveJobs = "reject", RequestMetadataDto? Metadata = null);

/// <summary>共享持久化、正式验证、导出与音频分析任务的权威状态；完整结果通过具名任务读取。</summary>
public sealed record JobResponse(string JobId, string Kind, string State, string? ProjectSessionId,
    ProjectSessionSummaryResponse? Result, string? SavedProjectRevision, string? CurrentProjectRevision,
    bool? IsDirty, BackendErrorResponse? Error, DateTimeOffset ExpiresAtUtc, bool CancellationRequested, bool CanCancel = false,
    BackendDiagnosticDto[]? Diagnostics = null, BackendJobInputDto? Input = null, BackendJobProgressDto? Progress = null,
    ValidationJobResultDto? ValidationResult = null, string? FailureCode = null,
    BackendExportInputDto? ExportInput = null, ExportJobResultDto? ExportResult = null,
    BackendJobReportReferenceDto? FailureReportRef = null, BackendAnalysisJobInputDto? AnalysisInput = null,
    BackendAnalysisProgressDto? AnalysisProgress = null, AnalysisJobResultDto? AnalysisResult = null,
    AuthorCompilationJobInputDto? AuthorCompilationInput = null,
    BackendJobReportReferenceDto? AuthorCompilationResult = null,
    AuthorValidationJobInputDto? AuthorValidationInput = null);

/// <summary>实际存在的恢复候选，不承诺恢复全部历史。</summary>
public sealed record RecoveryCandidateResponse(string RecoveryId, string ProjectName,
    DateTimeOffset SavedAtUtc, bool HasOriginalPath);

/// <summary>进程重启后保留的任务诊断；不重新激活旧会话或自动重放副作用。</summary>
public sealed record RecoveredJobResponse(JobResponse Job, string PreviousHostInstanceId,
    string? OutputRootGrantId, string? OutputRelativePath, string? ProjectFileSha256);

/// <summary>现有工程加载和保存报告的固定诊断，不暴露服务端内部路径。</summary>
public sealed record BackendDiagnosticDto(string Code, string Severity, string Message, string? Location,
    BackendDiagnosticOccurrenceDto? Occurrence = null, string? OriginKind = null,
    BackendSourceDiagnosticDto? Source = null, BackendImageEffectDiagnosticDto? ImageEffect = null,
    BackendMaterialDiagnosticDto? Material = null,
    BackendSourceMaterialFailureDto? SourceMaterialFailure = null);

/// <summary>真实包源码编译报告；不具有运行时发生序号或时刻，0行列合法。</summary>
public sealed record BackendSourceDiagnosticDto(string PackageId, string Revision, string? InstanceId, string? ExportId,
    string Code, string Message, bool IsError, string? Path, int Line, int Column);
public sealed record BackendDiagnosticResourceDto(string PackageId, string Revision, string ExportId);
public sealed record BackendImageEffectDiagnosticDto(string InstanceId, string ArtifactHash,
    BackendDiagnosticResourceDto Graph, string InputId, string Code);
public sealed record BackendMaterialDiagnosticDto(BackendDiagnosticMaterialOwnerDto Owner,
    BackendDiagnosticResourceDto Material, string InputId, string Code);
/// <summary>严格script/source联合；仅与Kind对应的payload可以存在。</summary>
public sealed record BackendDiagnosticMaterialOwnerDto(string Kind,
    BackendDiagnosticScriptOwnerDto? Script = null, BackendDiagnosticSourceOwnerDto? Source = null);
public sealed record BackendDiagnosticScriptOwnerDto(string InstanceId, string ArtifactHash,
    BackendDiagnosticResourceDto? PackageIdentity);
public sealed record BackendDiagnosticSourceOwnerDto(string Path, string Region, int Slot,
    BackendDiagnosticResourceDto SourceMaterial, string ParametersJson);
/// <summary>完整公共预览诊断的组合预算，独立于私有Worker封包预算。</summary>
public static class BackendPreviewDiagnosticsLimits
{
    public const int MaximumItemsPerKind = 256;
    public const int MaximumPackageItems = 512;
    public const int MaximumItems = 768;
    public const int MaximumJsonBytes = 512 * 1024;
}

public sealed record BackendDiagnosticOccurrenceDto(Guid Epoch, string Sequence, string InstanceId, string ArtifactHash,
    string Stage, string TimeUs, BackendDiagnosticLocationDto? Location, BackendDiagnosticPackageDto? Package);
public sealed record BackendDiagnosticLocationDto(string Path, int Line, int Column);
public sealed record BackendDiagnosticPackageDto(string PackageId, string Revision, string ExportId);

/// <summary>事件与state用的有限任务元信息；完整结果和诊断从具名job查询读取。</summary>
public sealed record JobSummaryDto(string JobId, string Kind, string State, string? ProjectSessionId,
    bool CanCancel, DateTimeOffset ExpiresAtUtc, BackendErrorCode? ErrorCode = null,
    BackendJobProgressDto? Progress = null, string? FailureCode = null, BackendAnalysisProgressDto? AnalysisProgress = null);
