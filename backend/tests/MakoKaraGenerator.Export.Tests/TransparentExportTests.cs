using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// 透明输出（roadmap `EXPORT-07`）的端到端测试：真的导出一次，再用探测确认**实际** Alpha。
/// </summary>
/// <remarks>
/// <para>
/// 这一条是"不能只看扩展名"的兑现：验收标准是 ffprobe 报出来的像素格式确实带 Alpha，
/// 而不是"文件名叫 .mov 而且编码器返回了 0"。
/// </para>
/// <para>
/// 另一组断言针对预乘/直乘：渲染帧是预乘 BGRA，而 ffmpeg 的 <c>bgra</c> 是直乘，
/// 少了换算就会把半透明歌词二次压暗（ADR-0037）。
/// </para>
/// </remarks>
public sealed class TransparentExportTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 180;

    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task 透明输出的产物真的带_Alpha_通道()
    {
        string output = Path.Combine(_directory.Path, "lyrics.mov");

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        FfmpegMediaProbe probe = new();
        VideoExporter exporter = new(pipeline, probe, new FfmpegMediaEncoder());

        ExportReport report = await exporter.ExportAsync(
            CreateProject(),
            new VideoExportOptions
            {
                OutputPath = output,
                Format = ExportVideoFormat.TransparentProRes4444,
                IncludeAudio = false,
            });

        Assert.True(File.Exists(output), "没有写出文件。");
        Assert.Equal("prores", report.VideoCodecName);
        Assert.Equal("prores_ks / profile 4444 / yuva444p10le / vendor apl0", report.EncoderSettings);

        MediaFileInfo info = (await probe.ProbeFileAsync(output))!;

        Assert.True(info.HasVideo);
        Assert.Contains("mov", info.ContainerName, StringComparison.Ordinal);
        Assert.StartsWith("yuva", info.Video!.PixelFormat ?? string.Empty, StringComparison.Ordinal);
        Assert.True(info.Video.HasAlpha, $"产物像素格式是 {info.Video.PixelFormat}，不含 Alpha 通道。");

        // 报告里必须留下"确认过 Alpha"的痕迹，用户不必自己去翻探测结果。
        Assert.Contains(
            report.Warnings,
            static warning => warning.Contains("透明输出已确认", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 不透明导出不会声称验证了_Alpha()
    {
        string output = Path.Combine(_directory.Path, "opaque.mp4");

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        ExportReport report = await exporter.ExportAsync(
            CreateProject(),
            new VideoExportOptions { OutputPath = output, IncludeAudio = false });

        Assert.Equal("h264", report.VideoCodecName);
        Assert.DoesNotContain(
            report.Warnings,
            static warning => warning.Contains("透明输出", StringComparison.Ordinal));
    }

    [Fact]
    public void 透明档改用_ProRes_4444_与无损音频()
    {
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(new VideoEncodingRequest
        {
            OutputPath = "out.mov",
            Width = Width,
            Height = Height,
            FrameRate = FrameRate.Fps30,
            TotalFrames = 30,
            Format = ExportVideoFormat.TransparentProRes4444,
            AudioPath = "/tmp/song.mp3",
        });

        Assert.Contains("prores_ks", arguments);
        Assert.Contains("4444", arguments);
        Assert.Contains("yuva444p10le", arguments);
        Assert.Contains("pcm_s16le", arguments);

        // 不能残留不透明那条路径的参数。
        Assert.DoesNotContain("libx264", arguments);
        Assert.DoesNotContain("yuv420p", arguments);
        Assert.DoesNotContain("copy", arguments);
    }

    [Fact]
    public async Task 透明档接受奇数尺寸而不透明档拒绝()
    {
        // yuv420p 要求偶数，ProRes 4444 不需要：不能拿一种格式的限制去挡另一种。
        VideoEncodingRequest baseRequest = new()
        {
            OutputPath = "out.mov",
            Width = 321,
            Height = 181,
            FrameRate = FrameRate.Fps30,
            TotalFrames = 30,
        };

        FfmpegMediaEncoder encoder = new();

        await Assert.ThrowsAsync<ArgumentException>(() => encoder.StartAsync(baseRequest));

        Assert.NotNull(FfmpegCommandLine.BuildVideoEncoding(
            baseRequest with { Format = ExportVideoFormat.TransparentProRes4444 }));
    }

    [Fact]
    public void 反预乘把颜色还原成直乘()
    {
        // 预乘的半透明白：(128,128,128,128) 是 50% 的 (255,255,255)。
        byte[] pixels = [128, 128, 128, 128];

        AlphaFrameConverter.ToStraight(pixels, pixels);

        Assert.Equal((255, 255, 255, 128), (pixels[0], pixels[1], pixels[2], pixels[3]));
    }

    [Fact]
    public void 反预乘不碰全不透明与全透明像素()
    {
        byte[] pixels =
        [
            0x20, 0x30, 0x40, 0xFF,   // 不透明：恒等
            0x00, 0x00, 0x00, 0x00,   // 全透明：预乘的颜色必然是 0
            112, 0, 0, 128,           // 半透明：112 * 255 / 128 ≈ 223
        ];

        AlphaFrameConverter.ToStraight(pixels, pixels);

        Assert.Equal((0x20, 0x30, 0x40, 0xFF), (pixels[0], pixels[1], pixels[2], pixels[3]));
        Assert.Equal((0, 0, 0, 0), (pixels[4], pixels[5], pixels[6], pixels[7]));

        // 少了这一步的话，成片里的半透明像素会停在 112（二次压暗）。
        Assert.InRange(pixels[8], 220, 226);
        Assert.Equal(128, pixels[11]);
    }

    [Fact]
    public void 反预乘要求目标缓冲够大()
    {
        Assert.Throws<ArgumentException>(() => AlphaFrameConverter.ToStraight(new byte[8], new byte[4]));
    }

    private static Project CreateProject() => Project.Create(
        "透明",
        Composition.Create(Width, Height, FrameRate.Fps30, outputDurationUs: 500_000),
        LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜空",
                new TimeRange(0, 500_000),
                TimingStatus.Edited,
                [LyricToken.Create("t1", "夜空", new TimeRange(0, 500_000), TimingStatus.Edited)]),
        ]));
}
