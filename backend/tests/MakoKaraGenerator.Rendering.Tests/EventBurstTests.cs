using System.Collections.Immutable;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ContinuousParticleTests
{
    private static readonly Lazy<Task<ScriptCompilationResult>> BurstArtifact = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([
            new("src/EventBurst.cs", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "script-samples/EventBurst.cs"))),
            new("src/BurstProbe.cs", """
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public class BurstProbe : EffectBase
            {
                private BurstWriter previous;
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    string mode = context.Parameters.GetEnum("mode");
                    bool trigger = mode == "mark" ? evt is LeadInStarted
                        : mode == "tail" || mode == "empty" ? evt is TokenCompleted && evt.TimeUs == 1400000
                        : evt is TokenStarted;
                    if (!trigger) return;
                    BurstAnchor anchor = mode switch
                    {
                        "cursors" or "empty" => new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter),
                        "line" => new BurstAnchor.SourceLine(((TokenStarted)evt).LineId),
                        "outside" => new BurstAnchor.SourceToken(new("parallel-a")),
                        "unknown" => new BurstAnchor.SourceToken(new("missing")),
                        "notmark" => new BurstAnchor.TextMark(context.Project.Scene.Programs[0].RootNodeId),
                        "mark" => new BurstAnchor.TextMark(context.Project.Scene.Nodes.First(n => n.Kind == SceneNodeKind.Text && n.LineId == null && n.RubyId == null).Id),
                        "tail" => new BurstAnchor.SourceToken(((TokenCompleted)evt).TokenId),
                        _ => new BurstAnchor.SourceToken(((TokenStarted)evt).TokenId)
                    };
                    var desc = new BurstDescriptor { Count = (int)context.Parameters.GetNumber("count"), Anchor = anchor,
                        LifetimeUs = context.Parameters.GetDuration("lifeUs"), SizePx = 10, Color = new(1,0,0,1),
                        SpreadPx = context.Parameters.GetNumber("spreadPx"), TrailDurationUs = context.Parameters.GetDuration("trailUs"),
                        Velocity = new(10,0), SeedStreamKey = "probe" };
                    if (mode == "last") context.Bursts.Queue(new("burst"), desc with { Count = 999 });
                    context.Bursts.Queue(new("burst"), desc); previous = context.Bursts;
                    if (mode == "fatal") context.Diagnostics.Report(DiagnosticSeverity.Error, "burst-fatal", "stop");
                }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (frame.Parameters.GetEnum("mode") == "expired" && previous != null)
                        previous.Queue(new("burst"), new() { Count = 0, Anchor = new BurstAnchor.BindingCursors(CursorAnchorMode.Baseline),
                            LifetimeUs = 1, SizePx = 1, Color = default, SeedStreamKey = "expired" });
                }
            }
            public sealed class BurstCombo : BurstProbe
            {
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
                    => output.Set(new("burst"), new() { AnchorMode = CursorAnchorMode.AdvanceCenter,
                        RatePerSecond = frame.Parameters.GetNumber("rate"), LifetimeUs = frame.Parameters.GetDuration("lifeUs"),
                        SizePx = 5, Color = new(0,0,1,1), SeedStreamKey = "continuous" });
            }
            public sealed class BurstObserver : BurstProbe
            {
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    if (frame.TimeUs == 0 && (frame.Particles.Length != 2 || frame.EmissionSchedule.Births.Length != 0))
                        throw new System.Exception("current particles vs prior interval");
                    if (frame.TimeUs == 8333 && (frame.Particles.Length != 2 || frame.EmissionSchedule.Births.Length != 2))
                        throw new System.Exception("completed interval must contain each birth once");
                }
                public override void AdvanceVisuals(VisualSimulationStep step, VisualStateContext context)
                {
                    if (step.EndUs == 8333 && (context.PreviousParticles.Length != 2 || context.Particles.Length != 2 || context.EmissionSchedule.Births.Length != 2))
                        throw new System.Exception("author full views");
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static async Task<CompiledFrameEffect> BurstEffect(string mode = "token", int count = 2, long life = 2_000_000,
        double spread = 0, long trail = 0, double rate = 60, string entry = "BurstProbe")
        => new(new("burst-effect"), await BurstArtifact.Value, entry, new(new Dictionary<string, Fx.ParameterValue>
        {
            ["mode"] = new Fx.ParameterValue.Choice(mode), ["count"] = new Fx.ParameterValue.Number(count),
            ["lifeUs"] = new Fx.ParameterValue.Duration(life), ["sizePx"] = new Fx.ParameterValue.Number(10),
            ["spreadPx"] = new Fx.ParameterValue.Number(spread), ["trailUs"] = new Fx.ParameterValue.Duration(trail),
            ["rate"] = new Fx.ParameterValue.Number(rate), ["color"] = new Fx.ParameterValue.Color(new(1,0,0,1))
        }), new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Overlay) { Seed = 23 };

    [Fact]
    public async Task 公开起唱源码立即产生真实元素来源且出生归入下一半开区间()
    {
        var snapshot = Snapshot(); var definition = await BurstEffect(entry: "EventBurst");
        await using var source = await Factory([definition]).CreateAsync(snapshot);
        var zero = await Frame(source, snapshot, 0); Assert.Equal(2, zero.Particles.Length); Assert.Empty(zero.EmissionSchedule!.Births);
        var evt = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial).Events.OfType<Fx.TokenStarted>().First();
        Assert.All(zero.Particles, particle =>
        {
            Assert.Equal(evt.EventId, particle.EventId); Assert.Null(particle.TrackId);
            Assert.Equal("first-a", Assert.IsType<Fx.ParticleSource.Token>(particle.Source).Target.TokenId.Value);
        });
        Assert.Equal(Values(zero), Values(await Frame(source, snapshot, 0)));
        var next = await Frame(source, snapshot, 8_333); Assert.Equal(2, next.EmissionSchedule!.Births.Length);
        Assert.All(next.EmissionSchedule.Births, birth => { Assert.Equal(0, birth.BirthUs); Assert.Equal(evt.EventId, birth.EventId); });
        var second = await Frame(source, snapshot, 700_000); Assert.Equal(4, second.Particles.Length);
        Assert.Equal(4, second.Particles.Select(p => p.Id).Distinct().Count());
        Assert.DoesNotContain(second.EmissionSchedule!.Births, birth => birth.BirthUs == 700_000);
        Assert.Equal(2, (await Frame(source, snapshot, 708_333)).EmissionSchedule!.Births.Count(b => b.BirthUs == 700_000));
        Assert.NotEqual(Pixels(zero), Pixels(zero.WithVisualOutput([], zero.Diagnostics, particles: [])));
    }
    [Theory]
    [InlineData("token", 4)] [InlineData("last", 4)] [InlineData("line", 4)] [InlineData("cursors", 8)]
    public async Task 同事件最后登记且多个事件与锚点按真实顺序各自独立(string mode, int count)
    {
        var snapshot = Snapshot(parallel: true); await using var source = await Factory([await BurstEffect(mode)]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 0); Assert.Equal(count, frame.Particles.Length);
        Assert.Equal(count / 2, frame.Particles.GroupBy(p => p.BindingId).First().Count());
        Assert.All(frame.Particles, p => Assert.Equal(mode == "cursors", p.TrackId.HasValue));
        var timeline = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial);
        var events = timeline.Events.OfType<Fx.TokenStarted>().Where(e => e.TimeUs == 0).Select(e => e.EventId).ToArray();
        Assert.Equal(events, frame.Particles.Select(p => p.EventId!.Value).Distinct().ToArray());
        Assert.Equal(Enumerable.Range(0, count).Select(i => (ulong)i), frame.Particles.Select(p => p.Id.BirthIndex));
    }
    [Fact]
    public async Task 同刻Burst先于连续到期出生共享身份而不改变配额()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await BurstEffect(count: 1, rate: 60, entry: "BurstCombo")]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 700_000);
        var at = frame.Particles.Where(p => p.BirthUs == 700_000).ToArray(); Assert.Equal(2, at.Length);
        Assert.NotNull(at[0].EventId); Assert.Null(at[1].EventId); Assert.Equal(at[0].Id.BirthIndex + 1, at[1].Id.BirthIndex);
        Assert.Equal(42, frame.Particles.Count(p => p.EventId is null)); Assert.Equal(2, frame.Particles.Count(p => p.EventId is not null));
        Assert.Equal(44, frame.Particles.Select(p => p.Id).Distinct().Count());
    }
    [Fact]
    public async Task 短命Burst在死亡末端消失但完整出生记录仍保留()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await BurstEffect(life: 1)]).CreateAsync(snapshot);
        Assert.Equal(2, (await Frame(source, snapshot, 0)).Particles.Length);
        Assert.Empty((await Frame(source, snapshot, 1)).Particles);
        var end = await Frame(source, snapshot, 8_333); Assert.Empty(end.Particles); Assert.Equal(2, end.EmissionSchedule!.Births.Length);
        Assert.All(end.EmissionSchedule.Births, b => Assert.Equal(1, b.DeathUs));
    }
    [Theory]
    [InlineData("tail", "BurstAnchorUnavailable")] [InlineData("unknown", "UnknownBurstSource")]
    [InlineData("notmark", "UnknownBurstSource")] [InlineData("expired", "InvalidBurstOutputStage")]
    public async Task 无显式锚点或非法来源及过期writer带真实实例失败(string mode, string code)
    {
        var snapshot = Snapshot(); await using var source = await Factory([await BurstEffect(mode)]).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, mode == "tail" ? 1_400_000 : 0));
        Assert.Equal(code, error.Code); Assert.Equal("burst-effect", error.InstanceId.Value);
    }
    [Fact]
    public async Task 空播放头合法零出生而扩大订阅不扩大输出绑定()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await BurstEffect("empty")]).CreateAsync(snapshot);
        Assert.Empty((await Frame(source, snapshot, 1_400_000)).Particles);
        var parallel = Snapshot(parallel: true); var definition = await BurstEffect("outside");
        definition = definition with { Binding = new Fx.EffectBinding.Branch(new("primary")), Subscription = new Fx.SourceSubscription.Project() };
        await using var scoped = await Factory([definition]).CreateAsync(parallel);
        Assert.Equal("BurstTargetOutsideBinding", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(scoped, parallel, 0))).Code);
    }
    [Fact]
    public async Task 作者视觉阶段读取本帧全部Burst和上一完成区间()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await BurstEffect(entry: "BurstObserver")]).CreateAsync(snapshot);
        Assert.Equal(2, (await Frame(source, snapshot, 8_333)).Particles.Length);
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task Burst拖尾重放跳转暂停与导出会话和不同fps同像素(int fps)
    {
        var snapshot = Snapshot(twoRows: true); var definition = await BurstEffect(spread: 6, trail: 200_000);
        await using var direct = await Factory([definition], reason: Fx.ReplayReason.Export).CreateAsync(snapshot);
        var expected = await Frame(direct, snapshot, 600_001);
        await using var source = await Factory([definition]).CreateAsync(snapshot);
        for (long n = 0; n * 1_000_000 / fps < expected.TimeUs; n++) await Frame(source, snapshot, n * 1_000_000 / fps);
        var frame = await Frame(source, snapshot, expected.TimeUs); Assert.Equal(Values(expected), Values(frame)); Assert.Equal(Pixels(expected), Pixels(frame));
        Assert.Equal(Values(frame), Values(await Frame(source, snapshot, expected.TimeUs)));
        await Frame(source, snapshot, 1); Assert.Equal(Values(expected), Values(await Frame(source, snapshot, expected.TimeUs)));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.PrepareAsync(1_000_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), canceled.Token));
        Assert.Equal(Values(expected), Values(await Frame(source, snapshot, expected.TimeUs)));
    }
    [Fact]
    public async Task 所有实例出生预算覆盖Burst和连续出生且错误清理后可重建()
    {
        var snapshot = Snapshot(); var definition = await BurstEffect(count: 3);
        await using var source = await Factory([definition, definition with { Id = new("second") }], new(100,100,5)).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 0));
        Assert.Equal("EmissionRecordBudgetExceeded", error.Code); Assert.Equal("second", error.InstanceId.Value);
        await using var combo = await Factory([await BurstEffect(count: 1, life: 1, rate: 1000, entry: "BurstCombo")], new(100,100,4)).CreateAsync(snapshot);
        Assert.Equal("EmissionRecordBudgetExceeded", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(combo, snapshot, 4_000))).Code);
        Assert.Empty((await Frame(combo, snapshot, 3_001)).Particles);
    }
    [Fact]
    public async Task Burst使用全实例最终文字几何且独立端点不分配粒子()
    {
        var snapshot = Snapshot(); var burst = await BurstEffect();
        var move = new CompiledFrameEffect(new("move"), await Artifact.Value, "Movement", Fx.ParameterValues.Empty,
            new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms);
        await using var source = await Factory([burst, move]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 700_000);
        var target = frame.FinalGeometry.Single(t => t.WithRuby.TokenId.Value == "first-b");
        var center = target.BaseGeometry.BaseWorldMatrix.Map(target.BaseGeometry.AdvanceBounds.Center);
        Assert.All(frame.Particles.Where(p => p.BirthUs == 700_000), p => Assert.Equal(center, p.BirthPosition));
        await using var anchor = await Factory([await BurstEffect("tail")], reason: Fx.ReplayReason.AnchorQuery).CreateAsync(snapshot);
        Assert.Empty((await Frame(anchor, snapshot, 1_400_000)).Particles);
        PureFrameSourceFactory pure = new([burst], Fingerprint, static () => new SkiaRenderPipeline());
        var failure = await Assert.ThrowsAsync<EffectFailureException>(() => pure.CreateAsync(snapshot).AsTask());
        Assert.Equal("TimelineSessionRequired", failure.Code);
    }
    [Fact]
    public async Task Burst声明能力包输出和作者Error均为求值失败()
    {
        var snapshot = Snapshot(); var definition = await BurstEffect();
        await using var denied = await Factory([definition with { Capabilities = Fx.EffectCapabilities.None }]).CreateAsync(snapshot);
        Assert.Equal("UndeclaredCapability", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(denied, snapshot, 0))).Code);
        var undeclared = definition with { PackageDeclaration = EffectPackageFixture.WordEmphasis().GetController("word-emphasis") };
        await using var package = await Factory([undeclared]).CreateAsync(snapshot);
        Assert.Equal("UndeclaredPackageOutput", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(package, snapshot, 0))).Code);
        await using var fatal = await Factory([await BurstEffect("fatal")]).CreateAsync(snapshot);
        Assert.Equal("burst-fatal", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(fatal, snapshot, 0))).Code);
        await using var zero = await Factory([await BurstEffect(count: 0)], new(1,1,1)).CreateAsync(snapshot);
        var frame = await Frame(zero, snapshot, 8_333); Assert.Empty(frame.Particles); Assert.Empty(frame.EmissionSchedule!.Births);
    }
    [Theory]
    [InlineData("live", "LiveParticleBudgetExceeded")] [InlineData("trail", "TrailSampleBudgetExceeded")]
    [InlineData("draw", "VisualOutputBudgetExceeded")]
    public async Task Burst存活拖尾和绘制预算均在分配或绘制前失败(string kind, string code)
    {
        var snapshot = Snapshot(); var definition = await BurstEffect(trail: kind == "trail" ? 100_000 : 0);
        await using var source = await Factory([definition], new(kind == "live" ? 1 : 100, kind == "trail" ? 1 : 100),
            new(kind == "draw" ? 1 : 100)).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 0));
        Assert.Equal(code, error.Code); Assert.Equal(0, error.TimeUs); Assert.Equal(definition.Id, error.InstanceId);
    }
    [Fact]
    public async Task 前奏提前可见不制造起唱Burst()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Factory([await BurstEffect(entry: "EventBurst")]).CreateAsync(snapshot);
        Assert.Empty((await Frame(source, snapshot, 9_000_000)).Particles);
        Assert.Equal(2, (await Frame(source, snapshot, 12_000_000)).Particles.Length);
    }
    [Fact]
    public async Task 标记Burst保留真实标记来源不伪造轨道并跟随最终父树()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Factory([await BurstEffect("mark")]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 9_000_000); Assert.Equal(2, frame.Particles.Length);
        Assert.All(frame.Particles, p =>
        {
            var mark = Assert.IsType<Fx.ParticleSource.TextMark>(p.Source); Assert.Null(p.TrackId);
            Assert.Equal("cue-first", mark.Target.CueId.Value);
            var geometry = frame.FinalMarkGeometry.Single(m => m.Target == mark.Target).Geometry;
            Assert.Equal(geometry.BaseWorldMatrix.Map(geometry.AdvanceBounds.Center), p.BirthPosition);
        });
    }
}
