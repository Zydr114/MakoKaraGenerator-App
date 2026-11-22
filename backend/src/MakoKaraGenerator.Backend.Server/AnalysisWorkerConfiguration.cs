using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server;

internal sealed record AnalysisWorkerConfiguration(IBackendAnalysisExecutor? Executor, BackendAnalysisCapabilities Capabilities)
{
    internal static async Task<AnalysisWorkerConfiguration> ResolveAsync(WorkerProcessOptions? worker, int timeoutSeconds,
        WorkerProcessProxy? sharedProxy = null)
    {
        if (worker is null) return new(null, new(false, false, "WorkerUnavailable"));
        string? ffmpeg = ExportWorkerConfiguration.ResolveTool("MAKO_BACKEND_FFMPEG_PATH", "ffmpeg");
        string? ffprobe = ExportWorkerConfiguration.ResolveTool("MAKO_BACKEND_FFPROBE_PATH", "ffprobe");
        if (ffmpeg is null || ffprobe is null) return new(null, new(false, false, "AnalysisToolsUnavailable"));
        WorkerBackendAnalysisExecutor executor = new(worker, ffmpeg, ffprobe, TimeSpan.FromSeconds(timeoutSeconds));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        try
        {
            BackendAnalysisCapabilities capabilities = await executor.ProbeAsync(sharedProxy, deadline.Token).ConfigureAwait(false);
            return new(capabilities.GuardAvailable && capabilities.ToolStartupVerified ? executor : null, capabilities);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or RenderSessionFailureException)
        { return new(null, new(false, false, "AnalysisCapabilityProbeFailed")); }
    }
}
