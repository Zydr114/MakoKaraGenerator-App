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

    /// <summary>复用同一探测 Worker 依次验证静态、包与统一运行输入能力；包失败不禁用原静态来源。</summary>
    internal static Task<PreviewWorkerConfiguration> ResolveAsync(string? configuredPath = null, WorkerProcessProxy? sharedProxy = null)
        => ResolveAsync(Resolve(configuredPath), sharedProxy);

    internal static async Task<PreviewWorkerConfiguration> ResolveAsync(PreviewWorkerConfiguration configured, WorkerProcessProxy? sharedProxy = null)
    {
        if (configured.Options is null) return configured with
        {
            Info = configured.Info with { SourceProfiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis] }
        };
        WorkerProcessProxy proxy = sharedProxy ?? new(configured.Options);
        try
        {
            TimeSpan timeout = TimeSpan.FromSeconds(20);
            List<string> profiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis];
            try
            {
                StaticWorkerCapabilitiesDto staticCaps = await proxy.GetStaticCapabilitiesAsync(timeout).ConfigureAwait(false);
                if (staticCaps.SourceProfiles.Contains(BackendSnapshotProfiles.StaticLyricsScenePresets, StringComparer.Ordinal))
                    profiles.Add(BackendSnapshotProfiles.StaticLyricsScenePresets);
            }
            catch (Exception error)
            { Console.Error.WriteLine($"BACKEND_PREVIEW_CAPABILITY_PROBE_FAILED profile={BackendSnapshotProfiles.StaticLyricsScenePresets} type={error.GetType().Name}"); }
            try
            {
                PackageRenderWorkerCapabilities packageCaps = await proxy.GetPackageCapabilitiesAsync(timeout).ConfigureAwait(false);
                profiles.Add(PackageProjectSnapshotCodec.Profile);
                if (packageCaps.SourceProfiles?.Contains(PackagePresetProjectSnapshotCodec.Profile, StringComparer.Ordinal) == true)
                    profiles.Add(PackagePresetProjectSnapshotCodec.Profile);
            }
            catch (Exception error)
            { Console.Error.WriteLine($"BACKEND_PREVIEW_CAPABILITY_PROBE_FAILED profile={PackageProjectSnapshotCodec.Profile} type={error.GetType().Name}"); }
            try
            {
                await proxy.GetRuntimeInputCapabilitiesAsync(timeout).ConfigureAwait(false);
                profiles.Add(ProjectRuntimeInputWorkerContract.Profile);
            }
            catch (Exception error)
            { Console.Error.WriteLine($"BACKEND_PREVIEW_CAPABILITY_PROBE_FAILED profile={ProjectRuntimeInputWorkerContract.Profile} type={error.GetType().Name}"); }
            return configured with { Info = configured.Info with { SourceProfiles = [.. profiles] } };
        }
        finally
        {
            if (sharedProxy is null)
            {
                try { await proxy.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { Console.Error.WriteLine($"BACKEND_PREVIEW_CAPABILITY_PROBE_CLEANUP_FAILED type={cleanup.GetType().Name}"); }
            }
        }
    }

    internal static async Task<PreviewWorkerConfiguration> ResolveProfilesAsync(PreviewWorkerConfiguration configured,
        IBackendPreviewProfileCapability staticFactory, IBackendPreviewProfileCapability packageFactory)
    {
        List<string> profiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis];
        await ProbeProfileAsync(staticFactory, BackendSnapshotProfiles.StaticLyricsScenePresets, profiles).ConfigureAwait(false);
        await ProbeProfileAsync(packageFactory, PackageProjectSnapshotCodec.Profile, profiles).ConfigureAwait(false);
        await ProbeProfileAsync(packageFactory, PackagePresetProjectSnapshotCodec.Profile, profiles).ConfigureAwait(false);
        await ProbeProfileAsync(packageFactory, ProjectRuntimeInputWorkerContract.Profile, profiles).ConfigureAwait(false);
        return configured with { Info = configured.Info with { SourceProfiles = [.. profiles] } };
    }

    private static async Task ProbeProfileAsync(IBackendPreviewProfileCapability capability, string profile, List<string> profiles)
    {
        try
        {
            if (await capability.EnsureProfileAsync(profile).ConfigureAwait(false) is null)
                profiles.Add(profile);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BACKEND_PREVIEW_CAPABILITY_PROBE_FAILED profile={profile} type={error.GetType().Name}");
        }
    }
}
