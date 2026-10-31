using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ContinuousParticleTests
{
    private const string BridgeProbeSource = """
        using System.Linq;
        using MakoKaraGenerator.Scripting;
        public sealed class BridgeState : EffectBase
        {
            private int starts, steps;
            public override void OnEvent(TimelineEvent evt, EventContext context) { if (evt is TokenStarted) starts++; }
            public override void Advance(SimulationStep step, StateContext context) { steps++; }
            public override void Evaluate(FrameContext frame, FrameWriter output)
            {
                if (starts != frame.History.Records.Count(record => record.Event is TokenStarted))
                    throw new System.Exception("Future replay polluted main history");
                frame.Diagnostics.Report(DiagnosticSeverity.Info, "BridgeStateCount", starts + "/" + steps);
                if (starts >= 3 && frame.Parameters.GetBoolean("fail"))
                    frame.Diagnostics.Report(DiagnosticSeverity.Error, "FutureBridgeError", "future failed");
                foreach (var token in frame.Presentations.TokensInBinding)
                    output.Transforms.ScaleTokenFamily(token.WithRuby, 1 + starts * .3 + steps * .01, Pivot.BaseAdvanceCenter);
            }
        }
        public sealed class BridgeOnly : EffectBase
        {
            public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
            {
                if (frame.TimeUs < 400000 || frame.TimeUs >= 500000) return;
                var p = frame.Parameters;
                output.Set(new("cursor"), new()
                { AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = p.GetNumber("rate"), LifetimeUs = p.GetDuration("lifeUs"),
                  SizePx = 6, Color = new(1,0,0,1), TrailDurationUs = p.GetDuration("trailUs"), SeedStreamKey = "bridge-only",
                  TransitionMode = TransitionMode.Bridge, BridgeDurationUs = p.GetDuration("bridgeUs"), BridgeGapLimitUs = p.GetDuration("bridgeGapLimitUs"),
                  BridgeCurve = p.Ids.Contains("curve") ? p.GetCurve("curve") : Curves.LinearCurve });
            }
        }
        """;
    private static async Task<CompiledFrameEffect> BridgeState(bool fail = false) => new(new("future-state"), await Artifact.Value,
        "BridgeState", new(new Dictionary<string, Fx.ParameterValue> { ["fail"] = new Fx.ParameterValue.Toggle(fail) }),
        new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms);
    [Theory]
    [InlineData(180000, 400000, 5)] [InlineData(45001, 454999, 2)]
    public async Task 桥接独立最终端点与网格内精确配额不伪造Current(long duration, long start, int count)
    {
        var snapshot = Snapshot(twoRows: true); ImmutableArray<CompiledFrameEffect> effects = [await BridgeState(), await Effect(transition: "bridge", bridgeUs: duration)];
        var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var source = await Factory(effects).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 499999);
        Assert.Empty(frame.Playback.Cursors); var particles = frame.Particles.Where(p => p.Source is Fx.ParticleSource.Bridge).ToArray();
        Assert.Equal(count, particles.Length); Assert.Equal(start + 16667, particles[0].BirthUs);
        var edge = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial).CursorTransitions.Single(edge => edge.GapUs > 0);
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var previous = await query.ResolveAsync(edge.From, Fx.CursorEndpointKind.PreviousEndLimit, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
        var next = await query.ResolveAsync(edge.To, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
        foreach (var p in particles)
        {
            var origin = Assert.IsType<Fx.ParticleSource.Bridge>(p.Source);
            Assert.Equal(edge.EventId, origin.TransitionId); Assert.Equal(edge.From, origin.From); Assert.Equal(edge.To, origin.To);
            Assert.Equal(edge.From.BindingId, p.BindingId); Assert.Equal(edge.TrackId, p.TrackId); Assert.Null(p.EventId);
            Assert.Equal(new Fx.TimeRange(start, 500000), origin.FlightRange);
            double progress = Fx.Curves.EaseInOutCurve.Evaluate((p.BirthUs - start) / (double)(500000 - start));
            Assert.Equal(previous.Cursor!.Position.X * (1 - progress) + next.Cursor!.Position.X * progress, p.BirthPosition.X, 10);
            Assert.Equal(previous.Cursor.Position.Y * (1 - progress) + next.Cursor.Position.Y * progress, p.BirthPosition.Y, 10);
        }
        Assert.StartsWith("2/", frame.Diagnostics.Last(d => d.Diagnostic.Code == "BridgeStateCount").Diagnostic.Message, StringComparison.Ordinal);
        var atStart = await Frame(source, snapshot, 500000);
        Assert.Equal(count, atStart.Particles.Count(p => p.Source is Fx.ParticleSource.Bridge));
        Assert.DoesNotContain(atStart.Particles, p => p.BirthUs == 500000);
        Assert.Contains((await Frame(source, snapshot, 516667)).Particles, p => p.BirthUs == 516667 && p.Source is Fx.ParticleSource.Cursor);
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 桥接帧率暂停顺序倒退与密度模式重建保持全部出生和像素(int fps)
    {
        var snapshot = Snapshot(twoRows: true) with { Composition = Snapshot().Composition with { FrameRate = new(fps, 1) } };
        ImmutableArray<CompiledFrameEffect> effects = [await BridgeState(), await Effect(transition: "bridge", trail: 250000, spread: 8, bridgeUs: 45001)];
        await using var direct = await Factory(effects).CreateAsync(snapshot); var expected = await Frame(direct, snapshot, 850001);
        await using var source = await Factory(effects).CreateAsync(snapshot);
        for (long n = 0; n * 1000000 / fps < expected.TimeUs; n++) await Frame(source, snapshot, n * 1000000 / fps);
        var actual = await Frame(source, snapshot, expected.TimeUs); Assert.Equal(Values(expected), Values(actual)); Assert.Equal(Pixels(expected), Pixels(actual));
        Assert.Equal(Values(expected), Values(await Frame(source, snapshot, expected.TimeUs)));
        await Frame(source, snapshot, 19001); Assert.Equal(Values(expected), Values(await Frame(source, snapshot, expected.TimeUs)));
        var dense = await Frame(source, snapshot, expected.TimeUs, Fx.OutputMode.Opaque, new() { PixelWidth = 640, PixelHeight = 360 });
        Assert.Equal(Values(expected), Values(dense));
        Assert.Equal(Pixels(expected), Pixels(await Frame(source, snapshot, expected.TimeUs)));
        Assert.Contains(expected.Particles, p => p.Source is Fx.ParticleSource.Bridge);
    }
    [Theory]
    [InlineData(0, 2000000)] [InlineData(180000, 99999)]
    public async Task 零时长及超限空档桥接保持Jump粒子而不静默扩大范围(long duration, long gapLimit)
    {
        var snapshot = Snapshot(twoRows: true);
        await using var bridge = await Factory([await Effect(transition: "bridge", bridgeUs: duration, gapLimitUs: gapLimit)]).CreateAsync(snapshot);
        await using var jump = await Factory([await Effect()]).CreateAsync(snapshot);
        Assert.Equal(Values(await Frame(jump, snapshot, 850001)), Values(await Frame(bridge, snapshot, 850001)));
    }
    [Fact]
    public async Task 并发后继各自独立配额且宽源订阅不扩大两端写范围()
    {
        var snapshot = Snapshot(twoRows: true); var branch = snapshot.Lyrics.PrimaryBranch!;
        var second = branch.Lines[1]; var third = second with { Id = "third", Tokens = [.. second.Tokens.Select((t, i) => t with { Id = "third-" + i })] };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.Add(third) }) };
        var project = PresentationEditor.SetBranch(Project.Create("并发桥接", snapshot.Composition, snapshot.Lyrics, lyricStyle: snapshot.LyricStyle),
            new() { BranchId = "primary", SlotSequenceProfile = SlotSequenceAnimationProfile.Default with
                { EnterDurationUs = 0, ExitDurationUs = 0, RevealMode = SlotRevealMode.LeadBeforeNextStart } });
        snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var definition = await Effect(transition: "bridge");
        await using var source = await Factory([definition]).CreateAsync(snapshot); var frame = await Frame(source, snapshot, 499999);
        var paths = frame.Particles.Where(p => p.Source is Fx.ParticleSource.Bridge).GroupBy(p => ((Fx.ParticleSource.Bridge)p.Source).To.LineId).ToArray();
        Assert.Equal(2, paths.Length); Assert.All(paths, path => Assert.Equal(5, path.Count()));
        Assert.Empty(frame.Playback.Cursors);
        await using var narrow = await Factory([definition with { Binding = new Fx.EffectBinding.Lines([new("first")]), Subscription = new Fx.SourceSubscription.Project() }]).CreateAsync(snapshot);
        Assert.DoesNotContain((await Frame(narrow, snapshot, 499999)).Particles, p => p.Source is Fx.ParticleSource.Bridge);
    }
    [Fact]
    public async Task 句内及行依据桥接保持真实源身份且重叠不制造飞行()
    {
        var snapshot = Snapshot(); var branch = snapshot.Lyrics.PrimaryBranch!; var row = branch.Lines[0];
        row = row with { Tokens = [row.Tokens[0] with { Time = new(0, 100000) }, row.Tokens[1] with { Time = new(200000, 300000) }] };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, row) }) };
        await using var within = await Factory([await Effect(transition: "bridge")]).CreateAsync(snapshot);
        var p = Assert.IsType<Fx.ParticleSource.Bridge>((await Frame(within, snapshot, 199999)).Particles.Last().Source);
        Assert.Equal(p.From.LineId, p.To.LineId); Assert.NotEqual(p.From.TokenId, p.To.TokenId);
        var lines = Snapshot(twoRows: true, untimed: true);
        await using var lineSource = await Factory([await Effect(transition: "bridge")]).CreateAsync(lines);
        var lineBridge = Assert.IsType<Fx.ParticleSource.Bridge>((await Frame(lineSource, lines, 499999)).Particles.Last().Source);
        Assert.Null(lineBridge.From.TokenId); Assert.Null(lineBridge.To.TokenId); Assert.Equal(Fx.ProgressSource.Line, lineBridge.From.ProgressSource);
        var overlap = Snapshot(twoRows: true); var overlapBranch = overlap.Lyrics.PrimaryBranch!;
        var shifted = overlapBranch.Lines[1] with { Time = new(300000, 1400000), Tokens = [.. overlapBranch.Lines[1].Tokens.Select(t => t with { Time = new(t.Time!.Value.StartUs - 200000, t.Time.Value.EndUs - 200000) })] };
        overlap = overlap with { Lyrics = overlap.Lyrics.ReplaceBranch(overlapBranch with { Lines = overlapBranch.Lines.SetItem(1, shifted) }) };
        await using var overlapping = await Factory([await Effect(transition: "bridge")]).CreateAsync(overlap);
        Assert.DoesNotContain((await Frame(overlapping, overlap, 500000)).Particles, item => item.Source is Fx.ParticleSource.Bridge);
    }
    [Fact]
    public async Task 未来作者Error提前阻止桥接并保留真正失败实例且重建不复用端点()
    {
        var snapshot = Snapshot(twoRows: true);
        await using var source = await Factory([await BridgeState(true), await Effect(transition: "bridge", bridgeUs: 45001)]).CreateAsync(snapshot);
        var before = await Frame(source, snapshot, 400000); var failure = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 460000));
        Assert.Equal("FutureBridgeError", failure.Code); Assert.Equal("future-state", failure.InstanceId.Value); Assert.Equal("Evaluate", failure.Stage); Assert.Equal(500000, failure.TimeUs);
        Assert.Equal(Values(before), Values(await Frame(source, snapshot, 400000)));
    }
    [Theory]
    [InlineData("live", "LiveParticleBudgetExceeded")] [InlineData("trail", "TrailSampleBudgetExceeded")]
    [InlineData("records", "EmissionRecordBudgetExceeded")] [InlineData("draw", "VisualOutputBudgetExceeded")]
    public async Task 空档内真实桥接仍执行全部四类预算且不依赖Current(string kind, string code)
    {
        var snapshot = Snapshot(twoRows: true);
        var budget = kind switch { "live" => new Fx.ParticleSimulationBudget(1), "trail" => new(trailSampleLimit: 1),
            "records" => new(emissionRecordLimit: 2), _ => Fx.ParticleSimulationBudget.Default };
        await using var source = await Factory([await Effect(entry: "BridgeOnly", rate: kind == "records" ? 1000 : 60,
            life: kind == "records" ? 1 : 2000000, trail: kind == "trail" ? 250000 : 0)], budget,
            visual: kind == "draw" ? new Fx.VisualOutputBudget(1) : null).CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 499999));
        Assert.Equal(code, failure.Code); Assert.Equal("trail", failure.InstanceId.Value); Assert.True(failure.TimeUs > 400000 && failure.TimeUs < 500000);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 桥接曲线有限过冲保留而世界坐标溢出明确失败(bool overflow)
    {
        var snapshot = Snapshot(twoRows: true); var definition = await Effect(entry: "BridgeOnly");
        Fx.CurveValue curve = overflow ? new Fx.CurveValue.ControlPoints([new(0,0), new(.5,double.MaxValue), new(1,1)])
            : new Fx.CurveValue.CubicBezier(new(.3,8), new(.7,8));
        definition = definition with { Parameters = new(new Dictionary<string, Fx.ParameterValue> {
            ["rate"] = new Fx.ParameterValue.Number(60), ["lifeUs"] = new Fx.ParameterValue.Duration(2000000),
            ["trailUs"] = new Fx.ParameterValue.Duration(0), ["bridgeUs"] = new Fx.ParameterValue.Duration(180000),
            ["bridgeGapLimitUs"] = new Fx.ParameterValue.Duration(2000000), ["curve"] = new Fx.ParameterValue.Curve(curve) }) };
        await using var source = await Factory([definition]).CreateAsync(snapshot);
        if (overflow) Assert.Equal("ParticleGeometryNonFinite", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 450000))).Code);
        else
        {
            var frame = await Frame(source, snapshot, 450000); var particle = frame.Particles.Last();
            Assert.IsType<Fx.ParticleSource.Bridge>(particle.Source);
            Assert.True(curve.Evaluate(.5) > 1); Assert.NotEmpty(frame.DotBatches);
            var edge = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial).CursorTransitions.Single(edge => edge.GapUs > 0);
            SemanticAnchorQuery query = new(snapshot, [definition], Fingerprint, static () => new SkiaRenderPipeline());
            var old = await query.ResolveAsync(edge.From, Fx.CursorEndpointKind.PreviousEndLimit, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, frame.Target);
            var next = await query.ResolveAsync(edge.To, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, frame.Target);
            double progress = curve.Evaluate((particle.BirthUs - 400000) / 100000d);
            Assert.Equal(old.Cursor!.Position.X * (1-progress) + next.Cursor!.Position.X * progress, particle.BirthPosition.X, 10);
        }
    }
}
