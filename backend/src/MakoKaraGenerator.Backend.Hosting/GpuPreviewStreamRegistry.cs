using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>独立 GPU 流的 owner、预算及私有消费者领取状态。</summary>
public sealed class GpuPreviewStreamRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedClients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedConsumers = new(StringComparer.Ordinal);

    private sealed class Entry(string client, string consumer, GpuPreviewStreamState state,
        BackendPreviewPinnedInput input, GpuStreamWorkerSession worker)
    {
        internal readonly string Client = client;
        internal readonly string Consumer = consumer;
        internal GpuPreviewStreamState State = state;
        internal BackendPreviewPinnedInput Input = input;
        internal GpuStreamWorkerSession Worker = worker;
        internal bool Claimed;
        internal bool Busy;
    }

    /// <summary>启动 Worker 前保留每 client、consumer 和 host 的有限名额。</summary>
    public IDisposable? TryReserve(string client, string consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        lock (_gate)
        {
            if (_entries.Count + _reservedClients.Count >= 2 || _reservedClients.Contains(client) ||
                _reservedConsumers.Contains(consumer) || _entries.Values.Any(e => e.Client == client || e.Consumer == consumer)) return null;
            _reservedClients.Add(client); _reservedConsumers.Add(consumer);
            return new Reservation(this, client, consumer);
        }
    }

    /// <summary>固定来源复核和实际池分配均成功后发布；成功时转移输入和 Worker 所有权。</summary>
    public bool Publish(IDisposable reservation, GpuPreviewStreamState state, BackendPreviewPinnedInput input,
        GpuStreamWorkerSession worker)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(worker);
        lock (_gate)
        {
            if (reservation is not Reservation r || r.Owner != this || !r.Active ||
                worker.Opened.StreamId != state.StreamId || _entries.ContainsKey(state.StreamId)) return false;
            _entries.Add(state.StreamId, new(r.Client, r.Consumer, state, input, worker));
            ReleaseReservation(r);
            return true;
        }
    }

    /// <summary>仅返回属于当前客户端的公开状态。</summary>
    public GpuPreviewStreamState? Read(string client, string streamId)
    {
        lock (_gate) return _entries.TryGetValue(streamId, out var e) && e.Client == client ? e.State : null;
    }

    /// <summary>绑定 consumer 只能领取指定输入代次的私有通道一次。</summary>
    public GpuPreviewWorkerClaim? Claim(string consumer, string streamId, string expectedInputGeneration)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Consumer != consumer || e.Claimed || e.Busy ||
                e.State.InputGeneration != expectedInputGeneration) return null;
            e.Claimed = true;
            return new(e.Worker.Opened.ProducerEpoch, e.Worker.Opened.WorkerPid, e.Worker.BrokerPath, e.Worker.Handshake);
        }
    }

    /// <summary>仅 owner 的精确修订可开始一次变更；操作排空时阻止并行领取或变更。</summary>
    public bool TryBeginChange(string client, string streamId, string expectedRevision, out bool found)
    {
        lock (_gate)
        {
            found = _entries.TryGetValue(streamId, out var e) && e.Client == client;
            if (!found || e!.Busy || e.State.StreamRevision != expectedRevision) return false;
            e.Busy = true;
            return true;
        }
    }

    /// <summary>取得已锁定会话的资源；排空期间会话继续占用配额。</summary>
    public (GpuPreviewStreamState State, BackendPreviewPinnedInput Input, GpuStreamWorkerSession Worker)? GetForClose(string client, string streamId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client || !e.Busy) return null;
            return (e.State, e.Input, e.Worker);
        }
    }

    /// <summary>仅在 Worker 和固定输入均已释放后归还配额。</summary>
    public bool CompleteClose(string client, string streamId, GpuStreamWorkerSession worker)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client || !e.Busy ||
                !ReferenceEquals(e.Worker, worker)) return false;
            _entries.Remove(streamId);
            return true;
        }
    }

    /// <summary>旧 Worker 排空后原子发布新代；每代拥有独立领取机会。</summary>
    public bool CompleteChange(string client, string streamId, GpuStreamWorkerSession previous,
        GpuPreviewStreamState state, BackendPreviewPinnedInput input, GpuStreamWorkerSession worker)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client || !e.Busy ||
                !ReferenceEquals(e.Worker, previous) || state.StreamId != streamId ||
                worker.Opened.StreamId != streamId || state.InputGeneration == e.State.InputGeneration ||
                state.StreamRevision == e.State.StreamRevision || state.ProducerEpoch != worker.Opened.ProducerEpoch ||
                state.PoolGeneration != worker.Opened.PoolGeneration) return false;
            e.State = state;
            e.Input = input;
            e.Worker = worker;
            e.Claimed = false;
            e.Busy = false;
            return true;
        }
    }

    /// <summary>排空后无法建立新代时公开失败，且不允许领取旧通道。</summary>
    public bool FailChange(string client, string streamId, GpuStreamWorkerSession previous, string failureCode)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client || !e.Busy ||
                !ReferenceEquals(e.Worker, previous)) return false;
            e.State = e.State with { State = "failed", FailureCode = failureCode };
            e.Claimed = true;
            e.Busy = false;
            return true;
        }
    }

    private void ReleaseReservation(Reservation r)
    {
        if (!r.Active) return;
        r.Active = false; _reservedClients.Remove(r.Client); _reservedConsumers.Remove(r.Consumer);
    }

    private sealed class Reservation(GpuPreviewStreamRegistry owner, string client, string consumer) : IDisposable
    {
        internal readonly GpuPreviewStreamRegistry Owner = owner;
        internal readonly string Client = client, Consumer = consumer;
        internal bool Active = true;
        public void Dispose() { lock (Owner._gate) Owner.ReleaseReservation(this); }
    }
}

/// <summary>不得公开给 renderer 或写入日志的 Worker 领取数据。</summary>
public sealed record GpuPreviewWorkerClaim(string ProducerEpoch, int WorkerPid, string BrokerEndpoint, string Handshake);
