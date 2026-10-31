using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Particles;

internal readonly record struct EmitterKey(EffectInstanceId Instance, EmitterId Emitter);
internal readonly record struct HeadKey(CursorTrackId Track, BindingId Binding, EventId? Transition = null, long FlightStartUs = 0);
internal sealed record ResolvedEmitter(EmitterBinding Binding, DeterministicRandom Random, PlaybackFrame Playback,
    ImmutableArray<ResolvedCursorBridge> Bridges);
internal sealed record ParticleFrame(ImmutableArray<ParticleSample> Particles, ImmutableArray<DotBatch> Dots);

// 只持有固定 SDK 值，不保留作者实例或委托；清理与脚本模块生命周期相同。
internal sealed partial class ContinuousParticleSimulation(ParticleSimulationBudget budget)
{
    private Dictionary<EmitterKey, EmitterState> _emitters = [];
    private List<Particle> _particles = [];
    private List<ParticleBirthSample> _boundaryBirths = [];
    private long _boundary;
    private int _trailCount;
    private long _lastPruned = -1;
    public EmissionSchedule EmissionSchedule { get; private set; } = EmissionSchedule.Initial;
    public void Clear() { _emitters.Clear(); _particles.Clear(); _boundaryBirths.Clear(); _boundary = 0; _trailCount = 0; _lastPruned = -1; EmissionSchedule = EmissionSchedule.Initial; }
    public void Register(long timeUs, IEnumerable<ResolvedEmitter> bindings)
    {
        if (timeUs != _boundary) throw new InvalidOperationException("发射器只能注册在已提交边界。");
        foreach (var state in _emitters.Values) state.Active = null;
        foreach (var binding in bindings)
        {
            var key = new EmitterKey(binding.Binding.InstanceId, binding.Binding.Id);
            if (!_emitters.TryGetValue(key, out var state)) _emitters.Add(key, state = new());
            state.Active = binding; state.Layer = binding.Binding.Descriptor.Layer;
            var heads = binding.Playback.GetCursors(binding.Binding.Descriptor.AnchorMode)
                .Select(static cursor => new HeadKey(cursor.TrackId, cursor.BindingId)).ToHashSet();
            foreach (var bridge in binding.Bridges)
                heads.Add(new(bridge.Source.Track, bridge.Source.BindingId, bridge.Source.TransitionId, bridge.Source.FlightRange.StartUs));
            foreach (var old in state.Quotas.Keys.Where(head => !heads.Contains(head)).ToArray()) state.Quotas.Remove(old);
            foreach (var head in heads) state.Quotas.TryAdd(head, ExactEmissionQuota.Zero);
        }
        foreach (var state in _emitters.Values.Where(static state => state.Active is null)) state.Quotas.Clear();
    }
    public void Commit(long endUs, Func<EffectInstanceId, long, PlaybackFrame> geometry, CancellationToken cancellationToken)
    {
        var births = Advance(endUs, geometry, cancellationToken);
        EmissionSchedule = new(_boundary, endUs, births); _boundaryBirths.Clear(); _boundary = endUs;
        Prune(endUs);
        foreach (var particle in _particles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (particle.Descriptor.TrailDurationUs == 0) continue;
            AddTrail(particle, endUs);
        }
    }
    private ImmutableArray<ParticleBirthSample> Advance(long endUs, Func<EffectInstanceId, long, PlaybackFrame> geometry, CancellationToken cancellationToken)
    {
        if (endUs <= _boundary) throw new InvalidOperationException("连续粒子区间须前进。");
        // 逐出生全实例时序合并；不能以单区间总出生数冒充同时存活峰值。
        PriorityQueue<ScheduledHead, (long Time, string Instance, string Emitter, string Track, string Binding, string Transition, long Start)> queue = new(
            Comparer<(long Time, string Instance, string Emitter, string Track, string Binding, string Transition, long Start)>.Create(static (left, right) =>
            {
                int order = left.Time.CompareTo(right.Time);
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Instance, right.Instance);
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Emitter, right.Emitter);
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Track, right.Track);
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Binding, right.Binding);
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Transition, right.Transition);
                if (order == 0) order = left.Start.CompareTo(right.Start);
                return order;
            }));
        List<ScheduledHead> scheduled = []; List<ParticleBirthSample> births = [.. _boundaryBirths];
        foreach (var entry in _emitters)
        {
            if (entry.Value.Active is not { } active) continue;
            var rate = ExactEmissionQuota.PerMicrosecond(active.Binding.Descriptor.RatePerSecond);
            foreach (var quota in entry.Value.Quotas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bridge = quota.Key.Transition is { } transition ? active.Bridges.Single(item => item.Source.TransitionId == transition
                    && item.Source.FlightRange.StartUs == quota.Key.FlightStartUs) : null;
                long start = bridge is null ? _boundary : Math.Max(_boundary, bridge.Source.FlightRange.StartUs);
                long stop = bridge is null ? endUs : Math.Min(endUs, bridge.Source.FlightRange.EndUs);
                if (stop <= start) continue;
                var head = new ScheduledHead(entry.Key, quota.Key, entry.Value, quota.Value.During(rate, stop - start), start, bridge);
                scheduled.Add(head); Enqueue(head);
            }
        }
        while (queue.TryDequeue(out var head, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested(); long birthUs = priority.Time;
            Prune(birthUs); var active = head.State.Active!; var descriptor = active.Binding.Descriptor;
            if (births.Count >= budget.EmissionRecordLimit)
                throw new ParticleSimulationException(head.Key.Instance, birthUs, "EmissionRecordBudgetExceeded", "全部实例区间出生记录超过固定会话预算。");
            if (_particles.Count >= budget.LiveParticleLimit)
                throw new ParticleSimulationException(head.Key.Instance, birthUs, "LiveParticleBudgetExceeded", "全部实例存活粒子超过固定会话预算。");
            ParticleSource source; Point2 position;
            if (head.Bridge is { } bridge)
            {
                source = bridge.Source;
                try { position = bridge.At(birthUs); }
                catch (ArgumentOutOfRangeException) { throw new ParticleSimulationException(head.Key.Instance, birthUs,
                    "ParticleGeometryNonFinite", "桥接曲线或世界坐标溢出。"); }
            }
            else
            {
                var cursor = geometry(head.Key.Instance, birthUs).GetCursors(descriptor.AnchorMode)
                    .SingleOrDefault(cursor => cursor.TrackId == head.Head.Track && cursor.BindingId == head.Head.Binding)
                    ?? throw new ParticleSimulationException(head.Key.Instance, birthUs, "ParticleBirthCursorUnavailable", "出生时刻的真实装载播放头缺失。");
                source = new ParticleSource.Cursor(cursor.TrackId, cursor.Identity); position = cursor.Position;
            }
            births.Add(Spawn(head.Key, head.State, source, position,
                birthUs, descriptor, active.Random, null));
            Enqueue(head);
        }
        foreach (var head in scheduled) head.State.Quotas[head.Head] = head.Schedule.Remaining;
        return [.. births];
        void Enqueue(ScheduledHead head)
        {
            if (head.Schedule.NextOffset() is { } offset)
                queue.Enqueue(head, (checked(head.StartUs + offset), head.Key.Instance.Value, head.Key.Emitter.Value, head.Head.Track.Value,
                    head.Head.Binding.Value, head.Head.Transition?.Value ?? "", head.Head.FlightStartUs));
        }
    }
    private void Prune(long timeUs)
    {
        if (_lastPruned == timeUs) return;
        _lastPruned = timeUs;
        for (int index = _particles.Count - 1; index >= 0; index--)
        {
            var particle = _particles[index];
            if (particle.DeathUs <= timeUs) { _trailCount -= particle.Trail.Count; _particles.RemoveAt(index); continue; }
            long cutoff = timeUs - particle.Descriptor.TrailDurationUs;
            int count = particle.Trail.TakeWhile(sample => sample.TimeUs < cutoff).Count();
            if (count > 0) { particle.Trail.RemoveRange(0, count); _trailCount -= count; }
        }
    }
    private void AddTrail(Particle particle, long timeUs)
    {
        if (particle.Trail.Count > 0 && particle.Trail[^1].TimeUs == timeUs) return;
        if (_trailCount >= budget.TrailSampleLimit)
            throw new ParticleSimulationException(particle.Key.Instance, timeUs, "TrailSampleBudgetExceeded", "全部实例固定拖尾采样超过会话预算。");
        particle.Trail.Add(new(timeUs, particle.Position(timeUs))); _trailCount++;
    }
    private static string Encode(string emitter, string stream) => emitter.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + emitter
        + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + stream + ":";
    private sealed record ScheduledHead(EmitterKey Key, HeadKey Head, EmitterState State, ExactEmissionQuota.Schedule Schedule,
        long StartUs, ResolvedCursorBridge? Bridge);
    private sealed class EmitterState
    {
        public ResolvedEmitter? Active;
        public ulong NextIndex;
        public OverlayLayer Layer;
        public Dictionary<HeadKey, ExactEmissionQuota> Quotas = [];
        public EmitterState Copy() => new() { Active = Active, NextIndex = NextIndex, Layer = Layer, Quotas = new(Quotas) };
    }
    private sealed class Particle(EmitterKey key, ParticleSource source, ulong index, long birthUs, long deathUs, Point2 birthPosition, ParticleDescription descriptor, EventId? eventId)
    {
        public EmitterKey Key { get; } = key;
        public ParticleSource Source { get; } = source;
        public EventId? EventId { get; } = eventId;
        public ulong Index { get; } = index;
        public long BirthUs { get; } = birthUs;
        public long DeathUs { get; } = deathUs;
        public Point2 BirthPosition { get; } = birthPosition;
        public ParticleDescription Descriptor { get; } = descriptor;
        public List<ParticleTrailSample> Trail = [];
        public Particle Copy() => new(Key, Source, Index, BirthUs, DeathUs, BirthPosition, Descriptor, EventId) { Trail = [.. Trail] };
        public Point2 Position(long timeUs)
        {
            double seconds = (timeUs - BirthUs) / 1_000_000d;
            try { return new(BirthPosition.X + Descriptor.Velocity.X * seconds + Descriptor.Motion.Acceleration.X * seconds * seconds / 2,
                BirthPosition.Y + Descriptor.Velocity.Y * seconds + Descriptor.Motion.Acceleration.Y * seconds * seconds / 2); }
            catch (ArgumentOutOfRangeException) { throw new ParticleSimulationException(Key.Instance, timeUs, "ParticleGeometryNonFinite", "粒子解析运动坐标溢出。"); }
        }
    }
}
internal sealed class ParticleSimulationException(EffectInstanceId instance, long timeUs, string code, string message) : Exception(message)
{
    public EffectInstanceId Instance { get; } = instance;
    public long TimeUs { get; } = timeUs;
    public string Code { get; } = code;
}
