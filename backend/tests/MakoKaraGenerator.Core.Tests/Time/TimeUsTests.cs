using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Time;

/// <summary><see cref="TimeUs"/> 的换算与格式化边界测试（roadmap CORE-01）。</summary>
public sealed class TimeUsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1_000)]
    [InlineData(1_234, 1_234_000)]
    [InlineData(86_400_000, 86_400_000_000)]
    public void 毫秒换算为微秒(long milliseconds, long expected)
    {
        Assert.Equal(expected, TimeUs.FromMilliseconds(milliseconds));
    }

    [Fact]
    public void 负毫秒被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.FromMilliseconds(-1));
    }

    [Fact]
    public void 毫秒溢出被检测()
    {
        Assert.Throws<OverflowException>(() => TimeUs.FromMilliseconds(long.MaxValue));
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(0.5d, 500_000)]
    [InlineData(1d, 1_000_000)]
    [InlineData(0.0000005d, 1)]
    [InlineData(0.0000004d, 0)]
    public void 秒换算为微秒并四舍五入(double seconds, long expected)
    {
        Assert.Equal(expected, TimeUs.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 非有限秒数被拒绝(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.FromSeconds(seconds));
    }

    [Fact]
    public void 超出范围的秒数被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.FromSeconds(1e15));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.FromSeconds(-1e15));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 40_000)]
    [InlineData(25, 1_000_000)]
    [InlineData(1_500, 60_000_000)]
    public void 整数帧率下的帧号换算(long frameIndex, long expectedTimeUs)
    {
        Assert.Equal(expectedTimeUs, TimeUs.FromFrames(frameIndex, FrameRate.Fps25));
    }

    [Fact]
    public void NTSC_帧率下_30000_帧正好是一千零一秒()
    {
        // 30000 帧 / (30000/1001 fps) = 1001 秒，用有理数换算必须精确命中，不能靠浮点凑。
        Assert.Equal(1_001_000_000, TimeUs.FromFrames(30000, FrameRate.NtscVideo));
    }

    [Fact]
    public void NTSC_帧率下单帧时长按四舍五入取值()
    {
        // 一帧 = 1001/30000 秒 = 33366.666... 微秒 -> 33367
        Assert.Equal(33_367, TimeUs.FromFrames(1, FrameRate.NtscVideo));
    }

    [Fact]
    public void 负帧号被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.FromFrames(-1, FrameRate.Fps30));
    }

    [Fact]
    public void 帧号换算溢出被检测()
    {
        Assert.Throws<OverflowException>(() => TimeUs.FromFrames(long.MaxValue, FrameRate.Fps60));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19_999, 0)]
    [InlineData(20_000, 1)]
    [InlineData(1_000_000, 25)]
    [InlineData(1_000_001, 25)]
    public void 时间换算为最近帧号(long timeUs, long expectedFrame)
    {
        Assert.Equal(expectedFrame, TimeUs.ToFrames(timeUs, FrameRate.Fps25));
    }

    [Fact]
    public void 帧号与时间可以往返()
    {
        foreach (long frameIndex in new long[] { 0, 1, 2, 24, 25, 30000, 123_456 })
        {
            long timeUs = TimeUs.FromFrames(frameIndex, FrameRate.NtscVideo);

            Assert.Equal(frameIndex, TimeUs.ToFrames(timeUs, FrameRate.NtscVideo));
        }
    }

    [Fact]
    public void 负时间不能换算为帧号()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.ToFrames(-1, FrameRate.Fps25));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(999, 0)]
    [InlineData(1_999, 1)]
    [InlineData(1_000_000, 1_000)]
    public void 毫秒向下取整(long timeUs, long expected)
    {
        Assert.Equal(expected, TimeUs.ToMillisecondsFloor(timeUs));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_499, 1)]
    [InlineData(1_500, 2)]
    [InlineData(1_999_500, 2_000)]
    public void 毫秒四舍五入(long timeUs, long expected)
    {
        Assert.Equal(expected, TimeUs.ToMillisecondsRounded(timeUs));
    }

    [Fact]
    public void 负时间不能换算为毫秒()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.ToMillisecondsFloor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.ToMillisecondsRounded(-1));
    }

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(1_000, "00:00:00.001")]
    [InlineData(3_661_001_000, "01:01:01.001")]
    [InlineData(59_999_600, "00:01:00.000")]
    [InlineData(3_600_000_000, "01:00:00.000")]
    public void 完整时间码格式化(long timeUs, string expected)
    {
        Assert.Equal(expected, TimeUs.FormatClock(timeUs));
    }

    [Fact]
    public void 完整时间码支持负值()
    {
        Assert.Equal("-00:00:01.500", TimeUs.FormatClock(-1_500_000));
    }

    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(63_500_000, "01:03.500")]
    [InlineData(6_120_000_000, "102:00.000")]
    public void 紧凑时间码不把分钟截断到六十(long timeUs, string expected)
    {
        Assert.Equal(expected, TimeUs.FormatClockShort(timeUs));
    }

    [Theory]
    [InlineData(0, "00:00.00")]
    [InlineData(1_200_000, "00:01.20")]
    [InlineData(1_204_000, "00:01.20")]
    [InlineData(1_205_000, "00:01.21")]
    public void LRC_时间戳按厘秒四舍五入(long timeUs, string expected)
    {
        Assert.Equal(expected, TimeUs.FormatLrcTimestamp(timeUs));
    }

    [Fact]
    public void LRC_时间戳在进位时不会产生错误分解()
    {
        // 119.998 秒 -> 11999.8 厘秒 -> 进位到 12000 厘秒 = 2 分 00.00 秒
        Assert.Equal("02:00.00", TimeUs.FormatLrcTimestamp(119_998_000));

        // 59.999 秒 -> 6000 厘秒，不能分解成 00:60.00
        Assert.Equal("01:00.00", TimeUs.FormatLrcTimestamp(59_999_000));
    }

    [Fact]
    public void LRC_时间戳拒绝负值()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUs.FormatLrcTimestamp(-1));
    }

    [Fact]
    public void 秒回换算保持精度()
    {
        Assert.Equal(0.5d, TimeUs.ToSeconds(500_000), 1e-12);
        Assert.Equal(1.5d, TimeUs.ToSeconds(1_500_000), 1e-12);
    }

    [Theory]
    [InlineData("01:02.345", 62_345_000L)]
    [InlineData("00:00.000", 0L)]
    [InlineData("102:03.500", 6_123_500_000L)]
    [InlineData("00:12.5", 12_500_000L)]
    [InlineData("00:12.05", 12_050_000L)]
    [InlineData("00:12.005", 12_005_000L)]
    [InlineData("01:02", 62_000_000L)]
    [InlineData("12.5", 12_500_000L)]
    [InlineData("75", 75_000_000L)]
    [InlineData(".5", 500_000L)]
    [InlineData("  00:01.000  ", 1_000_000L)]
    public void 解析时间码文本(string text, long expected)
    {
        Assert.True(TimeUs.TryParseClock(text, out long actual), $"应能解析 '{text}'");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-00:01.000")]   // 负时间：时间轴以音频零点为原点
    [InlineData("00:99.000")]     // 冒号后的秒必须 < 60
    [InlineData("01:02:03")]      // 不接受 hh:mm:ss，避免误读成 62 分 3 秒
    [InlineData("00:12.3456")]    // 小数超过三位
    [InlineData("00:12.")]        // 有小数点但没有小数位
    [InlineData("00:1a")]
    [InlineData("abc")]
    [InlineData(":12")]          // 缺少分钟
    [InlineData("00:")]          // 缺少秒
    [InlineData("1..2")]
    [InlineData("+5")]
    public void 无法识别的时间码一律拒绝(string? text)
    {
        Assert.False(TimeUs.TryParseClock(text, out long actual));
        Assert.Equal(0L, actual);
    }

    [Fact]
    public void 解析与格式化互为逆运算_毫秒对齐时()
    {
        long[] samples =
        [
            0L, 1_000L, 999_000L, 60_000_000L, 3_599_999_000L, 6_123_500_000L,
        ];

        foreach (long sample in samples)
        {
            string formatted = TimeUs.FormatClockShort(sample);
            Assert.True(TimeUs.TryParseClock(formatted, out long parsed), $"应能解析自己格式化出的 '{formatted}'");
            Assert.Equal(sample, parsed);
        }
    }

    [Fact]
    public void 时间码文本只有毫秒精度()
    {
        // 界面显示与输入都只到毫秒，因此非毫秒对齐的值会先被四舍五入。
        // 这是刻意的：让"输入的值"和"看到的值"一致（否则用户改一位就会丢精度）。
        string formatted = TimeUs.FormatClockShort(999_999);

        Assert.Equal("00:01.000", formatted);
        Assert.True(TimeUs.TryParseClock(formatted, out long parsed));
        Assert.Equal(1_000_000L, parsed);
    }
}
