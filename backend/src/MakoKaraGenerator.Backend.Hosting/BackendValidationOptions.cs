namespace MakoKaraGenerator.Backend.Hosting;

public sealed record BackendValidationOptions
{
    public int MaxRunning { get; init; } = 2;
    public int MaxRunningPerClient { get; init; } = 1;
    public int MaxRunningPerProject { get; init; } = 1;
    public int MaxQueued { get; init; } = 16;
    public int MaxQueuedPerClient { get; init; } = 4;
    public int MaxFramesPerOutput { get; init; } = 600;
    public int MaxCanvasWidth { get; init; } = 1920;
    public int MaxCanvasHeight { get; init; } = 1080;
    public long MaxRawFrameBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxReportBytes { get; init; } = 256 * 1024;
    public int MaxIssues { get; init; } = 256;
    public int MaxOccurrences { get; init; } = 512;
    public long MaxReportStorageBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxDiagnosticsPageSize { get; init; } = 128;

    internal void Validate()
    {
        if (MaxRunning is <= 0 or > 2 || MaxRunningPerClient != 1 || MaxRunningPerProject != 1
            || MaxQueued is <= 0 or > 16 || MaxQueuedPerClient is <= 0 or > 4
            || MaxFramesPerOutput is <= 0 or > 600 || MaxCanvasWidth is <= 0 or > 1920 || MaxCanvasHeight is <= 0 or > 1080
            || MaxRawFrameBytes is <= 0 or > 8 * 1024 * 1024 || MaxReportBytes is <= 0 or > 256 * 1024
            || MaxIssues is <= 0 or > 256 || MaxOccurrences is <= 0 or > 512 || MaxReportStorageBytes is <= 0 or > 16 * 1024 * 1024
            || MaxDiagnosticsPageSize is <= 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(BackendValidationOptions));
    }
}
