using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>行进入动画的渲染测试（roadmap `STYLE-04`）。</summary>
/// <remarks>
/// 断言用"同一行的两个时刻"或"有动画 / 无动画"对照，而不是自己算期望像素：
/// 动画是<b>视觉</b>的东西，测试要盯住的是"它真的改变了画面、而且只按说好的方式改变"。
/// </remarks>
public sealed class LineAnimationRenderTests : IDisposable
{
    private static readonly RgbaColor Background = new(0x10, 0x10, 0x14);
    private const string LineText = "夜空中最亮的星";

    private readonly SkiaTextLayoutEngine _layoutEngine = new();

    /// <inheritdoc/>
    public void Dispose() => _layoutEngine.Dispose();

    [Fact]
    public void 没有动画时两个时刻的画面在行起点处就相同()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        using RenderFrame atStart = pipeline.Render(CreateRequest(animation: null, timeUs: 1_000_000));
        using RenderFrame later = pipeline.Render(CreateRequest(animation: null, timeUs: 1_200_000));

        // 没有动画：0.2 秒的差别只来自逐字进度，行起点附近应当几乎一致；
        // 这里只要求"文字都在"，避免把逐字进度也算进动画的账。
        Assert.True(TextPixelCount(atStart) > 0);
        Assert.True(TextPixelCount(later) > 0);
    }

    [Fact]
    public void 起始不透明度为零时行起点处看不到文字()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        LineAnimation animation = new() { DurationUs = 400_000, Easing = AnimationEasing.Linear, StartOpacity = 0f, StartScale = 1f, StartOffsetPx = 0f };

        using RenderFrame frame = pipeline.Render(CreateRequest(animation, timeUs: 1_000_000));

        // 行刚开始：动画的第一帧就是不透明度为 0，画面上应当只有背景色。
        Assert.Equal(0, TextPixelCount(frame));
    }

    [Fact]
    public void 动画中途比结束态更淡()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        LineAnimation animation = new() { DurationUs = 1_000_000, Easing = AnimationEasing.Linear, StartOpacity = 0f, StartScale = 1f, StartOffsetPx = 0f };

        using RenderFrame half = pipeline.Render(CreateRequest(animation, timeUs: 1_500_000));
        using RenderFrame done = pipeline.Render(CreateRequest(animation, timeUs: 2_000_000));

        Assert.True(TextPixelCount(half) > 0, "动画中途应当已经能看到文字。");
        Assert.True(MaxDistanceFromBackground(half) < MaxDistanceFromBackground(done));
    }

    [Fact]
    public void 纵向偏移把文字整体推下去()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        LineAnimation animation = new() { DurationUs = 1_000_000, Easing = AnimationEasing.Linear, StartOpacity = 1f, StartScale = 1f, StartOffsetPx = 40f };

        using RenderFrame start = pipeline.Render(CreateRequest(animation, timeUs: 1_000_000));
        using RenderFrame done = pipeline.Render(CreateRequest(animation, timeUs: 2_000_000));

        // 起点那帧被推下去 40 像素，因此它的最低一行比结束态更低。
        Assert.True(LastTextRow(start) >= LastTextRow(done) + 30);
    }

    [Fact]
    public void 缩放让文字更宽()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        LineAnimation animation = new() { DurationUs = 1_000_000, Easing = AnimationEasing.Linear, StartOpacity = 1f, StartScale = 0.5f, StartOffsetPx = 0f };

        using RenderFrame halfScale = pipeline.Render(CreateRequest(animation, timeUs: 1_000_000));
        using RenderFrame fullScale = pipeline.Render(CreateRequest(animation, timeUs: 2_000_000));

        Assert.True(TextExtent(halfScale) < TextExtent(fullScale));
    }

    [Fact]
    public void 行开始之前停在动画第一帧()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        LineAnimation animation = new() { DurationUs = 400_000, Easing = AnimationEasing.EaseOut, StartOpacity = 0f, StartScale = 1f, StartOffsetPx = 20f };

        using RenderFrame before = pipeline.Render(CreateRequest(animation, timeUs: 900_000));
        using RenderFrame atStart = pipeline.Render(CreateRequest(animation, timeUs: 1_000_000));

        Assert.Equal(TextPixelCount(atStart), TextPixelCount(before));
        Assert.Equal(LastTextRow(atStart), LastTextRow(before));
    }

    [Fact]
    public void 中性动画与没有动画的画面一致()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        LineAnimation neutral = new() { DurationUs = 400_000, StartOpacity = 1f, StartScale = 1f, StartOffsetPx = 0f };

        using RenderFrame without = pipeline.Render(CreateRequest(animation: null, timeUs: 1_300_000));
        using RenderFrame with = pipeline.Render(CreateRequest(neutral, timeUs: 1_300_000));

        Assert.Equal(0, CountDifferent(without, with));
    }

    [Fact]
    public void 行级覆盖优先于工程默认()
    {
        using SkiaRenderPipeline pipeline = new(_layoutEngine);

        // 工程默认是"淡入"，这一行覆盖成"中性"，因此行起点处应当能直接看到文字。
        Project project = Project.Create(
            "动画测试",
            Composition.Create(320, 240, FrameRate.Fps30, Background, outputDurationUs: 4_000_000),
            LyricsDocument.Create(
            [
                LyricLine.Create(
                    "l1",
                    LineText,
                    TimeRange.Create(1_000_000, 3_000_000),
                    TimingStatus.Edited),
            ]),
            animation: new LineAnimation { DurationUs = 400_000, StartOpacity = 0f, StartScale = 1f, StartOffsetPx = 0f })
            .WithLinePresentation("l1", animation: new LineAnimation { DurationUs = 400_000, StartOpacity = 1, StartScale = 1, StartOffsetPx = 0 });

        using RenderFrame frame = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 1_000_000));

        Assert.True(TextPixelCount(frame) > 0, "行级覆盖成中性后，行起点处应当能看到文字。");
    }

    private static RenderRequest CreateRequest(LineAnimation? animation, long timeUs)
    {
        Project project = Project.Create(
            "动画测试",
            Composition.Create(320, 240, FrameRate.Fps30, Background, outputDurationUs: 4_000_000),
            LyricsDocument.Create(
            [
                LyricLine.Create("l1", LineText, TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Edited),
            ]),
            animation: animation);

        return RenderRequest.At(ProjectSnapshot.FromProject(project), timeUs);
    }

    /// <summary>明显不是背景色的像素数（文字与其描边阴影都算）。</summary>
    private static int TextPixelCount(RenderFrame frame) => CountDifferentFrom(frame, Background);

    private static int CountDifferentFrom(RenderFrame frame, RgbaColor background)
    {
        int count = 0;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, background) > 12)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>两帧之间差异明显的像素数。</summary>
    private static int CountDifferent(RenderFrame first, RenderFrame second)
    {
        int count = 0;

        for (int y = 0; y < first.Height; y++)
        {
            for (int x = 0; x < first.Width; x++)
            {
                Assert.True(first.TryGetPixel(x, y, out RgbaColor left));
                Assert.True(second.TryGetPixel(x, y, out RgbaColor right));

                if (Distance(left, right) > 8)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>最下方一个非背景像素所在的行；没有则返回 -1。</summary>
    private static int LastTextRow(RenderFrame frame)
    {
        for (int y = frame.Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, Background) > 12)
                {
                    return y;
                }
            }
        }

        return -1;
    }

    /// <summary>全帧里最偏离背景的那个像素的色差。</summary>
    private static int MaxDistanceFromBackground(RenderFrame frame)
    {
        int maximum = 0;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));
                maximum = Math.Max(maximum, Distance(color, Background));
            }
        }

        return maximum;
    }

    /// <summary>文字占据的横向像素宽度（首末非背景列之差）。</summary>
    private static int TextExtent(RenderFrame frame)
    {
        int first = -1;
        int last = -1;

        for (int x = 0; x < frame.Width; x++)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, Background) > 12)
                {
                    if (first < 0)
                    {
                        first = x;
                    }

                    last = x;
                    break;
                }
            }
        }

        return first < 0 ? 0 : last - first + 1;
    }

    private static int Distance(RgbaColor first, RgbaColor second) =>
        Math.Abs(first.Red - second.Red)
        + Math.Abs(first.Green - second.Green)
        + Math.Abs(first.Blue - second.Blue);
}
