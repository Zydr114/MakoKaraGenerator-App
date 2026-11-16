using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal sealed record ExportWorkerConfiguration(IBackendExportExecutor? Executor, BackendExportCapabilities Capabilities)
{
    internal static async Task<ExportWorkerConfiguration> ResolveAsync(WorkerProcessOptions? worker, int? timeoutSeconds, BackendExportOptions? options = null)
    {
        if (worker is null) return new(null, new(false, false, false, "WorkerUnavailable"));
        string? ffmpeg = ResolveTool("MAKO_BACKEND_FFMPEG_PATH", "ffmpeg");
        string? ffprobe = ResolveTool("MAKO_BACKEND_FFPROBE_PATH", "ffprobe");
        if (ffmpeg is null || ffprobe is null) return new(null, new(false, false, false, "ExportToolsUnavailable"));
        WorkerBackendExportExecutor executor = new(worker, ffmpeg, ffprobe, timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, options);
        using CancellationTokenSource probeDeadline = new(TimeSpan.FromSeconds(15));
        try
        {
            BackendExportCapabilities capabilities = await executor.ProbeAsync(probeDeadline.Token).ConfigureAwait(false);
            return new(capabilities.GuardAvailable ? executor : null, capabilities);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or MakoKaraGenerator.Rendering.RenderSessionFailureException)
        { return new(null, new(false, false, false, "ExportCapabilityProbeFailed")); }
    }
    internal static string? ResolveTool(string variable, string defaultName)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (configured is not null) return IsExecutable(configured) ? configured : null;
        string fileName = defaultName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        foreach (string rawDirectory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = rawDirectory.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            string candidate = Path.Combine(directory, fileName);
            if (IsExecutable(candidate)) return candidate;
        }
        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                string winGetLinksCandidate = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", fileName);
                if (IsExecutable(winGetLinksCandidate)) return winGetLinksCandidate;
                string winGetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(winGetPackages))
                {
                    foreach (string packageDir in Directory.EnumerateDirectories(winGetPackages, "Gyan.FFmpeg*"))
                    {
                        foreach (string candidate in Directory.EnumerateFiles(packageDir, fileName, SearchOption.AllDirectories))
                            if (IsExecutable(candidate)) return candidate;
                    }
                }
            }
        }
        return null;
    }
    private static bool IsExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
