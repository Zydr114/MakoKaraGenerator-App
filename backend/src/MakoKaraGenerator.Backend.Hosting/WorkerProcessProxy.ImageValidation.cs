using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    /// <summary>独立探测图片正式验证能力；不复用图片预览能力标记。</summary>
    public async Task<ProjectImageValidationWorkerCapabilities> GetImageValidationCapabilitiesAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] payload = ProjectImageValidationWorkerContract.EncodeCapabilitiesRequest();
            WorkerMessage request = new(ProjectImageValidationWorkerContract.CapabilitiesKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(payload), payload);
            ProjectImageValidationWorkerCapabilities? value = null;
            await InvokeTimedMessageAsync(request, ProjectImageValidationWorkerContract.CapabilitiesResultKind, timeout,
                response =>
                {
                    value = ProjectImageValidationWorkerContract.DecodeCapabilities(response.Payload);
                    EffectPackageProfile profile = ProjectEffectsJson.PackageProfile;
                    if (value.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                        || value.PackageCapabilities.PackageVersion != profile.PackageVersion
                        || value.PackageCapabilities.SdkVersion != profile.SdkVersion
                        || value.PackageCapabilities.ShaderProfile != profile.ShaderProfile)
                        throw new WorkerProtocolException("图片验证 Worker 引擎或包基础能力不匹配。", "WorkerImageValidationUnavailable");
                    (string Identity, int Count)? expected = TryComputeScriptReferenceIdentity();
                    if (expected is null || value.PackageCapabilities.ScriptRefsIdentity != expected.Value.Identity
                        || value.PackageCapabilities.ScriptRefsCount != expected.Value.Count)
                        throw new WorkerProtocolException("图片验证 Worker 的实际编译参考集不匹配。", "WorkerImageValidationUnavailable");
                }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
            return value!;
        }
        catch (Exception primary)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            if (primary is WorkerProtocolException protocol
                && protocol.Code is not ("WorkerImageValidationCapabilitiesInvalid" or "WorkerImageValidationUnavailable"))
                throw new WorkerProtocolException("Worker 未提供当前图片验证所需的独立能力。", "WorkerImageValidationUnavailable",
                    protocol.Diagnostics, protocol.PackageDiagnostics);
            if (primary is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new WorkerProtocolException("图片验证 Worker 不可用。", "WorkerImageValidationUnavailable");
            throw;
        }
    }
}
