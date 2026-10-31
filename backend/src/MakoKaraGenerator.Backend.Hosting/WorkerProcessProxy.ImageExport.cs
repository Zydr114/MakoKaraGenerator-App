using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    private Process? _imageExportVerifiedProcess;

    /// <summary>每次真实图片导出都在同一实际进程重新协商能力、引擎和编译参考集。</summary>
    public Task<ProjectImageExportWorkerCapabilities> GetImageExportCapabilitiesAsync(string ffmpegPath, string ffprobePath,
        TimeSpan timeout, CancellationToken cancellationToken = default)
        => GetImageExportCapabilitiesAsync(ffmpegPath, ffprobePath, timeout, null, cancellationToken);

    public async Task<ProjectImageExportWorkerCapabilities> GetImageExportCapabilitiesAsync(string ffmpegPath, string ffprobePath,
        TimeSpan timeout, WorkerExportResourcePolicy? resourcePolicy, CancellationToken cancellationToken = default)
    {
        Guid taskId = Guid.NewGuid();
        ProjectImageExportWorkerMetadata metadata = new(ProjectImageExportWorkerContract.ExportProfile,
            BackendSnapshotProfiles.StaticLyricsScene, WorkerExportContract.ExecutionProfile, "opaqueMp4", false, null, null,
            ffmpegPath, ffprobePath, ResourcePolicy: resourcePolicy);
        byte[] payload = ProjectImageExportWorkerContract.EncodeInput(taskId, metadata, [], capabilities: true);
        WorkerMessage request = new(ProjectImageExportWorkerContract.CapabilitiesKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sourceSha256: WorkerProtocol.ComputeSha256([]),
            taskId: taskId.ToString("N"), exportFormat: metadata.Format, exportIncludeAudio: false);
        try
        {
            WorkerMessage response = await InvokeExportTaskAsync(request, 0, timeout, null, cancellationToken,
                imageExport: true).ConfigureAwait(false);
            ProjectImageExportWorkerCapabilities value = ProjectImageExportWorkerContract.DecodeCapabilities(response.Payload);
            EffectPackageProfile profile = ProjectEffectsJson.PackageProfile;
            var expected = TryComputeScriptReferenceIdentity();
            if (value.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                || value.PackageCapabilities.PackageVersion != profile.PackageVersion
                || value.PackageCapabilities.SdkVersion != profile.SdkVersion
                || value.PackageCapabilities.ShaderProfile != profile.ShaderProfile || expected is null
                || value.PackageCapabilities.ScriptRefsIdentity != expected.Value.Identity
                || value.PackageCapabilities.ScriptRefsCount != expected.Value.Count)
                throw new WorkerProtocolException("图片导出 Worker 引擎、包基础或实际编译参考集不匹配。", "WorkerImageExportUnavailable");
            return value;
        }
        catch (Exception primary)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            if (primary is WorkerProtocolException protocol
                && protocol.Code is not ("WorkerImageExportCapabilitiesInvalid" or "WorkerImageExportUnavailable"))
                throw new WorkerProtocolException("Worker 未提供独立图片导出能力。", "WorkerImageExportUnavailable");
            throw;
        }
    }
}
