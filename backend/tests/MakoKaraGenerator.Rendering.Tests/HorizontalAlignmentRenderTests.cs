using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>水平对齐方式的像素测试。</summary>
/// <remarks>
/// 对齐是"把同一个文字块挪到画布的不同位置"，断言的是几何而不是颜色：
/// 取画面里最靠左的文字像素列，三档之间必须严格递增，且右对齐时的偏移
/// 恰好是居中时的两倍（无边距，这是 ADR-0035 定的规则）。
/// </remarks>
public sealed class HorizontalAlignmentRenderTests : IDisposable
{
    private const int CanvasWidth = 640;
    private const int CanvasHeight = 360;

    private static readonly RgbaColor Background = new(0x10, 0x10, 0x14);
    private static readonly RgbaColor Unsung = new(0xE0, 0xE0, 0xE0);

    private readonly SkiaRenderPipeline _pipeline = new(new SkiaTextLayoutEngine());

    /// <inheritdoc/>
    public void Dispose() => _pipeline.Dispose();

    [Fact]
    public void 三档对齐依次向右移动且右对齐恰好是居中的两倍()
    {
        int left = LeftmostPaintedColumn(LyricHorizontalAlignment.Left);
        int center = LeftmostPaintedColumn(LyricHorizontalAlignment.Center);
        int right = LeftmostPaintedColumn(LyricHorizontalAlignment.Right);

        Assert.True(left < center, $"左对齐（{left}）应当比居中（{center}）更靠左。");
        Assert.True(center < right, $"居中（{center}）应当比右对齐（{right}）更靠左。");

        // 无边距：右对齐的起点 = 画布宽 - 文字宽 = 居中起点的两倍。
        Assert.InRange(right, (center * 2) - 2, (center * 2) + 2);

        // 左对齐应当真的贴着左边缘。
        Assert.InRange(left, 0, 2);
    }

    [Fact]
    public void 对齐不改变纵向位置()
    {
        Assert.Equal(VerticalSpan(LyricHorizontalAlignment.Left), VerticalSpan(LyricHorizontalAlignment.Right));
    }

    private static (int Top, int Bottom) VerticalSpan(LyricHorizontalAlignment alignment)
    {
        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        using RenderFrame frame = pipeline.Render(RenderRequest.At(Snapshot(alignment), 500_000));

        int top = int.MaxValue;
        int bottom = -1;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (IsPainted(frame, x, y))
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return (top, bottom);
    }

    private int LeftmostPaintedColumn(LyricHorizontalAlignment alignment)
    {
        using RenderFrame frame = _pipeline.Render(RenderRequest.At(Snapshot(alignment), 500_000));

        for (int x = 0; x < frame.Width; x++)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                if (IsPainted(frame, x, y))
                {
                    return x;
                }
            }
        }

        Assert.Fail($"「{alignment}」这一档一个文字像素都没有画出来。");
        return -1;
    }

    private static bool IsPainted(RenderFrame frame, int x, int y)
    {
        Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

        return Math.Abs(color.Red - Background.Red) > 8
            || Math.Abs(color.Green - Background.Green) > 8
            || Math.Abs(color.Blue - Background.Blue) > 8;
    }

    private static ProjectSnapshot Snapshot(LyricHorizontalAlignment alignment) => ProjectSnapshot.FromProject(
        Project.Create(
            "对齐",
            Composition.Create(CanvasWidth, CanvasHeight, FrameRate.Fps30, backgroundColor: Background),
            LyricsDocument.Create(
            [
                LyricLine.Create(
                    "l1",
                    "夜空",
                    new TimeRange(0, 1_000_000),
                    TimingStatus.Edited,
                    [LyricToken.Create("t1", "夜空", new TimeRange(0, 1_000_000), TimingStatus.Edited)]),
            ]))
        with
        {
            LyricStyle = new LyricStyle
            {
                FontSizePx = 80f,
                VerticalCenterRatio = 0.5d,
                UnsungColor = Unsung,
                HorizontalAlignment = alignment,
            },
        });
}
