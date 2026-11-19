namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>一个尚未绑定输入的备用；所有开启、活动、回收和隔离对象共用两个硬名额。</summary>
/// <remarks>TWorker 在正式装配中为 WorkerProcessProxy；注入生命周期仅用于无 GPU 的状态机测试。
/// 构造委托不得启动进程，启动必须在 WarmAsync 内发生。StopAsync 必须使用原代理的所属进程树排水。</remarks>
public sealed class GpuWorkerSparePool<TWorker> : IAsyncDisposable where TWorker : class
{
    private readonly object _gate = new();
    private readonly HashSet<Slot> _slots = [];
    private readonly Func<TWorker> _create;
    private readonly Func<TWorker, CancellationToken, Task> _warm;
    private readonly Func<TWorker, Task> _stop;
    private readonly Func<TWorker, bool> _alive;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl, _initialBackoff, _maximumBackoff;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _maintenance;
    private Slot? _spare;
    private string _configurationIdentity;
    private int _failures;
    private long _failureTimestamp;
    private TimeSpan _retryDelay;
    private bool _disposed;
    private Task? _disposeTask;

    public GpuWorkerSparePool(string configurationIdentity, Func<TWorker> create,
        Func<TWorker, CancellationToken, Task> warmAsync, Func<TWorker, Task> stopAsync,
        Func<TWorker, bool> isAlive, TimeSpan? spareTtl = null, TimeProvider? timeProvider = null,
        TimeSpan? initialBackoff = null, TimeSpan? maximumBackoff = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationIdentity);
        _configurationIdentity = configurationIdentity;
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _warm = warmAsync ?? throw new ArgumentNullException(nameof(warmAsync));
        _stop = stopAsync ?? throw new ArgumentNullException(nameof(stopAsync));
        _alive = isAlive ?? throw new ArgumentNullException(nameof(isAlive));
        _time = timeProvider ?? TimeProvider.System;
        _ttl = spareTtl ?? TimeSpan.FromSeconds(30);
        _initialBackoff = initialBackoff ?? TimeSpan.FromMilliseconds(250);
        _maximumBackoff = maximumBackoff ?? TimeSpan.FromSeconds(30);
        if (_ttl <= TimeSpan.Zero || _initialBackoff <= TimeSpan.Zero || _maximumBackoff < _initialBackoff)
            throw new ArgumentOutOfRangeException(nameof(spareTtl));
        _maintenance = MaintainAsync();
    }

    /// <summary>只取未绑定过业务输入的原 Worker；取用后永不放回备用。</summary>
    public async Task<Lease> AcquireAsync(CancellationToken token = default)
    {
        while (true)
        {
            Slot? expired = null;
            Slot? selected = null;
            string admission = "cold-reservation";
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                if (_spare is { } candidate && (Expired(candidate) ||
                    candidate.State == State.Spare && !_alive(candidate.Worker)))
                { expired = candidate; _spare = null; candidate.State = State.Retiring; }
                else if (_spare is { } ready)
                {
                    selected = ready; _spare = null;
                    admission = selected.Ready.Task.IsCompletedSuccessfully ? "prepared-spare-hit" : "opening-spare-wait";
                    selected.State = State.Bound;
                }
                else if (_slots.Count < 2 && !BackingOff()) selected = Reserve(bound: true);
                else throw new InvalidOperationException("GPU Worker 两个进程名额已占用或预热正在退避。");
            }
            if (expired is not null) { await RetireAsync(expired).ConfigureAwait(false); continue; }
            Start(selected!);
            Signal();
            try
            {
                await selected!.Ready.Task.WaitAsync(token).ConfigureAwait(false);
                bool accepted;
                lock (_gate) accepted = !_disposed && selected.Configuration == _configurationIdentity &&
                    selected.State == State.Bound && _alive(selected.Worker);
                if (!accepted)
                {
                    await RetireAsync(selected).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    continue;
                }
                return new Lease(this, selected, admission);
            }
            catch
            {
                // A cancelled caller cannot leave an opening process unaccounted.
                try { await RetireAsync(selected!).ConfigureAwait(false); } catch { }
                throw;
            }
        }
    }

    /// <summary>只在池内已准备的进程上读取设备状态；不取得业务绑定权、不创建池外进程。</summary>
    public async Task<TResult> InspectReadyAsync<TResult>(Func<TWorker, CancellationToken, Task<TResult>> inspect,
        CancellationToken token = default)
    {
        while (true)
        {
            Slot[] candidates;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                candidates = _slots.Where(IsInspectable).ToArray();
            }
            foreach (var slot in candidates)
            {
                try
                {
                    lock (_gate)
                        if (_disposed || slot.Configuration != _configurationIdentity ||
                            slot.State is not (State.Spare or State.Bound) || !_alive(slot.Worker)) continue;
                    TResult result = await inspect(slot.Worker, token).ConfigureAwait(false);
                    lock (_gate)
                        if (!_disposed && slot.Configuration == _configurationIdentity &&
                            slot.State is State.Spare or State.Bound && _alive(slot.Worker)) return result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* A concurrent retirement cannot provide current device proof. */ }
            }
            Slot? reserved = null;
            Task[] pending;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_slots.Any(slot => !candidates.Contains(slot) && IsInspectable(slot))) continue;
                pending = _slots.Where(slot => slot.Configuration == _configurationIdentity &&
                    slot.State is State.Opening or State.Bound && !slot.Ready.Task.IsCompleted).Select(slot => slot.Ready.Task).ToArray();
                if (pending.Length == 0 && !BackingOff())
                {
                    if (_spare is null && _slots.Count < 2) reserved = Reserve(bound: false);
                    else pending = _slots.Where(slot => slot.State == State.Retiring).Select(ObserveRetirementAsync).ToArray();
                }
            }
            if (reserved is not null)
            {
                Start(reserved);
                await reserved.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            }
            else if (pending.Length != 0) await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
            else throw new InvalidOperationException("没有同部署存活的已准备 GPU Worker。");
        }

        bool IsInspectable(Slot slot) => slot.Configuration == _configurationIdentity &&
            slot.State is State.Spare or State.Bound && slot.Ready.Task.IsCompletedSuccessfully && _alive(slot.Worker);
    }

    /// <summary>部署／配置内容身份改变只淘汰未绑定备用；已使用对象不会变成新配置备用。</summary>
    public async Task InvalidateAsync(string configurationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationIdentity);
        Slot? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_configurationIdentity == configurationIdentity) return;
            _configurationIdentity = configurationIdentity; previous = _spare; _spare = null;
            _failures = 0; _retryDelay = TimeSpan.Zero;
            if (previous is not null) previous.State = State.Retiring;
        }
        try { if (previous is not null) await RetireAsync(previous).ConfigureAwait(false); }
        finally { Signal(); }
    }

    public GpuWorkerSparePoolSnapshot Read()
    {
        lock (_gate) return new(_slots.Count, _slots.Count(s => !s.Ready.Task.IsCompleted && s.State is State.Opening or State.Bound),
            _slots.Count(s => s.State == State.Spare), _slots.Count(s => s.State == State.Bound && s.Ready.Task.IsCompletedSuccessfully),
            _slots.Count(s => s.State == State.Retiring), _slots.Count(s => s.State == State.Quarantined),
            _failures, BackingOff(), _configurationIdentity);
    }

    // Trusted in-process acceptance only. References grant no new business lease.
    internal TResult[] ReadPrivateDiagnostics<TResult>(Func<TWorker, string, bool, bool, TResult> read)
    {
        lock (_gate) return _slots.Select(slot => read(slot.Worker, slot.State.ToString(),
            slot.Configuration == _configurationIdentity, slot.Ready.Task.IsCompletedSuccessfully)).ToArray();
    }

    private Slot Reserve(bool bound)
    {
        if (_slots.Count >= 2 || !bound && _spare is not null) throw new InvalidOperationException("GPU Worker 预算状态无效。");
        var slot = new Slot(_create(), _configurationIdentity, bound ? State.Bound : State.Opening);
        _slots.Add(slot);
        if (!bound) _spare = slot;
        return slot;
    }

    private void Start(Slot slot)
    {
        lock (_gate) { if (slot.Started) return; slot.Started = true; }
        _ = WarmCoreAsync(slot);
    }

    private async Task WarmCoreAsync(Slot slot)
    {
        bool failed = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, slot.Lifetime.Token);
            await _warm(slot.Worker, linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                slot.ReadyTimestamp = _time.GetTimestamp();
                if (slot.State == State.Opening) slot.State = State.Spare;
                // Success resets warmup backoff, never quarantined process quota.
                _failures = 0; _retryDelay = TimeSpan.Zero;
            }
            slot.Ready.TrySetResult();
        }
        catch (Exception error)
        {
            failed = true;
            lock (_gate)
            {
                if (!_shutdown.IsCancellationRequested && !slot.Lifetime.IsCancellationRequested)
                {
                    _failures = Math.Min(_failures + 1, 30);
                    _failureTimestamp = _time.GetTimestamp();
                    _retryDelay = TimeSpan.FromTicks((long)Math.Min(_maximumBackoff.Ticks,
                        _initialBackoff.Ticks * Math.Pow(2, _failures - 1)));
                }
            }
            slot.Ready.TrySetException(error);
            _ = slot.Ready.Task.Exception; // Observe background-only warmup failure.
        }
        finally { slot.WarmFinished.TrySetResult(); }
        if (failed) try { await RetireAsync(slot).ConfigureAwait(false); } catch { }
        Signal();
    }

    private async Task MaintainAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                Slot? start = null;
                Slot[] retired;
                lock (_gate)
                {
                    if (_disposed) return;
                    if (_spare is { } spare && (Expired(spare) || spare.State == State.Spare && !_alive(spare.Worker)))
                    { spare.State = State.Retiring; _spare = null; }
                    retired = _slots.Where(s => s.State == State.Retiring || s.State == State.Quarantined &&
                        _time.GetElapsedTime(s.LastDrainFailureTimestamp, _time.GetTimestamp()) >= TimeSpan.FromSeconds(1)).ToArray();
                    if (_spare is null && _slots.Count < 2 && !BackingOff()) start = Reserve(bound: false);
                }
                if (start is not null) Start(start);
                foreach (var slot in retired) _ = ObserveRetirementAsync(slot);
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ObserveRetirementAsync(Slot slot)
    { try { await RetireAsync(slot).ConfigureAwait(false); } catch { /* Kept in quota for the next owned drain retry. */ } }

    private Task RetireAsync(Slot slot)
    {
        lock (_gate)
        {
            if (!_slots.Contains(slot)) return Task.CompletedTask;
            if (slot.Retirement is { IsCompleted: false }) return slot.Retirement;
            if (ReferenceEquals(_spare, slot)) _spare = null;
            slot.State = State.Retiring;
            // A reservation cancelled before Start has never launched a process.
            // Do not turn retirement into a fresh warmup or recursively schedule two drains.
            if (!slot.Started)
            {
                slot.Started = true; slot.Ready.TrySetCanceled(); slot.WarmFinished.TrySetResult();
            }
            return slot.Retirement = Task.Run(async () =>
            {
                try
                {
                    slot.Lifetime.Cancel();
                    await slot.WarmFinished.Task.ConfigureAwait(false);
                    await _stop(slot.Worker).ConfigureAwait(false);
                    lock (_gate) _slots.Remove(slot); // Confirmed full owned drain only.
                    slot.Dispose();
                    Signal();
                }
                catch { lock (_gate) { slot.State = State.Quarantined; slot.LastDrainFailureTimestamp = _time.GetTimestamp(); } throw; }
            });
        }
    }

    private bool Expired(Slot slot) => slot.State == State.Spare &&
        _time.GetElapsedTime(slot.ReadyTimestamp, _time.GetTimestamp()) >= _ttl;
    private bool BackingOff() => _retryDelay > TimeSpan.Zero &&
        _time.GetElapsedTime(_failureTimestamp, _time.GetTimestamp()) < _retryDelay;
    private void Signal() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>仅关闭开始后重试仍被占用的所属身份；失败仍保留名额。</summary>
    public Task RetryShutdownDrainAsync()
    {
        Slot[] slots;
        lock (_gate)
        {
            if (!_disposed) throw new InvalidOperationException("仅关闭期间可重试整个池排水。");
            slots = _slots.ToArray();
        }
        return Task.WhenAll(slots.Select(RetireAsync));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null) _disposeTask = DisposeCoreAsync();
            else if (_disposeTask.IsFaulted || _disposeTask.IsCanceled)
                _disposeTask = RetryShutdownDrainAsync();
            return new(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync()
    {
        Slot[] slots;
        lock (_gate) { _disposed = true; slots = _slots.ToArray(); _spare = null; }
        _shutdown.Cancel(); Signal();
        await _maintenance.ConfigureAwait(false);
        await Task.WhenAll(slots.Select(RetireAsync)).ConfigureAwait(false);
    }

    internal enum State { Opening, Spare, Bound, Retiring, Quarantined }
    internal sealed class Slot(TWorker worker, string configuration, State state) : IDisposable
    {
        internal readonly TWorker Worker = worker;
        internal readonly string Configuration = configuration;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WarmFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal State State = state;
        internal bool Started;
        internal long ReadyTimestamp, LastDrainFailureTimestamp;
        internal Task? Retirement;
        public void Dispose() => Lifetime.Dispose();
    }

    /// <summary>固定输入拥有此一次性名额直至所属原进程树已确认回收；绝不返回备用。</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly GpuWorkerSparePool<TWorker> _owner;
        private readonly Slot _slot;
        internal Lease(GpuWorkerSparePool<TWorker> owner, Slot slot, string admission)
        { _owner = owner; _slot = slot; Admission = admission; }
        public TWorker Worker => _slot.Worker;
        internal string Admission { get; }
        public ValueTask DisposeAsync() => new(_owner.RetireAsync(_slot));
        public Task RetryTerminationAsync() => _owner.RetireAsync(_slot);
    }
}

public sealed record GpuWorkerSparePoolSnapshot(int Total, int Opening, int Spare, int Bound, int Retiring,
    int Quarantined, int ConsecutiveWarmupFailures, bool BackingOff, string ConfigurationIdentity);
