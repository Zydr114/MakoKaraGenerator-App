using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>预览输入的稳定来源；修订值始终以不透明字符串传输。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewSourceDto(
    string Kind,
    string SourceId,
    string SourceRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TemplatePreviewSelectionDto? TemplateSample = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AuthorSourceRevision = null);

/// <summary>预览输出尺寸，宽高均为正的像素数。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewSizeDto(
    int Width,
    int Height);

/// <summary>透明通道语义。PNG 传输边界使用 straight；内部预乘不暴露在 HTTP 契约中。</summary>
public enum PreviewAlphaMode
{
    Opaque,
    Straight,
}

/// <summary>创建预览会话时固定的输出模式。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewSessionRequest(
    PreviewSourceDto Source,
    PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode = PreviewAlphaMode.Opaque,
    string Purpose = "preview",
    RequestMetadataDto? Metadata = null,
    string PreviewKind = "project",
    bool Committable = true,
    string? RenderBackend = null);

/// <summary>预览会话的当前固定输入和生成代次。</summary>
public sealed record PreviewSessionResponse(
    string PreviewId,
    PreviewSourceDto Source,
    string PreviewGeneration,
    PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode,
    string State,
    PreviewLimitsDto Limits,
    string? LatestRequestSeq = null,
    PreviewFrameMetadataDto? LatestFrame = null,
    string? DiagnosticsCursor = null,
    string ProtocolVersion = BackendProtocol.Version,
    string PreviewKind = "project",
    bool Committable = true,
    PreviewPackageClosureDto? PackageClosure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectEditCandidateDto? EditCandidate = null,
    string RenderBackend = "cpu");

/// <summary>直接效果包预览固定的只读资源闭包摘要；不暴露本地路径或原始资源字节。</summary>
public sealed record PreviewPackageClosureDto(
    string ClosureRevision,
    PreviewPackageClosureEntryDto[] Packages,
    int FileCount,
    long TotalBytes);

/// <summary>一个已固定包修订及其包内资源角色摘要。</summary>
public sealed record PreviewPackageClosureEntryDto(
    string PackageId,
    string Revision,
    int FileCount,
    long TotalBytes,
    string[] Roles);

/// <summary>预览会话公布的有界资源和请求限制。</summary>
public sealed record PreviewLimitsDto(
    int MaxWidth,
    int MaxHeight,
    int MaxInFlightRequests,
    int MaxRetainedFrames,
    long MaxRetainedFrameBytes,
    int FrameLeaseLifetimeSeconds,
    int MaxDiagnosticsPageSize,
    long MaxRawFrameBytes = PreviewFrameContract.MaxFrameBytes);

/// <summary>更换源、尺寸或透明模式时使用；成功后服务生成新的 PreviewGeneration。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewInputRequest(
    string ExpectedPreviewGeneration,
    PreviewSourceDto Source,
    PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode,
    RequestMetadataDto? Metadata = null,
    string? RenderBackend = null);

/// <summary>请求一个时间点的异步帧；TimeUs 与序号均按十进制字符串传输。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewFrameRequest(
    string PreviewGeneration,
    string RequestSeq,
    string TimeUs,
    string Intent,
    RequestMetadataDto? Metadata = null);

/// <summary>帧请求已进入有界队列，不代表像素已经生成。</summary>
public sealed record PreviewFrameAcceptedResponse(
    string PreviewId,
    string PreviewGeneration,
    string RequestSeq,
    string TimeUs,
    string State,
    string? EventSeq = null,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>不可变帧二进制句柄；读取期间由服务端持有租约，过期后返回 PreviewFrameExpired。</summary>
public sealed record PreviewBinaryHandleDto(
    string HandleId,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset ExpiresAtUtc,
    string ContentUrl);

/// <summary>帧像素及其固定输入的完整元数据，几何查询必须引用同一 FrameId。</summary>
public sealed record PreviewFrameMetadataDto(
    string FrameId,
    string PreviewId,
    string PreviewGeneration,
    string SourceRevision,
    string RequestSeq,
    string TimeUs,
    int Width,
    int Height,
    PreviewAlphaMode AlphaMode,
    PreviewBinaryHandleDto Content,
    string? DiagnosticsCursor = null,
    string? EventSeq = null,
    string RenderBackend = "cpu");

/// <summary>按固定预览代次读取的诊断页；游标失效时不能拼接不同修订的页。</summary>
public sealed record PreviewDiagnosticsPageDto(
    string PreviewId,
    string PreviewGeneration,
    string SourceRevision,
    BackendDiagnosticDto[] Diagnostics,
    string? NextCursor,
    bool HasMore,
    string? Cursor = null,
    string ProtocolVersion = BackendProtocol.Version);
