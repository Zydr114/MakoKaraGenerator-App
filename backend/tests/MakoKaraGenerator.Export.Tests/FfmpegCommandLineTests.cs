using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// 命令行参数拼装的测试（docs/roadmap.md §10 的 <c>EXPORT-02</c>/<c>03</c>/<c>05</c>/<c>06</c>）。
/// </summary>
/// <remarks>
/// 这一组不需要真的跑编码器：参数拼错的代价是"导出的片子不对"，
/// 而它的期望值就是一串确定的字符串，逐项断言比事后看视频可靠得多。
/// </remarks>
public sealed class FfmpegCommandLineTests
{
    [Fact]
    public void 裸帧输入使用_bgra_与固定帧率()
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(
            CreateRequest(quality: ExportQuality.Standard));

        Assert.Contains("rawvideo", arguments);
        Assert.Contains(FfmpegCommandLine.RawPixelFormat, arguments);
        Assert.Contains("1280x720", arguments);
        Assert.Contains("pipe:0", arguments);

        // -r 必须出现在 -i pipe:0 之前：它是输入侧的帧率。
        int rateIndex = arguments.ToList().IndexOf("-r");
        int inputIndex = arguments.ToList().IndexOf("pipe:0");

        Assert.True(rateIndex > 0 && rateIndex < inputIndex, "-r 必须在输入之前。");
        Assert.Equal("30", arguments[rateIndex + 1]);
    }

    [Fact]
    public void 分数帧率按有理数写出()
    {
        VideoEncodingRequest request = CreateRequest() with { FrameRate = FrameRate.NtscVideo };

        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(request);

        Assert.Contains("30000/1001", arguments);
    }

    [Fact]
    public void 不透明_MP4_使用_x264_与_yuv420p()
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(CreateRequest());

        Assert.Contains("libx264", arguments);
        Assert.Contains("yuv420p", arguments);
        Assert.Contains("+faststart", arguments);
        Assert.DoesNotContain("prores_ks", arguments);
    }

    [Theory]
    [InlineData(ExportQuality.Draft, "26", "fast")]
    [InlineData(ExportQuality.Standard, "20", "medium")]
    [InlineData(ExportQuality.High, "16", "slow")]
    public void 三档质量只改_crf_与_preset(ExportQuality quality, string crf, string preset)
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(
            CreateRequest(quality: quality));

        List<string> list = [.. arguments];

        Assert.Equal(crf, list[list.IndexOf("-crf") + 1]);
        Assert.Equal(preset, list[list.IndexOf("-preset") + 1]);

        // 分辨率与帧率不随质量档位变——它们属于工程设置。
        Assert.Contains("1280x720", arguments);
    }

    [Fact]
    public void 可复制的音频编码直接复制()
    {
        VideoEncodingRequest request = CreateRequest() with
        {
            AudioPath = "song.mp3",
            AudioPlan = AudioEncodingPlan.Decide("mp3"),
        };

        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(request);
        List<string> list = [.. arguments];

        Assert.Equal("copy", list[list.IndexOf("-c:a") + 1]);
        Assert.Contains("-shortest", arguments);
        Assert.Contains("-map", arguments);
    }

    [Fact]
    public void 不可复制的音频编码重编码为_aac()
    {
        VideoEncodingRequest request = CreateRequest() with
        {
            AudioPath = "song.flac",
            AudioPlan = AudioEncodingPlan.Decide("flac"),
        };

        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(request);
        List<string> list = [.. arguments];

        Assert.Equal(AudioEncodingPlan.FallbackCodecName, list[list.IndexOf("-c:a") + 1]);
        Assert.Equal(FfmpegCommandLine.AudioBitrate, list[list.IndexOf("-b:a") + 1]);
    }

    [Fact]
    public void 没有音频时不出现音频参数()
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(CreateRequest());

        Assert.DoesNotContain("-c:a", arguments);
        Assert.DoesNotContain("-shortest", arguments);
    }

    [Fact]
    public void 进度写在标准输出且覆盖受开关控制()
    {
        List<string> overwrite = [.. FfmpegCommandLine.BuildVideoEncoding(CreateRequest(overwrite: true))];
        List<string> keep = [.. FfmpegCommandLine.BuildVideoEncoding(CreateRequest(overwrite: false))];

        Assert.Equal("pipe:1", overwrite[overwrite.IndexOf("-progress") + 1]);
        Assert.Contains("-nostats", overwrite);
        Assert.Contains("-y", overwrite);
        Assert.Contains("-n", keep);
    }

    [Fact]
    public void 输出路径是最后一个参数()
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(CreateRequest());

        Assert.Equal("out.mp4", arguments[^1]);
    }

    [Fact]
    public void 参数里不出现_nostdin_之外的交互开关()
    {
        // 标准输入已经被帧数据占用，ffmpeg 不能再去读它。
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(CreateRequest());

        Assert.Contains("-nostdin", arguments);
    }

    [Fact]
    public void 输出字节上限将_fs_设为上限加64KiB作为兜底()
    {
        const long maxBytes = 128 * 1024;
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(
            CreateRequest() with { MaximumOutputBytes = maxBytes });

        List<string> list = [.. arguments];
        int index = list.IndexOf("-fs");
        Assert.True(index >= 0);
        Assert.Equal((maxBytes + 64L * 1024L).ToString(System.Globalization.CultureInfo.InvariantCulture), list[index + 1]);
    }

    private static VideoEncodingRequest CreateRequest(
        ExportQuality quality = ExportQuality.Standard,
        bool overwrite = true) => new()
        {
            OutputPath = "out.mp4",
            Width = 1280,
            Height = 720,
            FrameRate = FrameRate.Fps30,
            TotalFrames = 90,
            Quality = ExportQualityPreset.For(quality),
            Overwrite = overwrite,
        };
}
