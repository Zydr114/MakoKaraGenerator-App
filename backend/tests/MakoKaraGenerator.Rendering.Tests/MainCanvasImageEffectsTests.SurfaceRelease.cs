using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    private static CompiledImageGraph FullCanvasTransformGraph(string id, string expression = "half4(c.rgb*.9,c.a)",
        GraphCompositeMode composite = GraphCompositeMode.SourceOver, bool requireMedia = false)
    {
        var shader = new ShaderProgramDefinition(id + ".sksl",
            "uniform shader color; half4 main(float2 p) { half4 c=color.eval(p); return " + expression + "; }",
            ShaderProgramDefinition.RasterProfile, [], [new("color", ShaderSurfaceKind.Color)]);
        List<GraphInputDefinition> inputs =
        [
            new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
        ];
        if (requireMedia)
            inputs.Add(new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas));
        if (composite == GraphCompositeMode.ReplaceCoveredBackdrop)
            inputs.Add(new("mask", GraphInputSource.GlyphCoverage, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas));
        var nodeBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 16, GraphEdgeMode.Transparent);
        List<ImageGraphNode> nodes =
        [
            new ImageGraphNode.RuntimeShader("shade", id, ShaderSurfaceKind.Color, [], [new("color", GraphReference.Input("color"))], nodeBounds),
            new ImageGraphNode.Transform("fit", GraphReference.Node("shade"), SceneMatrix.Identity, Bounds, nodeBounds),
        ];
        if (requireMedia)
            nodes.Add(new ImageGraphNode.Blend("mix", GraphReference.Node("fit"), GraphReference.Input("media"), GraphBlendMode.SourceOver, nodeBounds));
        var output = GraphReference.Node(requireMedia ? "mix" : "fit");
        var definition = new ImageGraphDefinition(id, ImageGraphDefinition.CurrentVersion, inputs, [], nodes, output,
            GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, composite,
            compositeCoverage: composite == GraphCompositeMode.ReplaceCoveredBackdrop ? GraphReference.Input("mask") : null,
            bypassInput: requireMedia ? GraphReference.Input("color") : null);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new(id, shader)]));
    }

    [Fact]
    public void 连续SourceOver图立即释放临时输入与过期历史使峰值与链长无关()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: true)));
        var target = Line(request);
        using var graph = FullCanvasTransformGraph("chain");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);

        using var shortBudget = new ImageSurfaceBudgetSession();
        using var shortRenderer = new SkiaRenderPipeline(null, null, shortBudget, resources);
        using var shortRender = shortRenderer.Render(Bindings(request, Bind("g0", target), Bind("g1", target)));

        var tenBindings = Enumerable.Range(0, 10).Select(i => Bind("g" + i, target)).ToArray();
        using var longBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = shortBudget.Usage.PeakSurfaces });
        using var longRenderer = new SkiaRenderPipeline(null, null, longBudget, resources);
        using var longRender = longRenderer.Render(Bindings(request, tenBindings));

        Assert.Equal(shortBudget.Usage.PeakSurfaces, longBudget.Usage.PeakSurfaces);
        Assert.Equal(shortBudget.Usage.PeakBytes, longBudget.Usage.PeakBytes);
        Assert.Equal(0, longBudget.Usage.ActiveSurfaces);
        Assert.Equal(0, longBudget.Usage.EstimatedBytes);
        Assert.Contains(longRender.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public void 旁路图立即释放临时输入与旁路结果不累积表面峰值()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: false)), transparent: true);
        var target = Line(request);
        using var bypassGraph = FullCanvasTransformGraph("bypass", requireMedia: true);
        using var tintGraph = FullCanvasTransformGraph("tint");
        using var resources = new SceneImageEffectResources([Program("first", bypassGraph), Program("last", tintGraph)]);

        var bindings = Enumerable.Range(0, 8).Select(i => Bind("bypass-" + i, target, "first"))
            .Append(Bind("final", target, "last")).ToArray();
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 7 });
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        using var rendered = renderer.Render(Bindings(request, bindings));

        Assert.Equal(8, rendered.ImageEffectDiagnostics.Length);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.Contains(rendered.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public void 替换合成后接多段SourceOver保留替换覆盖同时释放多余历史()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: false)), transparent: false);
        var target = Line(request);
        using var replace = FullCanvasTransformGraph("replace", "half4(0,0,0,0)", GraphCompositeMode.ReplaceCoveredBackdrop);
        using var pass = FullCanvasTransformGraph("pass", "c");
        using var resources = new SceneImageEffectResources([Program("first", replace), Program("last", pass)]);

        using var twoStepBudget = new ImageSurfaceBudgetSession();
        using var twoStepRenderer = new SkiaRenderPipeline(null, null, twoStepBudget, resources);
        using var twoStep = twoStepRenderer.Render(Bindings(request, Bind("r0", target, "first"), Bind("p0", target, "last"), Bind("p1", target, "last")));

        var many = new[] { Bind("r0", target, "first") }
            .Concat(Enumerable.Range(0, 8).Select(i => Bind("p" + i, target, "last"))).ToArray();
        using var manyBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = twoStepBudget.Usage.PeakSurfaces });
        using var manyRenderer = new SkiaRenderPipeline(null, null, manyBudget, resources);
        using var chained = manyRenderer.Render(Bindings(request, many));

        Assert.Equal(twoStep.Pixels.ToArray(), chained.Pixels.ToArray());
        Assert.Equal(twoStepBudget.Usage.PeakSurfaces, manyBudget.Usage.PeakSurfaces);
        Assert.Equal(0, manyBudget.Usage.ActiveSurfaces);
        Assert.Equal(0, manyBudget.Usage.EstimatedBytes);
    }
}
