using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ImageSurfaceBudgetTests
{
    [Fact]
    public void 固定包图片跨帧保留且重复别名不重复字节释放不夺取原图()
    {
        using var colorSpace = SKColorSpace.CreateSrgb();
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace)); bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 8, 8), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        using var surface = SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color);
        using var alias = SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumBytes = 256, MaximumSurfaces = 1 });
        using var hold = budget.HoldFixedImages([surface, alias]);
        Assert.Equal(256, budget.Usage.EstimatedBytes); Assert.Equal(1, budget.Usage.ActiveSurfaces);
        using var frame = budget.BeginFrame(); using var graph = Graph(glass: true);
        using var borrowed = graph.Execute(Context(surface, true), Inputs(alias), frame);
        Assert.Equal(256, budget.Usage.EstimatedBytes); Assert.Equal(1, frame.Usage.ActiveSurfaces);
        hold.Dispose(); Assert.Equal(256, budget.Usage.EstimatedBytes);
        borrowed.Dispose(); Empty(budget); Assert.NotEqual(nint.Zero, image.Handle);
    }

    [Fact]
    public void 固定包图片超额整次回收已登记图片且不释放调用方图像()
    {
        using var bitmap = new SKBitmap(8, 8); using var image = SKImage.FromBitmap(bitmap); using var second = SKImage.FromBitmap(bitmap);
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 8, 8), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        using var a = SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color); using var b = SkiaGraphSurface.Borrow(second, descriptor, ShaderSurfaceKind.Color);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumBytes = 256 });
        Assert.Throws<ImageSurfaceBudgetException>(() => budget.HoldFixedImages([a, b])); Empty(budget);
        Assert.NotEqual(nint.Zero, image.Handle); Assert.NotEqual(nint.Zero, second.Handle);
    }
    private const long PixelBytes = 128 * 96 * 4;
    private static readonly GraphRect Bounds = new(0, 0, 128, 96);
    private static readonly SceneCaptureRecipe Recipe = new(true, false, false, true, SceneCaptureRegion.FillAndStroke);
    private static ProjectSnapshot Snapshot(bool stroke = false)
    {
        var line = LyricLine.Create("row", "歌語", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "歌", new(0, 2_000_000), TimingStatus.Edited), LyricToken.Create("b", "語", new(2_000_000, 5_000_000), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "か")] };
        var project = Project.Create("surface budget", Composition.Create(128, 96, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 32,
                Opacity = 1, Stroke = stroke ? StrokeSpec.Create(RgbaColor.White, 2) : null, Shadow = null });
        return ProjectSnapshot.FromProject(project);
    }
    private static RenderRequest Request(ProjectSnapshot snapshot, RenderTarget? target = null) => RenderRequest.At(snapshot, 1_000_000)
        with { TransparentBackground = true, Target = target };
    private static SceneCaptureTarget.Line Target(SkiaRenderPipeline renderer, RenderRequest request)
    {
        var line = renderer.GetSceneFrame(request.Project, request.TimeUs).Lines[0];
        return new(new(new(line.Frame.Line!.Id), new(line.BindingId!)));
    }
    private static SkiaGraphSurface Capture(SkiaRenderPipeline renderer, RenderRequest request, ImageSurfaceBudgetFrame? frame = null,
        SceneCaptureRecipe? recipe = null) => renderer.CaptureSceneTarget(request, Target(renderer, request), recipe ?? Recipe,
            GraphInputSource.TargetColor, Bounds, GraphExecutionBudget.Default, frame);
    private static ImageGraphFrameContext Context(SkiaGraphSurface capture, bool transparent = false) => new(1_000_000, 128, 96,
        capture.Descriptor.Bounds, capture.Descriptor.DensityX, capture.Descriptor.DensityY, Fx.ParameterValues.Empty, transparent);
    private static Dictionary<string, SkiaGraphInput> Inputs(SkiaGraphSurface capture) => new() { ["target"] = new(capture, GraphInputSource.TargetColor) };
    private static CompiledImageGraph Graph(bool blur = false, bool glass = false, bool invalidColor = false, bool aliases = false)
    {
        var inputs = new List<GraphInputDefinition> { new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true,
            GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas) };
        var bounds = new GraphNodeBounds(new GraphExpansion.Literal(blur ? 3 : 0), blur ? 3 : 0, blur ? 3 : 0, GraphEdgeMode.Transparent);
        List<KeyValuePair<string, ShaderProgramDefinition>> programs = [];
        ImageGraphNode node;
        if (glass)
        {
            inputs.Add(new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas));
            node = new ImageGraphNode.Blend("output", GraphReference.Input("media"), GraphReference.Input("target"), GraphBlendMode.SourceOver, bounds);
        }
        else if (invalidColor)
        {
            node = new ImageGraphNode.RuntimeShader("output", "bad", ShaderSurfaceKind.Color, [], [], bounds);
            programs.Add(new("bad", new("bad.sksl", "half4 main(float2 p) { return half4(1, 0, 0, .25); }", ShaderProgramDefinition.RasterProfile, [], [])));
        }
        else if (blur) node = new ImageGraphNode.Blur("output", GraphReference.Input("target"), new GraphNumberBinding.Literal(1), bounds);
        else
        {
            if (aliases) inputs.Add(new("alias", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas));
            node = new ImageGraphNode.Blend("output", GraphReference.Input("target"), GraphReference.Input(aliases ? "alias" : "target"), GraphBlendMode.SourceOver, bounds);
        }
        var definition = new ImageGraphDefinition("image", ImageGraphDefinition.CurrentVersion, inputs, [], [node], GraphReference.Node("output"),
            GraphCoordinateSpace.Canvas, blur ? 3 : 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: glass ? GraphReference.Input("target") : null);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], programs));
    }
    private static byte[] Bytes(SkiaGraphSurface surface) { using var bitmap = SKBitmap.FromImage(surface.Image); return bitmap.Bytes; }
    private static void Empty(ImageSurfaceBudgetSession session)
    { Assert.Equal(0, session.Usage.ActiveSurfaces); Assert.Equal(0, session.Usage.EstimatedBytes); }

    [Fact]
    public void 捕获产物跨帧保留会话字节关闭帧不提前减账释放后恢复()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 1, MaximumBytes = PixelBytes * 2 });
        using SkiaRenderPipeline renderer = new(surfaceBudget: budget); var request = Request(Snapshot());
        using var a = Capture(renderer, request); using var b = Capture(renderer, request);
        Assert.Equal(2, budget.Usage.ActiveSurfaces); Assert.Equal(PixelBytes * 2, budget.Usage.EstimatedBytes);
        Assert.Contains(Bytes(a).Where((_, i) => i % 4 == 3), alpha => alpha > 0);
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => Capture(renderer, request));
        Assert.Equal("CaptureSceneTarget", error.Operation); Assert.Equal(PixelBytes * 2, error.CurrentBytes); Assert.Equal(PixelBytes, error.AdditionalBytes);
        a.Dispose(); using var restored = Capture(renderer, request); Assert.Equal(Bytes(b), Bytes(restored));
        restored.Dispose(); b.Dispose(); Empty(budget);
        using var frame = budget.BeginFrame(); using var last = Capture(renderer, request, frame);
        frame.Dispose(); budget.Dispose(); Assert.Equal(PixelBytes, budget.Usage.EstimatedBytes);
        Assert.Throws<ObjectDisposedException>(() => budget.BeginFrame());
        Assert.True(last.Image.Handle != nint.Zero); last.Dispose(); Empty(budget);
    }

    [Fact]
    public void 同帧活动限制独立于会话字节且关闭后拒绝分配()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 1, MaximumBytes = PixelBytes * 10 });
        using var frame = budget.BeginFrame(); using SkiaRenderPipeline renderer = new(surfaceBudget: budget); var request = Request(Snapshot());
        using var a = Capture(renderer, request, frame);
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => Capture(renderer, request, frame));
        Assert.Equal(1, error.CurrentSurfaces); Assert.Equal(1, error.AdditionalSurfaces); Assert.Equal(1, error.MaximumSurfaces);
        Assert.Equal(PixelBytes, budget.Usage.EstimatedBytes); a.Dispose(); Empty(budget);
        frame.Dispose(); Assert.Throws<ObjectDisposedException>(() => Capture(renderer, request, frame)); Empty(budget);
    }

    [Fact]
    public void 跨图保留产物在节点分配前失败释放后可重新执行()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 2, MaximumBytes = PixelBytes * 10 });
        using var frame = budget.BeginFrame(); using SkiaRenderPipeline renderer = new(surfaceBudget: budget);
        using var capture = Capture(renderer, Request(Snapshot()), frame); using var a = Graph(); using var b = Graph();
        using var result = a.Execute(Context(capture), Inputs(capture), frame); byte[] preserved = Bytes(result.Output);
        Assert.Equal(2, frame.Usage.ActiveSurfaces); Assert.Equal(PixelBytes * 2, budget.Usage.EstimatedBytes);
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => b.Execute(Context(capture), Inputs(result.Output), frame));
        Assert.Equal("ExecuteImageGraphPeak", error.Operation); Assert.Equal(2, error.CurrentSurfaces); Assert.Equal(1, error.AdditionalSurfaces);
        Assert.Equal(preserved, Bytes(result.Output)); Assert.Equal(2, frame.Usage.PeakSurfaces);
        result.Dispose(); using var retry = b.Execute(Context(capture), Inputs(capture), frame);
        Assert.Equal(preserved, Bytes(retry.Output)); retry.Dispose(); capture.Dispose(); Empty(budget);
    }

    [Fact]
    public void 真实绑定适配器共享输入和输出账并附加实际实例失败身份()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 2, MaximumBytes = PixelBytes * 10 });
        using var frame = budget.BeginFrame(); using SkiaRenderPipeline renderer = new(surfaceBudget: budget);
        var snapshot = Snapshot() with { EffectInputsFingerprint = "surface-binding" }; var request = Request(snapshot);
        var scene = renderer.GetSceneFrame(snapshot, request.TimeUs); var target = Target(renderer, request).Target;
        var resource = new Fx.ResourceCatalog("test", "sha256:" + new string('a', 64), [], ["image"], []).GetGraph("image");
        PreparedImageEffectBinding Binding(string id) => new(new(id), "artifact", new(new Fx.ImageEffectTarget.Line(target), resource, Fx.ParameterValues.Empty));
        var first = Binding("first"); var next = Binding("next");
        var prepared = new PreparedRenderFrame(snapshot, request.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, [], [], [])
            .WithImageEffects([first, next]); request = request with { PreparedFrame = prepared };
        using var graph = Graph(); var program = new SceneImageGraphProgram(resource, graph, Recipe, Bounds);
        using var result = renderer.ExecuteSceneImageEffect(request, first, program, new Dictionary<string, SkiaGraphInput>(), null, frame);
        Assert.Equal(2, frame.Usage.ActiveSurfaces);
        var error = Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(request, next, program,
            new Dictionary<string, SkiaGraphInput>(), result.GraphResult.Output, frame));
        Assert.Equal("ImageSurfaceBudgetExceeded", error.Code); Assert.Equal(next.InstanceId, error.InstanceId); Assert.Equal("artifact", error.ArtifactHash);
        Assert.Equal("ExecuteImageGraph", error.Stage); Assert.Equal(request.TimeUs, error.TimeUs); Assert.IsType<ImageSurfaceBudgetException>(error.InnerException);
        Assert.Equal(2, frame.Usage.ActiveSurfaces); result.Dispose(); Empty(budget);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 输入别名只计一次且跨帧共享相同存储不重复会话字节(bool transparent)
    {
        using ImageSurfaceBudgetSession budget = new(); using var firstFrame = budget.BeginFrame(); using var secondFrame = budget.BeginFrame();
        using SkiaRenderPipeline renderer = new(surfaceBudget: budget); using var capture = Capture(renderer, Request(Snapshot()), firstFrame);
        using var alias = SkiaGraphSurface.Borrow(capture.Image, capture.Descriptor, capture.Kind); using var graph = Graph(glass: transparent, aliases: !transparent);
        var inputs = Inputs(capture); if (!transparent) inputs.Add("alias", new(alias, GraphInputSource.TargetColor));
        using var result = graph.Execute(Context(capture, transparent), inputs, secondFrame);
        Assert.Equal(1, firstFrame.Usage.ActiveSurfaces); Assert.Equal(transparent ? 1 : 2, secondFrame.Usage.ActiveSurfaces);
        Assert.Equal(PixelBytes * (transparent ? 1 : 2), budget.Usage.EstimatedBytes);
        if (transparent) { Assert.Same(capture, result.Output); Assert.Single(result.Diagnostics); }
        result.Dispose(); Assert.Equal(0, secondFrame.Usage.ActiveSurfaces); Assert.Equal(PixelBytes, budget.Usage.EstimatedBytes);
        capture.Dispose(); Empty(budget);
    }

    [Fact]
    public void 外部借用输入透明确认旁路计费保留至结果释放且不夺取图像()
    {
        using var sourceBudget = new ImageSurfaceBudgetSession(); using var sourceFrame = sourceBudget.BeginFrame();
        using SkiaRenderPipeline renderer = new(surfaceBudget: sourceBudget); using var capture = Capture(renderer, Request(Snapshot()), sourceFrame);
        using ImageSurfaceBudgetSession budget = new(new() { MaximumBytes = PixelBytes }); using var frame = budget.BeginFrame(); using var graph = Graph(glass: true);
        using var result = graph.Execute(Context(capture, true), Inputs(capture), frame); frame.Dispose();
        Assert.Equal(PixelBytes, budget.Usage.EstimatedBytes); Assert.Equal(Bytes(capture), Bytes(result.Output));
        result.Dispose(); Empty(budget); Assert.True(capture.Image.Handle != nint.Zero); Assert.Equal(PixelBytes, sourceBudget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 真实捕获描边临时图层计峰值且失败不漏账()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 1 }); using var frame = budget.BeginFrame();
        using SkiaRenderPipeline renderer = new(surfaceBudget: budget); var request = Request(Snapshot(stroke: true));
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => Capture(renderer, request, frame, Recipe with { IncludeStroke = true }));
        Assert.Equal("CaptureStrokeLayer", error.Operation); Assert.Equal(1, error.CurrentSurfaces); Empty(budget);
        using var allowed = Capture(renderer, request, frame); Assert.Contains(Bytes(allowed), pixel => pixel != 0);
        Assert.Equal(1, frame.Usage.PeakSurfaces); allowed.Dispose(); Empty(budget);
    }

    [Fact]
    public void 模糊临时存储峰值预检含其他目标且执行后立即释放scratch()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 4 }); using var frame = budget.BeginFrame();
        using SkiaRenderPipeline renderer = new(surfaceBudget: budget); var request = Request(Snapshot());
        using var capture = Capture(renderer, request, frame); using var other = Capture(renderer, request, frame); using var graph = Graph(blur: true);
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => graph.Execute(Context(capture), Inputs(capture), frame));
        Assert.Equal("ExecuteImageGraphPeak", error.Operation); Assert.Equal(3, error.AdditionalSurfaces); Assert.Equal(2, error.CurrentSurfaces);
        other.Dispose(); using var result = graph.Execute(Context(capture), Inputs(capture), frame);
        Assert.Equal(4, frame.Usage.PeakSurfaces); Assert.Equal(PixelBytes + 134 * 102 * 4 * 3, frame.Usage.PeakBytes); Assert.Equal(2, frame.Usage.ActiveSurfaces);
        Assert.Equal(134, result.Output.Image.Width); Assert.Contains(Bytes(result.Output), pixel => pixel != 0); result.Dispose(); capture.Dispose(); Empty(budget);
    }

    [Fact]
    public void 合成计入存活产物但不重复输入句柄且返回产物继续计费()
    {
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 3 }); using var frame = budget.BeginFrame();
        using SkiaRenderPipeline renderer = new(surfaceBudget: budget); var request = Request(Snapshot());
        using var capture = Capture(renderer, request, frame); using var other = Capture(renderer, request, frame); using var graph = Graph();
        using var result = graph.Execute(Context(capture), Inputs(capture), frame);
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => graph.Composite(result, capture, frame));
        Assert.Equal("CompositeImageGraphPeak", error.Operation); Assert.Equal(3, frame.Usage.ActiveSurfaces);
        other.Dispose(); using var output = graph.Composite(result, capture, frame); Assert.Equal(3, frame.Usage.ActiveSurfaces);
        Assert.Contains(Bytes(output), pixel => pixel != 0); result.Dispose(); Assert.Equal(2, frame.Usage.ActiveSurfaces);
        output.Dispose(); capture.Dispose(); Empty(budget);
    }

    [Theory]
    [InlineData(128, 96)] [InlineData(64, 48)] [InlineData(96, 48)]
    public void 连续真实密度捕获图和合成释放后归零且预乘像素合法(int width, int height)
    {
        using ImageSurfaceBudgetSession budget = new(); using SkiaRenderPipeline renderer = new(surfaceBudget: budget); using var graph = Graph();
        var request = Request(Snapshot(), new() { PixelWidth = width, PixelHeight = height });
        for (int iteration = 0; iteration < 12; iteration++)
        {
            using (var frame = budget.BeginFrame())
            using (var capture = Capture(renderer, request, frame))
            using (var result = graph.Execute(Context(capture), Inputs(capture), frame))
            using (var output = graph.Composite(result, capture, frame))
            {
                Assert.Equal((long)width * height * 4 * 3, budget.Usage.EstimatedBytes); Assert.Equal(width, output.Image.Width);
                byte[] bytes = Bytes(output); for (int i = 0; i < bytes.Length; i += 4)
                { Assert.True(bytes[i] <= bytes[i + 3]); Assert.True(bytes[i + 1] <= bytes[i + 3]); Assert.True(bytes[i + 2] <= bytes[i + 3]); }
            }
            Empty(budget);
        }
        Assert.Equal(3, budget.Usage.PeakSurfaces);
    }

    [Fact]
    public void 无效shader像素和取消不返回部分图像或泄漏中间表面()
    {
        using ImageSurfaceBudgetSession budget = new(); using var frame = budget.BeginFrame(); using SkiaRenderPipeline renderer = new(surfaceBudget: budget);
        var request = Request(Snapshot()); using var capture = Capture(renderer, request, frame); using var bad = Graph(invalidColor: true);
        var error = Assert.Throws<ImageGraphException>(() => bad.Execute(Context(capture), Inputs(capture), frame)); Assert.Equal("GraphColorNotPremultiplied", error.Code);
        Assert.Equal(PixelBytes, budget.Usage.EstimatedBytes); Assert.Equal(1, frame.Usage.ActiveSurfaces);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel(); using var good = Graph();
        Assert.Throws<OperationCanceledException>(() => good.Execute(Context(capture), Inputs(capture), frame, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => Capture(renderer, request with { CancellationToken = cancelled.Token }, frame));
        Assert.Equal(PixelBytes, budget.Usage.EstimatedBytes); capture.Dispose(); Empty(budget);
    }
}
