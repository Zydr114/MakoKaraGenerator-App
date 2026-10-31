using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using System.Diagnostics;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    public async Task<PackageResourceWorkerCapabilities> GetPackageResourceCapabilitiesAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        Process expectedProcess = _process ?? throw new WorkerProtocolException("Worker资源进程不可用。", "WorkerResourceUnavailable");
        byte[] payload = PackageResourceWorkerContract.EncodeCapabilitiesRequest();
        WorkerMessage request = new(PackageResourceWorkerContract.CapabilitiesKind,
            Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload);
        PackageResourceWorkerCapabilities? result = null;
        await InvokeTimedMessageAsync(request, PackageResourceWorkerContract.CapabilitiesResultKind, timeout,
            response =>
            {
                result = PackageResourceWorkerContract.DecodeCapabilities(response.Payload);
                if (result.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                    || result.Profile != PackageResourceWorkerContract.Profile
                    || !result.ImageProfiles.Contains(PackageResourceWorkerContract.ImageDecodeProfile, StringComparer.Ordinal)
                    || !result.VideoProfiles.Contains(PackageResourceWorkerContract.VideoMetadataProfile, StringComparer.Ordinal)
                    || !result.VideoProfiles.Contains(PackageResourceWorkerContract.VideoFrameProfile, StringComparer.Ordinal)
                    || !result.FontProfiles.Contains(PackageResourceWorkerContract.FontShapeProfile, StringComparer.Ordinal)
                    || result.FontProviderIdentity != PackageResourceWorkerContract.FontProviderIdentity)
                    throw new WorkerProtocolException("Worker资源能力与当前后端合同不匹配。", "WorkerResourceCapabilitiesMismatch");
            }, cancellationToken,
            requirePackageRuntimeGuards: true, expectedProcess: expectedProcess,
            expectedProcessFailureCode: "WorkerResourceCapabilitiesMismatch",
            expectedProcessFailureMessage: "Worker资源能力与当前后端合同不匹配。").ConfigureAwait(false);
        return result!;
    }

    public async Task<PackageResourceInspection> ValidatePackageResourceAsync(
        PackageResourceWorkerGrant grant, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        PackageResourceWorkerContract.ValidateGrant(grant);
        byte[] payload = PackageResourceWorkerContract.EncodeGrant(grant);
        await GetPackageResourceCapabilitiesAsync(timeout, cancellationToken).ConfigureAwait(false);
        Process expectedProcess = _packageResourceVerifiedProcess ?? throw new WorkerProtocolException(
            "Worker资源能力与验证请求不属于同一实际进程。", "WorkerResourceCapabilitiesMismatch");
        WorkerMessage request = new(PackageResourceWorkerContract.ValidateKind,
            Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload);
        PackageResourceInspection? result = null;
        await InvokeTimedMessageAsync(request, PackageResourceWorkerContract.ValidateResultKind, timeout,
            response =>
            {
                result = PackageResourceWorkerContract.DecodeInspection(response.Payload);
                if (result.GrantId != grant.GrantId || result.Role != grant.Role || result.Size != grant.Size
                    || !string.Equals(result.Sha256, grant.Sha256, StringComparison.Ordinal))
                    throw new WorkerProtocolException("Worker资源检查回执与原授权不一致。", "WorkerResourceGrantChanged");
            }, cancellationToken,
            requirePackageRuntimeGuards: true, expectedProcess: expectedProcess,
            expectedProcessFailureCode: "WorkerResourceCapabilitiesMismatch",
            expectedProcessFailureMessage: "Worker资源能力与验证请求不属于同一实际进程。").ConfigureAwait(false);
        return result!;
    }
}
