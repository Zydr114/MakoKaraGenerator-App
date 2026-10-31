using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>透明画布的像素测试（roadmap `EXPORT-07`）。</summary>
/// <remarks>
/// 透明输出的全部要求就是"该透明的地方真的透明、该不透明的地方保持不透明"，
/// 因此断言的是 Alpha 通道本身：画布四角必须是 0，歌词像素必须带着它自己的 Alpha，
/// 而且半透明样式要原样保留（那正是"预乘还是直乘"出问题的地方）。
/// </remarks>
public sealed class TransparentOutputRenderTests : IDisposable
{
    private const int CanvasWidth = 640;
    private const int CanvasHeight = 360;

    private static readonly RgbaColor Background = new(0x10, 0x10, 0x20);
    private static readonly RgbaColor Unsung = new(0xE0, 0xE0, 0xE0);

    private readonly string _directory = Directory.CreateTempSubdirectory("mkg-alpha-").FullName;
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
    public void 不透明导出时画面处处不透明()
    {
        using RenderFrame frame = Render(transparent: false);

        Assert.Equal(255, Alpha(frame, 5, 5));
        Assert.Equal(255, Alpha(frame, 320, 180));
    }

    [Fact]
    public void 透明导出时没有歌词的地方完全透明()
    {
        using RenderFrame frame = Render(transparent: true);

        Assert.Equal(0, Alpha(frame, 5, 5));
        Assert.Equal(0, Alpha(frame, 635, 355));
    }

    [Fact]
    public void 透明导出时歌词内部仍然不透明()
    {
        // 取最不透明的那个像素而不是"第一个画到的"：字形边缘是抗锯齿的，
        // 边缘像素的 Alpha 本来就是部分值（实测 180），拿它断言会测错东西。
        using RenderFrame frame = Render(transparent: true);

        Assert.Equal(255, MostOpaquePixel(frame).Alpha);
    }

    [Fact]
    public void 透明导出保留半透明样式()
    {
        // 半透明样式在透明输出里必须仍是"部分透明"：
        // 实测最不透明的像素约 224（文字分几层叠画，50% 叠三次是 87.5%），
        // 因此这里只断言"确实是部分值"，具体档位留给样式测试。
        using RenderFrame frame = Render(transparent: true, opacity: 0.5f);

        (_, _, int alpha) = MostOpaquePixel(frame);

        Assert.InRange(alpha, 2, 253);
        Assert.Equal(255, MostOpaquePixel(Render(transparent: true)).Alpha);
    }

    [Fact]
    public void 透明导出时不画背景图()
    {
        // 背景图是不透明的红：透明输出必须一个像素都不画它（它是"背景"）。
        string image = WritePng(60, 60, new SKColor(0xE0, 0x20, 0x20));

        using RenderFrame frame = Render(
            transparent: true,
            background: BackgroundSource.ForImage(image, BackgroundFit.Cover));

        Assert.Equal(0, Alpha(frame, 320, 180));
    }

    private RenderFrame Render(
        bool transparent,
        float opacity = 1f,
        BackgroundSource? background = null) => _pipeline.Render(new RenderRequest
        {
            Project = Snapshot(opacity, background ?? BackgroundSource.Color),
            TimeUs = 250_000,
            TransparentBackground = transparent,
        });

    private static (int X, int Y, int Alpha) MostOpaquePixel(RenderFrame frame)
    {
        int best = -1;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int alpha = Alpha(frame, x, y);

                if (alpha > best)
                {
                    best = alpha;
                }
            }
        }

        if (best <= 0)
        {
            Assert.Fail("画面里一个歌词像素都没有。");
        }

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (Alpha(frame, x, y) == best)
                {
                    return (x, y, best);
                }
            }
        }

        return (-1, -1, best);
    }

    private static int Alpha(RenderFrame frame, int x, int y)
    {
        Assert.True(frame.TryGetPixel(x, y, out RgbaColor color), $"取不到像素 ({x}, {y})。");

        return color.Alpha;
    }

    private static ProjectSnapshot Snapshot(float opacity, BackgroundSource background) =>
        ProjectSnapshot.FromProject(
            Project.Create(
                "透明",
                Composition.Create(CanvasWidth, CanvasHeight, FrameRate.Fps30, backgroundColor: Background),
                LyricsDocument.Create(
                [
                    LyricLine.Create(
                        "l1",
                        "夜空",
                        new TimeRange(0, 1_000_000),
                        TimingStatus.Edited,
                        [LyricToken.Create("t1", "夜空", new TimeRange(0, 1_000_000), TimingStatus.Edited)]),
                ]),
                background: background))
        with
        {
            LyricStyle = new LyricStyle
            {
                FontSizePx = 80f,
                VerticalCenterRatio = 0.5d,
                UnsungColor = Unsung,
                Opacity = opacity,
            },
        };

    private string WritePng(int width, int height, SKColor color)
    {
        string path = Path.Combine(_directory, $"bg-{width}x{height}.png");

        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(color);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);

        data.SaveTo(stream);

        return path;
    }
}
