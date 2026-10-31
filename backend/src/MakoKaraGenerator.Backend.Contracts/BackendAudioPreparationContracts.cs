namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>受管理Worker音频准备的实际能力及完整波形传输上限。</summary>
public sealed record BackendAudioPreparationCapabilitiesDto(
    bool Enabled,
    string? DisabledReason,
    string Profile,
    string[] Families,
    int MaxPayloadBytes,
    int MaxBaseBuckets,
    int BaseBucketSamples,
    int OperationTimeoutSeconds,
    int CancellationGraceSeconds,
    int MaxRunning,
    int MaxRunningPerClient,
    int MaxQueued,
    int MaxQueuedPerClient);
