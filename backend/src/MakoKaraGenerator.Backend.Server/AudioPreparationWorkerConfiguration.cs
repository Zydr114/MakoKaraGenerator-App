using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server;

internal sealed record AudioPreparationWorkerConfiguration(IBackendAudioPreparationExecutor? Executor,
    BackendAudioPreparationCapabilities Capabilities)
{
    internal static async Task<AudioPreparationWorkerConfiguration> ResolveAsync(WorkerProcessOptions? worker, int timeoutSeconds,
        WorkerProcessProxy? sharedProxy = null)
    {
        if (worker is null) return new(null, new(false, false, "WorkerAudioPreparationUnavailable"));
        string? ffmpeg = ExportWorkerConfiguration.ResolveTool("MAKO_BACKEND_FFMPEG_PATH", "ffmpeg");
        string? ffprobe = ExportWorkerConfiguration.ResolveTool("MAKO_BACKEND_FFPROBE_PATH", "ffprobe");
        if (ffmpeg is null || ffprobe is null) return new(null, new(false, false, "WorkerAudioPreparationToolsUnavailable"));
        WorkerBackendAudioPreparationExecutor executor = new(worker, ffmpeg, ffprobe, TimeSpan.FromSeconds(timeoutSeconds));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        try
        {
            BackendAudioPreparationCapabilities capabilities = await executor.ProbeAsync(sharedProxy, deadline.Token).ConfigureAwait(false);
            return new(capabilities.GuardAvailable && capabilities.ToolStartupVerified ? executor : null, capabilities);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or RenderSessionFailureException)
        { return new(null, new(false, false, "WorkerAudioPreparationCapabilityProbeFailed")); }
    }
}
