using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Timeline;

namespace MakoKaraGenerator.Timeline.Tests;

/// <summary>
/// <see cref="TimeAxis"/> 的映射与缩放测试。
/// </summary>
/// <remarks>
/// 这组测试是 docs/decisions.md ADR-0011 的回归防线：
/// 标尺、波形轨、逐字块轨必须共用同一个映射，因此映射本身必须先被证明是自洽的。
/// </remarks>
public sealed class TimeAxisTests
{
    private static TimeAxis Axis(double width = 1000d, double pixelsPerSecond = 100d, long startUs = 0) =>
        TimeAxis.Create(width, pixelsPerSecond, startUs);

    [Fact]
    public void 视口左端对应零像素()
    {
        TimeAxis axis = Axis(startUs: 2_500_000);

        Assert.Equal(0d, axis.TimeToX(2_500_000), 1e-9);
    }

    [Fact]
    public void 时间与像素可以互相换算()
    {
        TimeAxis axis = Axis();

        // 100 px/s：1 秒 = 100 px。
        Assert.Equal(100d, axis.TimeToX(1_000_000), 1e-9);
        Assert.Equal(1_000_000, axis.XToTime(100d));
        Assert.Equal(2_500_000, axis.XToTime(250d));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(500_000L)]
    [InlineData(1_234_567L)]
    [InlineData(9_999_999L)]
    public void 时间换算往返不丢失(long timeUs)
    {
        TimeAxis axis = Axis();

        // 允许一个像素对应的时长误差（XToTime 会取整）。
        AssertCloseTo(timeUs, axis.XToTime(axis.TimeToX(timeUs)), axis.UsFromPixels(1d));
    }

    [Fact]
    public void 映射单调递增()
    {
        TimeAxis axis = Axis();

        double previous = double.NegativeInfinity;

        for (long timeUs = 0; timeUs <= 3_000_000; timeUs += 37_000)
        {
            double x = axis.TimeToX(timeUs);

            Assert.True(x > previous, $"{timeUs} us 处位置没有递增。");
            previous = x;
        }
    }

    [Fact]
    public void 视口右端与可视时长一致()
    {
        TimeAxis axis = Axis(width: 1000d, pixelsPerSecond: 50d);

        Assert.Equal(20_000_000, axis.VisibleDurationUs);
        Assert.Equal(20_000_000, axis.ViewEndUs);
        Assert.Equal(20_000_000, axis.XToTime(1000d));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(250d)]
    [InlineData(1000d)]
    public void 缩放时锚点处的时间保持不变(double anchorX)
    {
        // 这是滚轮缩放的体验关键：鼠标指着哪一刻，缩放后那一刻仍在鼠标下。
        // 刻意把视口左端放在远离 0 的位置：缩小视口时左端可能被钳制到 0，
        // 那属于另一条不变式（由 缩小时候左端会被钳制在零 单独覆盖）。
        // 起点取 1000 秒可以保证本用例里的所有缩放倍数都不触发钳制。
        TimeAxis axis = Axis(startUs: 1_000_000_000);
        long anchorTimeUs = axis.XToTime(anchorX);

        foreach (double factor in new[] { 1.1d, 2d, 8d, 0.9d, 0.5d, 0.1d })
        {
            TimeAxis zoomed = axis.ZoomedAt(anchorX, factor);

            AssertCloseTo(anchorTimeUs, zoomed.XToTime(anchorX), axis.UsFromPixels(1d));
        }
    }

    [Fact]
    public void 缩小时候左端会被钳制在零()
    {
        // 视口不能显示负时间，因此靠近曲首缩小视口时锚点会随之偏移——
        // 这是所有编辑器的共同行为，但必须是被测试明确记录的行为，而不是意外。
        TimeAxis axis = Axis(pixelsPerSecond: 100d, startUs: 0);

        TimeAxis zoomedOut = axis.ZoomedAt(1000d, 0.1d);

        // 缩放本身生效了（100 → 10 px/s），只是左端被钳制在 0。
        Assert.Equal(10d, zoomedOut.PixelsPerSecond, 1e-9);
        Assert.Equal(0L, zoomedOut.ViewStartUs);
    }

    [Fact]
    public void 放大缩小是可逆的()
    {
        TimeAxis axis = Axis(startUs: 30_000_000);
        TimeAxis zoomed = axis.ZoomedAt(400d, 4d);
        TimeAxis back = zoomed.ZoomedAt(400d, 0.25d);

        Assert.Equal(axis.PixelsPerSecond, back.PixelsPerSecond, 1e-6);
        AssertCloseTo(axis.ViewStartUs, back.ViewStartUs, axis.UsFromPixels(1d));
    }

    [Fact]
    public void 缩放会被钳制在允许范围内()
    {
        TimeAxis axis = Axis(pixelsPerSecond: 100d);

        TimeAxis hugelyZoomed = axis.ZoomedAt(0d, 1e9d);
        TimeAxis hugelyZoomedOut = axis.ZoomedAt(0d, 1e-9d);

        Assert.Equal(TimeAxis.MaxPixelsPerSecond, hugelyZoomed.PixelsPerSecond, 1e-9);
        Assert.Equal(TimeAxis.MinPixelsPerSecond, hugelyZoomedOut.PixelsPerSecond, 1e-9);
    }

