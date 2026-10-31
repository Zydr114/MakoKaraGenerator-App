using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>背景层的像素测试（roadmap `EXPORT-04`）。</summary>
/// <remarks>
/// <para>
/// 断言的是"哪块像素是图、哪块是背景色"，因为背景层唯一可验证的东西就是落位：
/// 三种适配方式下留白的位置不同，这就是它们全部的区别。
/// </para>
/// <para>
/// 用真图片文件而不是内存位图：解码路径（含"文件不存在"）也是要验的一部分。
/// </para>
/// </remarks>
public sealed class BackgroundRenderTests : IDisposable
{
    private const int CanvasWidth = 640;
    private const int CanvasHeight = 360;

    private static readonly RgbaColor Background = new(0x10, 0x10, 0x20);
    private static readonly RgbaColor ImageColor = new(0xE0, 0x20, 0x20);
    private static readonly RgbaColor Transparent = new(0x00, 0x00, 0x00, 0x00);

    private readonly string _directory = Directory.CreateTempSubdirectory("mkg-bg-").FullName;
    private readonly SkiaRenderPipeline _pipeline = new(new SkiaTextLayoutEngine());

    /// <inheritdoc/>
    public void Dispose()
    {
        _pipeline.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void 没有背景图时画面只有背景色()
    {
        using RenderFrame frame = Render(BackgroundSource.Color);

        AssertBackground(frame, 320, 180);
        AssertBackground(frame, 5, 5);
        AssertBackground(frame, 635, 355);
    }

    [Fact]
    public void 完整显示时左右留背景色()
    {
        // 400×400 的方图放进 640×360：按高受限 → 360×360，水平居中（x 从 140 到 500）。
        string path = WritePng(400, 400, ToSkColor(ImageColor));

        using RenderFrame frame = Render(BackgroundSource.ForImage(path, BackgroundFit.Contain));

        AssertImage(frame, 320, 180);
        AssertBackground(frame, 100, 180);
        AssertBackground(frame, 540, 180);
    }

    [Fact]
    public void 铺满时四角也是图()
    {
        string path = WritePng(400, 400, ToSkColor(ImageColor));

        using RenderFrame frame = Render(BackgroundSource.ForImage(path, BackgroundFit.Cover));

        AssertImage(frame, 5, 5);
        AssertImage(frame, 320, 180);
        AssertImage(frame, 635, 355);
    }

    [Fact]
    public void 原始尺寸的小图居中()
    {
        string path = WritePng(100, 100, ToSkColor(ImageColor));

        using RenderFrame frame = Render(BackgroundSource.ForImage(path, BackgroundFit.Original));

        AssertImage(frame, 320, 180);
        AssertBackground(frame, 200, 180);
        AssertBackground(frame, 5, 5);
    }

    [Fact]
    public void 透明图片不改变背景色()
    {
        // 全透明的图等于什么都没有：这证明图片是**叠在**背景色之上的一层，
        // 而不是把画布清成它自己的内容。
        string path = WritePng(400, 400, ToSkColor(Transparent));

        using RenderFrame frame = Render(BackgroundSource.ForImage(path, BackgroundFit.Cover));

        AssertBackground(frame, 320, 180);
        AssertBackground(frame, 5, 5);
    }

    [Fact]
    public void 图片缺失时只剩背景色()
    {
        // 一张图坏掉不该让预览与整段导出都失败（界面另外负责把原因说给用户）。
        using RenderFrame frame = Render(
            BackgroundSource.ForImage(Path.Combine(_directory, "没有这个文件.png")));

        AssertBackground(frame, 320, 180);
    }

    private RenderFrame Render(BackgroundSource background) => _pipeline.Render(
        RenderRequest.At(
            ProjectSnapshot.FromProject(
                Project.Create(
                    "背景",
                    Composition.Create(CanvasWidth, CanvasHeight, FrameRate.Fps30, backgroundColor: Background),
                    background: background)),
            500_000));

    private static void AssertImage(RenderFrame frame, int x, int y) =>
        AssertColor(frame, x, y, ImageColor, "背景图");

    private static void AssertBackground(RenderFrame frame, int x, int y) =>
        AssertColor(frame, x, y, Background, "背景色");

    private static void AssertColor(RenderFrame frame, int x, int y, RgbaColor expected, string what)
    {
        Assert.True(frame.TryGetPixel(x, y, out RgbaColor actual), $"取不到像素 ({x}, {y})。");
        Assert.True(
            Math.Abs(actual.Red - expected.Red) <= 8
            && Math.Abs(actual.Green - expected.Green) <= 8
            && Math.Abs(actual.Blue - expected.Blue) <= 8,
            $"({x}, {y}) 应当是{what} {expected}，实际是 {actual}。");
    }

    private string WritePng(int width, int height, SKColor color)
    {
        string path = Path.Combine(_directory, $"image-{width}x{height}-{color.Alpha}.png");

        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(color);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);

        data.SaveTo(stream);

        return path;
    }

    private static SKColor ToSkColor(RgbaColor color) => new(color.Red, color.Green, color.Blue, color.Alpha);
}
