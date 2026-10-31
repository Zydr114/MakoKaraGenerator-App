using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ContinuousParticleTests
{
    private const string Fingerprint = "continuous-particles-fixed-inputs";
    private static readonly Lazy<Task<ScriptCompilationResult>> Artifact = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([
            new("src/CursorTrail.cs", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "effect-sources/CursorTrail/src/CursorTrail.cs"))),
            new("src/ParticleProbe.cs", ParticleProbeSource),
            new("src/BridgeProbe.cs", BridgeProbeSource),
            new("src/Movement.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class Movement : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1 + 4 * frame.TimeUs / 1000000d * frame.TimeUs / 1000000d, Pivot.BaseAdvanceCenter);
                }
            }
            public sealed class Motion : EffectBase
            {
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
                {
                    output.Set(new("cursor"), new()
                    {
                        AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = 60, LifetimeUs = 1000000, SizePx = 8,
                        Color = new ColorRgba(1, 0, 0, 1), SeedStreamKey = "motion", Velocity = new Vector2(20, -10), TrailDurationUs = 250000,
                        Motion = new ParticleMotion { Acceleration = new Vector2(40, 20),
                            Size = new ParticleAgeCurve(1, 2, new CurveValue.Easing(EasingKind.Linear)),
                            Opacity = new ParticleAgeCurve(1, 0, new CurveValue.Easing(EasingKind.Linear)) }
                    });
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static ProjectSnapshot Snapshot(bool parallel = false, bool twoRows = false, bool untimed = false)
    {
        var rows = twoRows ? new[] { Row("first", 0, 400_000), Row("second", 500_000, 1_400_000) } : new[] { Row("first", 0, 1_400_000) };
        if (untimed) rows = rows.Select(line => line with { Tokens = [], RubyAnnotations = [] }).ToArray();
        var lyrics = LyricsDocument.Create(rows);
        if (parallel) lyrics = lyrics with { Branches = lyrics.Branches.Add(LyricBranch.Create("other", "other", LyricBranchRole.Custom, [Row("parallel", 0, 1_400_000)])) };
        var project = Project.Create("particles", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_600_000), lyrics,
            lyricStyle: LyricStyle.Default with { FontSizePx = 40, SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null });
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        static LyricLine Row(string id, long start, long end) => LyricLine.Create(id, "田田", new(start, end), TimingStatus.Edited,
            [LyricToken.Create(id + "-a", "田", new(start, start + (end - start) / 2), TimingStatus.Edited),
             LyricToken.Create(id + "-b", "田", new(start + (end - start) / 2, end), TimingStatus.Edited)]);
    }
    private static async Task<CompiledFrameEffect> Effect(double rate = 60, long life = 2_000_000, long trail = 0,
        double spread = 0, string entry = "CursorTrail", string transition = "jump", ulong seed = 7,
        long bridgeUs = 180_000, long gapLimitUs = 2_000_000)
        => new(new("trail"), await Artifact.Value, entry, new(new Dictionary<string, Fx.ParameterValue>
        {
            ["rate"] = new Fx.ParameterValue.Number(rate), ["lifeUs"] = new Fx.ParameterValue.Duration(life), ["sizePx"] = new Fx.ParameterValue.Number(6),
            ["spreadPx"] = new Fx.ParameterValue.Number(spread), ["trailUs"] = new Fx.ParameterValue.Duration(trail),
            ["transition"] = new Fx.ParameterValue.Choice(transition), ["bridgeUs"] = new Fx.ParameterValue.Duration(bridgeUs),
            ["bridgeGapLimitUs"] = new Fx.ParameterValue.Duration(gapLimitUs), ["color"] = new Fx.ParameterValue.Color(new(1, 0, 0, 1))
        }), new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Overlay) { Seed = seed };
    private static TimelineFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects, Fx.ParticleSimulationBudget? budget = null,
        Fx.VisualOutputBudget? visual = null, Fx.ReplayReason reason = Fx.ReplayReason.Initial)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), reason, visualBudget: visual, particleBudget: budget);
    private static Task<PreparedRenderFrame> Frame(IEvaluatedFrameSource source, ProjectSnapshot snapshot, long time,
        Fx.OutputMode mode = Fx.OutputMode.Transparent, RenderTarget? target = null)
        => source.PrepareAsync(time, mode, target ?? RenderTarget.FromComposition(snapshot.Composition)).AsTask();
    private static object[] Values(PreparedRenderFrame frame) => frame.Particles.Select(p => (object)new
        { p.Id, p.BirthUs, p.DeathUs, p.BirthPosition, p.Position, p.Color, p.SizePx, Trail = string.Join(';', p.Trail) }).ToArray();
    private static byte[] Pixels(PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer = new(); using var result = renderer.Render(RenderRequest.At(frame.Snapshot, frame.TimeUs)
            with { PreparedFrame = frame, TransparentBackground = frame.OutputMode == Fx.OutputMode.Transparent, Target = frame.Target });
        return result.PixelMemory.ToArray();
    }
    [Theory]
    [InlineData(false, 60)] [InlineData(true, 120)]
    public async Task 每条合法头独立发射且整数边界和分数字形不重复(bool parallel, int count)
    {
        var snapshot = Snapshot(parallel); await using var source = await Factory([await Effect()]).CreateAsync(snapshot);
        var before = await Frame(source, snapshot, 999_999); Assert.Equal(count - (parallel ? 2 : 1), before.Particles.Length);
        var boundary = await Frame(source, snapshot, 1_000_000); Assert.Equal(count, boundary.Particles.Length);
        Assert.Equal(parallel ? 2 : 1, boundary.Playback.Cursors.Length);
        Assert.Equal(count, boundary.Particles.Select(p => p.Id).Distinct().Count());
        Assert.Equal(60, boundary.Particles.GroupBy(p => p.BindingId).First().Count());
        Assert.Contains(boundary.Particles, p => p.BirthUs == 1_000_000);
        var repeat = await Frame(source, snapshot, 1_000_000); Assert.Equal(Values(boundary), Values(repeat));
        var after = await Frame(source, snapshot, 1_000_001); Assert.Equal(Values(boundary).Length, Values(after).Length);
        Assert.Equal(boundary.Particles.Select(p => p.Id), after.Particles.Select(p => p.Id));
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 多帧率暂停重绘跳转和中段直达的粒子拖尾与像素相同(int fps)
    {
        var snapshot = Snapshot(twoRows: true); var definition = await Effect(trail: 250_000, spread: 8);
        await using var direct = await Factory([definition]).CreateAsync(snapshot);
        var expected = await Frame(direct, snapshot, 850_001);
        await using var source = await Factory([definition]).CreateAsync(snapshot);
        for (long n = 0; n * 1_000_000 / fps < expected.TimeUs; n++) await Frame(source, snapshot, n * 1_000_000 / fps);
        var frame = await Frame(source, snapshot, expected.TimeUs); Assert.Equal(Values(expected), Values(frame)); Assert.Equal(Pixels(expected), Pixels(frame));
        Assert.Equal(Values(frame), Values(await Frame(source, snapshot, expected.TimeUs)));
        await Frame(source, snapshot, 19_001); var rewind = await Frame(source, snapshot, expected.TimeUs);
        Assert.Equal(Values(expected), Values(rewind)); Assert.Equal(Pixels(expected), Pixels(rewind));
        Assert.Contains(frame.Particles, particle => particle.BirthUs < 400_000);
        Assert.Contains(frame.Particles, particle => particle.BirthUs > 500_000);
    }
    [Fact]
    public async Task 出生位置来自精确出生时刻最终非线性族变换而非边界插值()
    {
        var snapshot = Snapshot(); var movement = new CompiledFrameEffect(new("movement"), await Artifact.Value, "Movement", new(new Dictionary<string, Fx.ParameterValue>()),
            new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms);
        await using var source = await Factory([movement, await Effect(rate: 137)]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 31_000); var particle = frame.Particles[0]; Assert.Equal(7_300, particle.BirthUs);
        PureFrameSourceFactory pure = new([movement], Fingerprint, static () => new SkiaRenderPipeline());
        await using var basis = await pure.CreateAsync(snapshot);
        var atBirth = await Frame(basis, snapshot, particle.BirthUs);
        Assert.Equal(Assert.Single(atBirth.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)).Position, particle.BirthPosition);
        var left = Assert.Single((await Frame(basis, snapshot, 0)).Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)).Position;
        var right = Assert.Single((await Frame(basis, snapshot, 8_333)).Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)).Position;
        Assert.NotEqual(left.X + (right.X - left.X) * particle.BirthUs / 8_333d, particle.BirthPosition.X);
    }
    [Fact]
    public async Task 常加速度年龄曲线及固定拖尾使用歌曲秒且出生坐标不随切行变化()
    {
        var snapshot = Snapshot(twoRows: true); await using var source = await Factory([await Effect(entry: "Motion")]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 600_001); var particle = frame.Particles[0];
        double seconds = (frame.TimeUs - particle.BirthUs) / 1_000_000d;
        Assert.Equal(particle.BirthPosition.X + 20 * seconds + 20 * seconds * seconds, particle.Position.X, 11);
        Assert.Equal(particle.BirthPosition.Y - 10 * seconds + 10 * seconds * seconds, particle.Position.Y, 11);
        Assert.Equal(8 * (1 + seconds), particle.SizePx, 11); Assert.Equal(1 - seconds, particle.Color.Alpha, 11);
        Assert.All(particle.Trail, sample => Assert.InRange(sample.TimeUs, frame.TimeUs - 250_000, frame.TimeUs));
        Assert.Contains(particle.Trail, sample => sample.TimeUs == 500_000); Assert.Equal(frame.TimeUs, particle.Trail[^1].TimeUs);
        Assert.Contains(frame.Particles, p => p.BindingId != particle.BindingId);
        var plain = ProjectSnapshot.FromProject(Project.Create("plain", snapshot.Composition, snapshot.Lyrics));
        using SkiaRenderPipeline renderer = new(); using var plainPixels = renderer.Render(RenderRequest.At(plain, frame.TimeUs) with { TransparentBackground = true });
        Assert.NotEqual(plainPixels.PixelMemory.ToArray(), Pixels(frame));
    }
    [Fact]
    public async Task 寿命半开删除且大量历史出生不被误算为存活峰值()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Effect(life: 30_000)], new(2, 100)).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 1_000_000); Assert.InRange(frame.Particles.Length, 1, 2);
        Assert.DoesNotContain(frame.Particles, p => p.BirthUs == 16_667);
        await using var exact = await Factory([await Effect(life: 100_000)]).CreateAsync(snapshot);
        Assert.Contains((await Frame(exact, snapshot, 116_666)).Particles, p => p.BirthUs == 16_667);
        Assert.DoesNotContain((await Frame(exact, snapshot, 116_667)).Particles, p => p.BirthUs == 16_667);
    }
    [Fact]
    public async Task 全实例存活拖尾绘制预算错误归属真实实例()
    {
        var snapshot = Snapshot(); var definition = await Effect();
        await using var source = await Factory([definition, definition with { Id = new("z-other") }], new(3, 100)).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 40_000));
        Assert.Equal("LiveParticleBudgetExceeded", error.Code); Assert.Equal("z-other", error.InstanceId.Value); Assert.Equal(33_334, error.TimeUs);
        await using var trail = await Factory([await Effect(trail: 250_000)], new(100, 2)).CreateAsync(snapshot);
        Assert.Equal("TrailSampleBudgetExceeded", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(trail, snapshot, 100_000))).Code);
        await using var draw = await Factory([await Effect()], visual: new(2)).CreateAsync(snapshot);
        Assert.Equal("VisualOutputBudgetExceeded", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(draw, snapshot, 100_000))).Code);
    }
    [Fact]
    public async Task 散布固定种子和改变种子输出确实不同且未定时行用行播放头()
    {
        var snapshot = Snapshot(untimed: true);
        await using var first = await Factory([await Effect(spread: 8, seed: 3)]).CreateAsync(snapshot);
        await using var second = await Factory([await Effect(spread: 8, seed: 4)]).CreateAsync(snapshot);
        var a = await Frame(first, snapshot, 100_000); var b = await Frame(second, snapshot, 100_000);
        Assert.Equal(6, a.Particles.Length); Assert.NotEqual(a.Particles[0].BirthPosition, b.Particles[0].BirthPosition);
        Assert.Equal(Fx.ProgressSource.Line, Assert.Single(a.Playback.Cursors).Identity.ProgressSource);
        Assert.Equal(Values(a), Values(await Frame(first, snapshot, 100_000)));
    }
    [Fact]
    public async Task 纯帧拒绝连续发射而零空档桥接与独立端点不伪造粒子()
    {
        var snapshot = Snapshot(); var effect = await Effect();
        PureFrameSourceFactory pure = new([effect], Fingerprint, static () => new SkiaRenderPipeline());
        await using var direct = await pure.CreateAsync(snapshot);
        Assert.Equal("TimelineSessionRequired", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(direct, snapshot, 100_000))).Code);
        await using var bridge = await Factory([await Effect(transition: "bridge")]).CreateAsync(snapshot);
        var bridged = await Frame(bridge, snapshot, 100_000);
        Assert.Equal(6, bridged.Particles.Length); Assert.All(bridged.Particles, particle => Assert.IsType<Fx.ParticleSource.Cursor>(particle.Source));
        await using var anchor = await Factory([effect], reason: Fx.ReplayReason.AnchorQuery).CreateAsync(snapshot);
        Assert.Empty((await Frame(anchor, snapshot, 100_000)).Particles);
    }
    [Fact]
    public async Task 取消重试和输出密度改变重建保持逻辑粒子而不共享状态()
    {
        var snapshot = Snapshot(); await using var source = await Factory([await Effect(trail: 250_000, spread: 8)]).CreateAsync(snapshot);
        var first = await Frame(source, snapshot, 250_001);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.PrepareAsync(500_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), cancellation.Token));
        Assert.Equal(Values(first), Values(await Frame(source, snapshot, 250_001)));
        var doubled = await Frame(source, snapshot, 250_001, target: new() { PixelWidth = 640, PixelHeight = 360 });
        Assert.Equal(Values(first), Values(doubled));
    }
    [Fact]
    public async Task 极端速率和寿命溢出带真实实例错误而非占位成功()
    {
        var snapshot = Snapshot();
        await using var huge = await Factory([await Effect(rate: double.MaxValue)], new(2, 100)).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => Frame(huge, snapshot, 100));
        Assert.Equal("LiveParticleBudgetExceeded", error.Code); Assert.Equal(1, error.TimeUs); Assert.Equal("trail", error.InstanceId.Value);
        await using var overflow = await Factory([await Effect(life: long.MaxValue)]).CreateAsync(snapshot);
        Assert.Equal("ParticleTimeOrIdentityOverflow", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(overflow, snapshot, 20_000))).Code);
    }

    [Fact]
    public async Task 演唱空档不发射并且包未声明发射输出必须拒绝()
    {
        var snapshot = Snapshot(); var branch = snapshot.Lyrics.PrimaryBranch!; var row = branch.Lines[0];
        row = row with { Tokens = [row.Tokens[0] with { Time = new(0, 100_000) }, row.Tokens[1] with { Time = new(200_000, 300_000) }] };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, row) }) };
        var definition = await Effect();
        await using var source = await Factory([definition]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 1_300_000);
        Assert.Equal(10, frame.Particles.Length);
        Assert.All(frame.Particles, p => Assert.True(p.BirthUs < 100_000 || p.BirthUs is >= 200_000 and < 300_000));
        var declared = definition with { PackageDeclaration = EffectPackageFixture.WordEmphasis().GetController("word-emphasis") };
        await using var rejected = await Factory([declared]).CreateAsync(snapshot);
        Assert.Equal("UndeclaredPackageOutput", (await Assert.ThrowsAsync<EffectFailureException>(() => Frame(rejected, snapshot, 50_000))).Code);
    }
    [Fact]
    public async Task 真实三槽与前奏不会提前发射且出生保持真实槽位装载()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true)) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Factory([(await Effect(life: 2_000_000)) with { Binding = new Fx.EffectBinding.Branch(new("primary")) }]).CreateAsync(snapshot);
        var preparation = await Frame(source, snapshot, 9_500_000); Assert.Empty(preparation.Particles);
        var frame = await Frame(source, snapshot, 12_100_000); Assert.NotEmpty(frame.Particles);
        var particle = frame.Particles[0];
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot with { EffectInputsFingerprint = null }, particle.BirthUs);
        var cursor = PlaybackProjection.Create(snapshot, scene, scene.TokenGeometries).GetCursors(Fx.CursorAnchorMode.AdvanceCenter)
            .Single(cursor => cursor.BindingId == particle.BindingId);
        Assert.Equal(cursor.Position, particle.BirthPosition);
    }

}
