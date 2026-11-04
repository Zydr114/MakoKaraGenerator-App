using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ParticleFastPathTests
{
    private const string Fingerprint = "m08-empty-particle-fixed-inputs";
    private static readonly string[] LegacyBatchIds = ["a", "z"];

    [Theory]
    [InlineData("WordEmphasis")]
    [InlineData("RepeatEmphasis")]
    [InlineData("ClusterExit")]
    public async Task 真实M07纯文字与强制完整视觉路径的结构像素及全部空快照一致(string sample)
    {
        var plain = await Sample(sample);
        // The otherwise identical author implements a no-op visual callback. It
        // must use the original full query path, independently of this shortcut.
        var full = await Sample(sample, forceVisual: true);
        ImmutableArray<CompiledFrameEffect> actualEffects = [plain, plain with { Id = new("text-b") }];
        ImmutableArray<CompiledFrameEffect> fullEffects = [full, full with { Id = new("text-b") }];
        var snapshot = Snapshot(); VisualOutputBudget budget = new(17);
        await using var actual = await Factory(actualEffects, budget).CreateAsync(snapshot, CancellationToken.None);
        await using var reference = await Factory(fullEffects, budget).CreateAsync(snapshot, CancellationToken.None);
        foreach (long time in new long[] { 750_000, 1_300_000, 1_600_000, 180_750_000, 1_300_000 })
        {
            var frame = await Read(actual, snapshot, time);
            var expected = await Read(reference, snapshot, time);
            EmptyState(frame, actualEffects, budget);
            EmptyState(expected, fullEffects, budget);
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(Structure(expected), Structure(frame));
            Assert.Equal(Pixels(snapshot, expected), Pixels(snapshot, frame));
        }
    }

    [Fact]
    public async Task 无粒子计划的纯视觉作者仍在每个请求执行并输出可见图元()
    {
        var package = await ReplaceSource(await Package("CursorTrail"), "src/CursorTrail.cs", VisualOnlySource,
            manifest =>
            {
                manifest["emitters"] = new JsonArray();
                manifest["requires"] = new JsonArray("visuals.dots");
                manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("visuals.dots");
            });
        var effect = await Compile(package); var snapshot = Snapshot(); VisualOutputBudget budget = new(1);
        await using var session = await Factory([effect], budget).CreateAsync(snapshot, CancellationToken.None);
        foreach (long time in new long[] { 1_000, 2_000, 1_000 })
        {
            var frame = await Read(session, snapshot, time);
            EmptyState(frame, [effect], budget);
            var dot = Assert.Single(Assert.Single(frame.DotBatches).Dots);
            Assert.Equal(new Point2(10 + time / 1_000d, 10), dot.Position);
            var without = frame.WithOverlayOutput([], []);
            Assert.False(Pixels(snapshot, frame).AsSpan().SequenceEqual(Pixels(snapshot, without)));
        }
    }

    [Fact]
    public async Task 非空粒子计划在零出生帧保留调度预算且后续请求仍解析真实出生()
    {
        var effect = await Compile(await ReplaceSource(await Package("CursorTrail"), "src/CursorTrail.cs", EmitterSource));
        var snapshot = Snapshot();
        await using (var session = await Factory([effect]).CreateAsync(snapshot, CancellationToken.None))
        {
            var zero = await Read(session, snapshot, 0);
            Assert.Empty(Assert.Single(zero.V2ParticleSnapshots).Births.Births);
            var later = await Read(session, snapshot, 30_000);
            Assert.Equal(6, Assert.Single(later.V2ParticleSnapshots).Births.Births.Length);
            Assert.Equal(6, later.V2Particles.Length);
            Assert.Equal(6, later.V2AnchorStatistics.UniqueTimes);
            Assert.NotEmpty(later.OverlayBatches);
        }
        // A real episode costs more than the empty-read 16 KiB even at t=0.
        // Returning a generic empty snapshot here would bypass that hard gate.
        await using var limited = await Factory([effect], options: new(frameBytes: 16_384))
            .CreateAsync(snapshot, CancellationToken.None);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Read(limited, snapshot, 0));
        Assert.Equal("ParticleFrameMemoryExceeded", error.Code);
        Assert.Equal(effect.Id, error.InstanceId);
    }

    [Fact]
    public async Task 混合旧图元保留批次顺序全部来源诊断发生和固定视觉预算()
    {
        var plain = await Sample("WordEmphasis"); var full = await Sample("WordEmphasis", forceVisual: true);
        var legacy = await Legacy(); var snapshot = Snapshot(); VisualOutputBudget budget = new(2);
        ImmutableArray<CompiledFrameEffect> effects = [plain, legacy];
        await using var session = await Factory(effects, budget).CreateAsync(snapshot, CancellationToken.None);
        await using var reference = await Factory([full, legacy], budget).CreateAsync(snapshot, CancellationToken.None);
        var frame = await Read(session, snapshot, 100_000);
        var expected = await Read(reference, snapshot, 100_000);
        EmptyState(frame, effects, budget);
        Assert.Equal(LegacyBatchIds, frame.OverlayBatches.Select(static item => item.Id.Value));
        Assert.All(frame.OverlayBatches, batch => Assert.Equal(legacy.Id, batch.InstanceId));
        Assert.Contains(frame.Diagnostics, static item => item.Diagnostic.Code == "legacy-overlay");
        Assert.Equal(expected.Diagnostics.ToArray(), frame.Diagnostics.ToArray());
        Assert.Equal(Structure(expected), Structure(frame));
        Assert.Equal(Pixels(snapshot, expected), Pixels(snapshot, frame));
        var copied = frame.WithGroupOpacities([]).WithOverlaySnapshot(frame.OverlayBatches, frame.DiagnosticSnapshot, budget);
        Assert.Same(frame.DiagnosticSnapshot, copied.DiagnosticSnapshot);
        Assert.Equal(frame.DiagnosticSnapshot.ReadAll().ToArray(), copied.DiagnosticSnapshot.ReadAll().ToArray());
        Assert.Equal(frame.OverlayOwners.ToArray(), copied.OverlayOwners.ToArray());
    }

    [Theory]
    [InlineData(16_383, "text-a")]
    [InlineData(16_384, "text-b")]
    [InlineData(32_767, "text-b")]
    [InlineData(32_768, null)]
    public async Task 空调度保守预算与完整路径在相同边界成功或归属实际失败实例(long bytes, string? failingId)
    {
        var plain = await Sample("WordEmphasis"); var full = await Sample("WordEmphasis", forceVisual: true);
        var snapshot = Snapshot();
        foreach (var effect in new[] { plain, full })
        {
            ImmutableArray<CompiledFrameEffect> effects = [effect, effect with { Id = new("text-b") }];
            await using var session = await Factory(effects, options: new(frameBytes: bytes))
                .CreateAsync(snapshot, CancellationToken.None);
            if (failingId is null)
            {
                EmptyState(await Read(session, snapshot, 1_300_000), effects, VisualOutputBudget.Default);
                continue;
            }
            var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Read(session, snapshot, 1_300_000));
            Assert.Equal(failingId, error.InstanceId.Value);
            Assert.Equal("ParticleFrameMemoryExceeded", error.Code);
            Assert.Equal("ReadParticleBirths", error.Stage); Assert.Equal(1_300_000, error.TimeUs);
            Assert.Equal(effect.PackageIdentity, error.PackageIdentity);
            var diagnostic = Assert.Single(Assert.IsType<RuntimeDiagnosticSnapshot>(error.DiagnosticSnapshot).ReadAll()).Diagnostic;
            Assert.Equal(error.InstanceId, diagnostic.InstanceId);
            Assert.Equal(error.ArtifactHash, diagnostic.ArtifactHash);
            Assert.Equal(error.Code, diagnostic.Diagnostic.Code);
        }
    }

    private static void EmptyState(PreparedRenderFrame frame, ImmutableArray<CompiledFrameEffect> effects, VisualOutputBudget budget)
    {
        Assert.Equal(effects.Where(static effect => effect.IsV2).Select(static effect => effect.Id),
            frame.V2ParticleSnapshots.Select(static snapshot => snapshot.InstanceId));
        var window = new BirthWindow(Math.Max(0, frame.TimeUs - ParticleLimits.MaximumLifetimeUs), frame.TimeUs);
        Assert.All(frame.V2ParticleSnapshots, snapshot =>
        {
            Assert.Equal(window, snapshot.Births.Window);
            Assert.False(snapshot.Births.Births.IsDefault); Assert.Empty(snapshot.Births.Births);
            Assert.False(snapshot.Alive.IsDefault); Assert.Empty(snapshot.Alive);
            Assert.False(snapshot.Trails.IsDefault); Assert.Empty(snapshot.Trails);
        });
        Assert.Empty(frame.V2Particles);
        Assert.Equal(new PreparedV2AnchorStatistics(0, 0, 0, 0, 0), frame.V2AnchorStatistics);
        Assert.Same(budget, frame.VisualBudget);
        Assert.Equal(effects.Select(static effect => new PreparedOverlayOwner(effect.Id, effect.RuntimeHash, effect.PackageIdentity)).ToArray(),
            frame.OverlayOwners.ToArray());
    }

    private static string Structure(PreparedRenderFrame frame) => JsonSerializer.Serialize(new
    {
        frame.TimeUs, frame.OutputMode, frame.Target,
        frame.TokenTransforms, frame.TokenStyles, frame.LineTransforms, frame.GroupTransforms,
        frame.BaseTransforms, frame.BaseStyles, frame.ClusterTransforms, frame.MarkTransforms, frame.MarkStyles,
        frame.FinalGeometry, frame.FinalLineGeometry, frame.FinalGroupGeometry, frame.FinalMarkGeometry, frame.Playback,
        Bindings = frame.Scene.Lines.Select(static line => new { line.BindingId, line.ElementId, line.ParentId,
            Line = line.Frame.Line!.Id, HideTimeUs = line.ExitReplica?.HideTimeUs, LoadOrdinal = line.ExitReplica?.LoadOrdinal }),
        frame.V2ParticleSnapshots, frame.V2Particles, frame.V2AnchorStatistics,
        Batches = frame.OverlayBatches.Select(static batch => JsonSerializer.Serialize(batch, batch.GetType())),
        frame.Particles, HasEmissionSchedule = frame.EmissionSchedule is not null,
    });

    private static async Task<CompiledFrameEffect> Sample(string name, bool forceVisual = false)
    {
        var package = await Package(name);
        if (forceVisual)
        {
            string path = "src/" + name + ".cs";
            string source = Encoding.UTF8.GetString(package.Files.Single(file => file.Path == path).Content.AsSpan());
            source = source.Replace(": IPureKaraokeEffect", ": IPureKaraokeEffect, IPureVisualEffect", StringComparison.Ordinal);
            source = source.Insert(source.LastIndexOf('}'),
                "public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output) { }\n");
            package = await ReplaceSource(package, path, source);
        }
        return await Compile(package);
    }

    private static Task<EffectPackageContents> Package(string name) => EffectPackageStorage.ReadSupportedDirectoryAsync(
        Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));

    private static Task<EffectPackageContents> ReplaceSource(EffectPackageContents package, string path, string source,
        Action<JsonNode>? edit = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source); var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!["sha256"]
            = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        edit?.Invoke(manifest);
        return Task.FromResult(EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path,
                (ReadOnlyMemory<byte>)(file.Path == path ? bytes : file.Content.ToArray())))));
    }

    private static async Task<CompiledFrameEffect> Compile(EffectPackageContents package)
    {
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(package), Compiler());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled.CreateEffect(Assert.Single(compiled.Definition.Controllers).Id, new("text-a"), new EffectBinding.Project(), 17);
    }

    private static async Task<CompiledFrameEffect> Legacy()
    {
        var artifact = await Compiler().CompileAsync([new("Legacy.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class Legacy : EffectBase
            {
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    frame.Diagnostics.Report(DiagnosticSeverity.Warning, "legacy-overlay", "retained occurrence");
                    output.Overlay.DrawDots(new("z"), [new(new(20, 20), 3, new(1, 0, 0, 1))], OverlayLayer.AboveText);
                    output.Overlay.DrawDots(new("a"), [new(new(30, 20), 3, new(0, 1, 0, 1))], OverlayLayer.AboveText);
                }
            }
            """)]);
        Assert.True(artifact.Succeeded, string.Join('\n', artifact.Diagnostics));
        return new(new("legacy"), artifact, "Legacy", ParameterValues.Empty, new EffectBinding.Project(), EffectCapabilities.Overlay);
    }

    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static FrameSessionFactory Factory(ImmutableArray<CompiledFrameEffect> effects,
        VisualOutputBudget? budget = null, V2RuntimeBudgetOptions? options = null)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), null, ReplayReason.Initial,
            visualBudget: budget, v2Budgets: options);
    private static ValueTask<PreparedRenderFrame> Read(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer = new();
        using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        return pixels.PixelMemory.ToArray();
    }
    private static ProjectSnapshot Snapshot()
    {
        var rows = new long[] { 0, 1_000_000, 180_000_000 }.Select((start, index) =>
            LyricLine.Create("line-" + index, "星光", new(start, start + 1_000_000), TimingStatus.Edited,
                [LyricToken.Create("word-" + index, "星光", new(start, start + 1_000_000), TimingStatus.Edited)])
                with { RubyAnnotations = [RubyAnnotation.Create("ruby-" + index, "word-" + index, "ほし")] });
        var project = Project.Create("M07 empty particle equivalence", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 182_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 });
        return ProjectSnapshot.FromProject(project with { EnterAnimation = null }) with { EffectInputsFingerprint = Fingerprint };
    }

    private const string VisualOnlySource = """
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record ProbeData : PreparedEffect;
        public sealed class CursorTrail : IPureKaraokeEffect, IPureVisualEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new ProbeData
            { VisualOutputs = [new(new("cursor"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
            {
                if (frame.Particles.Alive.Length != 0 || frame.Particles.Births.Births.Length != 0
                    || frame.Particles.Births.Window.EndInclusiveUs != frame.TimeUs)
                    throw new InvalidOperationException("EmptyParticleViewMissing");
                output.DrawDots(new("cursor"), [new(new(10 + frame.TimeUs / 1000d, 10), 3, new(1, 0, 0, 1))], OverlayLayer.AboveText);
            }
        }
        """;

    private const string EmitterSource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record ProbeData : PreparedEffect;
        public sealed class CursorTrail : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new ProbeData
            {
                Particles = new([new(new("cursor"), new([new(new(0, 100_000), 200, 200)], [new(0, 100_000)]),
                    new ParticleSpec { LifetimeUs = 200_000, SizePx = 4, Color = new(1, 1, 1, 1), SeedStreamKey = "probe" },
                    CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump)], []),
            };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
        }
        """;
}
