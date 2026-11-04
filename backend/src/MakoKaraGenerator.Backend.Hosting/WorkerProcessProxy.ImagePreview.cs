using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    /// <summary>独立探测图片预览能力，证明当前实际进程的解码预算和全部基础来源。</summary>
    public async Task<ProjectImagePreviewWorkerCapabilities> GetImagePreviewCapabilitiesAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] payload = ProjectImagePreviewWorkerContract.EncodeCapabilitiesRequest();
            WorkerMessage request = new(ProjectImagePreviewWorkerContract.CapabilitiesKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(payload), payload);
            ProjectImagePreviewWorkerCapabilities? value = null;
            await InvokeTimedMessageAsync(request, ProjectImagePreviewWorkerContract.CapabilitiesResultKind, timeout,
                response =>
                {
                    value = ProjectImagePreviewWorkerContract.DecodeCapabilities(response.Payload);
                    WorkerPackageProfileAdmission.RequireCurrent(value.PackageCapabilities, "WorkerImagePreviewUnavailable");
                    var package = value.PackageCapabilities;
                    var profile = ProjectEffectsJson.PackageProfile;
                    if (value.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                        || package.PackageVersion != profile.PackageVersion || package.SdkVersion != profile.SdkVersion
                        || package.ShaderProfile != profile.ShaderProfile)
                        throw new WorkerProtocolException("图片预览 Worker 引擎或包基础能力不匹配。", "WorkerImagePreviewUnavailable");
                    var expected = TryComputeScriptReferenceIdentity();
                    if (expected is null || package.ScriptRefsIdentity != expected.Value.Identity || package.ScriptRefsCount != expected.Value.Count)
                        throw new WorkerProtocolException("图片预览 Worker 的实际编译参考集不匹配。", "WorkerImagePreviewUnavailable");
                }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
            return value!;
        }
        catch (Exception primary)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            if (primary is WorkerProtocolException protocol
                && protocol.Code is not ("WorkerImagePreviewCapabilitiesInvalid" or "WorkerImagePreviewUnavailable"))
                throw new WorkerProtocolException("Worker 未提供当前图片预览所需的独立能力。", "WorkerImagePreviewUnavailable",
                    protocol.Diagnostics, protocol.PackageDiagnostics);
            throw;
        }
    }

    /// <summary>只在同一实际 Worker 通过独立能力握手后打开固定内存图片来源。</summary>
    public async Task<StaticWorkerSessionHandle> OpenImagePreviewSessionAsync(ReadOnlyMemory<byte> snapshotBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        _ = PackageRenderWorkerContract.EncodeOpen(id, snapshotBytes.Span);
        var capabilities = await GetImagePreviewCapabilitiesAsync(timeout, cancellationToken).ConfigureAwait(false);
        return await OpenPreparedScriptSessionAsync(ProjectImagePreviewWorkerContract.OpenKind, id, snapshotBytes, timeout, capabilities.PackageCapabilities, cancellationToken, image: true).ConfigureAwait(false);
    }
}
