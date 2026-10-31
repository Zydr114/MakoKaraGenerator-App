using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisJobRequest(string ExpectedRevision, RequestMetadataDto? Metadata = null);

public sealed record BackendAnalysisAudioReferenceDto(string AssetId, long SizeBytes, string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AudioProfile = null);
public sealed record BackendAnalysisJobInputDto(string SourceKind, string SourceId, string SourceRevision,
    string SourceProfile, BackendAnalysisAudioReferenceDto Audio,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceDurationUs);
public sealed record BackendAnalysisProgressDto(string Stage, string CompletedSamples, string? TotalSamples = null,
    string? TimeUs = null);
public sealed record AnalysisJobResultDto(string Algorithm, int SampleRate, int HopSamples, string OriginUs,
    string SourceHash, string PcmHash, string SampleCount, string DurationUs, string Revision,
    string ContentHash, int PointCount, BackendJobReportReferenceDto Report);

/// <summary>工程已采用分析的有界投影；不依赖原任务或结果文件的保留期。</summary>
public sealed record ProjectAudioAnalysisSummaryDto(string Status, string? Algorithm = null, int? SampleRate = null,
    int? HopSamples = null, string? OriginUs = null, string? SourceHash = null, string? PcmHash = null,
    string? SampleCount = null, string? DurationUs = null, string? Revision = null, string? ContentHash = null,
    int? PointCount = null);
public sealed record AnalysisTaskCapabilitiesDto(bool Enabled, string SourceProfile, string Algorithm,
    string[] AudioProfiles, long MaxAudioBytes, int MaxSourceSampleRate, string MaxSourceDurationUs,
    int SampleRate, int HopSamples, string MaxSamples, int MaxPoints, int MaxResultBytes,
    long MaxResultStorageBytes, int OperationTimeoutSeconds, int CancellationGraceSeconds,
    int MaxRunning, int MaxRunningPerClient, int MaxRunningPerProject, int MaxQueued, int MaxQueuedPerClient,
    bool GuardAvailable, bool ToolStartupVerified, BackendErrorResponse? DisabledReason = null,
    string[]? SourceProfiles = null);
