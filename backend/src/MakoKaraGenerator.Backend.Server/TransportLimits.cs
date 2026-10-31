using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

/// <summary>Preparation is bounded before request bodies enter memory or receive a receipt.</summary>
internal sealed class UploadPreparationLimits
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _clients = new(StringComparer.Ordinal);
    private int _active;

    public IDisposable? TryAcquire(string clientId)
    {
        lock (_gate)
        {
            _clients.TryGetValue(clientId, out int count);
            if (_active >= 4 || count >= 2) return null;
            _active++;
            _clients[clientId] = count + 1;
            return new Reservation(this, clientId);
        }
    }

    private void Release(string clientId)
    {
        lock (_gate)
        {
            _active--;
            if (_clients[clientId] == 1) _clients.Remove(clientId);
            else _clients[clientId]--;
        }
    }

    private sealed class Reservation(UploadPreparationLimits owner, string clientId) : IDisposable
    {
        private UploadPreparationLimits? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(clientId);
    }
}

/// <summary>Accepted actions outlive HTTP reads and are drained before host-owned services are released.</summary>
internal sealed class AcceptedOperationTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private bool _stopping;

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping) throw new ResourceStoreException(BackendErrorCode.SessionClosing, "服务已经停止接受新操作。");
            _pending.Add(done.Task);
        }
        try { return await action().ConfigureAwait(false); }
        finally
        {
            lock (_gate) _pending.Remove(done.Task);
            done.SetResult();
        }
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate) pending = _pending.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public void StopAccepting() { lock (_gate) _stopping = true; }
}