    [Fact]
    public void 非正缩放被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeAxis.Create(1000d, 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeAxis.Create(1000d, -1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeAxis.Create(1000d, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeAxis.Create(0d, 100d));
        Assert.Throws<ArgumentOutOfRangeException>(() => Axis().ZoomedAt(0d, 0d));
    }

    [Fact]
    public void 平移量按缩放换算()
    {
        TimeAxis axis = Axis(pixelsPerSecond: 100d, startUs: 5_000_000);

        // 向右拖 100 px = 视口左移 1 秒。
        TimeAxis panned = axis.PannedBy(100d);

        Assert.Equal(4_000_000, panned.ViewStartUs);

        Assert.Equal(6_000_000, axis.PannedBy(-100d).ViewStartUs);
    }

    [Fact]
    public void 平移不会把视口推到负时间()
    {
        TimeAxis axis = Axis(startUs: 500_000);

        Assert.Equal(0L, axis.PannedBy(10_000d).ViewStartUs);
    }

    [Fact]
    public void 钳制到内容范围()
    {
        TimeAxis axis = Axis(width: 1000d, pixelsPerSecond: 100d, startUs: 0);

        // 可视 10 秒，内容 25 秒：左端最多到 15 秒。
        Assert.Equal(0L, axis.ClampedTo(25_000_000).ViewStartUs);
        Assert.Equal(
            15_000_000L,
            Axis(width: 1000d, pixelsPerSecond: 100d, startUs: 99_000_000).ClampedTo(25_000_000).ViewStartUs);
    }

    [Fact]
    public void 内容比视口短时左端归零()
    {
        TimeAxis axis = Axis(width: 1000d, pixelsPerSecond: 100d, startUs: 3_000_000);

        // 可视 10 秒 > 内容 4 秒。
        Assert.Equal(0L, axis.ClampedTo(4_000_000).ViewStartUs);
    }

    [Fact]
    public void 聚焦区间时该区间落在视口内()
    {
        TimeAxis axis = Axis(width: 800d, pixelsPerSecond: 100d);

        TimeAxis focused = axis.ShowingRange(12_000_000, 14_000_000);

        double left = focused.TimeToX(12_000_000);
        double right = focused.TimeToX(14_000_000);

        Assert.True(left > 0d, $"区间起点应当留白，实际 x={left}。");
        Assert.True(right < focused.ViewportWidthPx, $"区间终点应当留白，实际 x={right}。");

        // 2 秒区间 + 两侧各 8% 留白 = 2.32 秒占满 800 px。
        Assert.Equal(800d / 2.32d, focused.PixelsPerSecond, 0.01d);
        Assert.Equal(11_840_000L, focused.ViewStartUs);
    }

    [Fact]
    public void 聚焦非法区间被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Axis().ShowingRange(2_000_000, 2_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Axis().ShowingRange(3_000_000, 1_000_000));
    }

    [Fact]
    public void 改变视口宽度不影响缩放与左端()
    {
        TimeAxis axis = Axis(width: 1000d, pixelsPerSecond: 100d, startUs: 1_000_000);

        TimeAxis resized = axis.WithViewportWidth(640d);

        Assert.Equal(640d, resized.ViewportWidthPx);
        Assert.Equal(100d, resized.PixelsPerSecond);
        Assert.Equal(1_000_000, resized.ViewStartUs);
    }

    [Fact]
    public void 值语义相等性()
    {
        Assert.Equal(Axis(), Axis());
        Assert.NotEqual(Axis(), Axis(pixelsPerSecond: 101d));
    }

    [Fact]
    public void 毫秒吸附()
    {
        Assert.Equal(1_234_000, TimeSnapping.Snap(1_234_400, TimeSnapMode.Millisecond, FrameRate.Fps30));
        Assert.Equal(1_235_000, TimeSnapping.Snap(1_234_600, TimeSnapMode.Millisecond, FrameRate.Fps30));
        Assert.Equal(1_234_567, TimeSnapping.Snap(1_234_567, TimeSnapMode.None, FrameRate.Fps30));
    }

    [Fact]
    public void 帧吸附使用有理数帧率()
    {
        // 25 fps 下 1 帧 = 40000 us。
        Assert.Equal(1_240_000L, TimeSnapping.Snap(1_235_000, TimeSnapMode.Frame, FrameRate.Fps25));
        Assert.Equal(1_200_000L, TimeSnapping.Snap(1_210_000, TimeSnapMode.Frame, FrameRate.Fps25));

        // 29.97 fps 下不能退化成 30 fps。
        long snapped = TimeSnapping.Snap(1_000_000, TimeSnapMode.Frame, FrameRate.NtscVideo);

        Assert.Equal(1_001_000L, snapped);
    }

    /// <summary>断言两个时刻之差在容差内。长整型没有带容差的 Assert.Equal 重载。</summary>
    private static void AssertCloseTo(long expected, long actual, long tolerance) =>
        Assert.True(
            Math.Abs(actual - expected) <= tolerance,
            $"期望 {expected} ± {tolerance} 微秒，实际 {actual}。");
}
