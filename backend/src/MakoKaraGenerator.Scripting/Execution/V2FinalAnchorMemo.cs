using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

// A memo stores only exact final anchor points. A request stages new entries until
// its births, late visuals and immutable frame have all completed successfully.
internal readonly record struct V2AnchorNodeKey(ParticleSource Source, CursorAnchorMode Anchor,
    long SegmentStartUs, long SegmentEndUs, long TimeUs, OutputMode Mode, RenderTarget Target);

internal sealed class V2FinalAnchorMemo(int entryLimit = ParticleLimits.MaximumMemoEntries,
    long byteLimit = ParticleLimits.MaximumMemoBytes)
{
    private readonly int _entryLimit = entryLimit;
    private readonly long _byteLimit = byteLimit;
    private sealed record Entry(V2AnchorNodeKey Key, Point2 Point, long Bytes);
    private readonly Dictionary<V2AnchorNodeKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private long _bytes;
    public int Count => _entries.Count;
    public long Bytes => _bytes;
    public Transaction Begin() => new(this);
    public void Clear() { _entries.Clear(); _lru.Clear(); _bytes = 0; }

    internal sealed class Transaction(V2FinalAnchorMemo owner)
    {
        private readonly Dictionary<V2AnchorNodeKey, (Point2 Point, long Bytes)> _pending = [];
        private bool _completed;
        public Point2 Read(V2AnchorNodeKey key, Func<Point2> evaluate, Action<long> reserveBytes, V2AnchorQueryBudget budget)
        {
            if (_completed) throw new InvalidOperationException("锚点事务已完成。");
            if (_pending.TryGetValue(key, out var staged)) { budget.Hit(); return staged.Point; }
            if (owner._entries.TryGetValue(key, out var existing))
            {
                owner._lru.Remove(existing); owner._lru.AddLast(existing); budget.Hit(); return existing.Value.Point;
            }
            // Conservative retained size: object graph and bounded identity strings.
            long bytes = 1024L + 2L * key.Source.BindingId.Value.Length;
            reserveBytes(bytes); budget.Miss();
            var point = evaluate(); budget.Check();
            _pending.Add(key, (point, bytes)); return point;
        }
        public void Commit(CancellationToken token)
        {
            if (_completed) throw new InvalidOperationException("锚点事务已完成。");
            token.ThrowIfCancellationRequested();
            foreach (var pair in _pending)
            {
                if (pair.Value.Bytes > owner._byteLimit || owner._entryLimit <= 0) continue;
                while (owner._entries.Count >= owner._entryLimit || owner._bytes + pair.Value.Bytes > owner._byteLimit)
                {
                    var first = owner._lru.First!; owner._lru.RemoveFirst();
                    owner._entries.Remove(first.Value.Key); owner._bytes -= first.Value.Bytes;
                }
                var node = owner._lru.AddLast(new Entry(pair.Key, pair.Value.Point, pair.Value.Bytes));
                owner._entries.Add(pair.Key, node); owner._bytes += pair.Value.Bytes;
            }
            _completed = true; _pending.Clear();
        }
    }
}

internal sealed class V2AnchorQueryBudget(CancellationToken token,
    int timeLimit = ParticleLimits.MaximumAnchorTimes, int textLimit = ParticleLimits.MaximumTextCalls,
    int futureLimit = ParticleLimits.MaximumFutureEndpoints, long wallLimitMs = ParticleLimits.MaximumAnchorWallMilliseconds,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private TimeSpan _elapsed;
    private long _started;
    private int _depth;
    private readonly HashSet<long> _times = [];
    private readonly HashSet<(ParticleSource, long)> _future = [];
    private int _textCalls, _hits, _misses;
    public PreparedV2AnchorStatistics Statistics => new(_times.Count, _textCalls, _future.Count, _hits, _misses);
    public IDisposable Measure()
    {
        if (_depth++ == 0) _started = _time.GetTimestamp();
        return new TimingScope(this);
    }
    private sealed class TimingScope(V2AnchorQueryBudget owner) : IDisposable
    {
        public void Dispose()
        {
            if (--owner._depth == 0) owner._elapsed += owner._time.GetElapsedTime(owner._started, owner._time.GetTimestamp());
        }
    }
    public void Check()
    {
        token.ThrowIfCancellationRequested();
        var elapsed = _elapsed + (_depth > 0 ? _time.GetElapsedTime(_started, _time.GetTimestamp()) : TimeSpan.Zero);
        if (elapsed.Ticks / TimeSpan.TicksPerMillisecond > wallLimitMs)
            throw new EffectOutputException("FinalAnchorWallBudgetExceeded", "完整文字锚点补算超过固定耗时保护；未降低采样精度。");
    }
    public void Time(long timeUs)
    {
        Check();
        if (_times.Add(timeUs) && _times.Count > timeLimit)
            throw new EffectOutputException("FinalAnchorTimeBudgetExceeded", "锚点唯一补算时刻超过固定预算。");
    }
    public void TextCall()
    {
        Check();
        if (++_textCalls > textLimit) throw new EffectOutputException("FinalAnchorTextCallBudgetExceeded", "锚点实际文字实例调用超过固定预算。");
    }
    public void Endpoint(ParticleSource source, long timeUs, long requestUs)
    {
        Check();
        if (timeUs >= Math.Max(0, requestUs - ParticleLimits.MaximumLifetimeUs) && timeUs <= requestUs) return;
        if (_future.Add((source, timeUs)) && _future.Count > futureLimit)
            throw new EffectOutputException("FinalAnchorFutureEndpointBudgetExceeded", "寿命窗外或未来桥接端点超过固定预算。");
    }
    public void Hit() => _hits++;
    public void Miss() => _misses++;
}
