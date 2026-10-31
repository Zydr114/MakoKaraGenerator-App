namespace MakoKaraGenerator.Backend.Hosting;

internal sealed record BackendTaskSchedulingOptions(int MaxRunning = 2, int MaxRunningPerClient = 1,
    int MaxRunningPerProject = 1, int MaxQueued = 16, int MaxQueuedPerClient = 4);
