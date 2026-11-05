namespace MakoKaraGenerator.Core.Media;

/// <summary>Fixed media-ab-capacity-v1 safety ceilings. Smaller requests may fail admission; no caller can enlarge these limits.</summary>
public static class MediaAbCapacityV1
{
    /// <summary>Maximum canonical video endpoint in microseconds.</summary>
    public const long MaximumVideoDurationUs = 3_600_000_000;

    /// <summary>Maximum video presentation frame count.</summary>
    public const long MaximumFrames = 54_000;

    /// <summary>Maximum authorized file byte length.</summary>
    public const long MaximumSourceBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Maximum encoded or canonical raster side.</summary>
    public const int MaximumSourceSide = 8_192;

    /// <summary>Maximum source raster pixels; this alone does not imply decode admission.</summary>
    public const long MaximumSourcePixels = 16_777_216;

    /// <summary>Maximum complete canonical artifact bytes.</summary>
    public const long MaximumArtifactBytes = 512L * 1024 * 1024;

    /// <summary>Maximum simultaneous scratch sections and complete candidate bytes.</summary>
    public const long MaximumScratchAndCandidateBytes = 1024L * 1024 * 1024;

    /// <summary>Maximum simultaneous native envelope and managed decode backing.</summary>
    public const long MaximumDecodeWorkingBytes = 256L * 1024 * 1024;

    /// <summary>Maximum builder working backing.</summary>
    public const long MaximumBuilderWorkingBytes = 16L * 1024 * 1024;

    /// <summary>Maximum runtime CPU cache backing.</summary>
    public const long MaximumRuntimeCpuCacheBytes = 96L * 1024 * 1024;

    /// <summary>Maximum one structured metadata record in bytes.</summary>
    public const int MaximumMetadataRecordBytes = 16 * 1024;

    /// <summary>Maximum queued structured metadata records.</summary>
    public const int MaximumMetadataQueueRecords = 32;

    /// <summary>Maximum bounded probe output bytes.</summary>
    public const int MaximumProbeOutputBytes = 1024 * 1024;

    /// <summary>Maximum diagnostic ring backing; metadata has separate capacity.</summary>
    public const int MaximumRetainedDiagnosticBytes = 64 * 1024;

    /// <summary>Maximum entries in each executable or dependency tool manifest.</summary>
    public const int MaximumToolManifestEntries = 1_024;

    /// <summary>Maximum strict UTF-8 bytes in one logical tool identifier.</summary>
    public const int MaximumToolLogicalIdUtf8Bytes = 256;

    /// <summary>Maximum sum of logical identifier UTF-8 bytes in one tool manifest.</summary>
    public const int MaximumToolManifestTextUtf8Bytes = 64 * 1024;

    /// <summary>Maximum concurrently owned decode processes.</summary>
    public const int MaximumOwnedDecodeProcesses = 2;

    /// <summary>Maximum native decoder threads.</summary>
    public const int MaximumNativeDecoderThreads = 1;

    /// <summary>Maximum grids awaiting their presentation interval.</summary>
    public const int MaximumUnpairedFrameGrids = 1;

    /// <summary>Maximum temporal query window in microseconds.</summary>
    public const long MaximumWindowUs = 2_000_000;

    /// <summary>Maximum queries in one batch.</summary>
    public const int MaximumQueries = 64;

    /// <summary>Maximum distinct windows in one batch.</summary>
    public const int MaximumWindows = 4;

    /// <summary>Maximum probe wall time.</summary>
    public static TimeSpan ProbeTimeout => TimeSpan.FromSeconds(30);

    /// <summary>Maximum period without metadata or pixel pipe progress.</summary>
    public static TimeSpan PipeProgressTimeout => TimeSpan.FromSeconds(120);

    /// <summary>Maximum bounded drain period for owned processes and pumps.</summary>
    public static TimeSpan OwnedDrainTimeout => TimeSpan.FromSeconds(10);

}
