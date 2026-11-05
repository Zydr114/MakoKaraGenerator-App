using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

// Checks candidate graph admission without capturing pixels.
public sealed class GroupImagePreflightBudgetTests
{
    private static readonly SceneCaptureRecipe Recipe = new(true, true, true, true, SceneCaptureRegion.FillAndStroke);
    private static GraphNodeBounds Bounds(double expand = 0) => new(new GraphExpansion.Literal(expand), expand, expand, GraphEdgeMode.Transparent);
    private static GraphInputDefinition Input(string id = "target", GraphInputSource source = GraphInputSource.TargetColor, GraphBackgroundRequirement requirement = GraphBackgroundRequirement.None)
        => new(id, source, ShaderSurfaceKind.Color, true, requirement, GraphCoordinateSpace.Canvas);
    private static ImageGraphFrameContext Frame(bool transparent = false) => new(0, 16, 16, new(0, 0, 16, 16), 1, 1, ParameterValues.Empty, transparent);
    private static ImageGraphDefinition Definition(ImageGraphNode[] nodes, GraphReference output, GraphInputDefinition[]? inputs = null, double expand = 0, GraphReference? bypass = null)
        => new("empty-preflight", ImageGraphDefinition.CurrentVersion, inputs ?? [Input()], [], nodes, output, GraphCoordinateSpace.Canvas,
            expand, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: bypass);
    private static CompiledImageGraph Compile(ImageGraphDefinition definition, GraphExecutionBudget budget)
        => CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [], budget));
    private static void Check(CompiledImageGraph graph, ImageGraphFrameContext? frame = null, Func<string, SceneCaptureRecipe>? recipe = null)
        => graph.ValidateScenePreflight(frame ?? Frame(), new Dictionary<string, SkiaGraphInput>(), recipe ?? (_ => Recipe), default);
    [Theory]
    [InlineData("count")] [InlineData("bytes")]
    public void 空目标预检拒绝节点峰值表面数或字节超限并与实际执行相同(string mode)
    {
        var node = new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds());
        var budget = GraphExecutionBudget.Default with { MaximumSurfaces = mode == "count" ? 1 : 64, MaximumBytes = mode == "bytes" ? 1500 : 1_000_000 };
        using var graph = Compile(Definition([node], GraphReference.Node("move")), budget);
        Assert.Equal("GraphSurfaceBudgetExceeded", Assert.Throws<ImageGraphException>(() => Check(graph)).Code);
        using var srgb = SKColorSpace.CreateSrgb(); using var surface = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        surface.Canvas.Clear(SKColors.Red); using var image = surface.Snapshot();
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 16, 16), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        var actual = Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["target"] = new(input, GraphInputSource.TargetColor) }));
        Assert.Equal("GraphSurfaceBudgetExceeded", actual.Code);
    }
    [Fact]
    public void 空目标Blur也包含真实两个scratch峰值且不把全部链节点累加为常驻()
    {
        var blur = new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Literal(1), Bounds(3));
        using var rejected = Compile(Definition([blur], GraphReference.Node("blur"), expand: 3), GraphExecutionBudget.Default with { MaximumSurfaces = 3 });
        Assert.Equal("GraphSurfaceBudgetExceeded", Assert.Throws<ImageGraphException>(() => Check(rejected)).Code);
        var chain = Enumerable.Range(0, 12).Select(index => (ImageGraphNode)new ImageGraphNode.Transform("n" + index,
            index == 0 ? GraphReference.Input("target") : GraphReference.Node("n" + (index - 1)), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds())).ToArray();
        using var accepted = Compile(Definition(chain, GraphReference.Node("n11")), GraphExecutionBudget.Default with { MaximumSurfaces = 3, MaximumBytes = 3072 });
        Check(accepted);
    }
    [Fact]
    public void 同来源同配方输入按共享捕获去重而不同配方仍独立计入()
    {
        var blend = new ImageGraphNode.Blend("blend", GraphReference.Input("a"), GraphReference.Input("b"), GraphBlendMode.SourceOver, Bounds());
        using var graph = Compile(Definition([blend], GraphReference.Node("blend"), [Input("a"), Input("b")]),
            GraphExecutionBudget.Default with { MaximumSurfaces = 2, MaximumBytes = 2048 });
        Check(graph);
        var error = Assert.Throws<ImageGraphException>(() => Check(graph, recipe: id => id == "a" ? Recipe : Recipe with { IncludeRuby = false }));
        Assert.Equal("GraphSurfaceBudgetExceeded", error.Code);
    }
    [Fact]
    public void 透明依赖旁路按一个原输入预算而不计不会执行的节点scratch()
    {
        var blend = new ImageGraphNode.Blend("blend", GraphReference.Input("target"), GraphReference.Input("background"), GraphBlendMode.SourceOver, Bounds());
        using var graph = Compile(Definition([blend], GraphReference.Node("blend"), [Input(), Input("background", GraphInputSource.BackgroundColor, GraphBackgroundRequirement.OpaqueCanvas)], bypass: GraphReference.Input("target")),
            GraphExecutionBudget.Default with { MaximumSurfaces = 1, MaximumBytes = 1024 });
        Check(graph, Frame(true));
        Assert.Equal("GraphSurfaceBudgetExceeded", Assert.Throws<ImageGraphException>(() => Check(graph, Frame(false))).Code);
    }
}

public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(false, "count")] [InlineData(false, "bytes")] [InlineData(true, "count")] [InlineData(true, "bytes")]
    public void 无可见孩子或显式空配方仍不能吞图峰值超限且完整归因回收(bool noVisibleChildren, string limit)
    {
        var request = noVisibleChildren
            ? Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true)), time: 10_000_000)
            : Request(Snapshot());
        var graphBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
        var node = new ImageGraphNode.Transform("copy", GraphReference.Input("color"), SceneMatrix.Identity, Bounds, graphBounds);
        var graphDefinition = new ImageGraphDefinition("empty-peak", ImageGraphDefinition.CurrentVersion,
            [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [node],
            GraphReference.Node("copy"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        var graphBudget = GraphExecutionBudget.Default with { MaximumSurfaces = limit == "count" ? 1 : 64, MaximumBytes = limit == "bytes" ? 1_000_000 : 10_000_000 };
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(graphDefinition, [], [], graphBudget));
        // Whole excludes descendant groups. The visible-root case has an explicit
        // empty selection; the late case has no real visible descendants at all.
        using var resources = new SceneImageEffectResources([Program("first", graph, Whole)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var target = noVisibleChildren
            ? new ImageEffectTarget.Group(request.PreparedFrame!.FinalGroupGeometry.First(item => item.WorldInkCorners.IsEmpty
                && request.PreparedFrame.HierarchyScene.Elements.Any(node => node.Id == item.Target.NodeId.Value && node.SourceNodeId is null)).Target)
            : GroupRoot(request);
        var geometry = request.PreparedFrame!.FinalGroupGeometry.Single(item => item.Target == target.Target);
        if (noVisibleChildren) Assert.Empty(geometry.WorldInkCorners); else Assert.NotEmpty(geometry.WorldInkCorners);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, Bind("empty-budget", target))));
        Assert.Equal("GraphSurfaceBudgetExceeded", error.Code); Assert.Equal("empty-budget", error.InstanceId.Value); Assert.Equal("artifact-empty-budget", error.ArtifactHash);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
}
