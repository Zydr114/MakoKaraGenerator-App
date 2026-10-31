using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>像素格式与 Alpha 判断的测试（roadmap `EXPORT-07` 的验证依据）。</summary>
public sealed class MediaPixelFormatTests
{
    [Theory]
    [InlineData("yuva444p12le", true)]
    [InlineData("yuva444p10le", true)]
    [InlineData("yuva420p", true)]
    [InlineData("rgba", true)]
    [InlineData("bgra", true)]
    [InlineData("argb", true)]
    [InlineData("abgr", true)]
    [InlineData("gbrap", true)]
    [InlineData("pal8", true)]
    [InlineData("ya8", true)]
    [InlineData("yuv420p", false)]
    [InlineData("yuv444p10le", false)]
    [InlineData("gray", false)]
    public void 按像素格式名判断有没有_Alpha(string pixelFormat, bool expected) =>
        Assert.Equal(expected, MediaStreamInfo.HasAlphaPixelFormat(pixelFormat));

    [Fact]
    public void 拿不到像素格式时不算带_Alpha()
    {
        // "不知道"必须与"确认没有"区分开：透明输出的验收标准是**确认有**。
        MediaStreamInfo stream = new("video", "prores", 1920, 1080, 30d, 0);

        Assert.Null(stream.PixelFormat);
        Assert.False(stream.HasAlpha);

        Assert.True((stream with { PixelFormat = "yuva444p12le" }).HasAlpha);
        Assert.False((stream with { PixelFormat = "yuv420p" }).HasAlpha);
    }

    [Fact]
    public void 非视频流的像素格式判断不影响其它属性()
    {
        MediaStreamInfo audio = new("audio", "pcm_s16le", 0, 0, 0d, 0) { PixelFormat = "yuv420p" };

        Assert.True(audio.IsAudio);
        Assert.False(audio.IsVideo);
        Assert.False(audio.HasAlpha);
    }
}
