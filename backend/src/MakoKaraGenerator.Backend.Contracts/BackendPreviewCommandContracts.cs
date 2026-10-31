using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewProjectInputRequest(string ExpectedPreviewGeneration, string ExpectedSourceRevision,
    PreviewSourceDto Source, PreviewSizeDto Size, PreviewAlphaMode AlphaMode = PreviewAlphaMode.Opaque,
    RequestMetadataDto? Metadata = null, string? RenderBackend = null);

/// <summary>在同一固定来源上切换渲染设备；相同设备通过代次校验后保持原会话。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewRenderBackendRequest(string ExpectedPreviewGeneration, string RenderBackend,
    RequestMetadataDto? Metadata = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewFrameCommandRequest(string ExpectedSourceRevision, string PreviewGeneration,
    string RequestSeq, string TimeUs, string Intent, RequestMetadataDto? Metadata = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClosePreviewSessionRequest(string ExpectedPreviewGeneration, string ExpectedSourceRevision,
    RequestMetadataDto? Metadata = null);

public sealed record PreviewSessionStateResponse(PreviewSessionResponse Session, BackendErrorResponse? Failure = null,
    string? RenderFailureCode = null);

/// <summary>无像素的小型SSE提示；缺帧或事件缺口时从GET状态重新读取。</summary>
public sealed record PreviewEventDto(string PreviewId, string PreviewGeneration, string SourceRevision,
    string State, string? LatestRequestSeq, PreviewFrameMetadataDto? LatestFrame, BackendErrorResponse? Failure,
    string? RenderFailureCode = null, string RenderBackend = "cpu");

/// <summary>主机配置事实，不把文件存在称为已通过Worker握手。</summary>
public sealed record PreviewRuntimeInfoResponse(bool Enabled, string Status, string SourceProfile,
    string ExecutionMode, int MaxPixelWidth, int MaxPixelHeight, long MaxRawFrameBytes,
    BackendErrorResponse? DisabledReason = null,
    int MaxSnapshotBytes = StaticRenderWorkerContract.MaximumSnapshotBytes,
    int FrameLeaseLifetimeSeconds = 30, int MaxRetainedFramesPerSession = 2,
    long MaxRetainedFrameBytesPerSession = 8 * 1024 * 1024, int MaxReadLeases = 8,
    long MaxReadLeaseBytes = PreviewFrameContract.MaxFrameBytes * 8,
    string ClientLeaseExpiryPolicy = "close-previews-preserve-projects",
    string EventDelivery = "bounded-advisory-read-state-on-gaps",
    string[]? SourceProfiles = null);
