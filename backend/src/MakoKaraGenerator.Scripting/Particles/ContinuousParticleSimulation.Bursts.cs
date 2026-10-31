using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Particles;

internal sealed record ResolvedParticleAnchor(ParticleSource Source, Point2 Position);

internal sealed partial class ContinuousParticleSimulation
{
    public void Burst(BurstIntent intent, DeterministicRandom random, ImmutableArray<ResolvedParticleAnchor> anchors, CancellationToken cancellationToken)
    {
        if (intent.TimeUs != _boundary) throw new InvalidOperationException("Burst 只能提交在当前真实事件边界。");
        long requested = (long)intent.Descriptor.Count * anchors.Length;
        if (requested + _boundaryBirths.Count > budget.EmissionRecordLimit)
            throw new ParticleSimulationException(intent.InstanceId, intent.TimeUs, "EmissionRecordBudgetExceeded", "全部实例区间出生记录超过固定会话预算。");
        if (requested == 0) return;
        Prune(intent.TimeUs);
        var key = new EmitterKey(intent.InstanceId, intent.EmitterId);
        if (!_emitters.TryGetValue(key, out var state)) _emitters.Add(key, state = new());
        // 同 emitter 仍使用最近登记的批次层级，物理外观按各次出生固定。
        state.Layer = intent.Descriptor.Layer;
        foreach (var anchor in anchors)
            for (int count = 0; count < intent.Descriptor.Count; count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _boundaryBirths.Add(Spawn(key, state, anchor.Source, anchor.Position, intent.TimeUs, intent.Descriptor, random, intent.EventId));
            }
    }
    private ParticleBirthSample Spawn(EmitterKey key, EmitterState state, ParticleSource source, Point2 anchor,
        long birthUs, ParticleDescription descriptor, DeterministicRandom random, EventId? eventId)
    {
        if (_particles.Count >= budget.LiveParticleLimit)
            throw new ParticleSimulationException(key.Instance, birthUs, "LiveParticleBudgetExceeded", "全部实例存活粒子超过固定会话预算。");
        long deathUs; ulong index;
        try { deathUs = checked(birthUs + descriptor.LifetimeUs); index = state.NextIndex; state.NextIndex = checked(index + 1); }
        catch (OverflowException) { throw new ParticleSimulationException(key.Instance, birthUs, "ParticleTimeOrIdentityOverflow", "粒子时间或出生身份溢出。"); }
        string stream = Encode(key.Emitter.Value, descriptor.SeedStreamKey);
        double dx = (random.GetUnitDouble(stream + "x", index) * 2 - 1) * descriptor.SpreadPx;
        double dy = (random.GetUnitDouble(stream + "y", index) * 2 - 1) * descriptor.SpreadPx;
        Point2 position;
        try { position = new(anchor.X + dx, anchor.Y + dy); }
        catch (ArgumentOutOfRangeException) { throw new ParticleSimulationException(key.Instance, birthUs, "ParticleGeometryNonFinite", "粒子出生坐标溢出。"); }
        var particle = new Particle(key, source, index, birthUs, deathUs, position, descriptor, eventId);
        _particles.Add(particle);
        if (descriptor.TrailDurationUs > 0) AddTrail(particle, birthUs);
        return new(new(key.Instance, key.Emitter, index), source, birthUs, deathUs, position, descriptor, eventId);
    }
}
