using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(true, .5)] [InlineData(false, .5)] [InlineData(true, 0)]
    public void 主行图先完成真实效果再乘父组透明度且alpha零不隐藏非法图(bool transparent, double factor)
    {
        var request = Request(Snapshot(), transparent, 480, 180); using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var bound = Bindings(request, Bind("tint", Line(request))); using var original = renderer.Render(bound);
        var root = request.Project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var grouped = bound with { PreparedFrame = bound.PreparedFrame!.WithGroupOpacities([new(new("alpha"), "alpha-artifact", new(new(new(root)), factor))]) };
        using var actual = renderer.Render(grouped);
        var expected = original.Pixels.ToArray(); byte[] background = transparent ? [0, 0, 0, 0] : [60, 40, 25, 255];
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)Math.Round(background[i % 4] * (1 - factor) + expected[i] * factor);
        Close(expected, actual.Pixels.ToArray()); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        using var emptyResources = new SceneImageEffectResources([]); using var missing = new SkiaRenderPipeline(null, null, null, emptyResources);
        var failure = Assert.Throws<EffectFailureException>(() => missing.Render(grouped));
        Assert.Equal("tint", failure.InstanceId.Value); Assert.Equal("ImageEffectResourceUnavailable", failure.Code);
    }
    [Theory]
    [InlineData(.3)] [InlineData(.7)]
    public void 替换下层图随整组一起淡出保留原下层而不重复相乘蒙版(double factor)
    {
        var request = Request(Snapshot(), false, 480, 180);
        var shader = new Core.Effects.ShaderProgramDefinition("glass.sksl",
            "uniform shader below; half4 main(float2 p) { half4 c=below.eval(p); return half4(c.b,c.g,c.r,c.a); }",
            Core.Effects.ShaderProgramDefinition.RasterProfile, [], [new("below", Core.Effects.ShaderSurfaceKind.Color)]);
        var nodeBounds = new Core.Effects.GraphNodeBounds(new Core.Effects.GraphExpansion.Literal(0), 0, 0, Core.Effects.GraphEdgeMode.Transparent);
        var definition = new Core.Effects.ImageGraphDefinition("glass", Core.Effects.ImageGraphDefinition.CurrentVersion,
            [new("below", Core.Effects.GraphInputSource.BackdropBelow, Core.Effects.ShaderSurfaceKind.Color, true, Core.Effects.GraphBackgroundRequirement.None, Core.Effects.GraphCoordinateSpace.Canvas),
                new("mask", Core.Effects.GraphInputSource.GlyphCoverage, Core.Effects.ShaderSurfaceKind.Coverage, true, Core.Effects.GraphBackgroundRequirement.None, Core.Effects.GraphCoordinateSpace.Canvas)], [],
            [new Core.Effects.ImageGraphNode.RuntimeShader("tint", "glass", Core.Effects.ShaderSurfaceKind.Color, [],
                [new("below", Core.Effects.GraphReference.Input("below"))], nodeBounds),
                new Core.Effects.ImageGraphNode.Mask("masked", Core.Effects.GraphReference.Node("tint"), Core.Effects.GraphReference.Input("mask"), nodeBounds)],
            Core.Effects.GraphReference.Node("masked"), Core.Effects.GraphCoordinateSpace.Canvas, 0, Core.Effects.GraphAnchorMode.ColorOnly,
            Core.Effects.GraphCompositeMode.ReplaceCoveredBackdrop, Core.Effects.GraphReference.Input("mask"));
        using var graph = CompiledImageGraph.Compile(Core.Effects.ImageGraphCompiler.Compile(definition, [], [new("glass", shader)]));
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources); var bound = Bindings(request, Bind("glass", Line(request)));
        using var before = renderer.Render(bound); var expected = before.Pixels.ToArray(); byte[] below = [60, 40, 25, 255];
        var root = request.Project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        using var actual = renderer.Render(bound with { PreparedFrame = bound.PreparedFrame!.WithGroupOpacities([new(new("alpha"), "artifact", new(new(new(root)), factor))]) });
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)Math.Round(expected[i] * factor + below[i % 4] * (1 - factor));
        Close(expected, actual.Pixels.ToArray()); Assert.False(before.Pixels.SequenceEqual(actual.Pixels));
    }
    [Fact]
    public void 没有图绑定的组透明度不依赖已关闭的作者图集合()
    {
        var request = Request(Snapshot()); var root = request.Project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        request = request with { PreparedFrame = request.PreparedFrame!.WithGroupOpacities([new(new("alpha"), "artifact", new(new(new(root)), .5))]) };
        using var resources = new SceneImageEffectResources([]); resources.Dispose();
        using var renderer = new SkiaRenderPipeline(null, null, null, resources); using var independent = new SkiaRenderPipeline();
        using var actual = renderer.Render(request); using var expected = independent.Render(request);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }
    [Fact]
    public void 嵌套组里的行仍从合成前下层读取而不读取自身或重复绘制背景()
    {
        var request = Request(Snapshot(), false);
        var definition = new Core.Effects.ImageGraphDefinition("below", Core.Effects.ImageGraphDefinition.CurrentVersion,
            [new("below", Core.Effects.GraphInputSource.BackdropBelow, Core.Effects.ShaderSurfaceKind.Color, true,
                Core.Effects.GraphBackgroundRequirement.None, Core.Effects.GraphCoordinateSpace.Canvas)], [], [],
            Core.Effects.GraphReference.Input("below"), Core.Effects.GraphCoordinateSpace.Canvas, 0,
            Core.Effects.GraphAnchorMode.ColorOnly, Core.Effects.GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(Core.Effects.ImageGraphCompiler.Compile(definition, [], []));
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var bound = Bindings(request, Bind("below", Line(request))); var program = request.Project.LyricPresentation.FindProgram("primary")!;
        var nested = bound with { PreparedFrame = bound.PreparedFrame!.WithGroupOpacities([
            new(new("root"), "artifact", new(new(new(program.RootContainerId)), .5)),
            new(new("child"), "artifact", new(new(new(program.DefaultContainerId)), .5))]) };
        using var actual = renderer.Render(nested);
        for (int i = 0; i < actual.Pixels.Length; i += 4)
        { Assert.Equal(60, actual.Pixels[i]); Assert.Equal(40, actual.Pixels[i + 1]); Assert.Equal(25, actual.Pixels[i + 2]); Assert.Equal(255, actual.Pixels[i + 3]); }
    }
}
