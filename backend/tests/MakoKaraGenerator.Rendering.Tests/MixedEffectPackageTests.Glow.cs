using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 包材质填充透明或不透明时保留静态辉光且不污染覆盖(bool transparentMaterial)
    {
        string shader = transparentMaterial
            ? "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(0,0,0,0); }"
            : "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(0,0,amount,1); }";
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(
            manifest => manifest["exports"]![1]!["coverageMode"] = "unmasked", shaderSource: shader));
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var original = Snapshot();
        var snapshot = original with { LyricStyle = original.LyricStyle with { Glow = GlowSpec.Create(new(255, 0, 0, 255), 1, 8, 2) } };
        var plain = snapshot with { LyricStyle = snapshot.LyricStyle with { Glow = null } };
        var effect = package.CreateEffect("controller", new("glow-material"), new Fx.EffectBinding.Project(), 1);
        var factory = new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline());
        await using var session = await factory.CreateAsync(snapshot);
        await using var plainSession = await factory.CreateAsync(plain);
        var output = RenderTarget.FromComposition(snapshot.Composition);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, output);
        var plainPrepared = await plainSession.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, output);
        var target = Assert.Single(prepared.FinalGeometry).WithRuby;
        Assert.IsType<Fx.MaterialValue.PackageMaterial>(prepared.GetTokenAppearance(target).UnsungMaterial);
        Assert.NotNull(prepared.GetTokenAppearance(target).Glow);
        using var native = CompiledPackageGraphExports.Compile(package.Definition);
        using var resources = MaterialResources(native, package, new(0, 0, 640, 360));
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        var plainRequest = RenderRequest.At(plain, plainPrepared.TimeUs) with { PreparedFrame = plainPrepared, TransparentBackground = true };
        using var actual = renderer.Render(request);
        using var without = renderer.Render(plainRequest);
        var recipe = new SceneCaptureRecipe(true, true, true, false, SceneCaptureRegion.FillAndStroke);
        var captureTarget = new SceneCaptureTarget.Token(target);
        var bounds = new GraphRect(0, 0, 640, 360);
        using var coverage = renderer.CaptureSceneTarget(request, captureTarget, recipe, GraphInputSource.GlyphCoverage, bounds, GraphExecutionBudget.Default);
        using var plainCoverage = renderer.CaptureSceneTarget(plainRequest, captureTarget, recipe, GraphInputSource.GlyphCoverage, bounds, GraphExecutionBudget.Default);
        var mask = Bytes(coverage);
        Assert.Equal(Bytes(plainCoverage), mask);
        int redExterior = 0;
        for (int i = 3; i < mask.Length; i += 4)
        {
            if (mask[i] != 0) continue;
            int pixel = i / 4;
            Assert.True(actual.TryGetPixel(pixel % 640, pixel / 640, out var color));
            Assert.True(without.TryGetPixel(pixel % 640, pixel / 640, out var prior));
            if (color.Alpha > prior.Alpha && color.Red > color.Green && color.Red > color.Blue) redExterior++;
        }
        Assert.True(redExterior > 50, $"Expected red glow outside glyph coverage, got {redExterior} pixels.");
    }

    [Fact]
    public async Task 包图目标颜色按IncludeGlow开关捕获而覆盖不变()
    {
        var package = await Compile(MixedEffectPackageFixture.Contents());
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var original = Snapshot();
        var snapshot = original with { LyricStyle = original.LyricStyle with { Glow = GlowSpec.Create(new(255, 0, 0, 255), 1, 8, 2) } };
        await using var session = await new PureFrameSourceFactory([package.CreateEffect("controller", new("glow-graph"), new Fx.EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var binding = Assert.Single(prepared.ImageEffects);
        using var native = CompiledPackageGraphExports.Compile(package.Definition);
        var program = native.CreateProgram(binding.Binding.Graph, new(0, 0, 640, 360));
        using var renderer = new SkiaRenderPipeline();
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        var target = new SceneCaptureTarget.Line(Assert.Single(prepared.FinalLineGeometry).Target);
        var recipe = program.GetInputRecipe("original");
        using var on = renderer.CaptureSceneTarget(request, target, recipe with { IncludeGlow = true }, GraphInputSource.TargetColor, program.Bounds, program.Graph.Plan.Budget);
        using var off = renderer.CaptureSceneTarget(request, target, recipe with { IncludeGlow = false }, GraphInputSource.TargetColor, program.Bounds, program.Graph.Plan.Budget);
        using var coverageOn = renderer.CaptureSceneTarget(request, target, recipe with { IncludeGlow = true }, GraphInputSource.GlyphCoverage, program.Bounds, program.Graph.Plan.Budget);
        using var coverageOff = renderer.CaptureSceneTarget(request, target, recipe with { IncludeGlow = false }, GraphInputSource.GlyphCoverage, program.Bounds, program.Graph.Plan.Budget);
        var withGlow = Bytes(on); var withoutGlow = Bytes(off); var mask = Bytes(coverageOn);
        Assert.Equal(mask, Bytes(coverageOff));
        int exterior = 0;
        for (int i = 3; i < mask.Length; i += 4)
            if (mask[i] == 0 && withGlow[i] > withoutGlow[i]) exterior++;
        Assert.True(exterior > 50, $"Expected graph target color to include exterior glow, got {exterior} pixels.");
    }
}
