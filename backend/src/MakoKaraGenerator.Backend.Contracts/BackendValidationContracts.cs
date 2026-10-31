using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ValidationJobRequest(string ExpectedRevision, int FramesPerOutput, RequestMetadataDto? Metadata = null,
    string? RenderBackend = null);

public sealed record BackendJobInputDto(string SourceKind, string SourceId, string SourceRevision,
    string SourceProfile, string SourceSha256, int FramesPerOutput, int CanvasWidth, int CanvasHeight, int FrameRateNumerator = 0, int FrameRateDenominator = 0,
    string? OutputDurationUs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderBackend = null);

public sealed record BackendJobProgressDto(string Stage, int CompletedFrames, int TotalFrames,
    string? TimeUs = null, bool? Transparent = null);

public sealed record ValidationIssueDto(bool Transparent, string TimeUs, string Code, string? Message, bool IsError,
    BackendDiagnosticLocationDto? Location = null, string? SampleFailureCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackendDiagnosticDto? Diagnostic = null);

public sealed record ValidationDiagnosticDto(bool Transparent, BackendDiagnosticDto Diagnostic);

public sealed record ValidationReportDto(bool Succeeded, int OpaqueFrames, int TransparentFrames,
    ValidationIssueDto[] Issues, ValidationDiagnosticDto[] Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderBackend = null);

public sealed record BackendJobReportReferenceDto(string ReportId, long SizeBytes, string Sha256);

public sealed record ValidationJobResultDto(bool Passed, int OpaqueFrames, int TransparentFrames,
    BackendJobReportReferenceDto Report);

public sealed record ValidationDiagnosticsPageDto(string JobId, string ReportSha256,
    ValidationDiagnosticDto[] Diagnostics, string? NextCursor, string? Cursor = null);

public sealed record ValidationTaskCapabilitiesDto(bool Enabled, string SourceProfile, int MaxFramesPerOutput,
    int MaxCanvasWidth, int MaxCanvasHeight, long MaxRawFrameBytes, int OperationTimeoutSeconds,
    int MaxReportBytes, int MaxIssues, int MaxOccurrences, int MaxRunning, int MaxRunningPerClient,
    int MaxRunningPerProject, int MaxQueued, int MaxQueuedPerClient, long MaxReportStorageBytes,
    BackendErrorResponse? DisabledReason = null, int MaxDiagnosticsPageSize = 128,
    string[]? SourceProfiles = null);
