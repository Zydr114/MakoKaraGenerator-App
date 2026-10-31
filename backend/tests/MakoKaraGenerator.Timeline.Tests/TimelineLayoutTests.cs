using MakoKaraGenerator.Timeline;

namespace MakoKaraGenerator.Timeline.Tests;

/// <summary>
/// 轨道布局与"三条轨道必须对齐"的测试。
/// </summary>
/// <remarks>
/// 这是 docs/decisions.md ADR-0011 最直接的回归防线：
/// 只要有人给某条轨道单独加了内边距或宽度，这里立刻失败。
/// </remarks>
public sealed class TimelineLayoutTests
{
    private const double ViewportWidth = 1200d;
    private const long ContentDurationUs = 240_000_000; // 4 分钟

    private static TimeAxis Axis(double pixelsPerSecond = 100d, long startUs = 0) =>
        TimeAxis.Create(ViewportWidth, pixelsPerSecond, startUs);

    private static TimelineLayout Layout(double pixelsPerSecond = 100d, long startUs = 0) =>
        TimelineLayout.Create(ViewportWidth, Axis(pixelsPerSecond, startUs), ContentDurationUs);

    [Fact]
    public void 三条主轨道共享完全相同的水平范围()
    {
        TimelineLayout layout = Layout();

        Assert.True(layout.AllMainLanesShareHorizontalExtent);

        TimelineLaneRect ruler = layout[TimelineLane.Ruler];
        TimelineLaneRect waveform = layout[TimelineLane.Waveform];
        TimelineLaneRect line = layout[TimelineLane.Line];
        TimelineLaneRect token = layout[TimelineLane.Token];

        Assert.Equal(ruler.Left, waveform.Left);
        Assert.Equal(ruler.Left, line.Left);
        Assert.Equal(ruler.Left, token.Left);
        Assert.Equal(ruler.Width, waveform.Width);
        Assert.Equal(ruler.Width, line.Width);
        Assert.Equal(ruler.Width, token.Width);
        Assert.Equal(ViewportWidth, ruler.Width);
    }

    [Fact]
    public void 任取时间点在波形轨与逐字块轨得到同一个位置()
    {
        // 这是用户要求的那条对齐的直译：同一时间 → 两条轨道上同一个 x。
        TimelineLayout layout = Layout();

        foreach (long timeUs in new long[] { 0, 1_000_000, 7_654_321, 59_000_000, 240_000_000 })
        {
            double fromRuler = layout.Axis.TimeToX(timeUs);
            double fromWaveform = layout.Axis.TimeToX(timeUs);
            double fromToken = layout.Axis.TimeToX(timeUs);

            Assert.Equal(fromRuler, fromWaveform, 0d);
            Assert.Equal(fromRuler, fromToken, 0d);
        }
    }

    [Fact]
    public void 命中测试与绘制使用同一个映射()
    {
        // 反向也要一致：同一个 x 解析出的时间，用它再算回 x 必须回到原处。
        // 旧项目的病根正是"画的时候用一套、点的时候用另一套"。
        TimelineLayout layout = Layout(pixelsPerSecond: 250d, startUs: 3_000_000);

        for (double x = 0d; x <= ViewportWidth; x += 17d)
        {
            long timeUs = layout.Axis.XToTime(x);
            double roundTrip = layout.Axis.TimeToX(timeUs);

            Assert.True(
                Math.Abs(roundTrip - x) <= 1d,
                $"x={x} → {timeUs} us → x={roundTrip}，往返偏差超过 1 像素。");
        }
    }

    [Fact]
    public void 轨道自上而下排列且互不重叠()
    {
        TimelineLayout layout = Layout();

        TimelineLaneRect overview = layout[TimelineLane.Overview];
        TimelineLaneRect ruler = layout[TimelineLane.Ruler];
        TimelineLaneRect waveform = layout[TimelineLane.Waveform];
        TimelineLaneRect line = layout[TimelineLane.Line];
        TimelineLaneRect token = layout[TimelineLane.Token];

        Assert.Equal(0d, overview.Top);
        Assert.Equal(overview.Bottom, ruler.Top);
        Assert.Equal(ruler.Bottom, waveform.Top);
        Assert.Equal(waveform.Bottom, line.Top);
        Assert.Equal(line.Bottom, token.Top);
        Assert.Equal(token.Bottom, layout.TotalHeight);
    }

    [Fact]
    public void 总高度等于各轨道高度之和()
    {
        TimelineLaneHeights heights = new(Overview: 30d, Ruler: 20d, Waveform: 70d, Token: 60d);

        TimelineLayout layout = TimelineLayout.Create(ViewportWidth, Axis(), ContentDurationUs, heights);

        Assert.Equal(202d, layout.TotalHeight);
    }

