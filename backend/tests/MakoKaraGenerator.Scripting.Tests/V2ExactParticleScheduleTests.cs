using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2ExactParticleScheduleTests
{
    private static readonly EmitterId Emitter = new("precise");
    private static readonly ParticleOwner Owner = new(new("instance"), new("package", "sha256:" + new string('a', 64), "export"),
        "runtime", 123, "inputs", "prepared");

    [Fact]
    public void 六十每秒精确阈值与半开终点没有零号出生()
    {
        var schedule = Compile([new(new(0, 1_000_000), 60, 60)], [new(0, 1_000_000)], [Source(0, 1_000_000)]);
        var births = schedule.ReadBirths(new(0, 1_000_000), new());
        Assert.Equal(59, births.Length);
        Assert.Equal([16_667L, 33_334, 50_000], births.Take(3).Select(static birth => birth.BirthUs));
        Assert.Equal(983_334, births[^1].BirthUs);
        Assert.Equal(Enumerable.Range(1, 59).Select(static value => (ulong)value),
            births.Select(static birth => Assert.IsType<ParticleOrigin.Continuous>(birth.Id.Origin).SequenceStartingAt1));
        Assert.Empty(schedule.ReadBirths(new(0, 16_666), new()));
        Assert.Single(schedule.ReadBirths(new(16_667, 16_667), new()));
        Assert.Empty(schedule.ReadBirths(new(1_000_000, 1_000_000), new()));
    }

    [Fact]
    public void 非整二进制零点三的第三个阈值严格晚于十秒()
    {
        // double 0.3 严格小于 3/10；十秒的累计量严格小于 3。
        var schedule = Compile([new(new(0, 10_000_002), .3, .3)], [new(0, 10_000_002)], [Source(0, 10_000_002)]);
        Assert.Equal([3_333_334L, 6_666_667], schedule.ReadBirths(new(0, 10_000_000), new()).Select(static birth => birth.BirthUs));
        Assert.Equal(10_000_001, Assert.Single(schedule.ReadBirths(new(10_000_000, 10_000_001), new())).BirthUs);
    }

    [Fact]
    public void 上升下降线性使用手算整数阈值而非浮点开方()
    {
        // 上升 Q(t)=t²/4，下降 Q(t)=2t-t²/4，仅检查 t=0,1,2,3。
        var up = Compile([new(new(0, 4), 0, 2_000_000)], [new(0, 4)], [Source(0, 4)]);
        var down = Compile([new(new(0, 4), 2_000_000, 0)], [new(0, 4)], [Source(0, 4)]);
        Assert.Equal([2L, 3], up.ReadBirths(new(0, 4), new()).Select(static birth => birth.BirthUs));
        Assert.Equal([1L, 2, 2], down.ReadBirths(new(0, 4), new()).Select(static birth => birth.BirthUs));
    }

    [Fact]
    public void 裁剪线性段仍从原始前缀相减且启用窗内缺段是零率()
    {
        // F(t)=t²/8；episode 从 2 开始，F(2)=1/2，t=4/5/6 的配额为 1.5/2.625/4。
        var linear = Compile([new(new(0, 8), 0, 2_000_000)], [new(2, 7)],
            [Source(0, 3, "a"), Source(3, 5, "b"), Source(5, 8, "c")]);
        Assert.Equal([4L, 5, 6, 6], linear.ReadBirths(new(0, 8), new()).Select(static row => row.BirthUs));
        var missing = Compile([new(new(0, 1), 500_000, 500_000), new(new(3, 5), 500_000, 500_000)],
            [new(0, 5)], [Source(0, 5)]);
        Assert.Equal(4, Assert.Single(missing.ReadBirths(new(0, 5), new())).BirthUs);
        var negativeZero = Compile([new(new(0, 5), -0d, -0d)], [new(0, 5)], [Source(0, 5)]);
        Assert.Empty(negativeZero.ReadBirths(new(0, 5), new()));
    }

    [Fact]
    public void 相接段整阈值在零率左端兑现一次且保留余量()
    {
        var schedule = Compile([new(new(0, 1), 1_000_000, 1_000_000), new(new(1, 3), 0, 0), new(new(3, 5), 1_000_000, 1_000_000)],
            [new(0, 5)], [Source(0, 1, "first"), Source(1, 3, "zero"), Source(3, 5, "third")]);
        var births = schedule.ReadBirths(new(0, 5), new());
        Assert.Equal([1L, 4], births.Select(static birth => birth.BirthUs));
        Assert.Equal(["zero", "third"], births.Select(static birth => Assert.IsType<ParticleSource.Cursor>(birth.Source).Identity.TokenId!.Value.Value));
    }

    [Fact]
    public void 末端最后微秒之后才达阈值与一微秒episode均不出生()
    {
        var fractional = Compile([new(new(0, 2), 750_000, 750_000)], [new(0, 2)], [Source(0, 2)]);
        Assert.Empty(fractional.ReadBirths(new(0, 2), new())); // Q(1)=.75，Q(2)=1.5，但 2 被排除。
        var one = Compile([new(new(0, 1), 10_000_000, 10_000_000)], [new(0, 1)], [Source(0, 1)]);
        Assert.Empty(one.ReadBirths(new(0, 1), new()));
        var many = Compile([new(new(0, 4), 2_000_000, 2_000_000)], [new(0, 4)], [Source(0, 4)]);
        Assert.Equal([1L, 1, 2, 2, 3, 3], many.ReadBirths(new(0, 4), new()).Select(static birth => birth.BirthUs));
    }

    [Fact]
    public void 连续换词传递配额而一微秒gap和显式停用重置()
    {
        RateSegment[] rate = [new(new(0, 6), 500_000, 500_000)];
        var continuous = Compile(rate, [new(0, 6)], [Source(0, 1, "first"), Source(1, 6, "next")]);
        Assert.Equal([2L, 4], continuous.ReadBirths(new(0, 6), new()).Select(static birth => birth.BirthUs));
        Assert.All(continuous.ReadBirths(new(0, 6), new()), static birth => Assert.Equal("next", Assert.IsType<ParticleSource.Cursor>(birth.Source).Identity.TokenId!.Value.Value));
        var gap = Compile(rate, [new(0, 6)], [Source(0, 1), Source(2, 6, "after-gap")]);
        var disabled = Compile(rate, [new(0, 1), new(2, 6)], [Source(0, 6)]);
        Assert.Equal([4L], gap.ReadBirths(new(0, 6), new()).Select(static birth => birth.BirthUs));
        Assert.Equal([4L], disabled.ReadBirths(new(0, 6), new()).Select(static birth => birth.BirthUs));
        Assert.Equal(2, Assert.IsType<ParticleOrigin.Continuous>(Assert.Single(gap.ReadBirths(new(0, 6), new())).Id.Origin).EpisodeStartUs);
    }

    [Fact]
    public void 相接启用窗合并而reload装载保持新head()
    {
        var enabled = Compile([new(new(0, 6), 500_000, 500_000)], [new(0, 1), new(1, 6)], [Source(0, 6)]);
        Assert.Equal([2L, 4], enabled.ReadBirths(new(0, 6), new()).Select(static birth => birth.BirthUs));
        var reload = Compile([new(new(0, 6), 500_000, 500_000)], [new(0, 6)],
            [Source(0, 1), Source(1, 6, binding: "binding/reload/1")]);
        var births = reload.ReadBirths(new(0, 6), new());
        Assert.Equal([3L, 5], births.Select(static birth => birth.BirthUs));
        Assert.All(births, static birth => Assert.Equal("binding/reload/1", Assert.IsType<ParticleHead.Normal>(
            Assert.IsType<ParticleOrigin.Continuous>(birth.Id.Origin).Head).Binding.Value));
    }

    [Fact]
    public void 普通头桥接和每个合法后继独立计数且不压缩身份()
    {
        var from = Source(0, 2, "from"); var toA = Source(6, 9, "to-a", "next-a"); var toB = Source(6, 9, "to-b", "next-b");
        var first = Bridge(from, toA); var second = Bridge(from, toB);
        var plan = Plan([new(new(0, 9), 500_000, 500_000)], [new(0, 9)]) with
        { Bridge = new(TransitionMode.Bridge, 4, 10, new CurveValue.Easing(EasingKind.Linear)) };
        var schedule = CompiledParticleSchedule.Compile(new([plan], []), new([from, first, second, toA, toB], []), Owner);
        var births = schedule.ReadBirths(new(0, 9), new());
        Assert.Equal([4L, 4, 8, 8], births.Select(static birth => birth.BirthUs));
        Assert.Equal(4, births.Select(static birth => birth.Id).Distinct().Count());
        Assert.Equal(2, births.Count(static birth => Assert.IsType<ParticleOrigin.Continuous>(birth.Id.Origin).Head is ParticleHead.Bridge));
    }

    [Fact]
    public void 相同head并发Current明确拒绝而不同head并发合法()
    {
        var plan = new ParticleSchedule([Plan([new(new(0, 10), 500_000, 500_000)], [new(0, 10)])], []);
        var error = Assert.Throws<EffectFailureException>(() => CompiledParticleSchedule.Compile(plan,
            new([Source(0, 6, "a"), Source(2, 8, "b")], []), Owner));
        Assert.Equal("EmissionHeadAmbiguous", error.Code); Assert.Equal(Owner.InstanceId, error.InstanceId);
        var concurrent = CompiledParticleSchedule.Compile(plan, new([Source(0, 6, "a"), Source(2, 8, "b", "other")], []), Owner);
        Assert.Equal(4, concurrent.ReadBirths(new(0, 10), new()).Length);
    }

    [Fact]
    public void 出生闭窗保留死亡记录death等于当前时刻不再存活()
    {
        var schedule = Compile([new(new(0, 10), 1_000_000, 1_000_000)], [new(0, 10)], [Source(0, 10)], lifetime: 2);
        var history = schedule.ReadBirths(new(1, 3), new());
        Assert.Equal([1L, 2, 3], history.Select(static birth => birth.BirthUs));
        Assert.Equal([2L, 3], schedule.ReadAlive(3, new()).Select(static birth => birth.BirthUs));
        Assert.Equal(3, history[0].DeathUs);
    }

    [Fact]
    public void Burst按真实同边界事件优先级排序而非数组或发射器顺序()
    {
        var source = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(source, ReplayReason.Initial);
        var started = timeline.Events.OfType<TokenStarted>().Single(static row => row.TokenId.Value == "b");
        var completed = timeline.Events.OfType<TokenCompleted>().Single(static row => row.TokenId.Value == "a");
        var binding = timeline.Events.OfType<BindingEntered>().First().Binding.BindingId;
        var startSource = new ParticleSource.Token(new(started.TokenId, binding));
        var endSource = new ParticleSource.Token(new(completed.TokenId, binding));
        var startPlan = new EventBurstPlan(new("a-start"), new(TimelineEventKind.TokenStarted),
            [new(started.EventId, 2, new BurstAnchor.SourceToken(started.TokenId), Spec(2))]);
        var endPlan = new EventBurstPlan(new("z-end"), new(TimelineEventKind.TokenCompleted),
            [new(completed.EventId, 2, new BurstAnchor.SourceToken(completed.TokenId), Spec(2))]);
        var schedule = CompiledParticleSchedule.Compile(new([], [startPlan, endPlan]), new([],
            [new(startPlan.Id, 0, started, startSource), new(endPlan.Id, 1, completed, endSource)]), Owner);
        var births = schedule.ReadBirths(new(2000, 2000), new());
        Assert.Equal(["z-end", "z-end", "a-start", "a-start"], births.Select(static birth => birth.Id.EmitterId.Value));
        Assert.Equal([0, 1, 0, 1], births.Select(static birth => Assert.IsType<ParticleOrigin.Burst>(birth.Id.Origin).ParticleOrdinal));
        Assert.Empty(schedule.ReadAlive(2002, new()));
        Assert.Equal(4, schedule.ReadBirths(new(2000, 2010), new()).Length);
    }

    [Fact]
    public void 长seek只枚举寿命邻域且逆序冷重建完全一致()
    {
        const long query = 900_000_000_000;
        RateSegment[] rates = [new(new(0, 1_000_000_000_000), 60, 60)];
        var schedule = Compile(rates, [new(0, 1_000_000_000_000)], [Source(0, 1_000_000_000_000)], lifetime: 5_000_000);
        var births = schedule.ReadBirths(new(query - 5_000_000, query), new(birthLimit: 350, inversionLimit: 20_000));
        Assert.Equal(301, births.Length); Assert.Equal(query - 5_000_000, births[0].BirthUs); Assert.Equal(query, births[^1].BirthUs);
        Assert.Equal(300, schedule.ReadAlive(query, new(aliveLimit: 350, inversionLimit: 20_000)).Length);
        schedule.ReadBirths(new(0, 50_000), new());
        Assert.Equal<ScheduledBirth>(births, schedule.ReadBirths(new(query - 5_000_000, query), new()));
        var cold = Compile(rates, [new(0, 1_000_000_000_000)], [Source(0, 1_000_000_000_000)], lifetime: 5_000_000);
        Assert.Equal<ScheduledBirth>(births, cold.ReadBirths(new(query - 5_000_000, query), new()));
    }

    [Fact]
    public void 数量反解位数内存取消与时间身份溢出各自拒绝且归属固定()
    {
        var schedule = Compile([new(new(0, 100_000), 4_000_000, 4_000_000)], [new(0, 100_000)], [Source(0, 100_000)]);
        Assert.Equal("EmissionRecordBudgetExceeded", Assert.Throws<EffectFailureException>(() =>
            schedule.ReadBirths(new(0, 99_999), new(inversionLimit: 1))).Code);
        Assert.Equal("LiveParticleBudgetExceeded", Assert.Throws<EffectFailureException>(() => schedule.ReadAlive(10, new(aliveLimit: 2))).Code);
        Assert.Equal("ParticleInversionBudgetExceeded", Assert.Throws<EffectFailureException>(() => schedule.ReadBirths(new(1, 1), new(inversionLimit: 1))).Code);
        Assert.Equal("ParticleFrameMemoryExceeded", Assert.Throws<EffectFailureException>(() => schedule.ReadBirths(new(1, 1), new(byteLimit: 1))).Code);
        var tiny = new ParticleSchedule([Plan([new(new(0, 10), double.Epsilon, double.Epsilon)], [new(0, 10)])], []);
        Assert.Equal("ParticleIntegerBudgetExceeded", Assert.Throws<EffectFailureException>(() =>
            CompiledParticleSchedule.Compile(tiny, new([Source(0, 10)], []), Owner, new(maximumBits: 64))).Code);
        Assert.Equal("ParticlePrepareMemoryExceeded", Assert.Throws<EffectFailureException>(() =>
            CompiledParticleSchedule.Compile(tiny, new([Source(0, 10)], []), Owner, new(byteLimit: 1))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => schedule.ReadBirths(new(0, 10), new(), cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => CompiledParticleSchedule.Compile(tiny, EmissionSourceCatalog.Empty, Owner, cancellationToken: cancel.Token));
        Assert.Equal(4, schedule.ReadBirths(new(1, 1), new()).Length);
        Assert.Equal("ParticleTimeOrIdentityOverflow", Overflow(3_000_000, lifetime: 1).Code);
        Assert.Equal("ParticleTimeOrIdentityOverflow", Overflow(2_000_000, lifetime: 3).Code);
        EffectFailureException Overflow(double rate, long lifetime)
            => Assert.Throws<EffectFailureException>(() => Compile([new(new(0, long.MaxValue), rate, rate)],
                [new(0, long.MaxValue)], [Source(0, long.MaxValue)], lifetime));
    }

    [Fact]
    public void 预算跨实例共享且超额仍归实际第二所有者()
    {
        var plan = new ParticleSchedule([Plan([new(new(0, 10), 2_000_000, 2_000_000)], [new(0, 10)])], []);
        var catalog = new EmissionSourceCatalog([Source(0, 10)], []);
        var first = CompiledParticleSchedule.Compile(plan, catalog, Owner);
        var otherOwner = Owner with { InstanceId = new("second"), PackageIdentity = new("other", "sha256:" + new string('b', 64), "export") };
        var second = CompiledParticleSchedule.Compile(plan, catalog, otherOwner);
        var budget = new ParticleQueryBudget(birthLimit: 3);
        Assert.Equal(2, first.ReadBirths(new(1, 1), budget).Length);
        var error = Assert.Throws<EffectFailureException>(() => second.ReadBirths(new(1, 1), budget));
        Assert.Equal("EmissionRecordBudgetExceeded", error.Code); Assert.Equal(otherOwner.InstanceId, error.InstanceId);
        Assert.Equal(otherOwner.PackageIdentity, error.PackageIdentity);
        var prepare = new ParticlePrepareBudget(intervalLimit: 1, episodeLimit: 1);
        Assert.Equal("ParticlePrepareBudgetExceeded", Assert.Throws<EffectFailureException>(() =>
            CompiledParticleSchedule.Compile(plan, catalog, Owner, prepare)).Code);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void 无效double必须在精确解码前拒绝(double rate)
    {
        var error = Assert.Throws<EffectFailureException>(() => Compile([new(new(0, 10), rate, rate)], [new(0, 10)], [Source(0, 10)]));
        Assert.Equal("InvalidParticleRate", error.Code); Assert.Equal("PrepareParticles", error.Stage);
        Assert.Equal(Owner.PackageIdentity, error.PackageIdentity);
    }

    private static CompiledParticleSchedule Compile(RateSegment[] rates, TimeRange[] enabled,
        EmissionSourceInterval[] sources, long lifetime = 10)
        => CompiledParticleSchedule.Compile(new([Plan([.. rates], [.. enabled], lifetime)], []), new([.. sources], []), Owner);
    private static CursorEmissionPlan Plan(ImmutableArray<RateSegment> rates, ImmutableArray<TimeRange> enabled, long lifetime = 10)
        => new(Emitter, new(rates, enabled), Spec(lifetime), CursorAnchorMode.Baseline, BridgePolicy.Jump);
    private static ParticleSpec Spec(long lifetime) => new() { LifetimeUs = lifetime, SizePx = 2, Color = new(1, .5, 0, 1), SeedStreamKey = "stream" };
    private static EmissionSourceInterval Source(long start, long end, string token = "token", string binding = "binding")
    {
        var identity = new CursorIdentity(new("branch"), new("line"), new(binding), new(start, end), ProgressSource.Token, new(token), new("occurrence-" + token));
        var source = new ParticleSource.Cursor(new("track"), identity);
        return new(Emitter, new ParticleHead.Normal(source.Track, source.BindingId), source, new(start, end));
    }
    private static EmissionSourceInterval Bridge(EmissionSourceInterval from, EmissionSourceInterval to)
    {
        var a = Assert.IsType<ParticleSource.Cursor>(from.Source); var b = Assert.IsType<ParticleSource.Cursor>(to.Source);
        var transition = new CursorTransitionEvent(a.Track, a.Identity, b.Identity, b.Identity.TimeRange.StartUs,
            CursorTransitionReason.BindingChange, new(0, 0, 0), ReplayReason.Initial);
        var source = new ParticleSource.Bridge(transition, new(a.Identity.TimeRange.EndUs, b.Identity.TimeRange.StartUs));
        return new(Emitter, new ParticleHead.Bridge(source), source, source.FlightRange);
    }
}
