using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ImageGraphCompositionChainTests
{
    private static readonly GraphNodeBounds Bounds = new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
    private static ImageGraphFrameContext Frame(double density = 1, long time = 0, bool transparent = false, GraphRect? bounds = null)
        => new(time, 16, 16, bounds ?? new(0, 0, 16, 16), density, density, ParameterValues.Empty, transparent);
    private static CompiledImageGraph Graph(bool replacement = false, double amount = 1, bool glass = false, GraphExecutionBudget? budget = null)
    {
        List<GraphInputDefinition> inputs = [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)];
        List<ImageGraphNode> nodes = [];
        List<KeyValuePair<string, ShaderProgramDefinition>> programs = [];
        GraphReference output = GraphReference.Input("target");
        if (amount != 1)
        {
            programs.Add(new("fade", new("fade.sksl", "uniform shader target; uniform float amount; half4 main(float2 p) { return target.eval(p)*amount; }",
                ShaderProgramDefinition.RasterProfile, [new("amount", ShaderUniformKind.Float)], [new("target", ShaderSurfaceKind.Color)])));
            nodes.Add(new ImageGraphNode.RuntimeShader("fade", "fade", ShaderSurfaceKind.Color,
                [new("amount", new GraphUniformBinding.Literal(ShaderLiteral.Numeric(ShaderUniformKind.Float, [amount])))], [new("target", output)], Bounds));
            output = GraphReference.Node("fade");
        }
        if (glass)
        {
            inputs.Add(new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas));
            output = GraphReference.Input("media");
        }
        if (replacement)
        {
            inputs.Add(new("mask", GraphInputSource.GlyphCoverage, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas));
            nodes.Add(new ImageGraphNode.Mask("masked", output, GraphReference.Input("mask"), Bounds)); output = GraphReference.Node("masked");
        }
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(new("chain", ImageGraphDefinition.CurrentVersion, inputs, [], nodes,
            output, GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ImageOnly, replacement ? GraphCompositeMode.ReplaceCoveredBackdrop : GraphCompositeMode.SourceOver,
            replacement ? GraphReference.Input("mask") : null, glass ? GraphReference.Input("target") : null), [], programs, budget));
    }
    private sealed class Surface : IDisposable
    {
        private readonly SKImage _image;
        public Surface(SKColor color, ShaderSurfaceKind kind = ShaderSurfaceKind.Color, double density = 1, GraphRect? bounds = null, Action<SKCanvas>? draw = null)
        {
            var rect = bounds ?? new GraphRect(0, 0, 16, 16);
            using var srgb = SKColorSpace.CreateSrgb();
            using var native = SKSurface.Create(new SKImageInfo((int)(rect.Width * density), (int)(rect.Height * density), SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            var canvas = native.Canvas; canvas.ClipRect(new(0, 0, native.Canvas.DeviceClipBounds.Width, native.Canvas.DeviceClipBounds.Height)); canvas.Clear(color);
            canvas.Scale((float)density, (float)density); canvas.Translate((float)-rect.Left, (float)-rect.Top); draw?.Invoke(canvas);
            _image = native.Snapshot(); Value = SkiaGraphSurface.Borrow(_image, new(rect, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, density, density), kind);
        }
        public SkiaGraphSurface Value { get; }
        public void Dispose() { Value.Dispose(); _image.Dispose(); }
    }
    private static Dictionary<string, SkiaGraphInput> Inputs(SkiaGraphSurface target, SkiaGraphSurface? mask = null)
    {
        Dictionary<string, SkiaGraphInput> result = new() { ["target"] = new(target, GraphInputSource.TargetColor) };
        if (mask is not null) result.Add("mask", new(mask, GraphInputSource.GlyphCoverage)); return result;
    }
    private static byte[] Bytes(SkiaGraphSurface surface) => SkiaTestReadback.Pixels(surface.Image);
    private static byte[] Pixel(SkiaGraphSurface surface, int x = 8, int y = 8)
        => SkiaTestReadback.Pixel(surface.Image, x, y);

    [Theory]
    [InlineData(.5, 1)] [InlineData(1, 1)] [InlineData(2, .5)]
    public void 替换后普通变淡保留旧范围且透明度只作用一次(double density, double opacity)
    {
        using var target = new Surface(SKColors.Red, density: density); using var below = new Surface(SKColors.Blue, density: density);
        using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage, density);
        using var replace = Graph(true); using var fade = Graph(amount: .5);
        using var first = replace.Execute(Frame(density), Inputs(target.Value, mask.Value));
        using var last = fade.Execute(Frame(density), Inputs(first.Output));
        using var result = fade.CompositeChain([first, last], below.Value, opacity: opacity);
        var pixel = Pixel(result, (int)(8 * density), (int)(8 * density));
        Assert.InRange(pixel[2], (byte)Math.Floor(64 * opacity - 1), (byte)Math.Ceiling(64 * opacity + 1));
        Assert.InRange(pixel[0], (byte)Math.Floor(255 - 128 * opacity - 1), (byte)Math.Ceiling(255 - 128 * opacity + 1));
        using var wrongLastOnly = fade.Composite(last, below.Value, opacity);
        Assert.NotEqual(Bytes(wrongLastOnly), Bytes(result));
    }

    [Fact]
    public void 重复替换同一抗锯齿边缘取max而不叠加覆盖或重乘颜色()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Blue);
        using var a = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage); using var b = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var graph = Graph(true); using var first = graph.Execute(Frame(), Inputs(target.Value, a.Value));
        using var last = graph.Execute(Frame(), Inputs(target.Value, b.Value));
        using var result = graph.CompositeChain([first, last], below.Value);
        var pixel = Pixel(result); Assert.Equal(new byte[] { 127, 0, 128, 255 }, pixel);
        using var once = graph.Composite(last, below.Value); Assert.Equal(Bytes(once), Bytes(result));
    }

    [Fact]
    public void 不同区域替换保留早期范围后续普通颜色在范围外仍按自身alpha合成()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Blue);
        using var mask = new Surface(SKColors.Transparent, ShaderSurfaceKind.Coverage, draw: canvas => { using var paint = new SKPaint { Color = SKColors.White }; canvas.DrawRect(0, 0, 8, 16, paint); });
        using var laterColor = new Surface(SKColors.Transparent, draw: canvas => { using var paint = new SKPaint { Color = new(0, 255, 0, 128) }; canvas.DrawRect(8, 0, 8, 16, paint); });
        using var replace = Graph(true); using var ordinary = Graph();
        using var first = replace.Execute(Frame(), Inputs(target.Value, mask.Value)); using var last = ordinary.Execute(Frame(), Inputs(laterColor.Value));
        using var result = ordinary.CompositeChain([first, last], below.Value);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(result, 4)); Assert.Equal(new byte[] { 127, 128, 0, 255 }, Pixel(result, 12));
    }

    [Fact]
    public void 三次替换并集保留不同框和区域最终只合成一次()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Blue);
        using var left = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage, bounds: new(0, 0, 8, 16));
        using var right = new Surface(new(0, 0, 0, 64), ShaderSurfaceKind.Coverage, bounds: new(8, 0, 16, 16));
        using var empty = new Surface(SKColors.Transparent, ShaderSurfaceKind.Coverage);
        using var graph = Graph(true); using var a = graph.Execute(Frame(), Inputs(target.Value, left.Value));
        using var b = graph.Execute(Frame(), Inputs(target.Value, right.Value)); using var c = graph.Execute(Frame(), Inputs(target.Value, empty.Value));
        using var result = graph.CompositeChain([a, b, c], below.Value);
        Assert.Equal(new byte[] { 127, 0, 0, 127 }, Pixel(result, 4)); Assert.Equal(new byte[] { 191, 0, 0, 191 }, Pixel(result, 12));
    }

    [Fact]
    public void 透明旁路不新增玻璃覆盖且继续保留独立效果的替换和诊断()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Transparent);
        using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var firstGraph = Graph(true); using var glass = Graph(true, glass: true); using var ordinary = Graph();
        using var first = firstGraph.Execute(Frame(transparent: true), Inputs(target.Value, mask.Value));
        using var bypass = glass.Execute(Frame(transparent: true), Inputs(first.Output));
        using var last = ordinary.Execute(Frame(transparent: true), Inputs(bypass.Output));
        using var result = ordinary.CompositeChain([first, bypass, last], below.Value);
        Assert.Equal(Bytes(first.Output), Bytes(result)); Assert.Null(bypass.Coverage);
        Assert.Equal("TransparentBackgroundDependency", Assert.Single(bypass.Diagnostics).Reason);
    }

    [Theory]
    [InlineData(0)] [InlineData(.25)] [InlineData(1)]
    public void 没有替换时与旧sourceOver合成逐字节相同(double opacity)
    {
        using var target = new Surface(new(255, 0, 0, 128)); using var below = new Surface(new(0, 0, 255, 180));
        using var graph = Graph(amount: .5); using var a = graph.Execute(Frame(), Inputs(target.Value)); using var b = graph.Execute(Frame(), Inputs(a.Output));
        using var old = graph.Composite(b, below.Value, opacity); using var chain = graph.CompositeChain([a, b], below.Value, opacity: opacity);
        Assert.Equal(Bytes(old), Bytes(chain));
    }

    [Theory]
    [InlineData("time")] [InlineData("density")] [InlineData("mode")]
    public void 不能把不同帧密度或输出模式的结果串接(string difference)
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Blue); using var graph = Graph();
        using var first = graph.Execute(Frame(), Inputs(target.Value));
        using var last = graph.Execute(Frame(density: difference == "density" ? 2 : 1, time: difference == "time" ? 1 : 0, transparent: difference == "mode"), Inputs(first.Output));
        Assert.Equal("GraphCompositeChainContextMismatch", Assert.Throws<ImageGraphException>(() => graph.CompositeChain([first, last], below.Value)).Code);
    }

    [Fact]
    public void 不允许下层框裁掉已经离开最终颜色框的历史覆盖()
    {
        using var target = new Surface(SKColors.Red); using var small = new Surface(SKColors.Transparent, bounds: new(4, 4, 12, 12));
        using var mask = new Surface(SKColors.White, ShaderSurfaceKind.Coverage); using var replace = Graph(true); using var ordinary = Graph();
        using var first = replace.Execute(Frame(), Inputs(target.Value, mask.Value)); using var last = ordinary.Execute(Frame(bounds: new(4, 4, 12, 12)), Inputs(small.Value));
        Assert.Equal("GraphCompositeBoundsExceeded", Assert.Throws<ImageGraphException>(() => ordinary.CompositeChain([first, last], small.Value)).Code);
    }

    [Fact]
    public void 覆盖并集和最终合成都计入共享预算失败后回收且不夺取输入()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Blue);
        using var m1 = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage); using var m2 = new Surface(new(0, 0, 0, 64), ShaderSurfaceKind.Coverage);
        using var graph = Graph(true); using var first = graph.Execute(Frame(), Inputs(target.Value, m1.Value)); using var last = graph.Execute(Frame(), Inputs(target.Value, m2.Value));
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 5, MaximumBytes = 5 * 16 * 16 * 4 }); using var frame = budget.BeginFrame();
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<ImageSurfaceBudgetException>(() => graph.CompositeChain([first, last], below.Value, frame)).Code);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes); Assert.Equal(0, frame.Usage.ActiveSurfaces);
        Assert.True(first.Output.Image.Handle != nint.Zero); Assert.True(last.Output.Image.Handle != nint.Zero);
        using var enough = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 6, MaximumBytes = 6 * 16 * 16 * 4 }); using var another = enough.BeginFrame();
        using var result = graph.CompositeChain([first, last], below.Value, another);
        Assert.Equal(1, enough.Usage.ActiveSurfaces); Assert.Equal(6, enough.Usage.PeakSurfaces); result.Dispose(); Assert.Equal(0, enough.Usage.ActiveSurfaces);
    }

    [Fact]
    public void 取消空链错误代次和非法透明度在分配前失败()
    {
        using var target = new Surface(SKColors.Red); using var graph = Graph(); using var other = Graph(); using var result = graph.Execute(Frame(), Inputs(target.Value));
        Assert.Equal("GraphCompositeChainEmpty", Assert.Throws<ImageGraphException>(() => graph.CompositeChain([], target.Value)).Code);
        Assert.Equal("GraphResultMismatch", Assert.Throws<ImageGraphException>(() => other.CompositeChain([result], target.Value)).Code);
        Assert.Equal("InvalidGraphOpacity", Assert.Throws<ImageGraphException>(() => graph.CompositeChain([result], target.Value, opacity: double.NaN)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => graph.CompositeChain([result], target.Value, cancellationToken: cancelled.Token));
        Assert.True(target.Value.Image.Handle != nint.Zero);
    }
}
