using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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

public sealed class V2MixedOverlayBudgetTests
{
    private const string Fingerprint = "m09-mixed-overlay-shared-frame-budget";
    private const long EmptyQueryBytes = 16_384;
    private const long DotBytes = 320;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 多v2实例图元与空调度合计字节超限归实际跨线实例且失败后原限额可恢复(bool fullVisualPath)
    {
        var dots = await DotOverlay(); var pure = await Pure(fullVisualPath); var snapshot = Snapshot();
        Assert.True(dots.IsV2); Assert.True(pure.IsV2);
        await using (var onlyDots = await Factory([dots], EmptyQueryBytes + DotBytes).CreateAsync(snapshot, CancellationToken.None))
        {
            var frame = await Read(onlyDots, snapshot, 1000);
            var batch = Assert.Single(frame.OverlayBatches);
            Assert.Equal(DotBytes, OverlayMemoryCost.Estimate(batch));
            Assert.Equal(dots.Id, batch.InstanceId);
        }
        await using (var onlyPure = await Factory([pure], EmptyQueryBytes).CreateAsync(snapshot, CancellationToken.None))
            AssertEmptyPure(await Read(onlyPure, snapshot, 1000), pure);

        ImmutableArray<CompiledFrameEffect> effects = [dots, pure];
        await using var shared = await Factory(effects, EmptyQueryBytes * 2).CreateAsync(snapshot, CancellationToken.None);
        await Failure(shared);
        var recovered = await Read(shared, snapshot, 0);
        AssertEmptyPure(recovered, pure); Assert.Empty(recovered.OverlayBatches); Assert.Empty(recovered.Diagnostics);
        Assert.Equal(effects.Select(effect => effect.Id), recovered.OverlayOwners.Select(owner => owner.InstanceId));
        await using (var fresh = await Factory(effects, EmptyQueryBytes * 2).CreateAsync(snapshot, CancellationToken.None))
        {
            var expected = await Read(fresh, snapshot, 0);
            AssertEmptyPure(expected, pure);
            Assert.Equal(expected.V2ParticleSnapshots[0].Births.Window, recovered.V2ParticleSnapshots[0].Births.Window);
            Assert.Equal(expected.V2AnchorStatistics, recovered.V2AnchorStatistics);
            Assert.Equal(expected.OverlayOwners.ToArray(), recovered.OverlayOwners.ToArray());
        }
        await Failure(shared);

        await using var exact = await Factory(effects, EmptyQueryBytes * 2 + DotBytes).CreateAsync(snapshot, CancellationToken.None);
        var combined = await Read(exact, snapshot, 1000);
        AssertEmptyPure(combined, pure);
        var retained = Assert.Single(combined.OverlayBatches);
        Assert.Equal(dots.Id, retained.InstanceId); Assert.Equal(DotBytes, OverlayMemoryCost.Estimate(retained));

        async Task Failure(IEvaluatedFrameSource session)
        {
            var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Read(session, snapshot, 1000));
            Assert.Equal("ParticleFrameMemoryExceeded", error.Code);
            Assert.Equal(dots.Id, error.InstanceId); Assert.Equal(dots.RuntimeHash, error.ArtifactHash);
            Assert.Equal(dots.PackageIdentity, error.PackageIdentity);
            Assert.Equal("ReadParticleBirths", error.Stage); Assert.Equal(1000, error.TimeUs);
            var diagnostics = Assert.IsType<RuntimeDiagnosticSnapshot>(error.DiagnosticSnapshot);
            Assert.True(diagnostics.HasError);
            var diagnostic = Assert.Single(diagnostics.ReadAll()).Diagnostic;
            Assert.Equal(error.InstanceId, diagnostic.InstanceId); Assert.Equal(error.ArtifactHash, diagnostic.ArtifactHash);
            Assert.Equal(error.PackageIdentity, diagnostic.PackageIdentity); Assert.Equal(error.Stage, diagnostic.Stage);
            Assert.Equal(error.TimeUs, diagnostic.TimeUs); Assert.Equal(error.Code, diagnostic.Diagnostic.Code);
        }
    }

    private static void AssertEmptyPure(PreparedRenderFrame frame, CompiledFrameEffect pure)
    {
        var snapshot = frame.V2ParticleSnapshots.Single(item => item.InstanceId == pure.Id);
        Assert.Empty(snapshot.Births.Births); Assert.Empty(snapshot.Alive); Assert.Empty(snapshot.Trails);
        Assert.Empty(frame.V2Particles);
        Assert.Equal(new PreparedV2AnchorStatistics(0, 0, 0, 0, 0), frame.V2AnchorStatistics);
    }

    private static async Task<CompiledFrameEffect> Pure(bool fullVisualPath)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(
            Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WordEmphasis"));
        string visual = fullVisualPath ? """
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
            {
                if (frame.Particles.Alive.Length != 0 || frame.Particles.Births.Births.Length != 0
                    || frame.Particles.Births.Window.EndInclusiveUs != frame.TimeUs)
                    throw new InvalidOperationException("EmptyParticleViewMissing");
            }
            """ : "";
        string source = $$"""
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class WordEmphasis : IPureKaraokeEffect{{(fullVisualPath ? ", IPureVisualEffect" : "")}}
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                {{visual}}
            }
            """;
        const string path = "src/WordEmphasis.cs";
        byte[] bytes = Encoding.UTF8.GetBytes(source); var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!["sha256"]
            = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var replaced = EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path,
                (ReadOnlyMemory<byte>)(file.Path == path ? bytes : file.Content.ToArray()))));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(replaced), Compiler());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled.CreateEffect(Assert.Single(compiled.Definition.Controllers).Id, new("pure-crossing-owner"), new EffectBinding.Project(), 17);
    }

    private static async Task<CompiledFrameEffect> DotOverlay()
    {
        var compiled = await V2RenderPackageHelper.CompileControllerAsync("""
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record DotState : PreparedEffect;
            public sealed class DotOverlay : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                    => new DotState { VisualOutputs = [new(new("dot"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    if (frame.TimeUs < 1000) return;
                    output.DrawDots(new("dot"), [new(new(20, 20), 3, new(1, 0, 0, 1))], OverlayLayer.AboveText);
                }
            }
            """,
            [CapabilityNames.VisualsDots],
            entryType: "DotOverlay",
            exportId: "dots",
            outputs: [V2RenderPackageHelper.Output("dot", "Overlay", "canvas", CapabilityNames.VisualsDots)],
            packageId: "demo.dot-overlay.v2");
        return compiled.CreateEffect("dots", new("dot-overlay-owner"), new EffectBinding.Project(), 19);
    }

    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static FrameSessionFactory Factory(ImmutableArray<CompiledFrameEffect> effects, long frameBytes)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), null,
            v2Budgets: new(frameBytes: frameBytes));
    private static ValueTask<PreparedRenderFrame> Read(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static ProjectSnapshot Snapshot()
    {
        var line = LyricLine.Create("line", "星光", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("word", "星光", new(0, 1_000_000), TimingStatus.Edited)]);
        var project = Project.Create("Mixed overlay frame budget", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 32 });
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    }
}
