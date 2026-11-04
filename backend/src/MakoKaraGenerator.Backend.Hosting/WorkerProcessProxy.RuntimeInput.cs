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
        _ = PackageRenderWorkerContract.EncodeOpen(id, sourceBytes.Span);
        var capabilities = await GetRuntimeInputCapabilitiesAsync(timeout, cancellationToken).ConfigureAwait(false);
        return await OpenPreparedScriptSessionAsync(ProjectRuntimeInputWorkerContract.OpenKind, id, sourceBytes, timeout, capabilities, cancellationToken).ConfigureAwait(false);
    }
}
