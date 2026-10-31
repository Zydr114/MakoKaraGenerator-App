namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>由可信启动器登记的持久 profile 与目录根，HTTP 请求不能扩大授权。</summary>
public sealed record ProjectPersistenceOptions
{
    public required string StateRoot { get; init; }
    public IReadOnlyDictionary<string, string> AllowedProjectRoots { get; init; } = new Dictionary<string, string>();
    public int MaxJobsPerClient { get; init; } = 128;
    public int MaxHandlesPerClient { get; init; } = 128;
    public int MaxRetainedJobs { get; init; } = 1024;
    public long MaxJobLedgerBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxPinnedClosures { get; init; } = 64;
    public long MaxPinnedClosureBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public TimeSpan JobRetention { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan AutoSaveInterval { get; init; } = TimeSpan.FromSeconds(30);
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StateRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxJobsPerClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxHandlesPerClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedJobs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxJobLedgerBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPinnedClosures);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPinnedClosureBytes);
        if (JobRetention <= TimeSpan.Zero || AutoSaveInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(JobRetention));
        foreach ((string id, string path) in AllowedProjectRoots)
        { ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(path); }
    }
}
