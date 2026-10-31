using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed class BackendAnalysisResultStore(string stateRoot, BackendAnalysisOptions options)
{
    private readonly BackendJobSidecarStore _files = new(Path.Combine(stateRoot, "analysis-results"), "analysis-", "AnalysisResult", options.MaxResultBytes, options.MaxResultStorageBytes);
    internal long UsedBytes => _files.UsedBytes;
    internal BackendJobSidecarStore.Reservation? TryReserve() => _files.TryReserve();
    internal BackendJobSidecarStore.Prepared Prepare(string jobId, byte[] bytes, BackendJobSidecarStore.Reservation reservation) => _files.Prepare(jobId, bytes, reservation);
    internal BackendJobSidecarStore.ReadLease AcquireRead(string jobId, BackendJobReportReferenceDto reference) => _files.AcquireRead(jobId, reference);
    internal void Prune(IEnumerable<string> retainedIds) => _files.Prune(retainedIds);
}

/// <summary>授权结果的读取租约；过期后已有读仍占结果存储预算直到释放。</summary>
public sealed class BackendAnalysisReadLease : IDisposable
{
    private readonly BackendJobSidecarStore.ReadLease _lease;
    internal BackendAnalysisReadLease(BackendJobSidecarStore.ReadLease lease) => _lease = lease;
    public Stream Stream => _lease.Stream;
    public BackendJobReportReferenceDto Reference => _lease.Reference;
    public void Dispose() => _lease.Dispose();
}
internal sealed class AnalysisTaskException(string code, Exception? inner = null) : Exception("固定音频分析任务无法完成或发布完整结果。", inner)
{ internal string Code { get; } = code; }
