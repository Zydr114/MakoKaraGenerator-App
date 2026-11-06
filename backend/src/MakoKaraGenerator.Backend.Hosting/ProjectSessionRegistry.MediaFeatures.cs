namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private BackendMediaFeatureService? _mediaFeatures;

    /// <summary>The installed Host background media-feature analysis service, if any.</summary>
    public BackendMediaFeatureService? MediaFeatures => Volatile.Read(ref _mediaFeatures);

    /// <summary>Installs the analysis service once; every runtime input prepared afterwards attaches media features.</summary>
    public void ConfigureMediaFeatures(BackendMediaFeatureService? service)
    {
        if (service is null) return;
        if (Interlocked.CompareExchange(ref _mediaFeatures, service, null) is not null)
            throw new InvalidOperationException("Media feature analysis is already configured.");
        service.Changed += PublishMediaFeaturesChanged;
        foreach (ProjectSession session in _sessions.Values) session.MediaFeatures = service;
    }

    // Analysis completion changes only Host-derived inputs, never project revision or dirty state.
    // Clients refresh fixed preview inputs on this hint.
    private void PublishMediaFeaturesChanged()
    {
        foreach (ProjectSession session in _sessions.Values)
            _ = PublishMediaFeaturesChangedAsync(session);
    }

    private static async Task PublishMediaFeaturesChangedAsync(ProjectSession session)
    {
        try
        {
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try { if (!session.IsClosed) session.PublishEvent("project.mediaFeaturesChanged"); }
            finally { session.Writer.Release(); }
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
    }
}
