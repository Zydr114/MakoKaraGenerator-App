using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ImageGraphExecutionTests
{
    private static GraphNodeBounds Bounds(double expand = 0, GraphEdgeMode edge = GraphEdgeMode.Transparent) => new(new GraphExpansion.Literal(expand), expand, expand, edge);
    private static GraphInputDefinition Input(string id, ShaderSurfaceKind kind = ShaderSurfaceKind.Color, GraphInputSource source = GraphInputSource.TargetColor,
        GraphBackgroundRequirement background = GraphBackgroundRequirement.None, bool required = true)
        => new(id, source, kind, required, background, GraphCoordinateSpace.Canvas);
    private static ImageGraphFrameContext Frame(double density = 1, double densityY = 0, GraphRect? bounds = null, ParameterValues? parameters = null, bool transparent = false, long timeUs = 0)
        => new(timeUs, 16, 16, bounds ?? new(0, 0, 16, 16), density, densityY == 0 ? density : densityY, parameters ?? ParameterValues.Empty, transparent);
    private static ImageGraphDefinition Definition(IEnumerable<ImageGraphNode> nodes, GraphReference output, IEnumerable<GraphInputDefinition>? inputs = null,
        IEnumerable<string>? parameters = null, double expand = 0, GraphCompositeMode composite = GraphCompositeMode.SourceOver, GraphReference? coverage = null, GraphReference? bypass = null)
        => new("test", ImageGraphDefinition.CurrentVersion, inputs ?? [Input("target")], parameters ?? [], nodes, output, GraphCoordinateSpace.Canvas, expand, GraphAnchorMode.ImageOnly, composite, coverage, bypass);
    private static CompiledImageGraph Compile(ImageGraphDefinition definition, IEnumerable<EffectParameterConstraint>? parameters = null,
        IEnumerable<KeyValuePair<string, ShaderProgramDefinition>>? programs = null, GraphExecutionBudget? budget = null)
        => CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, parameters ?? [], programs ?? [], budget));
    private sealed class Surface : IDisposable
    {
        private readonly SKSurface _surface;
        private readonly SKImage _image;
        public Surface(SKColor color, ShaderSurfaceKind kind = ShaderSurfaceKind.Color, GraphRect? bounds = null, double density = 1, double densityY = 0, Action<SKCanvas>? draw = null)
        {
            var rect = bounds ?? new GraphRect(0, 0, 16, 16); double dy = densityY == 0 ? density : densityY;
            using var srgb = SKColorSpace.CreateSrgb();
            _surface = SKSurface.Create(new SKImageInfo((int)Math.Round(rect.Width * density), (int)Math.Round(rect.Height * dy), SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            var canvas = _surface.Canvas; canvas.ClipRect(new(0, 0, _surface.Canvas.DeviceClipBounds.Width, _surface.Canvas.DeviceClipBounds.Height)); canvas.Clear(color);
            canvas.Scale((float)density, (float)dy); canvas.Translate((float)-rect.Left, (float)-rect.Top); draw?.Invoke(canvas);
            _image = _surface.Snapshot(); Value = SkiaGraphSurface.Borrow(_image, new(rect, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, density, dy), kind);
        }
        public SkiaGraphSurface Value { get; }
        public void Dispose() { Value.Dispose(); _image.Dispose(); _surface.Dispose(); }
    }
    private static SKColor Pixel(SkiaGraphSurface surface, int x, int y)
    {
        byte[] pixels = SkiaTestReadback.Pixels(surface.Image);
        int offset = checked((y * surface.Image.Width + x) * 4);
        byte alpha = pixels[offset + 3];
        static byte Unpremultiply(byte value, byte alpha) => alpha == 0 ? (byte)0 : (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);
        return new SKColor(Unpremultiply(pixels[offset + 2], alpha), Unpremultiply(pixels[offset + 1], alpha),
            Unpremultiply(pixels[offset], alpha), alpha);
    }
    private static byte[] Bytes(SkiaGraphSurface surface) => SkiaTestReadback.Pixels(surface.Image);
    private static void AssertBlendPixels(byte[] expected, byte[] actual, GraphBlendMode mode)
    {
        Assert.Equal(expected.Length, actual.Length);
        int tolerance = mode == GraphBlendMode.Multiply && string.Equals(Environment.GetEnvironmentVariable("MAKO_RENDER_BACKEND"), "vulkan", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Assert.All(expected.Zip(actual), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, tolerance));
    }
    private static Dictionary<string, SkiaGraphInput> Inputs(params (string Id, Surface Surface, GraphInputSource Source)[] values)
        => values.ToDictionary(value => value.Id, value => new SkiaGraphInput(value.Surface.Value, value.Source), StringComparer.Ordinal);
    private static string Code(Action action) => Assert.Throws<ImageGraphException>(action).Code;
    private static ShaderProgramDefinition Program(string source, IEnumerable<ShaderUniformDeclaration>? uniforms = null, IEnumerable<ShaderChildDeclaration>? children = null)
        => new("effects/test.sksl", source, ShaderProgramDefinition.RasterProfile, uniforms ?? [], children ?? []);

    [Fact]
    public void 真实shader读取时间画布参数颜色布尔与数组字面量()
    {
        var source = Program("""
            uniform float time; uniform float2 resolution; uniform float amount; uniform int enabled;
            layout(color) uniform float4 tint; uniform float samples[2];
            half4 main(float2 p) { return half4(tint.rgb*tint.a*amount*float(enabled)*samples[1]*(resolution.x/16)*(time/.5), tint.a); }
            """, [new("time", ShaderUniformKind.Float), new("resolution", ShaderUniformKind.Float2), new("amount", ShaderUniformKind.Float), new("enabled", ShaderUniformKind.Int), new("tint", ShaderUniformKind.Color), new("samples", ShaderUniformKind.Float, 2)]);
        var node = new ImageGraphNode.RuntimeShader("ink", "source", ShaderSurfaceKind.Color,
            [new("time", new GraphUniformBinding.Host(GraphHostValue.TimeSeconds)), new("resolution", new GraphUniformBinding.Host(GraphHostValue.CanvasSize)),
            new("amount", new GraphUniformBinding.Parameter("amount")), new("enabled", new GraphUniformBinding.Parameter("enabled")), new("tint", new GraphUniformBinding.Parameter("tint")),
            new("samples", new GraphUniformBinding.Literal(ShaderLiteral.Numeric(ShaderUniformKind.Float, [0, .5], 2)))], [], Bounds());
        using var graph = Compile(Definition([node], GraphReference.Node("ink"), inputs: [], parameters: ["amount", "enabled", "tint"]),
            [new EffectParameterConstraint.Number("amount", 0, 1), new EffectParameterConstraint.Toggle("enabled"), new EffectParameterConstraint.Color("tint")], [new("source", source)]);
        var parameters = new ParameterValues([new("amount", new ParameterValue.Number(.5)), new("enabled", new ParameterValue.Toggle(true)), new("tint", new ParameterValue.Color(new(1, 0, 0, .5)))]);
        using var result = graph.Execute(Frame(parameters: parameters, timeUs: 500_000), new Dictionary<string, SkiaGraphInput>());
        var pixel = Pixel(result.Output, 8, 8); Assert.InRange(pixel.Red, (byte)63, (byte)65); Assert.InRange(pixel.Alpha, (byte)127, (byte)129);
    }

    [Fact]
    public void shader非预乘产物明确失败且后续求值仍可执行()
    {
        var shader = new ImageGraphNode.RuntimeShader("ink", "source", ShaderSurfaceKind.Color, [], [], Bounds());
        using var graph = Compile(Definition([shader], GraphReference.Node("ink"), inputs: []), programs: [new("source", Program("half4 main(float2 p) { return half4(1, 0, 0, .25); }"))]);
        Assert.Equal("GraphColorNotPremultiplied", Code(() => graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput>())));
        Assert.Equal("GraphColorNotPremultiplied", Code(() => graph.Execute(Frame(timeUs: 500_000), new Dictionary<string, SkiaGraphInput>())));
        using var good = Compile(Definition([shader], GraphReference.Node("ink"), inputs: []), programs: [new("source", Program("half4 main(float2 p) { return half4(.25, 0, 0, .25); }"))]);
        using var result = good.Execute(Frame(), new Dictionary<string, SkiaGraphInput>());
        byte[] bytes = Bytes(result.Output); Assert.InRange(bytes[2], (byte)63, (byte)65); Assert.InRange(bytes[3], (byte)63, (byte)65);
    }

    [Fact]
    public void 颜色输入必须有真实sRGB编码与合法预乘像素()
    {
        using var graph = Compile(Definition([], GraphReference.Input("target")));
        using var linear = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, linear));
        bitmap.Erase(SKColors.Red); using var image = SKImage.FromBitmap(bitmap);
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 16, 16), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        Assert.Equal("GraphColorSpaceMismatch", Code(() => graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["target"] = new(input, GraphInputSource.TargetColor) })));
        using var srgb = SKColorSpace.CreateSrgb();
        var invalidInfo = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        byte[] invalidBytes = new byte[invalidInfo.BytesSize]; invalidBytes[2] = 255;
        GCHandle invalidHandle = GCHandle.Alloc(invalidBytes, GCHandleType.Pinned);
        try
        {
            using var invalidPixmap = new SKPixmap(invalidInfo, invalidHandle.AddrOfPinnedObject(), invalidInfo.RowBytes);
            using var invalidImage = SKImage.FromPixels(invalidPixmap);
            using var invalidInput = SkiaGraphSurface.Borrow(invalidImage, input.Descriptor, ShaderSurfaceKind.Color);
            Assert.Equal("GraphColorNotPremultiplied", Code(() => graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["target"] = new(invalidInput, GraphInputSource.TargetColor) })));
        }
        finally
        {
            invalidHandle.Free();
        }
    }

    [Fact]
    public void mask只乘一次且独立替换边缘不重复乘覆盖()
    {
        using var color = new Surface(SKColors.Red); using var coverage = new Surface(new(128, 128, 128, 128), ShaderSurfaceKind.Coverage);
        using var below = new Surface(SKColors.Blue);
        var definition = Definition([new ImageGraphNode.Mask("masked", GraphReference.Input("target"), GraphReference.Input("coverage"), Bounds())], GraphReference.Node("masked"),
            [Input("target"), Input("coverage", ShaderSurfaceKind.Coverage, GraphInputSource.GlyphCoverage)], composite: GraphCompositeMode.ReplaceCoveredBackdrop, coverage: GraphReference.Input("coverage"));
        using var graph = Compile(definition); using var result = graph.Execute(Frame(), Inputs(("target", color, GraphInputSource.TargetColor), ("coverage", coverage, GraphInputSource.GlyphCoverage)));
        Assert.InRange(Pixel(result.Output, 8, 8).Alpha, (byte)127, (byte)129);
        using var composite = graph.Composite(result, below.Value);
        var pixel = Pixel(composite, 8, 8); Assert.InRange(pixel.Red, (byte)127, (byte)129); Assert.InRange(pixel.Blue, (byte)126, (byte)128); Assert.Equal(255, pixel.Alpha);
        using var half = graph.Composite(result, below.Value, .5); var halfPixel = Pixel(half, 8, 8);
        Assert.InRange(halfPixel.Red, (byte)63, (byte)65); Assert.InRange(halfPixel.Blue, (byte)190, (byte)192);
    }

    [Theory]
    [InlineData(GraphBlendMode.SourceOver)]
    [InlineData(GraphBlendMode.Plus)]
    [InlineData(GraphBlendMode.Multiply)]
    public void 实际blend与独立Skia逐字节相同(GraphBlendMode mode)
    {
        using var a = new Surface(new(230, 40, 20, 128)); using var b = new Surface(new(10, 70, 200, 180));
        var blend = new ImageGraphNode.Blend("blend", GraphReference.Input("a"), GraphReference.Input("b"), mode, Bounds());
        using var graph = Compile(Definition([blend], GraphReference.Node("blend"), [Input("a"), Input("b")]));
        using var result = graph.Execute(Frame(), Inputs(("a", a, GraphInputSource.TargetColor), ("b", b, GraphInputSource.TargetColor)));
        using var expected = new Surface(SKColors.Transparent, draw: canvas =>
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src }; canvas.DrawImage(b.Value.Image, 0, 0, paint);
            paint.BlendMode = mode switch { GraphBlendMode.SourceOver => SKBlendMode.SrcOver, GraphBlendMode.Plus => SKBlendMode.Plus, _ => SKBlendMode.Multiply };
            canvas.DrawImage(a.Value.Image, 0, 0, paint);
        });
        AssertBlendPixels(Bytes(expected.Value), Bytes(result.Output), mode);
    }

    [Theory]
    [InlineData(1d, 1d)]
    [InlineData(.5d, .5d)]
    [InlineData(2d, 1d)]
    public void transform使用逻辑距离且不同密度位置一致(double density, double densityY)
    {
        using var source = new Surface(SKColors.Transparent, density: density, densityY: densityY, draw: canvas => { using var paint = new SKPaint { Color = SKColors.Red }; canvas.DrawRect(2, 4, 2, 4, paint); });
        var node = new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Translate(4, 0), new(0, 0, 16, 16), Bounds());
        using var graph = Compile(Definition([node], GraphReference.Node("move"))); using var result = graph.Execute(Frame(density, densityY), Inputs(("target", source, GraphInputSource.TargetColor)));
        Assert.Equal(0, Pixel(result.Output, (int)(3 * density), (int)(6 * densityY)).Alpha);
        Assert.Equal(SKColors.Red, Pixel(result.Output, (int)(7 * density), (int)(6 * densityY)));
    }

    [Theory]
    [InlineData(GraphEdgeMode.Transparent)]
    [InlineData(GraphEdgeMode.Clamp)]
    [InlineData(GraphEdgeMode.Repeat)]
    public void shader取样三种边缘实际生效(GraphEdgeMode edge)
    {
        using var source = new Surface(SKColors.Red, bounds: new(0, 0, 4, 4));
        var program = Program("uniform shader image; half4 main(float2 p) { return image.eval(p); }", children: [new("image", ShaderSurfaceKind.Color)]);
        var node = new ImageGraphNode.RuntimeShader("copy", "copy", ShaderSurfaceKind.Color, [], [new("image", GraphReference.Input("target"))], Bounds(edge: edge));
        using var graph = Compile(Definition([node], GraphReference.Node("copy")), programs: [new("copy", program)]);
        using var result = graph.Execute(Frame(), Inputs(("target", source, GraphInputSource.TargetColor)));
        Assert.Equal(edge == GraphEdgeMode.Transparent ? 0 : 255, Pixel(result.Output, 8, 8).Alpha);
    }

    [Fact]
    public void blur像素跨域外扩且缩图保持逻辑sigma()
    {
        var blur = new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Parameter("sigma"), new(new GraphExpansion.CeilingMultiple("sigma", 3), 6, 6, GraphEdgeMode.Transparent));
        using var graph = Compile(Definition([blur], GraphReference.Node("blur"), parameters: ["sigma"], expand: 6), [new EffectParameterConstraint.Number("sigma", 0, 2)]);
        var parameters = new ParameterValues([new("sigma", new ParameterValue.Number(2))]);
        using var source = new Surface(SKColors.Transparent, draw: canvas => { using var paint = new SKPaint { Color = SKColors.White }; canvas.DrawRect(4, 4, 8, 8, paint); });
        using var result = graph.Execute(Frame(parameters: parameters), Inputs(("target", source, GraphInputSource.TargetColor)));
        Assert.Equal(new GraphRect(-6, -6, 22, 22), result.Output.Descriptor.Bounds);
        Assert.InRange(Pixel(result.Output, 9, 14).Alpha, (byte)20, (byte)150); Assert.True(Pixel(result.Output, 14, 14).Alpha > 150);
        using var smallSource = new Surface(SKColors.Transparent, density: .5, draw: canvas => { using var paint = new SKPaint { Color = SKColors.White }; canvas.DrawRect(4, 4, 8, 8, paint); });
        using var small = graph.Execute(Frame(.5, parameters: parameters), Inputs(("target", smallSource, GraphInputSource.TargetColor)));
        Assert.Equal(14, small.Output.Image.Width);
        Assert.InRange(Math.Abs(Pixel(result.Output, 14, 14).Alpha - Pixel(small.Output, 7, 7).Alpha), 0, 25);
    }

    [Fact]
    public void enum背景来源与透明旁路真实区分且后续独立图继续执行()
    {
        var background = new GraphInputDefinition("background", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true,
            GraphBackgroundRequirement.OpaqueCanvas, GraphCoordinateSpace.Canvas, "source", [new("media", GraphInputSource.BackgroundMedia), new("below", GraphInputSource.BackdropBelow)]);
        var program = Program("uniform shader image; half4 main(float2 p) { return image.eval(p); }", children: [new("image", ShaderSurfaceKind.Color)]);
        var shader = new ImageGraphNode.RuntimeShader("glass", "copy", ShaderSurfaceKind.Color, [], [new("image", GraphReference.Input("background"))], Bounds());
        using var graph = Compile(Definition([shader], GraphReference.Node("glass"), [Input("target"), background], ["source"], bypass: GraphReference.Input("target")),
            [new EffectParameterConstraint.Choice("source", ["media", "below"])], [new("copy", program)]);
        using var target = new Surface(SKColors.Red); using var media = new Surface(SKColors.Blue); using var below = new Surface(SKColors.Green);
        var mediaParams = new ParameterValues([new("source", new ParameterValue.Choice("media"))]);
        using var opaque = graph.Execute(Frame(parameters: mediaParams), Inputs(("target", target, GraphInputSource.TargetColor), ("background", media, GraphInputSource.BackgroundMedia)));
        Assert.Equal(SKColors.Blue, Pixel(opaque.Output, 8, 8));
        using var switched = graph.Execute(Frame(parameters: new ParameterValues([new("source", new ParameterValue.Choice("below"))])), Inputs(("background", below, GraphInputSource.BackdropBelow)));
        Assert.Equal(SKColors.Green, Pixel(switched.Output, 8, 8));
        using var alpha = graph.Execute(Frame(parameters: mediaParams, transparent: true), Inputs(("target", target, GraphInputSource.TargetColor)));
        Assert.Same(target.Value, alpha.Output); Assert.Single(alpha.Diagnostics); Assert.Equal(SKColors.Red, Pixel(alpha.Output, 8, 8));
        using var missing = graph.Execute(Frame(parameters: mediaParams), Inputs(("target", target, GraphInputSource.TargetColor)));
        Assert.Same(target.Value, missing.Output); Assert.Equal("BackgroundMediaUnavailable", Assert.Single(missing.Diagnostics).Reason);
        Assert.Equal("GraphInputSourceMismatch", Code(() => graph.Execute(Frame(parameters: mediaParams), Inputs(("background", below, GraphInputSource.BackdropBelow)))));
        using var next = Compile(Definition([new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Identity, new(0,0,16,16), Bounds())], GraphReference.Node("move")));
        using var following = next.Execute(Frame(transparent: true), new Dictionary<string, SkiaGraphInput> { ["target"] = new(alpha.Output, GraphInputSource.TargetColor) });
        Assert.Equal(SKColors.Red, Pixel(following.Output, 8, 8));
    }

    [Fact]
    public void 透明可读不依赖背景的下层且编译错误不能旁路()
    {
        using var below = new Surface(new(10, 100, 200, 100));
        using var graph = Compile(Definition([], GraphReference.Input("target"), [Input("target", source: GraphInputSource.BackdropBelow)]));
        using var result = graph.Execute(Frame(transparent: true), Inputs(("target", below, GraphInputSource.BackdropBelow)));
        Assert.Empty(result.Diagnostics); Assert.Equal(Bytes(below.Value), Bytes(result.Output));
        var shader = new ImageGraphNode.RuntimeShader("bad", "bad", ShaderSurfaceKind.Color, [], [], Bounds());
        Assert.Throws<ShaderProgramException>(() => Compile(Definition([shader], GraphReference.Node("bad"), [Input("background", source: GraphInputSource.BackgroundMedia, background: GraphBackgroundRequirement.Media)]), programs: [new("bad", Program("broken source"))]));
    }

    [Fact]
    public void 像素内存表面预算在绘制前失败且输入保留()
    {
        using var target = new Surface(SKColors.Red); var node = new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds());
        var definition = Definition([node], GraphReference.Node("move"));
        foreach (var budget in new[] { GraphExecutionBudget.Default with { MaximumBytes = 1500 }, GraphExecutionBudget.Default with { MaximumSurfaces = 1 }, GraphExecutionBudget.Default with { MaximumPixelDimension = 8 } })
        {
            using var graph = Compile(definition, budget: budget);
            var error = Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor))));
            Assert.True(error.Code is "GraphSurfaceBudgetExceeded" or "GraphSurfaceDimensionExceeded", error.Message);
        }
        Assert.Equal(SKColors.Red, Pixel(target.Value, 8, 8));
    }

    [Fact]
    public void 合成预算按真实输入身份去重()
    {
        using var target = new Surface(SKColors.Red);
        using var graph = Compile(Definition([], GraphReference.Input("target")), budget: GraphExecutionBudget.Default with { MaximumSurfaces = 2, MaximumBytes = 2048 });
        using var result = graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor)));
        using var composite = graph.Composite(result, target.Value);
        Assert.Equal(SKColors.Red, Pixel(composite, 8, 8)); Assert.Equal(SKColors.Red, Pixel(target.Value, 8, 8));
    }

    [Fact]
    public void 最后使用后释放中间图保留最终产物且同帧可重绘()
    {
        var nodes = Enumerable.Range(0, 12).Select(index => new ImageGraphNode.Transform("n" + index, index == 0 ? GraphReference.Input("target") : GraphReference.Node("n" + (index - 1)), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds())).ToArray();
        using var target = new Surface(SKColors.Red); using var graph = Compile(Definition(nodes, GraphReference.Node("n11")), budget: GraphExecutionBudget.Default with { MaximumSurfaces = 3, MaximumBytes = 3072 });
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 4, MaximumBytes = 4096 }); using var shared = budget.BeginFrame();
        using var a = graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor)), shared);
        using var b = graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor)), shared);
        Assert.Equal(3, a.PeakSurfaces); Assert.Equal(3072, a.PeakBytes); Assert.Equal(Bytes(a.Output), Bytes(b.Output));
        Assert.Equal(4, shared.Usage.PeakSurfaces); Assert.Equal(4096, budget.Usage.PeakBytes); Assert.Equal(3, budget.Usage.ActiveSurfaces);
        graph.Dispose(); Assert.Equal(SKColors.Red, Pixel(a.Output, 8, 8)); Assert.Equal(SKColors.Red, Pixel(target.Value, 8, 8));
    }

    [Fact]
    public void 独立合成表面在图释放后仍可读取且能作为另一设备输入()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Blue);
        var node = new ImageGraphNode.Transform("copy", GraphReference.Input("target"), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds());
        using var graph = Compile(Definition([node], GraphReference.Node("copy")));
        using var result = graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor)));
        using var composite = graph.Composite(result, below.Value);
        result.Dispose(); graph.Dispose();
        Assert.Equal(SKColors.Red, Pixel(composite, 8, 8));
        using var next = Compile(Definition([node], GraphReference.Node("copy")));
        using var copied = next.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["target"] = new(composite, GraphInputSource.TargetColor) });
        Assert.Equal(SKColors.Red, Pixel(copied.Output, 8, 8));
    }

    [Fact]
    public void 同一光栅输入跨独立CPU与所选设备重复往返不转移源图所有权()
    {
        using var cpu = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        using var selected = SkiaRenderDeviceFactory.Create();
        using var srgb = SKColorSpace.CreateSrgb();
        using var source = cpu.CreateSurface(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        source.Canvas.Clear(SKColors.Red);
        using var image = source.Snapshot();
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 16, 16), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color, cpu);
        var node = new ImageGraphNode.Transform("copy", GraphReference.Input("target"), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds());
        var plan = ImageGraphCompiler.Compile(Definition([node], GraphReference.Node("copy")), [], []);
        using var outward = CompiledImageGraph.Compile(plan, selected);
        using var returning = CompiledImageGraph.Compile(plan, cpu);
        for (int i = 0; i < 3; i++)
        {
            using var first = outward.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["target"] = new(input, GraphInputSource.TargetColor) });
            using var second = returning.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["target"] = new(first.Output, GraphInputSource.TargetColor) });
            Assert.Equal(SKColors.Red, Pixel(first.Output, 8, 8));
            Assert.Equal(SKColors.Red, Pixel(second.Output, 8, 8));
            Assert.Equal(SKColors.Red, Pixel(input, 8, 8));
        }
        outward.Dispose(); returning.Dispose();
        Assert.Equal(SKColors.Red, Pixel(input, 8, 8));
    }

    [Fact]
    public void 取消参数范围和原生矩阵失败不返回部分图像()
    {
        using var target = new Surface(SKColors.Red); var node = new ImageGraphNode.Transform("move", GraphReference.Input("target"), SceneMatrix.Identity, new(0, 0, 16, 16), Bounds());
        using var graph = Compile(Definition([node], GraphReference.Node("move"))); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor)), cancel.Token));
        var invalid = new ImageGraphNode.Transform("move", GraphReference.Input("target"), new(1e-100, 0, 0, 1e-100, 0, 0), new(0,0,16,16), Bounds());
        using var native = Compile(Definition([invalid], GraphReference.Node("move")));
        Assert.Equal("GraphNativeValueRange", Code(() => native.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor)))));
        var blur = new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Parameter("sigma"), new(new GraphExpansion.CeilingMultiple("sigma", 3), 6, 6, GraphEdgeMode.Transparent));
        using var range = Compile(Definition([blur], GraphReference.Node("blur"), parameters: ["sigma"], expand: 6), [new EffectParameterConstraint.Number("sigma", 0, 2)]);
        Assert.Equal("GraphParameterOutOfRange", Code(() => range.Execute(Frame(parameters: new ParameterValues([new("sigma", new ParameterValue.Number(3))])), Inputs(("target", target, GraphInputSource.TargetColor)))));
        Assert.Equal("GraphParameterTypeMismatch", Code(() => range.Execute(Frame(parameters: new ParameterValues([new("sigma", new ParameterValue.Whole(1))])), Inputs(("target", target, GraphInputSource.TargetColor)))));
        using var good = graph.Execute(Frame(), Inputs(("target", target, GraphInputSource.TargetColor))); Assert.Equal(SKColors.Red, Pixel(good.Output, 8, 8));
    }
}
