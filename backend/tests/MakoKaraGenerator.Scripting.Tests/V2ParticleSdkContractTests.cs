using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2ParticleSdkContractTests
{
    private static readonly EffectInstanceId Owner = new("pure-particles");
    private static readonly EmitterId Emitter = new("cursor");
    private static readonly ImmutableArray<string> Capabilities = ["particles.continuous", "particles.burst", "visuals.dots", "visuals.segments", "visuals.trails", "visuals.sprites"];

    [Fact]
    public void 准备入口固定非虚init属性且所有新阶段由宿主创建()
    {
        foreach (string name in new[] { "Particles", "GeometrySampling", "VisualOutputs" })
        {
            var property = typeof(PreparedEffect).GetProperty(name)!;
            Assert.False(property.GetMethod!.IsVirtual);
            Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }
        Assert.Empty(typeof(VisualFrame).GetConstructors()); Assert.Empty(typeof(ParticleFrame).GetConstructors()); Assert.Empty(typeof(VisualOutput).GetConstructors());
        Assert.Null(typeof(VisualOutput).GetMethod("Complete")); Assert.Null(typeof(ParticleFrame).GetMethod("Invalidate"));
        Assert.Null(typeof(BurstEventRow).GetProperty("TimeUs"));
    }

    [Fact]
    public void 零率保留启用窗且声明允许未使用的清单发射器()
    {
        var plan = Plan() with { Rate = new([new(new(0, 100), 0, 0), new(new(100, 200), 0, 10)], [new(0, 200)]) };
        Validate(new Data { Particles = new([plan], []) }, emitters: [new(Emitter, "cursor", 500_000), new(new("unused"), "burst", 1)]);
    }

    [Theory]
    [InlineData("undeclared", "UndeclaredEmitter")]
    [InlineData("wrong-kind", "UndeclaredEmitter")]
    [InlineData("duplicate", "UndeclaredEmitter")]
    [InlineData("lifetime", "ParticleLifetimeExceeded")]
    [InlineData("missing-output", "UndeclaredPackageOutput")]
    [InlineData("output-capability", "UndeclaredPackageOutput")]
    [InlineData("package-capability", "UndeclaredCapability")]
    [InlineData("overlap", "InvalidParticleRate")]
    [InlineData("nonfinite", "InvalidParticleRate")]
    [InlineData("disabled-default", "InvalidParticleDeclaration")]
    [InlineData("bridge-zero", "InvalidParticleDeclaration")]
    public void Declare后二次验证不能以with绕过清单与封闭数值(string failure, string code)
    {
        var plan = Plan(); ImmutableArray<CursorEmissionPlan> plans = [plan];
        ImmutableArray<ParticleEmitterDeclaration> emitters = [new(Emitter, "cursor", 500_000)];
        var outputs = Outputs(); var capabilities = Capabilities;
        switch (failure)
        {
            case "undeclared": emitters = []; break;
            case "wrong-kind": emitters = [new(Emitter, "burst", 500_000)]; break;
            case "duplicate": plans = [plan, plan]; break;
            case "lifetime": emitters = [new(Emitter, "cursor", 499_999)]; break;
            case "missing-output": outputs = []; break;
            case "output-capability": outputs = [new(new("cursor"), ["particles.continuous"])]; break;
            case "package-capability": capabilities = ["particles.burst", "visuals.dots"]; break;
            case "overlap": plans = [plan with { Rate = new([new(new(0, 100), 1, 1), new(new(99, 200), 1, 1)], [new(0, 200)]) }]; break;
            case "nonfinite": plans = [plan with { Rate = new([new(new(0, 100), double.NaN, 1)], [new(0, 100)]) }]; break;
            case "disabled-default": plans = [plan with { Rate = new([], default) }]; break;
            case "bridge-zero": plans = [plan with { Bridge = new(TransitionMode.Bridge, 0, 100, BridgePolicy.Jump.Curve) }]; break;
        }
        Assert.Equal(code, Assert.Throws<EffectOutputException>(() => Validate(new Data { Particles = new(plans, []) }, capabilities, emitters, outputs)).Code);
    }

    [Fact]
    public void 原始粒子寿命与速率集合总预算独立拒绝()
    {
        Assert.Equal("ParticleLifetimeExceeded", Assert.Throws<EffectOutputException>(() => (Spec() with { LifetimeUs = 5_000_001 }).Validate()).Code);
        var windows = Enumerable.Range(0, 4097).Select(index => new TimeRange(index * 2, index * 2 + 1)).ToImmutableArray();
        var data = new Data { Particles = new([Plan() with { Rate = new([], windows) }], []) };
        Assert.Equal("ParticlePrepareBudgetExceeded", Assert.Throws<EffectOutputException>(() => Validate(data)).Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PreparedEffectDeclaration.Validate(new Data(), Source(), Capabilities, [], [], canceled.Token));
    }

    [Fact]
    public void Burst必须重取selector中的真实事件且同发射器绘制策略固定()
    {
        var first = Start("one", 0); var second = Start("two", 100);
        var source = Source(new([first, second])); var id = new EmitterId("burst");
        var selector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Token(first.TokenId));
        var row = new BurstEventRow(first.EventId, 2, new BurstAnchor.SourceToken(first.TokenId), Spec());
        Data DataWith(ImmutableArray<BurstEventRow> rows) => new() { Particles = new([], [new(id, selector, rows)]) };
        void Check(Data data) => PreparedEffectDeclaration.Validate(data, source, Capabilities,
            [new(id, "burst", 500_000)], [new(new("burst"), ["particles.burst", "visuals.dots"])]);
        Check(DataWith([row]));
        Assert.Equal("InvalidParticleDeclaration", Assert.Throws<EffectOutputException>(() => Check(DataWith([row with { EventId = second.EventId }]))).Code);
        Assert.Equal("InvalidParticleDeclaration", Assert.Throws<EffectOutputException>(() => Check(DataWith([row, row]))).Code);
        var all = new TimelineSelector(TimelineEventKind.TokenStarted);
        var mixed = DataWith([row, row with { EventId = second.EventId, Particle = Spec() with { Rendering = ParticleRenderMode.Custom } }]);
        mixed = mixed with { Particles = new([], [mixed.Particles.Bursts[0] with { Selector = all }]) };
        Assert.Equal("ParticleEmitterRenderPolicyMismatch", Assert.Throws<EffectOutputException>(() => Check(mixed)).Code);
    }

    [Fact]
    public void Custom保留同名输出且手工声明不能占用任何清单发射器身份()
    {
        var custom = new Data { Particles = new([Plan() with { Particle = Spec() with { Rendering = ParticleRenderMode.Custom } }], []),
            VisualOutputs = [new(new("manual"), VisualPrimitiveKind.Segments, OverlayLayer.AboveText)] };
        Validate(custom, outputs: [new(new("cursor"), ["particles.continuous"]), new(new("manual"), ["visuals.segments"])]);
        var collision = custom with { VisualOutputs = [new(new("cursor"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
        Assert.Equal("InvalidVisualDeclaration", Assert.Throws<EffectOutputException>(() => Validate(collision)).Code);
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => Validate(custom, outputs: [new(new("manual"), ["visuals.segments"])] )).Code);
    }

    [Fact]
    public void 采样声明拒绝默认集合重叠时间窗与重复断点()
    {
        foreach (var sampling in new GeometrySamplingPolicy[]
        {
            new(GeometrySamplingMode.Continuous120Hz, default, []),
            new(GeometrySamplingMode.Continuous120Hz, [new(0, 10), new(9, 20)], []),
            new(GeometrySamplingMode.Continuous120Hz, [], [10, 10]),
            new((GeometrySamplingMode)999, [], []),
        })
            Assert.Equal("InvalidParticleDeclaration", Assert.Throws<EffectOutputException>(() => Validate(new Data { GeometrySampling = sampling })).Code);
    }

    [Fact]
    public void 出生窗包含已死亡记录且存活副本必须与完整出生记录一致()
    {
        var live = Birth(0); var dead = Birth(1) with { DeathUs = 10, Spec = Spec() with { LifetimeUs = 10 } };
        var view = View(live);
        var frame = new ParticleFrame(Owner, 100, [view], new(new(0, 100), [live, dead]));
        Assert.Equal(2, frame.Births.Births.Length); Assert.Single(frame.Alive);
        var altered = view with { Birth = live with { BirthPosition = new(1, 0) } };
        Assert.Equal("InvalidParticleFrame", Assert.Throws<EffectOutputException>(() => new ParticleFrame(Owner, 100, [altered], new(new(0, 100), [live]))).Code);
        Assert.Equal("InvalidParticleFrame", Assert.Throws<EffectOutputException>(() => new ParticleFrame(Owner, live.DeathUs, [view], new(new(0, live.DeathUs), [live]))).Code);
        frame.Invalidate(); Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => frame.Alive).Code);
    }

    [Fact]
    public void 结构身份保留装载且错误episode与外实例查询拒绝()
    {
        var birth = Birth(0); var other = birth.Id with { InstanceId = new("other") };
        var frame = new ParticleFrame(Owner, 100, [View(birth)], new(new(0, 100), [birth]));
        Assert.Equal("ParticleOutsideInstance", Assert.Throws<EffectOutputException>(() => frame.GetTrail(other)).Code);
        var forged = birth with { Id = birth.Id with { Origin = new ParticleOrigin.Continuous(new ParticleHead.Normal(new("track"), new("other-binding")), 0, 1) } };
        Assert.Equal("InvalidParticleFrame", Assert.Throws<EffectOutputException>(() => new ParticleFrame(Owner, 100, [], new(new(0, 100), [forged]))).Code);
        Assert.Equal(new BirthWindow(long.MaxValue, long.MaxValue), new BirthWindow(long.MaxValue, long.MaxValue));
        Assert.True(new BirthWindow(long.MaxValue, long.MaxValue).Contains(long.MaxValue));
    }

    [Fact]
    public void 拖尾查询缓存同身份且按帧累计所有唯一节点()
    {
        var first = Birth(0); var second = Birth(1); int calls = 0;
        var samples = Enumerable.Range(0, 100_001).Select(index => new ParticleTrailSample(index, new(index, 0))).ToImmutableArray();
        var frame = new ParticleFrame(Owner, 150_000, [View(first), View(second)], new(new(0, 150_000), [first, second]), _ => { calls++; return samples; });
        Assert.Equal(samples, frame.GetTrail(first.Id)); Assert.Equal(samples, frame.GetTrail(first.Id)); Assert.Equal(1, calls);
        Assert.Equal("ParticleTrailBudgetExceeded", Assert.Throws<EffectOutputException>(() => frame.GetTrail(second.Id)).Code);
        var unsorted = new ParticleFrame(Owner, 100, [View(first)], new(new(0, 100), [first]), _ => [new(10, default), new(10, default)]);
        Assert.Equal("InvalidParticleTrail", Assert.Throws<EffectOutputException>(() => unsorted.GetTrail(first.Id)).Code);
    }

    [Fact]
    public void 晚期输出核验能力类型图层唯一名字预算与关闭阶段()
    {
        ImmutableArray<VisualOutputDeclaration> declarations = [new(new("dots"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)];
        var dot = new Dot(default, 1, new(1, 1, 1, 1));
        var missing = new VisualOutput(Owner, [], declarations, ResourceCatalog.Empty);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => missing.DrawDots(new("dots"), [dot], OverlayLayer.AboveText)).Code);
        var output = new VisualOutput(Owner, Capabilities, declarations, ResourceCatalog.Empty, new(1));
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => output.DrawDots(new("dots"), [dot], OverlayLayer.BehindText)).Code);
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => output.DrawSegments(new("dots"), [], OverlayLayer.AboveText)).Code);
        Assert.Equal("VisualOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => output.DrawDots(new("dots"), [dot, dot], OverlayLayer.AboveText)).Code);
        output.DrawDots(new("dots"), [dot], OverlayLayer.AboveText);
        Assert.Equal("DuplicateOverlayBatch", Assert.Throws<EffectOutputException>(() => output.DrawDots(new("dots"), [], OverlayLayer.AboveText)).Code);
        Assert.Single(output.Complete());
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => output.DrawDots(new("dots"), [], OverlayLayer.AboveText)).Code);
    }

    [Fact]
    public void 晚期精灵拒绝同名另一修订资源并保留完整仿射()
    {
        var resources = new ResourceCatalog("test", "sha256:" + new string('a', 64), [], [], ["spark.png"]);
        var foreign = new ResourceCatalog("test", "sha256:" + new string('b', 64), [], [], ["spark.png"]);
        var output = new VisualOutput(Owner, Capabilities, [new(new("sprites"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText)], resources);
        Assert.Equal("InvalidSpriteResource", Assert.Throws<EffectOutputException>(() => output.DrawSprites(new("sprites"),
            [new(foreign.GetImage("spark.png"), Affine2.Identity, new(1, 1, 1, 1))], OverlayLayer.AboveText)).Code);
        var matrix = new Affine2(-2, 1, 3, 4, 10, 20);
        output.DrawSprites(new("sprites"), [new(resources.GetImage("spark.png"), matrix, new(1, 1, 1, 1))], OverlayLayer.AboveText);
        Assert.Equal(matrix, Assert.Single(Assert.IsType<SpriteBatch>(Assert.Single(output.Complete())).Sprites).UnitToWorld);
    }

    private sealed record Data : PreparedEffect;
    private static ParticleSpec Spec() => new() { LifetimeUs = 500_000, SizePx = 2, Color = new(1, 1, 1, 1), SeedStreamKey = "fixed", TrailDurationUs = 200_000 };
    private static CursorEmissionPlan Plan() => new(Emitter, new([new(new(0, 500_000), 10, 10)], [new(0, 500_000)]), Spec(), CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump);
    private static ImmutableArray<CanvasOutputDeclaration> Outputs() => [new(new("cursor"), ["particles.continuous", "visuals.dots"])];
    private static DeclareContext Source(TimelineIndex? timeline = null)
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        return new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, ResourceCatalog.Empty, 42, timeline ?? TimelineIndex.Empty, new(1920, 1080), Owner);
    }
    private static void Validate(Data data, ImmutableArray<string> capabilities = default,
        ImmutableArray<ParticleEmitterDeclaration> emitters = default, ImmutableArray<CanvasOutputDeclaration> outputs = default)
        => PreparedEffectDeclaration.Validate(data, Source(), capabilities.IsDefault ? Capabilities : capabilities,
            emitters.IsDefault ? [new(Emitter, "cursor", 500_000)] : emitters, outputs.IsDefault ? Outputs() : outputs);
    private static TokenStarted Start(string token, long time)
        => new(new(new(token), new("branch"), new("line"), token, new(time, time + 100), SourceTimingStatus.Confirmed, 0), new(0, 0, 0), ReplayReason.Initial);
    private static ParticleBirth Birth(int index)
    {
        var source = new ParticleSource.Cursor(new("track"), new(new("branch"), new("line"), new("binding/reload/1"), new(0, 500_000), ProgressSource.Line));
        return new(new(Owner, Emitter, new ParticleOrigin.Continuous(new ParticleHead.Normal(source.Track, source.BindingId), 0, (ulong)index + 1)), source,
            null, 0, 500_000, default, default, Spec());
    }
    private static ParticleView View(ParticleBirth birth) => new(birth, birth.BirthPosition, 2, birth.Spec.Color);
}
