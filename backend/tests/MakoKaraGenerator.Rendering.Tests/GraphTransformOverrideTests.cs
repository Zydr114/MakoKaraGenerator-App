using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GraphTransformOverrideTests
{
    private static readonly GraphRect Bounds = new(0, 0, 16, 16);
    private static GraphNodeBounds Limits(double sample = 8) => new(new GraphExpansion.Literal(0), 0, sample, GraphEdgeMode.Transparent);
    private static ImageGraphFrameContext Frame(double dx = 1, double dy = 1, GraphTransformValues? transforms = null)
        => new(500_000, 16, 16, Bounds, dx, dy, ParameterValues.Empty, true) { Transforms = transforms ?? GraphTransformValues.Empty };
    private static GraphTransformValues Move(double x = 4, Rect2? bounds = null) => new([new("move", new(Affine2.Translation(x, 0), bounds ?? new(0, 0, 16, 16)))]);
    private static CompiledImageGraph Graph(SceneMatrix? matrix = null, GraphRect? bounds = null, GraphExecutionBudget? budget = null,
        GraphEdgeMode edge = GraphEdgeMode.Transparent)
    {
        var definition = new ImageGraphDefinition("transform-override", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.Transform("move", GraphReference.Input("target"), matrix ?? SceneMatrix.Identity, bounds ?? Bounds, Limits() with { Edge = edge })],
            GraphReference.Node("move"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [], budget));
    }
    private sealed class Source : IDisposable
    {
        private readonly SKSurface _surface;
        private readonly SKImage _image;
        public Source(double dx = 1, double dy = 1)
        {
            using var srgb = SKColorSpace.CreateSrgb();
            _surface = SKSurface.Create(new SKImageInfo((int)(16 * dx), (int)(16 * dy), SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            var canvas = _surface.Canvas; canvas.ClipRect(new(0, 0, (float)(16 * dx), (float)(16 * dy))); canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)dx, (float)dy); using var paint = new SKPaint { Color = SKColors.Red }; canvas.DrawRect(2, 4, 2, 4, paint);
            _image = _surface.Snapshot(); Value = SkiaGraphSurface.Borrow(_image, new(Bounds, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, dx, dy), ShaderSurfaceKind.Color);
        }
        public SkiaGraphSurface Value { get; }
        public Dictionary<string, SkiaGraphInput> Inputs() => new() { ["target"] = new(Value, GraphInputSource.TargetColor) };
        public void Dispose() { Value.Dispose(); _image.Dispose(); _surface.Dispose(); }
    }
    private static byte[] Bytes(SkiaGraphSurface surface) => SkiaTestReadback.Pixels(surface.Image);
    private static byte Alpha(SkiaGraphSurface surface, int x, int y) => Bytes(surface)[(y * surface.Image.Width + x) * 4 + 3];

    [Theory]
    [InlineData(1d, 1d)] [InlineData(.5d, .5d)] [InlineData(2d, 1d)] [InlineData(.5d, 2d)]
    public void 动态矩阵与真实输出框逐像素匹配固定图且随机重复输入不串帧(double dx, double dy)
    {
        using var source = new Source(dx, dy); using var dynamicGraph = Graph();
        using var fixedGraph = Graph(SceneMatrix.Translate(4, 0), new(0, 0, 12, 16));
        var frame = Frame(dx, dy, Move(bounds: new(0, 0, 12, 16)));
        using var expected = fixedGraph.Execute(Frame(dx, dy), source.Inputs());
        using var first = dynamicGraph.Execute(frame, source.Inputs());
        Assert.Equal(new GraphRect(0, 0, 12, 16), first.Output.Descriptor.Bounds);
        Assert.Equal((int)(12 * dx), first.Output.Image.Width); Assert.Equal((int)(16 * dy), first.Output.Image.Height);
        Assert.Equal(Bytes(expected.Output), Bytes(first.Output));
        Assert.Equal(255, Alpha(first.Output, (int)(7 * dx), (int)(6 * dy)));
        Assert.Equal(0, Alpha(first.Output, (int)(3 * dx), (int)(6 * dy)));
        using var middle = dynamicGraph.Execute(Frame(dx, dy, Move(0)), source.Inputs());
        Assert.Equal(255, Alpha(middle.Output, (int)(3 * dx), (int)(6 * dy)));
        using var repeated = dynamicGraph.Execute(frame with { TimeUs = 1_500_000 }, source.Inputs());
        Assert.Equal(Bytes(first.Output), Bytes(repeated.Output));
        using var unchanged = dynamicGraph.Execute(Frame(dx, dy), source.Inputs());
        Assert.Equal(Bytes(source.Value), Bytes(unchanged.Output));
    }

    [Theory]
    [InlineData(GraphEdgeMode.Transparent)] [InlineData(GraphEdgeMode.Clamp)] [InlineData(GraphEdgeMode.Repeat)]
    public void 缩放反射及负坐标逆采样保留完整源域和边缘语义(GraphEdgeMode edge)
    {
        using var source = new Source(.5, 2);
        var matrix = new SceneMatrix(-1, 0, .25, 1, 16, 0);
        using var graph = Graph(edge: edge);
        using var expected = Graph(matrix, edge: edge);
        var values = new GraphTransformValues([new("move", new(new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty), new(0, 0, 16, 16)))]);
        // Reflection displaces up to 20 logical pixels at this output ROI.
        var definition = graph.Plan.Definition;
        var node = new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Identity, Bounds, Limits(32) with { Edge = edge });
        using var reflected = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(new(definition.Id, definition.Version,
            definition.Inputs, [], [node], definition.Output, definition.Space, 0, definition.AnchorMode, definition.Composite), [], []));
        using var actual = reflected.Execute(Frame(.5, 2, values), source.Inputs());
        using var baseline = expected.Execute(Frame(.5, 2), source.Inputs());
        Assert.Equal(Bytes(baseline.Output), Bytes(actual.Output)); Assert.Contains(Bytes(actual.Output), value => value != 0);
    }

    [Theory]
    [InlineData("unknown", "GraphTransformNodeUnknown")]
    [InlineData("shader", "GraphTransformNodeTypeMismatch")]
    [InlineData("offset", "GraphTransformSamplingExceeded")]
    [InlineData("bounds", "GraphTransformBoundsExceeded")]
    public void 非法覆盖在任何表面分配前失败且后续帧恢复(string kind, string code)
    {
        var shader = new ShaderProgramDefinition("fill.sksl", "half4 main(float2 p) { return half4(1,0,0,1); }", ShaderProgramDefinition.RasterProfile, [], []);
        var definition = new ImageGraphDefinition("validation", ImageGraphDefinition.CurrentVersion, [], [],
            [new ImageGraphNode.RuntimeShader("shader", "fill", ShaderSurfaceKind.Color, [], [], Limits()),
             new ImageGraphNode.Transform("move", GraphReference.Node("shader"), SceneMatrix.Identity, Bounds, Limits())],
            GraphReference.Node("move"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new("fill", shader)]));
        var overrides = kind switch
        {
            "unknown" => new GraphTransformValues([new("missing", new(Affine2.Identity, new(0, 0, 16, 16)))]),
            "shader" => new GraphTransformValues([new("shader", new(Affine2.Identity, new(0, 0, 16, 16)))]),
            "offset" => Move(9),
            _ => Move(bounds: new(-1, 0, 16, 16)),
        };
        using var budget = new ImageSurfaceBudgetSession(); using var resources = budget.BeginFrame();
        var failure = Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(transforms: overrides), new Dictionary<string, SkiaGraphInput>(), resources));
        Assert.Equal(code, failure.Code); Assert.Equal(definition.Id, failure.GraphId); Assert.Contains(kind is "unknown" or "shader" ? "transforms." : "nodes.move", failure.Field);
        Assert.Equal(0, budget.Usage.PeakSurfaces); Assert.Equal(0, budget.Usage.ActiveSurfaces);
        using var recovered = graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput>(), resources);
        Assert.Equal(255, Alpha(recovered.Output, 8, 8));
    }

    [Fact]
    public void 未执行节点的覆盖仍校验且透明旁路不能隐藏逆采样错误()
    {
        var definition = new ImageGraphDefinition("bypass", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
             new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Identity, Bounds, Limits())],
            GraphReference.Input("media"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: GraphReference.Input("target"));
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [])); using var source = new Source();
        Assert.Equal("GraphTransformSamplingExceeded", Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(transforms: Move(9)), source.Inputs())).Code);
        using var result = graph.Execute(Frame(transforms: Move()), source.Inputs());
        Assert.Same(source.Value, result.Output); Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void 输出像素预算使用覆盖框取消不发布部分结果并保留输入()
    {
        using var source = new Source(); using var graph = Graph(budget: GraphExecutionBudget.Default with { MaximumBytes = 1600 });
        using var budget = new ImageSurfaceBudgetSession(); using var resources = budget.BeginFrame();
        using (var small = graph.Execute(Frame(transforms: Move(bounds: new(0, 0, 8, 16))), source.Inputs(), resources))
        { Assert.Equal(8, small.Output.Image.Width); Assert.Equal(1536, small.PeakBytes); }
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal("GraphSurfaceBudgetExceeded", Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(transforms: Move()), source.Inputs(), resources)).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => graph.Execute(Frame(transforms: Move()), source.Inputs(), resources, cancel.Token));
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(255, Alpha(source.Value, 3, 6));
    }

    [Fact]
    public void 固定v1矩阵保持零采样声明语义而新增覆盖严格拒绝()
    {
        var definition = new ImageGraphDefinition("legacy-transform", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Translate(4, 0), Bounds, Limits(0))],
            GraphReference.Node("move"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [])); using var source = new Source();
        using var fixedResult = graph.Execute(Frame(), source.Inputs()); Assert.Equal(255, Alpha(fixedResult.Output, 7, 6));
        Assert.Equal("GraphTransformSamplingExceeded", Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(transforms: Move()), source.Inputs())).Code);
    }

    [Fact]
    public void 值层拒绝空框原生退化矩阵和重复身份且内容相等覆盖完整参数()
    {
        Assert.Throws<ArgumentException>(() => new GraphTransformValue(default, new(0, 0, 16, 16)));
        Assert.Throws<ArgumentException>(() => new GraphTransformValue(new(1, 1, 1, 1 + 1e-12, 0, 0), new(0, 0, 16, 16)));
        Assert.Throws<ArgumentException>(() => new GraphTransformValue(Affine2.Identity, new(100_000_000, 0, 100_000_001, 16)));
        Assert.Throws<ArgumentException>(() => new GraphTransformValue(Affine2.Identity, default));
        Assert.Throws<ArgumentException>(() => new GraphTransformValues([new("move", new(Affine2.Identity, new(0, 0, 16, 16))), new("move", new(Affine2.Identity, new(0, 0, 16, 16)))]));
        var catalog = new ResourceCatalog("values", "sha256:" + new string('a', 64), [], ["image"], ["image.png"]);
        ParameterValues Parameters(bool changed = false) => new([new("curve", new ParameterValue.Curve(new CurveValue.ControlPoints([new(0, 0), new(.5, changed ? .4 : .5), new(1, 1)]))),
            new("asset", new ParameterValue.Asset(catalog.GetImage("image.png"))), new("number", new ParameterValue.Number(.5))]);
        GraphBindingValue Binding(bool changed = false) => new(catalog.GetGraph("image"), Parameters(changed), GraphUniformValues.Empty, Move());
        var a = Binding(); var b = Binding(); Assert.Equal(a, b); Assert.Equal(a.GetHashCode(), b.GetHashCode()); Assert.NotEqual(a, Binding(true));
        var target = new ImageEffectTarget.Line(new(new("line"), new("binding")));
        Assert.Equal(new ImageEffectBinding(target, a), new ImageEffectBinding(target, b));
        var foreign = new ResourceCatalog("values", "sha256:" + new string('b', 64), [], ["image"], ["image.png"]);
        Assert.NotEqual(a, new GraphBindingValue(foreign.GetGraph("image"), b.Parameters, b.Uniforms, b.Transforms));
        var plus = new GraphBindingValue(a.Graph, a.Parameters, new([new(("shader", "number"), UniformValue.Float(0d))]), a.Transforms);
        var minus = new GraphBindingValue(a.Graph, a.Parameters, new([new(("shader", "number"), UniformValue.Float(-0d))]), a.Transforms);
        Assert.NotEqual(plus, minus);
    }

    [Fact]
    public void float32逆矩阵溢出和密度下溢在分配前拒绝且精确节点字段保留()
    {
        using var source = new Source();
        using var graph = Graph(new(1e-100, 0, 0, 1e-100, 0, 0));
        var native = Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(), source.Inputs()));
        Assert.Equal("GraphNativeValueRange", native.Code); Assert.Contains("nodes.move", native.Field);
        using var valid = Graph(); using var budget = new ImageSurfaceBudgetSession(); using var resources = budget.BeginFrame();
        var density = Assert.Throws<ImageGraphException>(() => valid.Execute(Frame(1e-100, 1), source.Inputs(), resources));
        Assert.True(density.Code is "GraphNativeValueRange" or "GraphSurfaceDimensionExceeded", density.Message);
        Assert.Equal(0, budget.Usage.PeakSurfaces);
        Assert.Throws<ArgumentException>(() => new GraphTransformValue(new(1e-40, 0, 0, 1, 0, 0), new(0, 0, 16, 16)));
    }

    [Fact]
    public void 材质作者缓存命中完整覆盖副本而矩阵框变化立即失效()
    {
        var shader = new ShaderProgramDefinition("fill.sksl", "half4 main(float2 p) { return half4(p.x<4 ? 1 : 0,0,0,1); }", ShaderProgramDefinition.RasterProfile, [], []);
        var definition = new ImageGraphDefinition("cached-transform", ImageGraphDefinition.CurrentVersion, [], [],
            [new ImageGraphNode.RuntimeShader("fill", "fill", ShaderSurfaceKind.Color, [], [], Limits()),
             new ImageGraphNode.Transform("move", GraphReference.Node("fill"), SceneMatrix.Identity, Bounds, Limits())],
            GraphReference.Node("move"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new("fill", shader)]));
        using var budget = new ImageSurfaceBudgetSession(); using var resources = budget.BeginFrame(); using var cache = new MaterialAuthorGraphCache(resources);
        var frame = Frame(transforms: Move()); var result = graph.Execute(frame, new Dictionary<string, SkiaGraphInput>(), resources);
        Assert.True(cache.TryStore(graph, result));
        using (var same = cache.Take(graph, Frame(transforms: Move()))) Assert.Same(result, same);
        var next = graph.Execute(frame, new Dictionary<string, SkiaGraphInput>(), resources); Assert.True(cache.TryStore(graph, next));
        Assert.Null(cache.Take(graph, Frame(transforms: Move(2))));
        var smaller = graph.Execute(frame, new Dictionary<string, SkiaGraphInput>(), resources); Assert.True(cache.TryStore(graph, smaller));
        Assert.Null(cache.Take(graph, Frame(transforms: Move(bounds: new(0, 0, 8, 16)))));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }
}
