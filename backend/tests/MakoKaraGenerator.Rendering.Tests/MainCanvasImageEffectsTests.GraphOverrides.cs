using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    private static CompiledImageGraph OverrideGraph(SceneMatrix? fixedMatrix = null, double literalAmount = 1)
    {
        var shader = new ShaderProgramDefinition("overrides.sksl", "uniform shader color; uniform float amount; half4 main(float2 p) { return color.eval(p)*amount; }",
            ShaderProgramDefinition.RasterProfile, [new("amount", ShaderUniformKind.Float)], [new("color", ShaderSurfaceKind.Color)]);
        var graph = new ImageGraphDefinition("overrides", ImageGraphDefinition.CurrentVersion,
            [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.RuntimeShader("tint", "tint", ShaderSurfaceKind.Color,
                [new("amount", new GraphUniformBinding.Literal(ShaderLiteral.Numeric(ShaderUniformKind.Float, [literalAmount])))],
                [new("color", GraphReference.Input("color"))], new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent)),
             new ImageGraphNode.Transform("move", GraphReference.Node("tint"), fixedMatrix ?? SceneMatrix.Identity, Bounds,
                new(new GraphExpansion.Literal(0), 0, 16, GraphEdgeMode.Transparent))],
            GraphReference.Node("move"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(graph, [], [new("tint", shader)]));
    }
    private static PreparedImageEffectBinding OverrideBinding(RenderRequest request, string invalid = "")
    {
        var value = new Fx.GraphBindingValue(Catalog.GetGraph("first"), Fx.ParameterValues.Empty,
            new([new(("tint", invalid == "uniform" ? "missing" : "amount"), Fx.UniformValue.Float(.5))]),
            new([new(invalid == "unknown" ? "missing" : invalid == "type" ? "tint" : "move",
                new(Fx.Affine2.Translation(invalid == "offset" ? 17 : 8, 0), new(0, 0, 640, 360)))]));
        return new(new("override-owner"), "override-artifact", new Fx.ImageEffectBinding(Line(request), value))
        { PackageIdentity = new("override-package", "sha256:" + new string('e', 64), "controller") };
    }

    [Theory]
    [InlineData(640, 360)] [InlineData(320, 180)] [InlineData(640, 180)]
    public void 动态uniform矩阵在正式主渲染和独立入口都匹配固定图像素且不改变锚点(int width, int height)
    {
        var request = Request(Snapshot(false), width: width, height: height);
        var binding = OverrideBinding(request); var bound = Bindings(request, binding);
        using var dynamicGraph = OverrideGraph(); using var fixedGraph = OverrideGraph(SceneMatrix.Translate(8, 0), .5);
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), dynamicGraph, Whole, Bounds)]);
        using var expectedResources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), fixedGraph, Whole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var pipeline = new SkiaRenderPipeline(null, null, budget, resources);
        using var expectedPipeline = new SkiaRenderPipeline(null, null, null, expectedResources);
        using var actual = pipeline.Render(bound);
        var expectedBinding = new PreparedImageEffectBinding(binding.InstanceId, binding.ArtifactHash, new(Line(request), Catalog.GetGraph("first"), Fx.ParameterValues.Empty));
        using var expected = expectedPipeline.Render(Bindings(request, expectedBinding));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 0);
        using var standalone = pipeline.ExecuteSceneImageEffect(bound, binding, Program("first", dynamicGraph), new Dictionary<string, SkiaGraphInput>(), null);
        var expectedRequest = Bindings(request, expectedBinding);
        using var standaloneExpected = expectedPipeline.ExecuteSceneImageEffect(expectedRequest, expectedBinding, Program("first", fixedGraph), new Dictionary<string, SkiaGraphInput>(), null);
        Assert.Equal(SkiaTestReadback.Pixels(standaloneExpected.GraphResult.Output.Image), SkiaTestReadback.Pixels(standalone.GraphResult.Output.Image));
        Assert.Equal(request.PreparedFrame!.FinalGeometry, bound.PreparedFrame!.FinalGeometry);
        Assert.Equal(request.PreparedFrame.FinalLineGeometry, bound.PreparedFrame.FinalLineGeometry);
        standalone.Dispose(); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Theory]
    [InlineData("unknown", "GraphTransformNodeUnknown")]
    [InlineData("type", "GraphTransformNodeTypeMismatch")]
    [InlineData("offset", "GraphTransformSamplingExceeded")]
    [InlineData("uniform", "MaterialUniformUnknown")]
    public void 两入口保留覆盖错误真实owner阶段时间和资源回收(string invalid, string code)
    {
        var request = Request(Snapshot(false)); var binding = OverrideBinding(request, invalid); var bound = Bindings(request, binding);
        using var graph = OverrideGraph(); using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var pipeline = new SkiaRenderPipeline(null, null, budget, resources);
        var main = Assert.Throws<EffectFailureException>(() => pipeline.Render(bound));
        var standalone = Assert.Throws<EffectFailureException>(() => pipeline.ExecuteSceneImageEffect(bound, binding, Program("first", graph), new Dictionary<string, SkiaGraphInput>(), null));
        foreach (var failure in new[] { main, standalone })
        {
            Assert.Equal(code, failure.Code); Assert.Equal(binding.InstanceId, failure.InstanceId); Assert.Equal(binding.ArtifactHash, failure.ArtifactHash);
            Assert.Equal(binding.PackageIdentity, failure.PackageIdentity); Assert.Equal(request.TimeUs, failure.TimeUs);
        }
        Assert.Equal("RenderImageEffects", main.Stage); Assert.Equal("ExecuteImageGraph", standalone.Stage); Assert.Equal(0, budget.Usage.ActiveSurfaces);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => pipeline.ExecuteSceneImageEffect(bound with { CancellationToken = cancel.Token }, binding,
            Program("first", graph), new Dictionary<string, SkiaGraphInput>(), null));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        var valid = OverrideBinding(request); using var recovered = pipeline.Render(Bindings(request, valid));
        Assert.Contains(recovered.Pixels.ToArray(), value => value != 0); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void 完整绑定副本保持Prepared帧noOp且任一覆盖变化创建新帧()
    {
        var request = Request(Snapshot(false)); var a = OverrideBinding(request); var b = OverrideBinding(request);
        var prepared = request.PreparedFrame!.WithImageEffects([a]);
        Assert.Same(prepared, prepared.WithImageEffects([b]));
        var replaced = b with { Binding = b.Binding with { Transforms = new([new("move", new(Fx.Affine2.Translation(4, 0), new(0, 0, 640, 360)))]) } };
        Assert.NotSame(prepared, prepared.WithImageEffects([replaced]));
        Assert.Throws<PreparedFrameException>(() => prepared.WithImageEffects([a, b]));
    }
}
