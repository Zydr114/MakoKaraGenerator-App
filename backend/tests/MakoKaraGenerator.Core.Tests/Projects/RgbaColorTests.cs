using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary><see cref="RgbaColor"/> 的解析与无损往返测试。</summary>
public sealed class RgbaColorTests
{
    [Fact]
    public void 六位十六进制默认完全不透明()
    {
        RgbaColor color = RgbaColor.Parse("#1A2B3C");

        Assert.Equal(0x1A, color.Red);
        Assert.Equal(0x2B, color.Green);
        Assert.Equal(0x3C, color.Blue);
        Assert.Equal(255, color.Alpha);
    }

    [Fact]
    public void 井号可以省略()
    {
        Assert.Equal(RgbaColor.Parse("#1A2B3C"), RgbaColor.Parse("1A2B3C"));
    }

    [Fact]
    public void 八位十六进制包含透明度()
    {
        RgbaColor color = RgbaColor.Parse("#1A2B3C4D");

        Assert.Equal(0x4D, color.Alpha);
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.Equal(RgbaColor.Parse("#AABBCCDD"), RgbaColor.Parse("#aabbccdd"));
    }

    [Fact]
    public void 前后空白被忽略()
    {
        Assert.Equal(RgbaColor.Parse("#AABBCC"), RgbaColor.Parse("  #AABBCC  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GGGGGG")]
    [InlineData("#12 345")]
    [InlineData("红色")]
    public void 非法文本解析失败(string? text)
    {
        Assert.False(RgbaColor.TryParse(text, out RgbaColor color));
        Assert.Equal(default(RgbaColor), color);
    }

    [Fact]
    public void 非法文本在严格解析时抛异常()
    {
        Assert.Throws<ArgumentException>(() => RgbaColor.Parse("不是颜色"));
        Assert.Throws<ArgumentNullException>(() => RgbaColor.Parse(null!));
    }

    [Fact]
    public void 文本形式总是九字符含井号()
    {
        Assert.Equal("#1A2B3CFF", RgbaColor.Parse("#1A2B3C").ToHex());
        Assert.Equal("#1A2B3C4D", RgbaColor.Parse("#1A2B3C4D").ToHex());
    }

    [Fact]
    public void 文本往返无损()
    {
        foreach (string hex in new[] { "#000000FF", "#FFFFFF00", "#1A2B3C4D", "#FF000080" })
        {
            RgbaColor parsed = RgbaColor.Parse(hex);

            Assert.Equal(hex, parsed.ToHex());
            Assert.Equal(parsed, RgbaColor.Parse(parsed.ToHex()));
        }
    }

    [Fact]
    public void 预设颜色可用于默认值()
    {
        Assert.Equal(255, RgbaColor.Black.Alpha);
        Assert.Equal(0, RgbaColor.Transparent.Alpha);
        Assert.Equal(RgbaColor.Parse("#FFFFFF"), RgbaColor.White);
    }

    [Fact]
    public void ToString_与文本形式一致()
    {
        RgbaColor color = RgbaColor.Parse("#1A2B3C4D");

        Assert.Equal(color.ToHex(), color.ToString());
    }
}
