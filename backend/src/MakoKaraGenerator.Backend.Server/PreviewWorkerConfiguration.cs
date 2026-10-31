using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Server;

internal sealed record PreviewWorkerConfiguration(WorkerProcessOptions? Options, PreviewRuntimeInfoResponse Info)
{
    internal static PreviewWorkerConfiguration Resolve(string? configuredPath = null)
    {
        string path = configuredPath ?? Environment.GetEnvironmentVariable("MAKO_BACKEND_WORKER_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "MakoKaraGenerator.Backend.Worker" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        string? reason = null;
        if (!Path.IsPathFullyQualified(path)) reason = "预览Worker配置必须为绝对apphost路径。";
        else if (!File.Exists(path)) reason = "预览Worker尚未部署。";
        else if (!OperatingSystem.IsWindows())
        {
            try
            {
                UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & executable) == 0) reason = "预览Worker apphost不可执行。";
            }
            catch (IOException) { reason = "预览Worker配置不可读取。"; }
            catch (UnauthorizedAccessException) { reason = "预览Worker配置不可读取。"; }
        }
        bool enabled = reason is null;
        return new(enabled ? new WorkerProcessOptions(path) : null,
            new(enabled, enabled ? "configured-unverified" : "disabled", StaticProjectSnapshotCodec.Profile,
                "worker-process", 1280, 720, 4L * 1024 * 1024,
                reason is null ? null : new(BackendErrorCode.PreviewUnavailable, reason, "worker"),
                SourceProfiles: [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets]));
    }

    /// <summary>分别探测静态及完整包能力；包失败不禁用原静态来源。</summary>
    internal static async Task<PreviewWorkerConfiguration> ResolveAsync(string? configuredPath = null)
    {
        PreviewWorkerConfiguration configured = Resolve(configuredPath);
        if (configured.Options is null) return configured with
        {
            Info = configured.Info with { SourceProfiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis] }
        };
        return await ResolveProfilesAsync(configured, new WorkerRenderFrameSessionFactory(configured.Options),
            new WorkerPackageRenderFrameSessionFactory(configured.Options)).ConfigureAwait(false);
    }

    internal static async Task<PreviewWorkerConfiguration> ResolveProfilesAsync(PreviewWorkerConfiguration configured,
        IBackendPreviewProfileCapability staticFactory, IBackendPreviewProfileCapability packageFactory)
    {
        List<string> profiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis];
        try
        {
            if (await staticFactory.EnsureProfileAsync(BackendSnapshotProfiles.StaticLyricsScenePresets).ConfigureAwait(false) is null)
                profiles.Add(BackendSnapshotProfiles.StaticLyricsScenePresets);
        }
        catch (Exception) { /* The first actual session still reports the stable unavailable code. */ }
        try
        {
            if (await packageFactory.EnsureProfileAsync(PackageProjectSnapshotCodec.Profile).ConfigureAwait(false) is null)
                profiles.Add(PackageProjectSnapshotCodec.Profile);
        }
        catch (Exception) { /* A failed package probe must not remove static capabilities. */ }
        try
        {
            if (await packageFactory.EnsureProfileAsync(PackagePresetProjectSnapshotCodec.Profile).ConfigureAwait(false) is null)
                profiles.Add(PackagePresetProjectSnapshotCodec.Profile);
        }
        catch (Exception) { /* Mixed preset sources require an explicit package Worker declaration. */ }
        try
        {
            if (await packageFactory.EnsureProfileAsync(ProjectImagePreviewSnapshotCodec.Profile).ConfigureAwait(false) is null)
                profiles.Add(ProjectImagePreviewSnapshotCodec.Profile);
        }
        catch (Exception) { /* Image requires its own actual Worker handshake. */ }
        try
        {
            if (await packageFactory.EnsureProfileAsync(ProjectRuntimeInputWorkerContract.Profile).ConfigureAwait(false) is null)
                profiles.Add(ProjectRuntimeInputWorkerContract.Profile);
        }
        catch (Exception) { /* Unified media inputs require their own actual Worker handshake. */ }
        return configured with { Info = configured.Info with { SourceProfiles = [.. profiles] } };
    }
}
