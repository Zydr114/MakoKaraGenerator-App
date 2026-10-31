using MakoKaraGenerator.Export;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// FFmpeg 可用性探测的测试（ADR-0031：不自带 FFmpeg，改为探测 + 可读提示）。
/// </summary>
/// <remarks>FFmpeg 是既定环境，因此这里直接要求它可用：探测本身就是要回答这个问题。</remarks>
public sealed class EncoderAvailabilityTests
{
    [Fact]
    public async Task 本机_FFmpeg_可用且带_H264_编码器()
    {
        EncoderAvailability availability = await EncoderAvailability.ProbeAsync();

        Assert.True(availability.IsAvailable, availability.Problem);
        Assert.True(availability.HasH264Encoder, availability.Problem);
        Assert.True(availability.CanExport);
        Assert.NotNull(availability.Version);
        Assert.Contains("ffmpeg", availability.Version, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(availability.Describe()));
    }

    [Fact]
    public async Task 找不到可执行文件时给出可读提示()
    {
        EncoderAvailability availability = await EncoderAvailability.ProbeAsync("definitely-not-a-real-binary");

        Assert.False(availability.IsAvailable);
        Assert.False(availability.CanExport);
        Assert.NotNull(availability.Problem);
        Assert.Contains("安装", availability.Problem, StringComparison.Ordinal);
    }
}
