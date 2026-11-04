using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2ParticleKinematicsTests
{
    private static readonly ParticleOwner Owner = new(new("motion"), null, "runtime", 83, "inputs", "prepared");

    [Fact]
    public void 恒加速度和年龄外观使用独立手算值且死亡端点拒绝()
    {
        var spec = Spec() with
        {
            LifetimeUs = 1_000_000, SizePx = 10, Color = new(.5, .25, 1, .8), Velocity = new(4, -2),
            Motion = new() { Acceleration = new(2, 4), Size = new(1, 3, new CurveValue.Easing(EasingKind.Linear)),
                Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) },
        };
        var birth = ParticleKinematics.ResolveBirth(Scheduled(1_000_000, spec), new(3, 4), Owner);
        var view = ParticleKinematics.Evaluate(birth, 1_500_000, Owner);
        Assert.Equal(new Point2(5.25, 3.5), view.Position); Assert.Equal(20, view.SizePx);
        Assert.Equal(new ColorRgba(.5, .25, 1, .4), view.Color);
        Assert.Equal("ParticleNotAlive", Assert.Throws<EffectFailureException>(() => ParticleKinematics.Evaluate(birth, 2_000_000, Owner)).Code);
    }

    [Fact]
    public void 解析拖尾使用绝对歌曲网格与裁剪端点不累计八三三三()
    {
        var spec = Spec() with { TrailDurationUs = 15_000, Velocity = new(1_000_000, 0) };
        var birth = ParticleKinematics.ResolveBirth(Scheduled(123, spec), new(0, 0), Owner);
        var trail = ParticleKinematics.GetTrail(birth, 20_000, Owner, new());
        Assert.Equal([5_000L, 8_333, 16_666, 20_000], trail.Select(static row => row.TimeUs));
        double[] expectedX = [4_877d, 8_210, 16_543, 19_877];
        for (int index = 0; index < expectedX.Length; index++) Assert.Equal(expectedX[index], trail[index].Position.X, precision: 8);
        ParticleKinematics.GetTrail(birth, 40_000, Owner, new());
        Assert.Equal<ParticleTrailSample>(trail, ParticleKinematics.GetTrail(birth, 20_000, Owner, new()));
        var atBirth = ParticleKinematics.GetTrail(birth, 123, Owner, new());
        Assert.Equal(123, Assert.Single(atBirth).TimeUs);
        var gridEnd = ParticleKinematics.GetTrail(birth, 25_000, Owner, new());
        Assert.Equal(25_000, gridEnd[^1].TimeUs); Assert.Equal(gridEnd.Length, gridEnd.Select(static row => row.TimeUs).Distinct().Count());
    }

    [Fact]
    public void 拖尾节点和帧内存共享预算并保持取消原类型()
    {
        var birth = ParticleKinematics.ResolveBirth(Scheduled(0, Spec() with { TrailDurationUs = 20_000 }), new(0, 0), Owner);
        var budget = new ParticleQueryBudget(trailNodeLimit: 4);
        Assert.Equal(3, ParticleKinematics.GetTrail(birth, 10_000, Owner, budget).Length);
        Assert.Equal("TrailSampleBudgetExceeded", Assert.Throws<EffectFailureException>(() => ParticleKinematics.GetTrail(birth, 10_000, Owner, budget)).Code);
        Assert.Equal("ParticleFrameMemoryExceeded", Assert.Throws<EffectFailureException>(() =>
            ParticleKinematics.GetTrail(birth, 10_000, Owner, new(byteLimit: 1))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ParticleKinematics.GetTrail(birth, 10_000, Owner, new(), cancel.Token));
        Assert.Equal(3, ParticleKinematics.GetTrail(birth, 10_000, Owner, new()).Length);
    }

    [Fact]
    public void Int64末端解析网格不构造当前时刻加一()
    {
        const long start = long.MaxValue - 25_000;
        var birth = ParticleKinematics.ResolveBirth(Scheduled(start, Spec() with { LifetimeUs = 20_000, TrailDurationUs = 10_000 }), new(1, 2), Owner);
        var trail = ParticleKinematics.GetTrail(birth, long.MaxValue - 6_000, Owner, new());
        Assert.Equal(long.MaxValue - 16_000, trail[0].TimeUs);
        Assert.Equal(long.MaxValue - 6_000, trail[^1].TimeUs);
        Assert.All(trail, static row => Assert.Equal(new Point2(1, 2), row.Position));
    }

    [Fact]
    public void 随机散布按完整结构分流不受无关head或查询方向扰动()
    {
        var spec = Spec() with { SpreadPx = 30 };
        var original = Scheduled(2, spec);
        var first = ParticleKinematics.ResolveBirth(original, new(5, 6), Owner);
        var unrelated = original with { Id = original.Id with
            { Origin = new ParticleOrigin.Continuous(new ParticleHead.Normal(new("other-track"), new("other-binding")), 0, 1) } };
        ParticleKinematics.ResolveBirth(unrelated, new(5, 6), Owner);
        ParticleKinematics.Evaluate(first, 700_000, Owner);
        Assert.Equal(first, ParticleKinematics.ResolveBirth(original, new(5, 6), Owner));
        Assert.NotEqual(first.BirthPosition, ParticleKinematics.ResolveBirth(unrelated, new(5, 6), Owner).BirthPosition);
        var reloaded = original with { Id = original.Id with
            { Origin = new ParticleOrigin.Continuous(new ParticleHead.Normal(new("track"), new("binding/reload/1")), 0, 1) } };
        Assert.NotEqual(first.Id, reloaded.Id);
        Assert.NotEqual(first.BirthPosition, ParticleKinematics.ResolveBirth(reloaded, new(5, 6), Owner).BirthPosition);
    }

    [Fact]
    public void 所有者与最终非有限坐标具名拒绝()
    {
        var other = Owner with { InstanceId = new("other") };
        var ownerError = Assert.Throws<EffectFailureException>(() => ParticleKinematics.ResolveBirth(Scheduled(0, Spec()), new(0, 0), other));
        Assert.Equal("ParticleOwnerMismatch", ownerError.Code); Assert.Equal(other.InstanceId, ownerError.InstanceId);
        var birth = ParticleKinematics.ResolveBirth(Scheduled(0, Spec() with { Velocity = new(double.MaxValue, 0) }), new(double.MaxValue, 0), Owner);
        Assert.Equal("ParticleGeometryNonFinite", Assert.Throws<EffectFailureException>(() => ParticleKinematics.Evaluate(birth, 500_000, Owner)).Code);
    }

    private static ParticleSpec Spec() => new()
    { LifetimeUs = 1_000_000, SizePx = 2, Color = new(1, 1, 1, 1), SeedStreamKey = "motion", Velocity = new(0, 0) };
    private static ScheduledBirth Scheduled(long timeUs, ParticleSpec spec)
    {
        var source = new ParticleSource.Line(new(new("line"), new("binding")));
        var id = new ParticleIdentity(Owner.InstanceId, new("emitter"),
            new ParticleOrigin.Continuous(new ParticleHead.Normal(new("track"), source.BindingId), 0, 1));
        return new(id, source, null, timeUs, checked(timeUs + spec.LifetimeUs), spec, CursorAnchorMode.Baseline);
    }
}
