using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class BaseMaterialRuntimeTests
{
    private static readonly string[] MaterialIds = ["ink", "other"];
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 8)] [InlineData(true, 8)]
    [InlineData(false, 10)] [InlineData(true, 10)]
    [InlineData(false, 12)] [InlineData(true, 12)]
    [InlineData(false, 13)] [InlineData(true, 13)]
    public async Task 真实包Base两态描边及父族Ruby按部件执行并匹配独立像素参考(bool gpu, int mode)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package, mode: mode), BaseMaterialPackageFixture.Instance(package, "opacity", 2)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot); Assert.True(compiled.Succeeded);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        using var device = Device(gpu); using var native = CompiledPackageGraphExports.CompileWithDevice(Assert.Single(compiled.Packages).Definition, null, device);
        using var resources = Resources(native); using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false);
        foreach (long time in new long[] { 500_000, 2_000_000, 3_500_000, 600_250_000, 2_000_000 })
        {
            var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot, time); var token = Assert.Single(frame.FinalGeometry);
            var parent = BaseMaterialPackageFixture.Parent(frame);
            using var actual = Render(renderer, snapshot, frame);
            // Base stroke/glow can overlap Ruby's ink rectangle. Those cases use the
            // full-image oracle with independent parent Ruby, instead of treating
            // every pixel in that rectangle as belonging exclusively to Ruby.
            if (mode is not (10 or 12 or 13))
            { using var family = Render(renderer, snapshot, parent); RubyEqual(family, actual, token); }
            if (mode == 3)
            {
                // Explicit Base Solid still uses the original direct Solid reference.
                MaterialValue color = MaterialValue.Solid(new(0,1,0,1));
                var reference = parent.WithTextPartitions(frame.BaseTransforms,
                    [new(token.BaseOnly, TestStyle.Update(color, color, opacityFactor: .5))], frame.ClusterTransforms);
                using var expected = Render(renderer, snapshot, reference);
                Compare(expected, actual, 2);
            }
            else
            {
                double amount = mode == 8 ? .75 : .5;
                byte[] expected = BaseMaterialPixelOracle.Expected(renderer, snapshot, frame, amount, amount, .5, strokeOnly: mode == 10);
                // 参考辉光在整屏表面上捕获；材质对象的辉光画在本行区域的表面上，Skia GPU 模糊随表面尺寸与原点差 1～3。
                BaseMaterialPixelOracle.Compare(expected, actual, gpu && mode is 12 or 13 ? 4 : 2);
            }
            if (mode == 3) Assert.Empty(frame.BaseMaterials); else Assert.NotEmpty(frame.BaseMaterials);
            Assert.Equal(.5, frame.GetBaseAppearance(token.BaseOnly).Opacity);
            Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        }
        await using var reopened = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var first = await BaseMaterialPackageFixture.Prepare(source, snapshot); var repeated = await BaseMaterialPackageFixture.Prepare(reopened, snapshot);
        using var a = Render(renderer, snapshot, first); using var b = Render(renderer, snapshot, repeated); Assert.Equal(a.PixelMemory.ToArray(), b.PixelMemory.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Base捕获即使IncludeRuby仍只含Base而Family完整包含Base及Ruby(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package, mode: 11)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot); var token = Assert.Single(frame.FinalGeometry);
        using var device = Device(gpu); using var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
        var request = Request(snapshot, frame); var recipe = new SceneCaptureRecipe(true, true, true, true, SceneCaptureRegion.FillAndStroke);
        foreach (var channel in new[] { GraphInputSource.GlyphCoverage, GraphInputSource.StrokeCoverage, GraphInputSource.SungCoverage, GraphInputSource.TargetRegionCoverage })
        {
            using var basic = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Base(token.BaseOnly), recipe, channel, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default);
            using var noRuby = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Token(token.WithRuby), recipe with { IncludeRuby = false, IncludeDescendantGroups = false }, channel, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default);
            Assert.Equal(SkiaTestReadback.Pixels(basic.Image), SkiaTestReadback.Pixels(noRuby.Image));
            if (channel == GraphInputSource.GlyphCoverage)
            {
                using var family = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Token(token.WithRuby), recipe, channel, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default);
                Assert.True(Alpha(SkiaTestReadback.Pixels(family.Image)) > Alpha(SkiaTestReadback.Pixels(basic.Image)));
            }
        }
        Assert.Equal("MaterialProcessedCaptureRequired", Assert.Throws<PreparedFrameException>(() => renderer.CaptureSceneTarget(request,
            new SceneCaptureTarget.Base(token.BaseOnly), recipe, GraphInputSource.TargetColor, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default)).Code);
        Assert.Equal("InvalidMaterialRegionTarget", Assert.Throws<PreparedFrameException>(() => renderer.CaptureSceneMaterialRegion(request,
            new SceneCaptureTarget.Base(token.BaseOnly), SceneColorPart.RubyFill, compiled.Packages[0].Definition.GetMaterial("ink").Fallback,
            false, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default)).Code);
    }

    internal static ISkiaRenderDevice Device(bool gpu) => SkiaRenderDeviceFactory.Create(SkiaRenderBackendOptions.Parse(gpu ? "vulkan" : "cpu"));
    internal static SceneImageEffectResources Resources(CompiledPackageGraphExports native) => new(
        [native.CreateProgram(native.Resources.GetGraph("tint"), BaseMaterialPackageFixture.Bounds)], null,
        MaterialIds.Select(id => native.CreateMaterialProgram(native.Resources.GetMaterial(id), BaseMaterialPackageFixture.Bounds)));
    internal static RenderRequest Request(ProjectSnapshot snapshot, PreparedRenderFrame frame)
        => RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true };
    internal static RenderFrame Render(SkiaRenderPipeline renderer, ProjectSnapshot snapshot, PreparedRenderFrame frame) => renderer.Render(Request(snapshot, frame));
    internal static void Compare(RenderFrame expected, RenderFrame actual, int tolerance)
    {
        Assert.Equal(expected.PixelMemory.Length, actual.PixelMemory.Length);
        int maximum = 0, offset = 0, different = 0;
        for (int i = 0; i < expected.PixelMemory.Length; i++)
        {
            int delta = Math.Abs(expected.PixelMemory.Span[i] - actual.PixelMemory.Span[i]);
            if (delta > tolerance) different++;
            if (delta > maximum) { maximum = delta; offset = i; }
        }
        int pixel = offset / 4 * 4;
        Assert.True(maximum <= tolerance, $"max={maximum}, channels={different}, x={pixel % actual.Stride / 4}, y={pixel / actual.Stride}, expected BGRA={string.Join(',', expected.PixelMemory.Span.Slice(pixel, 4).ToArray())}, actual BGRA={string.Join(',', actual.PixelMemory.Span.Slice(pixel, 4).ToArray())}");
    }
    private static long Alpha(byte[] pixels) => pixels.Where((_, i) => i % 4 == 3).Sum(value => (long)value);
    internal static void RubyEqual(RenderFrame expected, RenderFrame actual, TokenGeometrySample token)
    {
        var corners = Assert.Single(token.Ruby).Geometry.WorldInkCorners;
        int visible = 0;
        for (int y = Math.Max(0,(int)Math.Floor(corners.Min(p => p.Y))-1); y < Math.Min(actual.Height,(int)Math.Ceiling(corners.Max(p => p.Y))+1); y++)
        for (int x = Math.Max(0,(int)Math.Floor(corners.Min(p => p.X))-1); x < Math.Min(actual.Width,(int)Math.Ceiling(corners.Max(p => p.X))+1); x++)
        {
            int offset = y * actual.Stride + x * 4;
            Assert.True(expected.PixelMemory.Span.Slice(offset,4).SequenceEqual(actual.PixelMemory.Span.Slice(offset,4)));
            if (actual.PixelMemory.Span[offset+3] > 0) visible++;
        }
        Assert.True(visible > 0);
    }
}
