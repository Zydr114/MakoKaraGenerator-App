using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Effects;

public sealed class ImageGraphCompilerTests
{
    private static GraphNodeBounds Bounds(double expand = 0) => new(new GraphExpansion.Literal(expand), expand, expand, GraphEdgeMode.Transparent);
    private static GraphInputDefinition Input(string id = "target", ShaderSurfaceKind kind = ShaderSurfaceKind.Color, GraphInputSource source = GraphInputSource.TargetColor)
        => new(id, source, kind, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas);
    private static ImageGraphDefinition Graph(IEnumerable<ImageGraphNode> nodes, GraphReference output, IEnumerable<GraphInputDefinition>? inputs = null,
        IEnumerable<string>? parameters = null, double expand = 256, GraphCompositeMode composite = GraphCompositeMode.SourceOver, GraphReference? coverage = null, GraphReference? bypass = null)
        => new("graph", ImageGraphDefinition.CurrentVersion, inputs ?? [Input()], parameters ?? [], nodes, output, GraphCoordinateSpace.Canvas, expand, GraphAnchorMode.ImageOnly, composite, coverage, bypass);
    private static CompiledImageGraphDefinition Compile(ImageGraphDefinition graph, IEnumerable<EffectParameterConstraint>? parameters = null,
        IEnumerable<KeyValuePair<string, ShaderProgramDefinition>>? programs = null, GraphExecutionBudget? budget = null)
        => ImageGraphCompiler.Compile(graph, parameters ?? [], programs ?? [], budget);
    private static string Code(Action action) => Assert.Throws<ImageGraphException>(action).Code;

    [Fact]
    public void 后定义边拓扑排序且原集合改变不会改变图()
    {
        var nodes = new List<ImageGraphNode>
        {
            new ImageGraphNode.Mask("last", GraphReference.Node("first"), GraphReference.Input("coverage"), Bounds()),
            new ImageGraphNode.Transform("first", GraphReference.Input("target"), SceneMatrix.Identity, new(0,0,16,16), Bounds()),
        };
        var graph = Graph(nodes, GraphReference.Node("last"), [Input(), Input("coverage", ShaderSurfaceKind.Coverage, GraphInputSource.GlyphCoverage)]);
        nodes.Clear(); var plan = Compile(graph);
        Assert.Equal(["first", "last"], plan.Nodes.Select(node => node.Id));
        Assert.Equal(ShaderSurfaceKind.Color, plan.Types[graph.Output]);
    }

    [Fact]
    public void 未执行节点仍拒绝环悬空及重复()
    {
        var cycle = Graph([new ImageGraphNode.Blur("a", GraphReference.Node("b"), new GraphNumberBinding.Literal(0), Bounds()),
            new ImageGraphNode.Blur("b", GraphReference.Node("a"), new GraphNumberBinding.Literal(0), Bounds())], GraphReference.Input("target"));
        Assert.Equal("GraphCycle", Code(() => Compile(cycle)));
        Assert.Equal("GraphDanglingReference", Code(() => Compile(Graph([], GraphReference.Node("missing")))));
        Assert.Equal("GraphDuplicateIdentity", Code(() => Compile(Graph([new ImageGraphNode.Blur("a", GraphReference.Input("target"), new GraphNumberBinding.Literal(0), Bounds()),
            new ImageGraphNode.Blur("a", GraphReference.Input("target"), new GraphNumberBinding.Literal(0), Bounds())], GraphReference.Input("target")))));
    }

