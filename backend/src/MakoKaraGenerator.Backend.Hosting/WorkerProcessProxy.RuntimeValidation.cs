using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    /// <summary>独立探测统一 Runtime 正式验证能力；不复用统一 Runtime 预览能力标记。</summary>
    public async Task<RuntimeValidationWorkerCapabilities> GetRuntimeValidationCapabilitiesAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] payload = Array.Empty<byte>();
            WorkerMessage request = new(RuntimeValidationWorkerContract.CapabilitiesKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(payload), payload);
            RuntimeValidationWorkerCapabilities? value = null;
            await InvokeTimedMessageAsync(request, RuntimeValidationWorkerContract.CapabilitiesResultKind, timeout,
                response =>
                {
                    value = RuntimeValidationWorkerContract.DecodeCapabilities(response.Payload);
                    WorkerPackageProfileAdmission.RequireCurrent(value.PackageCapabilities, "WorkerRuntimeValidationUnavailable");
                    EffectPackageProfile profile = ProjectEffectsJson.PackageProfile;
                    if (value.PackageCapabilities.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                        || value.PackageCapabilities.PackageVersion != profile.PackageVersion
                        || value.PackageCapabilities.SdkVersion != profile.SdkVersion
                        || value.PackageCapabilities.ShaderProfile != profile.ShaderProfile)
                        throw new WorkerProtocolException("统一 Runtime 验证 Worker 引擎或包基础能力不匹配。", "WorkerRuntimeValidationUnavailable");
                    (string Identity, int Count)? expected = TryComputeScriptReferenceIdentity();
                    if (expected is null || value.PackageCapabilities.ScriptRefsIdentity != expected.Value.Identity
                        || value.PackageCapabilities.ScriptRefsCount != expected.Value.Count)
                        throw new WorkerProtocolException("统一 Runtime 验证 Worker 的实际编译参考集不匹配。", "WorkerRuntimeValidationUnavailable");
                }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
            return value!;
        }
        catch (Exception primary)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            if (primary is WorkerProtocolException protocol
                && protocol.Code != "WorkerRuntimeValidationUnavailable")
                throw new WorkerProtocolException("Worker 未提供当前统一 Runtime 验证所需的独立能力。", "WorkerRuntimeValidationUnavailable",
                    protocol.Diagnostics, protocol.PackageDiagnostics);
            if (primary is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new WorkerProtocolException("统一 Runtime 验证 Worker 不可用。", "WorkerRuntimeValidationUnavailable");
            throw;
        }
    }
}
