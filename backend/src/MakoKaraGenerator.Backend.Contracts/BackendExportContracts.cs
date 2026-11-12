using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportJobRequest(string ExpectedRevision, string Format = "opaqueMp4", bool IncludeAudio = true,
    RequestMetadataDto? Metadata = null, string Quality = "standard", string? RenderBackend = null);

public sealed record ExportReportDto(string Format, bool AudioIncluded, int ExpectedFrames, int DecodedFrames,
    int FramesWritten, int DroppedFrames, int FailedFrames, int Width, int Height,
    int FrameRateNumerator, int FrameRateDenominator, string DerivedVideoDurationUs, string ActualVideoDurationUs,
    string? AudioDurationUs, string VideoCodec, string? AudioCodec, string PixelFormat, bool HasAlpha,
    long MediaSize, string Sha256, string[] Warnings, BackendDiagnosticDto[] Diagnostics, bool IsVerified, string? ActualAudioDurationUs = null, bool StrictlyVerified = false,
    string RequestedQuality = "standard", string ActualQuality = "standard",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderBackend = null,
    BackendDiagnosticDto[] FallbackDiagnostics = null!, int FallbackFrameCount = 0, bool FallbackDiagnosticsTruncated = false)
{
    /// <summary>去重后的兜底诊断。0.112.86 之前登记的报告没有兜底三项，读取时按空数组、0 与 false，旧产物升级后仍可读取。</summary>
    public BackendDiagnosticDto[] FallbackDiagnostics { get; init; } = FallbackDiagnostics ?? [];
}

public sealed record BackendExportAudioReferenceDto(string AssetId, long SizeBytes, string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AudioProfile = null);
public sealed record BackendExportInputDto(string Format, bool IncludeAudio, BackendExportAudioReferenceDto? Audio,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutionProfile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskProfile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseSourceProfile = null,
    string Quality = "standard",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderBackend = null);
public sealed record BackendExportArtifactReferenceDto(string ArtifactId, string ContentType, long SizeBytes, string Sha256);
public sealed record ExportJobResultDto(BackendExportArtifactReferenceDto Artifact, BackendJobReportReferenceDto Report,
    int FallbackDiagnosticCount = 0);

public sealed record ExportDiagnosticsPageDto(string JobId, string ReportSha256, BackendDiagnosticDto[] Diagnostics,
    string? NextCursor, string? Cursor = null);

public sealed record ExportTaskCapabilitiesDto(bool Enabled, string SourceProfile, string[] Formats, string[] AudioProfiles,
    string Quality, string RenderQuality, int? MaxFrames, string? MaxDurationUs, int? MaxFrameRate,
    int? MaxCanvasWidth, int? MaxCanvasHeight, long? MaxRawFrameBytes, int? OperationTimeoutSeconds,
    int CancellationGraceSeconds, long? MaxMediaBytes, int MaxReportBytes, int MaxOccurrences,
    long? MaxArtifactStorageBytes, int MaxRunning, int MaxRunningPerClient, int MaxRunningPerProject,
    int MaxQueued, int MaxQueuedPerClient, string OutputFileGuard, BackendErrorResponse? DisabledReason = null,
    int MaxDiagnosticsPageSize = 128, string ExecutionProfile = "static-export-pcmwav-v1",
    string[]? SourceProfiles = null, string[]? ExecutionProfiles = null,
    string? MaxCompressedAudioDurationUs = null,
    string CompressedAudioDurationBasis = WorkerExportContract.CompressedAudioDurationBasis,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageTaskProfile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ImageBaseProfiles = null, string[]? Qualities = null, long? MaxAudioBytes = null);

public sealed record ExportFailureReportDto(string Kind, string FailureCode, BackendJobInputDto Source,
    BackendExportInputDto Input, BackendDiagnosticDto[] Diagnostics);