    [Fact]
    public void 颜色覆盖端口不得混用且替换合成必须独立覆盖()
    {
        var input = Input("target", ShaderSurfaceKind.Coverage, GraphInputSource.StrokeCoverage);
        Assert.Equal("GraphPortTypeMismatch", Code(() => Compile(Graph([new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Literal(0), Bounds())], GraphReference.Node("blur"), [input]))));
        Assert.Equal("GraphPortTypeMismatch", Code(() => Compile(Graph([new ImageGraphNode.Mask("mask", GraphReference.Input("target"), GraphReference.Input("target"), Bounds())], GraphReference.Node("mask")))));
        Assert.Equal("GraphCompositeCoverageMissing", Code(() => Compile(Graph([], GraphReference.Input("target"), composite: GraphCompositeMode.ReplaceCoveredBackdrop))));
        Assert.Equal("GraphCompositeCoverageMissing", Code(() => Compile(Graph([], GraphReference.Input("target"), composite: GraphCompositeMode.ReplaceCoveredBackdrop, coverage: GraphReference.Input("target")))));
    }

    [Fact]
    public void 最大参数范围与累计外扩在分配前校验()
    {
        var blur = new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Parameter("sigma"),
            new(new GraphExpansion.CeilingMultiple("sigma", 3), 192, 192, GraphEdgeMode.Clamp));
        var graph = Graph([blur], GraphReference.Node("blur"), parameters: ["sigma"]);
        Assert.Single(Compile(graph, [new EffectParameterConstraint.Number("sigma", 0, 64)]).Nodes);
        Assert.Equal("GraphExpansionExceeded", Code(() => Compile(graph, [new EffectParameterConstraint.Number("sigma", 0, 65)])));
        Assert.Equal("GraphCumulativeExpansionExceeded", Code(() => Compile(Graph([blur], GraphReference.Node("blur"), parameters: ["sigma"], expand: 100), [new EffectParameterConstraint.Number("sigma", 0, 64)])));
        var shortBlur = new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Parameter("sigma"), Bounds(8));
        Assert.Equal("GraphBlurBoundsInsufficient", Code(() => Compile(Graph([shortBlur], GraphReference.Node("blur"), parameters: ["sigma"]), [new EffectParameterConstraint.Number("sigma", 0, 64)])));
        Assert.Equal("GraphNumberBindingMismatch", Code(() => Compile(graph, [new EffectParameterConstraint.Whole("sigma", 0, 64)])));
    }

    [Theory]
    [InlineData(GraphBlendMode.SourceOver)]
    [InlineData(GraphBlendMode.Plus)]
    [InlineData(GraphBlendMode.Multiply)]
    public void 三种混合为明确封闭操作(GraphBlendMode mode)
    {
        var blend = new ImageGraphNode.Blend("blend", GraphReference.Input("target"), GraphReference.Input("target"), mode, Bounds());
        Assert.Single(Compile(Graph([blend], GraphReference.Node("blend"))).Nodes);
    }

    [Fact]
    public void enum输入选择须完整且不得伪造背景依赖()
    {
        var choice = new GraphInputDefinition("target", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true,
            GraphBackgroundRequirement.OpaqueCanvas, GraphCoordinateSpace.Canvas, "source", [new("media", GraphInputSource.BackgroundMedia), new("below", GraphInputSource.BackdropBelow)]);
        var graph = Graph([], GraphReference.Input("target"), [choice], ["source"]);
        Assert.Equal(2, Assert.IsType<EffectParameterConstraint.Choice>(Compile(graph, [new EffectParameterConstraint.Choice("source", ["media", "below"])]).Parameters["source"]).Values.Length);
        Assert.Equal("InvalidGraphInputChoice", Code(() => Compile(graph, [new EffectParameterConstraint.Choice("source", ["media"])])));
        var fake = new GraphInputDefinition("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas);
        Assert.Equal("InvalidGraphBackgroundRequirement", Code(() => Compile(Graph([], GraphReference.Input("target"), [fake]))));
        Assert.Equal("InvalidGraphBackgroundRequirement", Code(() => Compile(Graph([], GraphReference.Input("target"), [Input(source: GraphInputSource.BackgroundMedia)]))));
        Assert.Equal("InvalidGraphBypass", Code(() => Compile(Graph([], GraphReference.Input("target"), [choice], ["source"], bypass: GraphReference.Input("target")),
            [new EffectParameterConstraint.Choice("source", ["media", "below"])])));
    }

    [Fact]
    public void shader绑定必须是完整类型参数及正确child()
    {
        var program = new ShaderProgramDefinition("a.sksl", "source", ShaderProgramDefinition.RasterProfile, [new("amount", ShaderUniformKind.Float)], [new("target", ShaderSurfaceKind.Color)]);
        var node = new ImageGraphNode.RuntimeShader("shader", "a", ShaderSurfaceKind.Color, [new("amount", new GraphUniformBinding.Parameter("amount"))], [new("target", GraphReference.Input("target"))], Bounds());
        var graph = Graph([node], GraphReference.Node("shader"), parameters: ["amount"]);
        Assert.Single(Compile(graph, [new EffectParameterConstraint.Number("amount", 0, 1)], [new("a", program)]).Programs);
        Assert.Equal("GraphUniformBindingMismatch", Code(() => Compile(graph, [new EffectParameterConstraint.Color("amount")], [new("a", program)])));
        Assert.Equal("GraphShaderMissing", Code(() => Compile(graph, [new EffectParameterConstraint.Number("amount", 0, 1)])));
        var bad = new ImageGraphNode.RuntimeShader("shader", "a", ShaderSurfaceKind.Color,
            [new("amount", new GraphUniformBinding.Literal(ShaderLiteral.Numeric(ShaderUniformKind.Float2, [0, 0])))], [new("target", GraphReference.Input("target"))], Bounds());
        Assert.Equal("GraphUniformBindingMismatch", Code(() => Compile(Graph([bad], GraphReference.Node("shader")), programs: [new("a", program)])));
    }

    [Fact]
    public void 非有限及不可逆变换资源身份和pass预算均拒绝()
    {
        var transform = new ImageGraphNode.Transform("transform", GraphReference.Input("target"), default, new(0, 0, 16, 16), Bounds());
        Assert.Equal("InvalidGraphTransform", Code(() => Compile(Graph([transform], GraphReference.Node("transform")))));
        var resource = Input("target", ShaderSurfaceKind.Color, GraphInputSource.ResourceImage);
        Assert.Equal("InvalidGraphInput", Code(() => Compile(Graph([], GraphReference.Input("target"), [resource]))));
        var graph = Graph(Enumerable.Range(0, 33).Select(i => new ImageGraphNode.Blur(i.ToString(System.Globalization.CultureInfo.InvariantCulture), GraphReference.Input("target"), new GraphNumberBinding.Literal(0), Bounds())), GraphReference.Input("target"));
        Assert.Equal("GraphPassBudgetExceeded", Code(() => Compile(graph)));
    }
}
