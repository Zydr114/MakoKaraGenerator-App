using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    public Task<PackageRenderWorkerCapabilities> GetRuntimeInputCapabilitiesAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        GetPackageCapabilitiesCoreAsync(timeout, false, cancellationToken, runtimeInput: true);

    public async Task<StaticWorkerSessionHandle> OpenRuntimeInputSessionAsync(ReadOnlyMemory<byte> sourceBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        byte[] payload = PackageRenderWorkerContract.EncodeOpen(id, sourceBytes.Span);
        await GetRuntimeInputCapabilitiesAsync(timeout, cancellationToken).ConfigureAwait(false);
        string sourceHash = WorkerProtocol.ComputeSha256(payload.AsSpan(PackageRenderWorkerContract.OpenHeaderBytes));
        WorkerMessage request = new(ProjectRuntimeInputWorkerContract.OpenKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: id.ToString("N"), sourceSha256: sourceHash, frameSequence: 0);
        await InvokeTimedMessageAsync(request, PackageRenderWorkerContract.SessionResultKind, timeout,
            ValidateEmptyPackageSessionResult, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
        return new(id, sourceHash);
    }
}
