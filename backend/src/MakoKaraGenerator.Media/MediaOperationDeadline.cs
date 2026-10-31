using System.Diagnostics;

namespace MakoKaraGenerator.Media;

/// <summary>A single deadline spans startup, all reads and exit of one operation.</summary>
internal sealed class MediaOperationDeadline : IDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationToken _caller;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TimeSpan _timeout;
    internal MediaOperationDeadline(TimeSpan timeout, CancellationToken caller)
    { _caller = caller; _timeout = timeout; _deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); _deadline.CancelAfter(timeout); }
    internal CancellationToken Token => _deadline.Token;
    internal void ThrowIfExpired()
    {
        _caller.ThrowIfCancellationRequested();
        if (_elapsed.Elapsed >= _timeout) _deadline.Cancel();
        if (_deadline.IsCancellationRequested) throw new IOException("媒体操作超时。", new TimeoutException());
    }
    internal Exception ClassifyCancellation(OperationCanceledException error)
        => _caller.IsCancellationRequested ? new OperationCanceledException("媒体操作已取消。", error, _caller)
            : new IOException("媒体操作超时。", new TimeoutException("媒体操作超过期限。", error));
    public void Dispose() => _deadline.Dispose();
}
