using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests;

public sealed class CompositionFramePlanTests
{
    [Theory]
    [InlineData(1L, 30, 1, 1)]
    [InlineData(2_500_000L, 1, 1, 3)]
    [InlineData(1_000_000L, 24, 1, 24)]
    [InlineData(10_000_000L, 60, 1, 600)]
    [InlineData(1_000_000L, 30000, 1001, 30)]
    public void 正式帧计划保原四舍五入规则和整数时间(long duration, int numerator, int denominator, int frames)
    {
        var plan = CompositionFramePlan.Create(duration, new(numerator, denominator));
        Assert.Equal(frames, plan.TotalFrames);
        Assert.Equal((long)frames * 1_000_000 * denominator / numerator, plan.VideoDurationUs);
        Assert.Equal((long)(frames - 1) * 1_000_000 * denominator / numerator, plan.FrameTimeUs(frames - 1));
        Assert.Equal(0, plan.FrameTimeUs(0));
    }
    [Fact]
    public void 非法和不可表示计划明确拒绝且分母乘积用宽整数保真()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionFramePlan.Create(0, FrameRate.Fps30));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionFramePlan.Create(long.MaxValue, FrameRate.Fps30));
        var plan = CompositionFramePlan.Create(1, new(1, int.MaxValue));
        Assert.Equal(2_147_483_647_000_000L, plan.VideoDurationUs);
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.FrameTimeUs(1));
    }
}
