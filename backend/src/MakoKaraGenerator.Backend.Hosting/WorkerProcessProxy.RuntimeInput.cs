using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    public Task<PackageRenderWorkerCapabilities> GetRuntimeInputCapabilitiesAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        GetPackageCapabilitiesCoreAsync(timeout, false, cancellationToken, runtimeInput: true);

    /// <summary>打开固定运行输入的正式会话，图与材质不做逐像素检查。</summary>
    public Task<StaticWorkerSessionHandle> OpenRuntimeInputSessionAsync(ReadOnlyMemory<byte> sourceBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default)
        => OpenRuntimeInputSessionAsync(sourceBytes, timeout, false, cancellationToken);

    /// <summary>打开固定运行输入会话；<paramref name="graphPixelChecks"/> 为 true 时会话内图与材质执行逐像素检查（作者草稿）。</summary>
    public async Task<StaticWorkerSessionHandle> OpenRuntimeInputSessionAsync(ReadOnlyMemory<byte> sourceBytes,
        TimeSpan timeout, bool graphPixelChecks, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        _ = PackageRenderWorkerContract.EncodeOpen(id, sourceBytes.Span);
        var capabilities = await GetRuntimeInputCapabilitiesAsync(timeout, cancellationToken).ConfigureAwait(false);
        return await OpenPreparedScriptSessionAsync(ProjectRuntimeInputWorkerContract.OpenKind, id, sourceBytes, timeout, capabilities, cancellationToken,
            graphPixelChecks: graphPixelChecks).ConfigureAwait(false);
    }
}
