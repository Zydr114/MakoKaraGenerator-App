using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>独立 GPU 流的 owner、预算及私有消费者领取状态。</summary>
public sealed class GpuPreviewStreamRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedClients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedConsumers = new(StringComparer.Ordinal);
    private readonly List<(IDisposable? Reservation, BackendPreviewPinnedInput Input, GpuStreamWorkerSession Worker)> _quarantined = [];
    private readonly List<(BackendPreviewPinnedInput Input, GpuStreamOpenCleanupException Failure)> _failedOpens = [];

    public string[] ActiveClientIds { get { lock (_gate) return _entries.Values.Select(e => e.Client).Distinct(StringComparer.Ordinal).ToArray(); } }
    public (string StreamId, string Revision)[] ClientStreams(string client)
    {
        lock (_gate) return _entries.Values.Where(e => e.Client == client)
            .Select(e => (e.State.StreamId, e.State.StreamRevision)).ToArray();
    }

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
        internal bool InputReleased;
        internal bool Quarantined;
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
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client) return null;
            MarkExited(e);
            return e.State;
        }
    }

    public GpuStreamWorkerSession? WorkerForRead(string client, string streamId)
    {
        lock (_gate)
            return _entries.TryGetValue(streamId, out var e) && e.Client == client && !e.Busy ? e.Worker : null;
    }

    /// <summary>绑定 consumer 只能领取指定输入代次的私有通道一次。</summary>
    public GpuPreviewWorkerClaim? Claim(string consumer, string streamId, string expectedInputGeneration)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Consumer != consumer || e.Claimed || e.Busy ||
                e.State.InputGeneration != expectedInputGeneration) return null;
            MarkExited(e);
            if (e.State.RegistrationState == "failed") return null;
            e.Claimed = true;
            return new(e.Worker.Opened.ProducerEpoch, e.Worker.Opened.WorkerPid, e.Worker.BrokerPath, e.Worker.Handshake,
                e.State.InputGeneration, e.State.PoolGeneration, e.State.Size, e.Worker.Opened.Transport ?? e.State.Transport);
        }
    }

    private static void MarkExited(Entry entry)
    {
        if (!entry.Busy && entry.State.RegistrationState != "failed" && !entry.Worker.IsWorkerAlive)
        {
            entry.Worker.CleanupUnopenedShmIfDead();
            entry.State = entry.State with { RegistrationState = "failed", FailureCode = "GpuWorkerExited" };
            entry.Claimed = true;
        }
    }

    /// <summary>仅已领取的精确 consumer 代次能隔离所属预览；保留未知消费者的配额。</summary>
    public (string Client, GpuStreamWorkerSession Worker)? QuarantineConsumerFailure(string consumer, string streamId,
        string inputGeneration, string producerEpoch)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var entry) || entry.Consumer != consumer || !entry.Claimed || entry.Busy ||
                entry.State.InputGeneration != inputGeneration || entry.State.ProducerEpoch != producerEpoch) return null;
            entry.Busy = true;
            entry.Quarantined = true;
            return (entry.Client, entry.Worker);
        }
    }

    /// <summary>故障消费者的所属 Worker 已终止；配额仍保留，不能当作正常排空或重新开放。</summary>
    public bool IsConsumerIsolationComplete(string client, string streamId)
    {
        lock (_gate) return _entries.TryGetValue(streamId, out var entry) && entry.Client == client &&
            entry.Quarantined && entry.Worker.TerminationConfirmed;
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
                !ReferenceEquals(e.Worker, worker) || e.Quarantined) return false;
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
            e.InputReleased = false;
            e.Worker = worker;
            e.Claimed = false;
            e.Busy = false;
            return true;
        }
    }

    /// <summary>排空后无法建立新代时公开失败，且不允许领取旧通道。</summary>
    public bool FailChange(string client, string streamId, GpuStreamWorkerSession previous, string failureCode,
        GpuStreamWorkerState? workerState = null)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client || !e.Busy ||
                !ReferenceEquals(e.Worker, previous)) return false;
            e.State = e.State with { RegistrationState = "failed", FailureCode = failureCode, WorkerState = workerState,
                LastAppliedControlSeq = workerState?.LastAppliedControlSeq };
            e.Claimed = true;
            e.Busy = false;
            return true;
        }
    }

    /// <summary>失败后 Worker 已终止时释放固定输入，但继续保留配额以覆盖未知消费者引用。</summary>
    public void ReleaseRetiredInput(string client, string streamId, GpuStreamWorkerSession worker)
    {
        IDisposable? lease = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(streamId, out var e) && e.Client == client &&
                ReferenceEquals(e.Worker, worker) && !e.InputReleased && worker.TerminationConfirmed)
            { e.InputReleased = true; lease = e.Input.RetentionLease; }
        }
        lease?.Dispose();
    }

    /// <summary>进程关闭时释放剩余固定输入；不得用于重新开放流配额。</summary>
    public void ReleaseAllInputs()
    {
        List<IDisposable> leases = [];
        lock (_gate)
        {
            foreach (Entry e in _entries.Values)
                if (!e.InputReleased && e.Worker.TerminationConfirmed)
                {
                    e.InputReleased = true;
                    if (e.Input.RetentionLease is { } lease) leases.Add(lease);
                }
            foreach (var orphan in _quarantined.Where(item => item.Worker.TerminationConfirmed))
                if (orphan.Input.RetentionLease is { } lease) leases.Add(lease);
            _quarantined.RemoveAll(item => item.Worker.TerminationConfirmed);
        }
        List<Exception>? failures = null;
        foreach (IDisposable lease in leases)
            try { lease.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException(failures);
    }

    public void QuarantineOpen(IDisposable? reservation, string? client, string? streamId,
        BackendPreviewPinnedInput input, GpuStreamOpenCleanupException failure)
    {
        lock (_gate)
        {
            if (reservation is Reservation r && r.Owner == this && r.Active) r.Quarantined = true;
            else if (client is not null && streamId is not null && _entries.TryGetValue(streamId, out var e) && e.Client == client && e.Busy)
                e.Quarantined = true;
            else throw new InvalidOperationException("GPU 启动失败无法隔离。");
            _failedOpens.Add((input, failure));
        }
    }

    /// <summary>服务退出时重试启动失败的所属进程回收；仅确认终止后释放输入。</summary>
    public async Task RetryFailedOpensAsync()
    {
        (BackendPreviewPinnedInput Input, GpuStreamOpenCleanupException Failure)[] entries;
        GpuStreamWorkerSession[] workers;
        lock (_gate)
        {
            entries = _failedOpens.ToArray();
            workers = _quarantined.Select(item => item.Worker).Concat(_entries.Values.Select(item => item.Worker))
                .Where(worker => !worker.TerminationConfirmed).Distinct().ToArray();
        }
        List<Exception>? errors = null;
        foreach (GpuStreamWorkerSession worker in workers)
            try { await worker.RetryTerminationAsync().ConfigureAwait(false); }
            catch (Exception error) { (errors ??= []).Add(error); }
        foreach (var entry in entries)
        {
            try
            {
                await entry.Failure.RetryCleanupAsync().ConfigureAwait(false);
                lock (_gate) _failedOpens.Remove(entry);
                entry.Input.RetentionLease?.Dispose();
            }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException(errors);
    }

    /// <summary>创建回滚无法确认终止时保留配额和固定输入，直到服务退出。</summary>
    public void Quarantine(IDisposable reservation, BackendPreviewPinnedInput input, GpuStreamWorkerSession worker)
    {
        lock (_gate)
        {
            if (reservation is not Reservation r || r.Owner != this || !r.Active)
                throw new InvalidOperationException("GPU 预留不可隔离。");
            r.Quarantined = true;
            _quarantined.Add((reservation, input, worker));
        }
    }

    /// <summary>旧流配额继续保留时隔离未确认终止的新代。</summary>
    public void QuarantineChange(string client, string streamId, BackendPreviewPinnedInput input, GpuStreamWorkerSession worker)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(streamId, out var e) || e.Client != client || !e.Busy)
                throw new InvalidOperationException("GPU 流不可隔离。");
            e.Quarantined = true;
            _quarantined.Add((null, input, worker));
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
        internal bool Quarantined;
        public void Dispose() { lock (Owner._gate) if (!Quarantined) Owner.ReleaseReservation(this); }
    }
}

/// <summary>不得公开给 renderer 或写入日志的 Worker 领取数据。</summary>
public sealed record GpuPreviewWorkerClaim(string ProducerEpoch, int WorkerPid, string BrokerEndpoint, string Handshake,
    string InputGeneration, string PoolGeneration, PreviewSizeDto Size, string Transport = "dma-buf");