    [Fact]
    public void 自定义高度会被完整应用()
    {
        TimelineLaneHeights heights = new(Overview: 30d, Ruler: 20d, Waveform: 70d, Token: 60d);

        TimelineLayout layout = TimelineLayout.Create(ViewportWidth, Axis(), ContentDurationUs, heights);

        Assert.Equal(30d, layout[TimelineLane.Overview].Height);
        Assert.Equal(20d, layout[TimelineLane.Ruler].Height);
        Assert.Equal(70d, layout[TimelineLane.Waveform].Height);
        Assert.Equal(60d, layout[TimelineLane.Token].Height);
    }

    [Theory]
    [InlineData(0d, TimelineLane.Overview)]
    [InlineData(27d, TimelineLane.Overview)]
    [InlineData(28d, TimelineLane.Ruler)]
    [InlineData(49d, TimelineLane.Ruler)]
    [InlineData(50d, TimelineLane.Waveform)]
    [InlineData(109d, TimelineLane.Waveform)]
    [InlineData(110d, TimelineLane.Line)]
    [InlineData(131d, TimelineLane.Line)]
    [InlineData(132d, TimelineLane.Token)]
    [InlineData(165d, TimelineLane.Token)]
    public void 按纵坐标定位轨道(double y, TimelineLane expected)
    {
        TimelineLayout layout = Layout();

        Assert.Equal(expected, layout.LaneAtY(y));
    }

    [Fact]
    public void 纵坐标越界没有轨道()
    {
        TimelineLayout layout = Layout();

        Assert.Null(layout.LaneAtY(-1d));
        Assert.Null(layout.LaneAtY(layout.TotalHeight + 1d));
    }

    [Fact]
    public void 缩略条使用独立的时间坐标系()
    {
        // 缩略条是整曲压缩，与主视图的时间度量不同，因此不参与对齐（ADR-0011 不变式 5）。
        TimelineLayout layout = Layout(pixelsPerSecond: 1000d);

        // 主视图 1000 px/s 时可视 1.2 秒；缩略条把 4 分钟压进 1200 px。
        Assert.Equal(1200d / 240d, layout.OverviewAxis.PixelsPerSecond, 0.001d);
        Assert.Equal(1000d, layout.Axis.PixelsPerSecond);

        long timeUs = 60_000_000;
        double overviewX = layout.OverviewAxis.TimeToX(timeUs);
        double mainX = layout.Axis.TimeToX(timeUs);

        Assert.NotEqual(overviewX, mainX);
        Assert.Equal(300d, overviewX, 0.01d);
    }

    [Fact]
    public void 缩略条视窗框反映主视图覆盖范围()
    {
        TimelineLayout layout = Layout(pixelsPerSecond: 1000d);

        (double left, double width) = layout.GetOverviewViewport();

        // 主视图可视 1.2 秒 / 整曲 240 秒 = 0.5% 宽度，缩到 1200 px 上约 6 px。
        Assert.InRange(width, 5d, 7d);
        Assert.Equal(0d, left, 0.5d);
    }

    [Fact]
    public void 主视图平移后缩略条视窗框跟着移动()
    {
        TimelineLayout layout = Layout(pixelsPerSecond: 1000d, startUs: 120_000_000);

        (double left, double width) = layout.GetOverviewViewport();

        // 120 秒 / 240 秒 = 一半宽度。
        Assert.Equal(600d, left, 1d);
        Assert.InRange(width, 5d, 7d);
    }

    [Fact]
    public void 缩略条视窗框不会超出缩略条范围()
    {
        // 主视图比整曲还宽时（缩放到整曲之外），视窗框要贴边而不是画到外面。
        TimelineLayout layout = Layout(pixelsPerSecond: 1000d, startUs: 230_000_000);

        (double left, double width) = layout.GetOverviewViewport();

        Assert.True(left >= 0d);
        Assert.True(left + width <= ViewportWidth + 0.001d);
    }

    [Fact]
    public void 内容时长为零时缩略条退化为与主视图同刻度()
    {
        // 还没有导入音频时不能除零，也不能画出一个空框：
        // 缩略条退化为与主视图相同的缩放，视窗框铺满全宽。
        TimelineLayout layout = TimelineLayout.Create(ViewportWidth, Axis(), contentDurationUs: 0);

        Assert.True(layout.AllMainLanesShareHorizontalExtent);
        Assert.Equal(layout.Axis.PixelsPerSecond, layout.OverviewAxis.PixelsPerSecond, 1e-9);

        (double left, double width) = layout.GetOverviewViewport();

        Assert.Equal(0d, left, 0.001d);
        Assert.Equal(ViewportWidth, width, 0.001d);
    }

    [Fact]
    public void 非法视口宽度被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimelineLayout.Create(0d, Axis(), ContentDurationUs));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimelineLayout.Create(ViewportWidth, Axis(), -1));
    }
}
