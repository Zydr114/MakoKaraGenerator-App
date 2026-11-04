using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

/// <summary>出生散布、恒加速度和固定歌曲网格拖尾；全部输入输出均为值，不调用文字或作者代码。</summary>
public static class ParticleKinematics
{
    /// <summary>将已经解析的最终锚点与结构身份的独立随机流合成完整出生记录。</summary>
    public static ParticleBirth ResolveBirth(ScheduledBirth scheduled, Point2 anchor, ParticleOwner owner)
    {
        ArgumentNullException.ThrowIfNull(scheduled); ArgumentNullException.ThrowIfNull(owner);
        CheckOwner(scheduled.Id, owner, scheduled.BirthUs);
        if (scheduled.Source is null || scheduled.Spec is null || scheduled.BirthUs < 0 || scheduled.DeathUs <= scheduled.BirthUs
            || scheduled.DeathUs - scheduled.BirthUs != scheduled.Spec.LifetimeUs)
            throw owner.Failure("ReadParticleBirths", scheduled.BirthUs, "InvalidParticleBirth", "出生值与固定寿命不一致。");
        var random = new DeterministicRandom(owner.Seed, owner.InstanceId);
        string stream = ParticleIdentityEncoding.Stream(scheduled.Id, scheduled.Spec.SeedStreamKey);
        ulong index = ParticleIdentityEncoding.RandomIndex(scheduled.Id);
        double dx = (random.GetUnitDouble(stream + "x", index) * 2 - 1) * scheduled.Spec.SpreadPx;
        double dy = (random.GetUnitDouble(stream + "y", index) * 2 - 1) * scheduled.Spec.SpreadPx;
        try
        {
            return new(scheduled.Id, scheduled.Source, scheduled.EventId, scheduled.BirthUs, scheduled.DeathUs,
                new(anchor.X + dx, anchor.Y + dy), scheduled.Spec.Velocity, scheduled.Spec);
        }
        catch (ArgumentOutOfRangeException)
        { throw owner.Failure("ReadParticleBirths", scheduled.BirthUs, "ParticleGeometryNonFinite", "出生散布坐标不是有限值。"); }
    }

    /// <summary>只采样满足 birth&lt;=t&lt;death 的粒子；年龄外观和位置都重新检查有效性。</summary>
    public static ParticleView Evaluate(ParticleBirth birth, long timeUs, ParticleOwner owner)
    {
        ValidateAlive(birth, timeUs, owner);
        Point2 position = Position(birth, timeUs, owner);
        try
        {
            double age = (timeUs - birth.BirthUs) / (double)birth.Spec.LifetimeUs;
            double opacity = birth.Spec.Motion.Opacity.Evaluate(age);
            double size = birth.Spec.SizePx * birth.Spec.Motion.Size.Evaluate(age);
            if (!double.IsFinite(opacity) || opacity is < 0 or > 1 || !double.IsFinite(size) || size < 0)
                throw new ArgumentOutOfRangeException(nameof(timeUs));
            var color = new ColorRgba(birth.Spec.Color.Red, birth.Spec.Color.Green, birth.Spec.Color.Blue, birth.Spec.Color.Alpha * opacity);
            return new(birth, position, size, color);
        }
        catch (ArgumentOutOfRangeException)
        { throw owner.Failure("ReadParticleBirths", timeUs, "InvalidParticleAppearance", "解析年龄曲线产生非法尺寸或透明度。"); }
    }

    /// <summary>包含裁剪起点、固定 120 Hz 歌曲网格和当前点；不读取任何历史 Trail。</summary>
    public static ImmutableArray<ParticleTrailSample> GetTrail(ParticleBirth birth, long timeUs, ParticleOwner owner,
        ParticleQueryBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget); cancellationToken.ThrowIfCancellationRequested(); ValidateAlive(birth, timeUs, owner);
        if (birth.Spec.TrailDurationUs == 0) return [];
        long start = Math.Max(birth.BirthUs, timeUs - birth.Spec.TrailDurationUs);
        long first = SemanticSimulationGrid.IndexAtOrBefore(start) + 1;
        long last = SemanticSimulationGrid.IndexAtOrBefore(timeUs);
        if (SemanticSimulationGrid.TimeAt(last) == timeUs) last--;
        int interior = last >= first ? checked((int)(last - first + 1)) : 0;
        int count = start == timeUs ? 1 : checked(interior + 2);
        budget.ReserveTrailNodes(owner, count, timeUs); budget.ReserveBytes(owner, checked((long)count * 48), timeUs);
        var result = ImmutableArray.CreateBuilder<ParticleTrailSample>(count);
        result.Add(new(start, Position(birth, start, owner)));
        for (long index = first; index <= last; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); long sampleTime = SemanticSimulationGrid.TimeAt(index);
            result.Add(new(sampleTime, Position(birth, sampleTime, owner)));
        }
        if (start != timeUs) result.Add(new(timeUs, Position(birth, timeUs, owner)));
        cancellationToken.ThrowIfCancellationRequested(); return result.MoveToImmutable();
    }

    private static Point2 Position(ParticleBirth birth, long timeUs, ParticleOwner owner)
    {
        double seconds = (timeUs - birth.BirthUs) / 1_000_000d;
        try
        {
            return new(birth.BirthPosition.X + birth.InitialVelocity.X * seconds + birth.Spec.Motion.Acceleration.X * seconds * seconds / 2,
                birth.BirthPosition.Y + birth.InitialVelocity.Y * seconds + birth.Spec.Motion.Acceleration.Y * seconds * seconds / 2);
        }
        catch (ArgumentOutOfRangeException)
        { throw owner.Failure("ReadParticleBirths", timeUs, "ParticleGeometryNonFinite", "粒子解析运动坐标不是有限值。"); }
    }
    private static void ValidateAlive(ParticleBirth birth, long timeUs, ParticleOwner owner)
    {
        ArgumentNullException.ThrowIfNull(birth); ArgumentNullException.ThrowIfNull(owner); CheckOwner(birth.Id, owner, timeUs);
        if (timeUs < birth.BirthUs || timeUs >= birth.DeathUs || birth.Spec is null || birth.Spec.LifetimeUs <= 0
            || birth.Spec.LifetimeUs > ParticleLimits.MaximumLifetimeUs || birth.DeathUs - birth.BirthUs != birth.Spec.LifetimeUs)
            throw owner.Failure("ReadParticleBirths", timeUs, "ParticleNotAlive", "只可解析当前存活且寿命一致的粒子。");
    }
    private static void CheckOwner(ParticleIdentity id, ParticleOwner owner, long timeUs)
    {
        if (id is null || id.InstanceId != owner.InstanceId)
            throw owner.Failure("ReadParticleBirths", timeUs, "ParticleOwnerMismatch", "粒子不属于本实例。");
    }
}
