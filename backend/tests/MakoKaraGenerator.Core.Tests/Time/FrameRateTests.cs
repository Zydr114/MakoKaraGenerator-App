using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Time;

/// <summary><see cref="FrameRate"/> 的构造、约分与取值测试。</summary>
public sealed class FrameRateTests
{
    [Theory]
    [InlineData(25, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 4)]
    public void 等值的帧率在约分后相等(int numerator, int denominator)
    {
        FrameRate actual = new(numerator, denominator);

        Assert.Equal(25, actual.Numerator);
        Assert.Equal(1, actual.Denominator);
        Assert.True(actual.IsInteger);
    }

    [Fact]
    public void 约分使用最大公约数()
    {
        FrameRate actual = new(60000, 2002);

        Assert.Equal(30000, actual.Numerator);
        Assert.Equal(1001, actual.Denominator);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非正分子被拒绝(int numerator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(numerator, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void 非正分母被拒绝(int denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(30, denominator));
    }

    [Fact]
    public void NTSC_帧率使用精确有理数而不是浮点近似()
    {
        Assert.Equal(24000, FrameRate.NtscFilm.Numerator);
        Assert.Equal(1001, FrameRate.NtscFilm.Denominator);

        Assert.Equal(30000, FrameRate.NtscVideo.Numerator);
        Assert.Equal(1001, FrameRate.NtscVideo.Denominator);

        Assert.Equal(60000, FrameRate.NtscDouble.Numerator);
        Assert.Equal(1001, FrameRate.NtscDouble.Denominator);
    }

    [Fact]
    public void Value_仅用于显示()
    {
        Assert.Equal(29.97002997002997, FrameRate.NtscVideo.Value, 1e-12);
        Assert.Equal(30d, FrameRate.Fps30.Value, 1e-12);
    }

    [Fact]
    public void 整数帧率的文本形式不带分母()
    {
        Assert.Equal("30", FrameRate.Fps30.ToString());
        Assert.Equal("25", FrameRate.Fps25.ToString());
    }

    [Fact]
    public void 非整数帧率的文本形式带分子分母()
    {
        Assert.Equal("30000/1001", FrameRate.NtscVideo.ToString());
    }

    [Fact]
    public void 预设帧率可以直接比较()
    {
        Assert.Equal(new FrameRate(60, 1), FrameRate.Fps60);
        Assert.Equal(new FrameRate(30, 1), FrameRate.Fps30);
        Assert.Equal(new FrameRate(24, 1), FrameRate.Fps24);
        Assert.Equal(new FrameRate(50, 1), FrameRate.Fps50);
    }
}
