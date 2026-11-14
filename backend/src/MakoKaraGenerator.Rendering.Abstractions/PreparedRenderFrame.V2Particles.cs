using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering;

/// <summary>一个真实结构身份对应的已计预算、不可变解析拖尾。</summary>
public sealed record PreparedV2ParticleTrail(ParticleIdentity Id, ImmutableArray<ParticleTrailSample> Samples);

/// <summary>单实例完整出生窗和存活视图；独立于旧 ParticleId／EmissionSchedule。</summary>
public sealed record PreparedV2ParticleSnapshot(EffectInstanceId InstanceId, ParticleBirthWindow Births,
    ImmutableArray<ParticleView> Alive, ImmutableArray<PreparedV2ParticleTrail> Trails);

/// <summary>本次锚点补算的实际有界工作量；缓存命中不等同于新的文字求值。</summary>
public sealed record PreparedV2AnchorStatistics(int UniqueTimes, int TextCalls, int FutureEndpoints, int MemoHits, int MemoMisses);

public sealed partial class PreparedRenderFrame
{
    /// <summary>全部纯实例的独立结构身份快照，保留文字离场后的存活粒子。</summary>
    public ImmutableArray<PreparedV2ParticleSnapshot> V2ParticleSnapshots { get; private init; } = [];
    /// <summary>便于中立消费者读取的全部 v2 存活粒子；不向旧 BirthIndex 压缩身份。</summary>
    public ImmutableArray<ParticleView> V2Particles { get; private init; } = [];
    /// <summary>本次纯粒子查询实际锚点工作量。</summary>
    public PreparedV2AnchorStatistics V2AnchorStatistics { get; private init; } = new(0, 0, 0, 0, 0);

    /// <summary>仅发布已完成的纯粒子快照；后续视觉、材质和文字复制均保留它。</summary>
    public PreparedRenderFrame WithV2ParticleSnapshots(ImmutableArray<PreparedV2ParticleSnapshot> snapshots,
        PreparedV2AnchorStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (snapshots.IsDefault || snapshots.Any(static item => item is null)
            || snapshots.Select(static item => item.InstanceId).Distinct().Count() != snapshots.Length)
            throw Invalid();
        long births = 0, alive = 0, trails = 0;
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.InstanceId.IsValid || snapshot.Births is null || snapshot.Births.Births.IsDefault || snapshot.Alive.IsDefault
                || snapshot.Trails.IsDefault || snapshot.Births.Window != new BirthWindow(Math.Max(0, TimeUs - ParticleLimits.MaximumLifetimeUs), TimeUs)) throw Invalid();
            HashSet<ParticleIdentity> birthIds = [], liveIds = [], trailIds = [];
            foreach (var birth in snapshot.Births.Births)
                if (birth?.Id is null || birth.Id.InstanceId != snapshot.InstanceId || birth.Source is null || birth.Spec is null
                    || !snapshot.Births.Window.Contains(birth.BirthUs) || birth.DeathUs <= birth.BirthUs
                    || !birthIds.Add(birth.Id)) throw Invalid();
            foreach (var particle in snapshot.Alive)
                if (particle?.Id is null || particle.BirthUs > TimeUs || particle.DeathUs <= TimeUs
                    || !birthIds.Contains(particle.Id) || !liveIds.Add(particle.Id)
                    || !double.IsFinite(particle.SizePx) || particle.SizePx < 0) throw Invalid();
            foreach (var trail in snapshot.Trails)
            {
                if (trail?.Id is null || trail.Samples.IsDefault || !liveIds.Contains(trail.Id) || !trailIds.Add(trail.Id)) throw Invalid();
                trails += trail.Samples.Length;
            }
            births += snapshot.Births.Births.Length; alive += snapshot.Alive.Length;
        }
        if (births > ParticleLimits.MaximumBirths || alive > ParticleLimits.MaximumAlive || trails > ParticleLimits.MaximumTrailNodes)
            throw new PreparedFrameException("ParticleQueryBudgetExceeded", "全部实例的纯粒子快照超过出生、存活或拖尾预算。");
        return new(this, OverlayBatches, DiagnosticSnapshot, VisualBudget)
        {
            V2ParticleSnapshots = snapshots, V2Particles = [.. snapshots.SelectMany(static item => item.Alive)],
            V2AnchorStatistics = statistics,
        };
        static PreparedFrameException Invalid() => new("InvalidPreparedV2Particles", "纯粒子快照的实例、结构身份或时间窗无效。");
    }
}
