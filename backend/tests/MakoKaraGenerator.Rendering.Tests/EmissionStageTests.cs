using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ContinuousParticleTests
{
    private const string ParticleProbeSource = """
        using System;
        using System.Linq;
        using System.Collections.Immutable;
        using MakoKaraGenerator.Scripting;
        public sealed class ParticleProbe : EffectBase
        {
            private bool initialized;
            private long shared, visual;
            private int starts;
            private string owner = "", mode = "";
            private CursorEmitterDescriptor descriptor;
            private EmitterWriter saved;
            public override void Initialize(EffectInitializationContext context)
            {
                initialized = true; owner = context.Parameters.GetText("owner"); mode = context.Parameters.GetText("mode");
                descriptor = new() { AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = context.Parameters.GetNumber("rate"),
                    LifetimeUs = context.Parameters.GetDuration("lifeUs"), SizePx = 4, Color = new(1, 0, 0, 1), SeedStreamKey = "probe" };
            }
            public override void OnEvent(TimelineEvent evt, EventContext context) { if (evt is TokenStarted) starts++; }
            public override void Advance(SimulationStep step, StateContext context)
            {
                if (!initialized || shared != step.StartUs) throw new Exception("shared advance duplicated or wrong instance");
                shared = step.EndUs;
                context.Diagnostics.Report(DiagnosticSeverity.Info, "SharedStep", step.StartUs + ":" + step.EndUs);
            }
            public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
            {
                if (!initialized || shared != frame.TimeUs || frame.History.LastBoundaryUs != shared) throw new Exception("emission is not after shared/events/text");
                if (mode == "Error") frame.Diagnostics.Report(DiagnosticSeverity.Error, "EmissionFailed", "real author error");
                if (mode == "expired" && saved != null) saved.Set(new("old"), descriptor);
                if (mode == "expired") saved = output;
                frame.Diagnostics.Report(DiagnosticSeverity.Info, "EmissionBoundary", frame.TimeUs.ToString());
                if (mode != "stop" || frame.TimeUs < 100000)
                    output.Set(new("cursor"), descriptor with { RatePerSecond = mode == "vary" ? frame.TimeUs * 10d : descriptor.RatePerSecond });
            }
            public override void AdvanceVisuals(VisualSimulationStep step, VisualStateContext context)
            {
                if (shared != step.EndUs || visual != step.StartUs || context.CurrentGeometry.TimeUs != step.EndUs
                    || context.PreviousGeometry.TimeUs != step.StartUs || context.EmissionSchedule.StartUs != step.StartUs
                    || context.EmissionSchedule.EndUs != step.EndUs) throw new Exception("visual step must see prepared current particles and whole interval");
                Check(context.Particles, context.EmissionSchedule);
                if (context.PreviousParticles.Any(p => p.BirthUs > step.StartUs || p.DeathUs <= step.StartUs)) throw new Exception("wrong previous particle view");
                visual = step.EndUs;
                context.Diagnostics.Report(DiagnosticSeverity.Info, "VisualStep", step.StartUs + ":" + step.EndUs);
            }
            public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
            {
                if (!initialized || shared != frame.LastSimulationBoundaryUs || visual != shared) throw new Exception("fractional render must not advance or replace object");
                Check(frame.Particles, frame.EmissionSchedule);
                frame.Diagnostics.Report(DiagnosticSeverity.Info, "ParticleView", frame.TimeUs + ":" + shared + ":" + starts + ":" + frame.Particles.Length + ":" + frame.EmissionSchedule.Births.Length);
                output.Overlay.DrawDots(new(mode == "collision" ? "cursor" : "inspect"), frame.Particles.Select(p => new Dot(new Point2(p.Position.X, p.Position.Y - 12), 1, new ColorRgba(0, 1, 0, 1))).ToImmutableArray(), OverlayLayer.AboveText);
            }
            private void Check(ImmutableArray<ParticleSample> particles, EmissionSchedule schedule)
            {
                if (particles.IsDefault || particles.Any(p => p.Id.InstanceId.Value != owner)
                    || schedule.Births.Any(p => p.Id.InstanceId.Value != owner || p.BirthUs < schedule.StartUs || p.BirthUs >= schedule.EndUs))
                    throw new Exception("particle view leaked another instance or fabricated interval");
            }
        }
        public sealed class PureParticleQuery : EffectBase
        {
            public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
            { if (frame.Parameters.GetText("mode") == "particles") { var particles = frame.Particles; } else { var schedule = frame.EmissionSchedule; } }
        }
        public sealed class AnchorParticleProbe : EffectBase
        {
            public override void EvaluateEmitters(EmissionFrameContext f, EmitterWriter w) => throw new Exception("anchor must skip emission");
            public override void AdvanceVisuals(VisualSimulationStep s, VisualStateContext c) => throw new Exception("anchor must skip visual advance");
            public override void EvaluateVisuals(VisualFrameContext f, VisualWriter w) => throw new Exception("anchor must skip particles");
        }
        """;
    private static async Task<CompiledFrameEffect> Probe(string owner = "probe", string mode = "normal", double rate = 60,
        long life = 2_000_000, string entry = "ParticleProbe")
        => new(new(owner), await Artifact.Value, entry, new(new Dictionary<string, Fx.ParameterValue>
        {
            ["owner"] = new Fx.ParameterValue.Text(owner), ["mode"] = new Fx.ParameterValue.Text(mode),
            ["rate"] = new Fx.ParameterValue.Number(rate), ["lifeUs"] = new Fx.ParameterValue.Duration(life)
        }), new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Overlay);
    [Fact]
    public async Task 七阶段同实例完整查询绘图可读取当前粒子且区间出生不含右端()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Probe()]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 50_000);
        Assert.Equal(3, frame.Particles.Length); var births = frame.EmissionSchedule!;
        Assert.Equal(41_666, births.StartUs); Assert.Equal(50_000, births.EndUs); Assert.Empty(births.Births);
        Assert.Equal(3, frame.DotBatches.Single(batch => batch.Id.Value == "inspect").Dots.Length);
        Assert.Equal("50000:50000:1:3:0", frame.Diagnostics.Last(d => d.Diagnostic.Code == "ParticleView").Diagnostic.Message);
        Assert.Equal(7, frame.Diagnostics.Count(d => d.Diagnostic.Code == "EmissionBoundary"));
        Assert.Equal(6, frame.Diagnostics.Count(d => d.Diagnostic.Code == "SharedStep"));
        Assert.Equal(6, frame.Diagnostics.Count(d => d.Diagnostic.Code == "VisualStep"));
        var next = await Frame(source, snapshot, 58_333);
        Assert.Equal(50_000, Assert.Single(next.EmissionSchedule!.Births).BirthUs);
        Assert.Equal(Values(frame), Values(await Frame(source, snapshot, 50_000)));
    }
    [Fact]
    public async Task 作者完整出生包含区间内已死亡粒子且历史不积累()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Probe(rate: 1_000, life: 1)], new(1, 10)).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 8_333);
        Assert.Empty(frame.Particles); Assert.Equal(8, frame.EmissionSchedule!.Births.Length);
        Assert.All(frame.EmissionSchedule.Births, birth => Assert.True(birth.DeathUs < frame.TimeUs));
        var next = await Frame(source, snapshot, 16_666);
        Assert.Equal(8, next.EmissionSchedule!.Births.Length); Assert.Equal(8_333, next.EmissionSchedule.StartUs);
        var fractional = await Frame(source, snapshot, 16_700);
        Assert.Same(next.EmissionSchedule, fractional.EmissionSchedule);
        Assert.Equal(8, fractional.EmissionSchedule!.Births.Length);
    }
    [Fact]
    public async Task 全实例出生记录预算独立于存活预算且分配前失败()
    {
        var snapshot = Snapshot(); var budget = new Fx.ParticleSimulationBudget(2, 10, 2);
        await using var source = await Factory([await Probe("a", rate: 1_000, life: 1), await Probe("z", rate: 1_000, life: 1)], budget).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 8_333));
        Assert.Equal("EmissionRecordBudgetExceeded", error.Code); Assert.Equal("a", error.InstanceId.Value); Assert.Equal(2_000, error.TimeUs);
        await using var fractional = await Factory([await Probe(rate: 1_000, life: 1)], new(1, 10, 2)).CreateAsync(snapshot);
        var fractionalError = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(fractional, snapshot, 3_500));
        Assert.Equal("EmissionRecordBudgetExceeded", fractionalError.Code); Assert.Equal(3_000, fractionalError.TimeUs);
    }
    [Fact]
    public async Task 查询仅本实例但保留离场装载粒子且订阅不扩展发射()
    {
        var snapshot = Snapshot(parallel: true, twoRows: true);
        var primary = (await Probe("primary-probe")) with { Binding = new Fx.EffectBinding.Branch(new("primary")), Subscription = new Fx.SourceSubscription.Project() };
        var other = (await Probe("other-probe")) with { Binding = new Fx.EffectBinding.Branch(new("other")) };
        await using var source = await Factory([primary, other]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 600_001);
        Assert.Equal(2, frame.Particles.GroupBy(p => p.Id.InstanceId).Count());
        var batch = frame.DotBatches.Single(b => b.InstanceId == primary.Id && b.Id.Value == "inspect");
        Assert.Equal(frame.Particles.Count(p => p.Id.InstanceId == primary.Id), batch.Dots.Length);
        Assert.Equal(2, frame.Particles.Where(p => p.Id.InstanceId == primary.Id).Select(p => p.BindingId).Distinct().Count());
    }
    [Theory]
    [InlineData(24)] [InlineData(60)]
    public async Task 发射只在固定边界登记分数帧使用左端描述且帧率不改记录或像素(int fps)
    {
        var snapshot = Snapshot(); var definition = await Probe(mode: "vary", life: 1);
        var budget = new Fx.ParticleSimulationBudget(2000, 10, 100_000);
        await using var direct = await Factory([definition], budget).CreateAsync(snapshot);
        var expected = await Frame(direct, snapshot, 24_901);
        await using var stepped = await Factory([definition], budget).CreateAsync(snapshot);
        for (long n = 0; n * 1_000_000 / fps < 24_901; n++) await Frame(stepped, snapshot, n * 1_000_000 / fps);
        var actual = await Frame(stepped, snapshot, 24_901);
        Assert.Equal(Values(expected), Values(actual)); Assert.Equal(Pixels(expected), Pixels(actual));
        Assert.Equal(expected.EmissionSchedule!.Births.ToArray(), actual.EmissionSchedule!.Births.ToArray());
        Assert.Equal(3, actual.Diagnostics.Count(d => d.Diagnostic.Code == "EmissionBoundary"));
        Assert.Equal(2, actual.Diagnostics.Count(d => d.Diagnostic.Code == "SharedStep"));
        Assert.Equal(3, (await Frame(stepped, snapshot, 24_902)).Diagnostics.Count(d => d.Diagnostic.Code == "EmissionBoundary"));
    }
    [Theory]
    [InlineData("Error", "EmissionFailed", 0)]
    [InlineData("expired", "InvalidEmissionOutputStage", 8_333)]
    [InlineData("collision", "DuplicateOverlayBatch", 25_000)]
    public async Task 发射诊断过期writer和跨阶段同名输出明确失败(string mode, string code, long errorTime)
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Probe(mode: mode)]).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 50_000));
        Assert.Equal(code, error.Code); Assert.Equal(errorTime, error.TimeUs); Assert.Equal("probe", error.InstanceId.Value);
    }
    [Fact]
    public async Task 停止登记后存活粒子仍可读取但不会再发射()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Probe(mode: "stop")]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 200_000);
        Assert.Equal(5, frame.Particles.Length); Assert.Empty(frame.EmissionSchedule!.Births);
        Assert.Equal(5, frame.DotBatches.Single(batch => batch.Id.Value == "inspect").Dots.Length);
    }
    [Theory]
    [InlineData("particles")] [InlineData("schedule")]
    public async Task 纯帧粒子服务不能伪装为空集合(string mode)
    {
        var snapshot = Snapshot(); PureFrameSourceFactory factory = new([await Probe(mode: mode, entry: "PureParticleQuery")], Fingerprint, static () => new SkiaRenderPipeline());
        await using var source = await factory.CreateAsync(snapshot);
        Assert.Equal("TimelineSessionRequired", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 0))).Code);
    }
    [Fact]
    public async Task 独立端点跳过全部发射及视觉入口()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Probe(entry: "AnchorParticleProbe")], reason: Fx.ReplayReason.AnchorQuery).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 100_000); Assert.Empty(frame.Particles); Assert.Null(frame.EmissionSchedule);
    }
    [Fact]
    public async Task 真实CursorTrail清单编译参数覆盖及包绑定产生实际粒子和像素()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "effect-sources", "CursorTrail");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "manifest.json")
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes);
        var contents = EffectPackageContents.Create(File.ReadAllBytes(Path.Combine(root, "manifest.json")), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var compiled = await MakoKaraGenerator.Scripting.Compilation.CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        var plain = compiled.CreateEffect("cursor-trail", new("trail"), new Fx.EffectBinding.Project(), 7);
        var red = compiled.CreateEffect("cursor-trail", new("trail"), new Fx.EffectBinding.Project(), 7,
            "{\"rate\":120,\"color\":{\"r\":1,\"g\":0,\"b\":0,\"a\":0.75},\"spreadPx\":0}"u8.ToArray());
        Assert.Equal(60, plain.Parameters.GetNumber("rate")); Assert.Equal(600_000, plain.Parameters.GetDuration("lifeUs"));
        var snapshot = Snapshot(); await using var first = await Factory([plain]).CreateAsync(snapshot); await using var second = await Factory([red]).CreateAsync(snapshot);
        var a = await Frame(first, snapshot, 100_000); var b = await Frame(second, snapshot, 100_000);
        Assert.Equal(6, a.Particles.Length); Assert.Equal(12, b.Particles.Length); Assert.NotEqual(Pixels(a), Pixels(b));
        Assert.All(b.Particles, p => { Assert.Equal(1, p.Color.Red); Assert.Equal(0, p.Color.Green); Assert.InRange(p.Color.Alpha, 0, .75); });
        Assert.Same(plain.Artifact, red.Artifact);
    }
}
