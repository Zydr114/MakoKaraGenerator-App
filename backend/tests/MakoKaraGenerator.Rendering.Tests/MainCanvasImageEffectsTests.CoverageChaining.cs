using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    private static CompiledImageGraph TintWithCoverage(string id, GraphInputSource coverageSource, bool coverageFirst = false)
    {
        var shader = new ShaderProgramDefinition(id + ".sksl",
            "uniform shader color; uniform shader mask; half4 main(float2 p) { half4 c=color.eval(p); half a=mask.eval(p).a; return half4(c.rgb*(0.5 + 0.5*a), c.a); }",
            ShaderProgramDefinition.RasterProfile, [],
            [new("color", ShaderSurfaceKind.Color), new("mask", ShaderSurfaceKind.Coverage)]);
        var colorInput = new GraphInputDefinition("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas);
        var maskInput = new GraphInputDefinition("mask", coverageSource, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas);
        var graph = new ImageGraphDefinition(id, ImageGraphDefinition.CurrentVersion,
            coverageFirst ? [maskInput, colorInput] : [colorInput, maskInput], [],
            [new ImageGraphNode.RuntimeShader("out", id, ShaderSurfaceKind.Color, [],
                [new("color", GraphReference.Input("color")), new("mask", GraphReference.Input("mask"))],
                new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent))],
            GraphReference.Node("out"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(graph, [], [new(id, shader)]));
    }

    private static SceneImageGraphProgram CoverageProgram(string graphId, CompiledImageGraph compiled, bool includeDescendantGroups = false)
        => new(Catalog.GetGraph(graphId), compiled, includeDescendantGroups ? GroupWhole : Whole, Bounds);

    [Theory]
    [InlineData(GraphInputSource.SungCoverage, false)]
    [InlineData(GraphInputSource.SungCoverage, true)]
    [InlineData(GraphInputSource.GlyphCoverage, false)]
    [InlineData(GraphInputSource.StrokeCoverage, false)]
    [InlineData(GraphInputSource.TargetRegionCoverage, false)]
    public void 整行全量图后接带覆盖率遮罩的全量图正常链式叠加(GraphInputSource coverageSource, bool coverageFirst)
    {
        var request = Request(Snapshot(decorated: true), time: 2_000_000);
        var target = Line(request);
        using var first = Tint("first");
        using var second = TintWithCoverage("last", coverageSource, coverageFirst);
        using var resources = new SceneImageEffectResources([
            new(Catalog.GetGraph("first"), first, Whole, Bounds),
            CoverageProgram("last", second),
        ]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);

        using var actual = renderer.Render(Bindings(request, Bind("shadow", target, "first"), Bind("echo", target, "last")));
        Assert.Contains(actual.Pixels.ToArray(), value => value != 0);
        Assert.Empty(actual.ImageEffectDiagnostics);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 两个均带覆盖率遮罩的整行图正反顺序叠加均不冲突()
    {
        var request = Request(Snapshot(decorated: true), time: 2_000_000);
        var target = Line(request);
        using var a = TintWithCoverage("first", GraphInputSource.SungCoverage);
        using var b = TintWithCoverage("last", GraphInputSource.GlyphCoverage);
        using var resources = new SceneImageEffectResources([
            CoverageProgram("first", a),
            CoverageProgram("last", b),
        ]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);

        using var ab = renderer.Render(Bindings(request, Bind("a", target, "first"), Bind("b", target, "last")));
        using var ba = renderer.Render(Bindings(request, Bind("b", target, "last"), Bind("a", target, "first")));
        Assert.Contains(ab.Pixels.ToArray(), value => value != 0);
        Assert.Contains(ba.Pixels.ToArray(), value => value != 0);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 元素级全量图后接带覆盖率遮罩的整行图正常链式叠加()
    {
        var request = Request(Snapshot(decorated: true, secondToken: true), time: 1_500_000);
        var token = new Fx.ImageEffectTarget.Token(request.PreparedFrame!.FinalGeometry[0].WithRuby);
        var line = Line(request);
        using var tokenGraph = Tint("first");
        using var lineGraph = TintWithCoverage("last", GraphInputSource.SungCoverage);
        using var resources = new SceneImageEffectResources([
            new(Catalog.GetGraph("first"), tokenGraph, Whole, Bounds),
            CoverageProgram("last", lineGraph),
        ]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);

        using var actual = renderer.Render(Bindings(request, Bind("word-blur", token, "first"), Bind("line-echo", line, "last")));
        Assert.Contains(actual.Pixels.ToArray(), value => value != 0);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 组图在子行融合后读取全量颜色与覆盖率遮罩正常合成()
    {
        var request = Request(Snapshot(decorated: true), time: 2_000_000);
        using var child = Tint("first");
        using var parent = TintWithCoverage("last", GraphInputSource.SungCoverage);
        using var resources = new SceneImageEffectResources([
            Program("first", child, Whole),
            CoverageProgram("last", parent, includeDescendantGroups: true),
        ]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);

        using var actual = renderer.Render(Bindings(request, Bind("child", Line(request), "first"), Bind("parent", GroupRoot(request), "last")));
        Assert.Contains(actual.Pixels.ToArray(), value => value != 0);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 无TargetColor仅覆盖率遮罩的替换图在已融合整行上仍拒绝部分替换()
    {
        var request = Request(Snapshot(decorated: true), time: 2_000_000);
        var target = Line(request);
        var shader = new ShaderProgramDefinition("glass.sksl",
            "uniform shader below; half4 main(float2 p) { half4 c=below.eval(p); return half4(c.b,c.g,c.r,c.a); }",
            ShaderProgramDefinition.RasterProfile, [], [new("below", ShaderSurfaceKind.Color)]);
        var nodeBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
        var maskOnlyDef = new ImageGraphDefinition("last", ImageGraphDefinition.CurrentVersion,
            [new("below", GraphInputSource.BackdropBelow, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
                new("mask", GraphInputSource.GlyphCoverage, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.RuntimeShader("tint", "glass", ShaderSurfaceKind.Color, [], [new("below", GraphReference.Input("below"))], nodeBounds),
                new ImageGraphNode.Mask("masked", GraphReference.Node("tint"), GraphReference.Input("mask"), nodeBounds)],
            GraphReference.Node("masked"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly,
            GraphCompositeMode.ReplaceCoveredBackdrop, GraphReference.Input("mask"));
        using var first = Tint("first");
        using var maskOnly = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(maskOnlyDef, [], [new("glass", shader)]));
        using var resources = new SceneImageEffectResources([
            new(Catalog.GetGraph("first"), first, Whole, Bounds),
            new(Catalog.GetGraph("last"), maskOnly, Fill, Bounds),
        ]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);

        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, Bind("first", target, "first"), Bind("mask-only", target, "last"))));
        Assert.Equal("ImageEffectPartialFusion", error.Code);
        Assert.Equal(new Fx.EffectInstanceId("mask-only"), error.InstanceId);
    }
}
