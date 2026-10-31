using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>宿主可配置的文件和逐帧资源策略；省略不施加产品短片限额。</summary>
public sealed record BackendExportOptions
{
    public int? MaxFrames { get; init; }
    public long? MaxDurationUs { get; init; }
    public int? MaxFrameRate { get; init; } = 60;
    public int? MaxCanvasWidth { get; init; } = 1920;
    public int? MaxCanvasHeight { get; init; } = 1080;
    public long? MaxRawFrameBytes { get; init; } = 8 * 1024 * 1024;
    public long? MaxMediaBytes { get; init; }
    public long? MaxAudioBytes { get; init; }
    public long? MaxCompressedAudioDurationUs { get; init; }
    public long? MaxArtifactStorageBytes { get; init; }
    public int MaxReportBytes { get; init; } = 256 * 1024;
    public int MaxOccurrences { get; init; } = 512;
    public int MaxDiagnosticsPageSize { get; init; } = 128;

    public WorkerExportResourcePolicy ResourcePolicy(long? mediaBytes = null, int? operationTimeoutSeconds = null) => new()
    {
        MaxFrames = MaxFrames, MaxDurationUs = MaxDurationUs, MaxFrameRate = MaxFrameRate,
        MaxCanvasWidth = MaxCanvasWidth, MaxCanvasHeight = MaxCanvasHeight, MaxRawFrameBytes = MaxRawFrameBytes,
        MaxMediaBytes = mediaBytes ?? MaxMediaBytes, MaxAudioBytes = MaxAudioBytes,
        MaxCompressedAudioDurationUs = MaxCompressedAudioDurationUs, OperationTimeoutSeconds = operationTimeoutSeconds,
    };

    internal void Validate()
    {
        ResourcePolicy().Validate();
        if (MaxArtifactStorageBytes is <= 0 || MaxReportBytes is <= 0 or > 256 * 1024 || MaxOccurrences is <= 0 or > 512
            || MaxDiagnosticsPageSize is <= 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(BackendExportOptions));
    }
}
