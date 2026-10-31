using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SceneBackgroundCaptureTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scene-background-").FullName;
    public void Dispose() => Directory.Delete(_directory, true);
    private string Image()
    {
        string path = Path.Combine(_directory, "media.png"); using var bitmap = new SKBitmap(8, 8); bitmap.Erase(new(255, 0, 0, 128));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); using var file = File.Create(path); data.SaveTo(file); return path;
    }
    private static RenderRequest Request(BackgroundSource source, double dx = 1, double dy = 1) => RenderRequest.At(ProjectSnapshot.FromProject(Project.Create("background capture",
        Composition.Create(32, 16, FrameRate.Fps30, new(0, 0, 255)), background: source)), 123_456) with { Target = new() { PixelWidth = (int)(32 * dx), PixelHeight = (int)(16 * dy) } };
    private static byte[] Pixel(SkiaGraphSurface surface, int x, int y)
        => SkiaTestReadback.Pixel(surface.Image, x, y);

    [Theory]
    [InlineData(BackgroundFit.Contain, 1, 1)] [InlineData(BackgroundFit.Cover, 1, 1)] [InlineData(BackgroundFit.Original, 1, 1)]
    [InlineData(BackgroundFit.Contain, .5, .5)] [InlineData(BackgroundFit.Cover, 2, 1)] [InlineData(BackgroundFit.Original, 1, 2)]
    public void 真实图片仅媒体捕获与底色捕获分开合成等于生产Render(BackgroundFit fit, double dx, double dy)
    {
        using var pipeline = new SkiaRenderPipeline(); var request = Request(BackgroundSource.ForImage(Image(), fit), dx, dy);
        using var media = pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, new(0, 0, 32, 16), GraphExecutionBudget.Default)!;
        using var color = pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, new(0, 0, 32, 16), GraphExecutionBudget.Default)!;
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(color, (int)(16 * dx), (int)(8 * dy)));
        Assert.Equal(new byte[] { 0, 0, 128, 128 }, Pixel(media, (int)(16 * dx), (int)(8 * dy)));
        if (fit != BackgroundFit.Cover) Assert.Equal(new byte[4], Pixel(media, 0, 0));
        using var srgb = SKColorSpace.CreateSrgb(); using var expected = SKSurface.Create(new SKImageInfo(media.Image.Width, media.Image.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        expected.Canvas.ClipRect(new(0, 0, media.Image.Width, media.Image.Height)); expected.Canvas.Clear(SKColors.Transparent);
        expected.Canvas.DrawImage(color.Image, 0, 0); expected.Canvas.DrawImage(media.Image, 0, 0);
        using var image = expected.Snapshot(); using var actual = pipeline.Render(request);
        Assert.Equal(SkiaTestReadback.Pixels(image), actual.Pixels.ToArray()); Assert.True(media.Image.ColorSpace!.IsSrgb);
    }

    [Fact]
    public void 画布外透明显式分数捕获框对齐实际像素网格()
    {
        using var pipeline = new SkiaRenderPipeline(); var request = Request(BackgroundSource.ForImage(Image(), BackgroundFit.Cover), 2, .5);
        using var capture = pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, new(-1.25, -2.25, 33.25, 18.25), GraphExecutionBudget.Default)!;
        Assert.Equal(new GraphRect(-1.5, -4, 33.5, 20), capture.Descriptor.Bounds);
        Assert.Equal(new byte[4], Pixel(capture, 0, 0)); Assert.Equal(new byte[] { 0, 0, 128, 128 }, Pixel(capture, 35, 6));
        using var color = pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, capture.Descriptor.Bounds, GraphExecutionBudget.Default)!;
        Assert.Equal(new byte[4], Pixel(color, 0, 0));
    }

    [Fact]
    public void 缺失媒体返回不可用而不是透明替代图且底色仍可独立读取()
    {
        using var pipeline = new SkiaRenderPipeline(); var missing = Request(BackgroundSource.ForImage(Path.Combine(_directory, "missing.png")));
        Assert.Null(pipeline.CaptureSceneBackground(missing, GraphInputSource.BackgroundMedia, new(0, 0, 32, 16), GraphExecutionBudget.Default));
        Assert.Null(pipeline.CaptureSceneBackground(Request(BackgroundSource.Color), GraphInputSource.BackgroundMedia, new(0, 0, 32, 16), GraphExecutionBudget.Default));
        using var color = pipeline.CaptureSceneBackground(missing, GraphInputSource.BackgroundColor, new(0, 0, 32, 16), GraphExecutionBudget.Default);
        Assert.NotNull(color); Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(color, 0, 0));
    }

    [Fact]
    public void 透明输出不解析媒体且预算取消和无效来源明确失败()
    {
        using var resolver = new RecordingResolver(); using var budget = new ImageSurfaceBudgetSession(new() { MaximumBytes = 1 });
        using var pipeline = new SkiaRenderPipeline(backgroundResolver: resolver, surfaceBudget: budget);
        var request = Request(BackgroundSource.ForVideo("/tmp/never-open.mp4")) with { TransparentBackground = true };
        Assert.Null(pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, new(0, 0, 32, 16), GraphExecutionBudget.Default));
        Assert.Null(pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, new(0, 0, 32, 16), GraphExecutionBudget.Default)); Assert.Equal(0, resolver.Calls);
        Assert.Throws<ImageSurfaceBudgetException>(() => pipeline.CaptureSceneBackground(request with { TransparentBackground = false }, GraphInputSource.BackgroundColor, new(0, 0, 32, 16), GraphExecutionBudget.Default));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal("UnsupportedBackgroundCaptureSource", Assert.Throws<PreparedFrameException>(() => pipeline.CaptureSceneBackground(request, GraphInputSource.BackdropBelow, new(0, 0, 32, 16), GraphExecutionBudget.Default)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => pipeline.CaptureSceneBackground(request with { CancellationToken = cancelled.Token }, GraphInputSource.BackgroundMedia, new(0, 0, 32, 16), GraphExecutionBudget.Default)); Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void 已返回背景表面在创建管线释放后仍保留真实像素()
    {
        using var pipeline = new SkiaRenderPipeline();
        using var capture = pipeline.CaptureSceneBackground(Request(BackgroundSource.Color), GraphInputSource.BackgroundColor,
            new(0, 0, 32, 16), GraphExecutionBudget.Default)!;
        pipeline.Dispose();
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(capture, 16, 8));
    }

    [Fact]
    public void 真实工程整数时刻传给媒体来源()
    {
        using var resolver = new RecordingResolver(); using var pipeline = new SkiaRenderPipeline(backgroundResolver: resolver);
        using var capture = pipeline.CaptureSceneBackground(Request(BackgroundSource.ForVideo("/tmp/video.mp4")), GraphInputSource.BackgroundMedia, new(0, 0, 32, 16), GraphExecutionBudget.Default);
        Assert.Null(capture); Assert.Equal(1, resolver.Calls); Assert.Equal(123_456, resolver.Time);
    }
    private sealed class RecordingResolver : IBackgroundFrameSourceResolver, IBackgroundFrameSource
    {
        public int Calls { get; private set; }
        public long Time { get; private set; }
        public IBackgroundFrameSource Resolve(BackgroundSource background) { Calls++; return this; }
        public SKBitmap? GetFrame(long timeUs) { Time = timeUs; return null; }
        public void Dispose() { }
    }
}
