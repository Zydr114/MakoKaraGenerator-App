using System.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// 端到端导出测试：真的调 FFmpeg 写出文件，再用探测确认它是可播放的成片。
/// </summary>
/// <remarks>
/// <para>
/// 画面来自真实的 Skia 渲染管线（而不是假的渲染器）：导出的底线是"预览什么样、成片就什么样"
/// （docs/development.md §3.2），用一个假管线测出来的绿是假的。
/// </para>
/// <para>
/// FFmpeg / FFprobe 是既定环境（docs/development.md §22.1），所以这里直接要求它们存在：
/// 缺失时测试失败并指出原因，而不是悄悄跳过——导出正是这个功能的全部。
/// </para>
/// </remarks>
public sealed class VideoExporterTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 180;
    private const int AudioSampleRate = 48_000;
    private const double DurationSeconds = 1d;

    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task 导出带音轨的_MP4_且产物可探测()
    {
        string audioPath = CreateWav();
        Project project = CreateProject(audioPath);
        string output = Path.Combine(_directory.Path, "out.mp4");

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        FfmpegMediaProbe probe = new();
        VideoExporter exporter = new(pipeline, probe, new FfmpegMediaEncoder());

        List<ExportProgress> progress = [];
        ExportReport report = await exporter.ExportAsync(
            project,
            new VideoExportOptions { OutputPath = output, IncludeAudio = true },
            new SynchronousProgress(progress.Add));

        Assert.True(File.Exists(output), "没有写出文件。");
        Assert.True(report.IsVerified, "产物没有通过校验。");

        // 产物里必须有画面与音轨，且编码器就是约定的那两个。
        MediaFileInfo info = (await probe.ProbeFileAsync(output))!;

        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal("h264", info.Video!.CodecName);
        Assert.Contains("mp4", info.ContainerName, StringComparison.Ordinal);

        // 帧率与尺寸按工程设置写出。
        Assert.Equal(Width, info.Video.Width);
        Assert.Equal(Height, info.Video.Height);
        Assert.InRange(info.Video.FrameRate, 29.9d, 30.1d);

        // 报告的字段与探测结果一致。
        Assert.Equal("h264", report.VideoCodecName);
        Assert.Equal("aac", report.AudioCodecName);
        Assert.Equal("libx264 CRF 20 / preset medium", report.EncoderSettings);

        // 逐帧进度必须走完整条流程，并以 Completed 收尾。
        Assert.NotEmpty(progress);
        Assert.Equal(ExportStage.Completed, progress[^1].Stage);
        Assert.Equal(1d, progress[^1].Fraction, precision: 6);
        Assert.Contains(progress, static item => item.Stage == ExportStage.Rendering && item.FramesWritten > 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 音频报告与普通复制或透明PCM的实际产物一致(bool transparent, bool aacSource)
    {
        string source = CreateWav();
        if (aacSource)
        {
            string encoded = Path.Combine(_directory.Path, "source.m4a");
            ProcessStartInfo start = new("ffmpeg")
            {
                RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", source, "-c:a", "aac", "-b:a", "192k", encoded })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            source = encoded;
        }

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        FfmpegMediaProbe probe = new();
        VideoExporter exporter = new(pipeline, probe, new FfmpegMediaEncoder());
        string output = Path.Combine(_directory.Path, transparent ? "actual.mov" : "actual.mp4");
        ExportReport report = await exporter.ExportAsync(CreateProject(source), new VideoExportOptions
        {
            OutputPath = output, IncludeAudio = true,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4,
        });
        MediaFileInfo actual = (await probe.ProbeFileAsync(output))!;
        Assert.True(report.IsVerified);
        Assert.Equal(transparent ? "pcm_s16le" : "aac", actual.Audio!.CodecName);
        Assert.Equal(actual.Audio.CodecName, report.AudioCodecName);
        Assert.Equal(!transparent && aacSource, report.AudioCopied);
        if (transparent)
        {
            Assert.Contains(report.Warnings, warning => warning.Contains("透明 MOV 使用 pcm_s16le", StringComparison.Ordinal));
            Assert.DoesNotContain(report.Warnings, warning => warning.Contains("MP4", StringComparison.Ordinal)
                || warning.Contains("无损复制", StringComparison.Ordinal));
        }
        else Assert.Contains(report.Warnings, warning => warning.Contains(aacSource ? "无损复制" : "重编码为 aac", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 30, 1)]
    [InlineData(true, 30, 1)]
    [InlineData(false, 30000, 1001)]
    [InlineData(true, 30000, 1001)]
    public async Task 长音轨不能延长短画面的实际产物(bool transparent, int numerator, int denominator)
    {
        Project source = CreateProject(CreateWav());
        var project = source with { Composition = source.Composition with
            { OutputDurationUs = 500_000, FrameRate = new FrameRate(numerator, denominator) } };
        using SkiaRenderPipeline pipeline = new();
        var probe = new FfmpegMediaProbe();
        var exporter = new VideoExporter(pipeline, probe, new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new VideoExportOptions
        {
            OutputPath = Path.Combine(_directory.Path, transparent ? "short.mov" : "short.mp4"),
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4,
            IncludeAudio = true,
        });
        Assert.True(report.IsVerified, ExportReportFormatter.Format(report));
        Assert.Equal(15, report.FramesWritten);
        var actual = await probe.ProbeFileAsync(report.OutputPath);
        Assert.NotNull(actual?.Audio);
        Assert.NotNull(actual.Video);
        long expectedUs = 15L * denominator * 1_000_000 / numerator;
        Assert.InRange(Math.Abs(actual.DurationUs - expectedUs), 0, 35_000);
        Assert.Equal(transparent ? "pcm_s16le" : "aac", actual.Audio.CodecName);
    }

    [Fact]
    public async Task 进度按帧推进且总帧数由时长与帧率算出()
    {
        Project project = CreateProject(CreateWav());
        string output = Path.Combine(_directory.Path, "progress.mp4");

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        List<ExportProgress> progress = [];
        await exporter.ExportAsync(
            project,
            new VideoExportOptions { OutputPath = output },
            new SynchronousProgress(progress.Add));

        ExportProgress rendering = progress.First(static item => item.Stage == ExportStage.Rendering);

        // 1 秒 @30fps = 30 帧。
        Assert.Equal(30, rendering.TotalFrames);
        Assert.InRange(rendering.FramesWritten, 1, 30);

        // 帧数在渲染阶段单调不减。
        int previous = 0;

        foreach (ExportProgress item in progress.Where(static item => item.Stage == ExportStage.Rendering))
        {
            Assert.True(item.FramesWritten >= previous, "进度倒退了。");
            previous = item.FramesWritten;
        }
    }

    [Fact]
    public async Task 没有音频时也能导出只有画面的成片()
    {
        Project project = CreateProject(audioPath: null);
        string output = Path.Combine(_directory.Path, "silent.mp4");

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        FfmpegMediaProbe probe = new();
        VideoExporter exporter = new(pipeline, probe, new FfmpegMediaEncoder());

        ExportReport report = await exporter.ExportAsync(
            project,
            new VideoExportOptions { OutputPath = output });

        MediaFileInfo info = (await probe.ProbeFileAsync(output))!;

        Assert.True(info.HasVideo);
        Assert.False(info.HasAudio);
        Assert.Null(report.AudioCodecName);
        Assert.Contains(report.Warnings, static warning => warning.Contains("没有音频", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 工程有阻断性错误时拒绝导出()
    {
        // 输出时长缺失在导出上下文里是阻断性错误（校验器负责判定）。
        Project project = Project.Create(
            "缺时长",
            Composition.Create(Width, Height, FrameRate.Fps30),
            CreateLyrics());

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.ExportAsync(
                project,
                new VideoExportOptions { OutputPath = Path.Combine(_directory.Path, "rejected.mp4") }));

        Assert.Contains("阻断性错误", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 奇数尺寸会被明确拒绝()
    {
        Project project = CreateProject(audioPath: null) with
        {
            Composition = Composition.Create(Width + 1, Height, FrameRate.Fps30, outputDurationUs: 1_000_000),
        };

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => exporter.ExportAsync(
                project,
                new VideoExportOptions { OutputPath = Path.Combine(_directory.Path, "odd.mp4") }));

        Assert.Contains("偶数", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消导出会中断并给出取消异常()
    {
        Project project = CreateProject(CreateWav());
        string output = Path.Combine(_directory.Path, "cancelled.mp4");
        byte[] original = [6, 2, 7];
        await File.WriteAllBytesAsync(output, original);

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        using CancellationTokenSource source = new();
        int framesSeen = 0;

        // 已写多帧后取消，验证编码进行中也不会破坏原目标。
        void OnProgress(ExportProgress item)
        {
            if (item.Stage == ExportStage.Rendering && ++framesSeen >= 3)
            {
                source.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exporter.ExportAsync(
                project,
                new VideoExportOptions { OutputPath = output },
                new SynchronousProgress(OnProgress),
                source.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal(2, Directory.GetFiles(_directory.Path).Length); // 只有原目标与音频，无临时编码产物。
    }

    [Fact]
    public async Task 产物已存在且不允许覆盖时拒绝()
    {
        string output = Path.Combine(_directory.Path, "exists.mp4");
        await File.WriteAllTextAsync(output, "占位");

        Project project = CreateProject(audioPath: null);

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => exporter.ExportAsync(
                project,
                new VideoExportOptions { OutputPath = output, Overwrite = false }));

        Assert.Contains("已存在", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>写一段测试音频（1 秒正弦）。</summary>
    private string CreateWav()
    {
        string path = Path.Combine(_directory.Path, "audio.wav");
        float[] samples = WavWriter.BuildSegmentedTone(AudioSampleRate, (DurationSeconds, 0.6f));

        WavWriter.Write(path, samples, AudioSampleRate);

        return path;
    }

    /// <summary>造一个可导出的最小工程。</summary>
    private static Project CreateProject(string? audioPath)
    {
        long durationUs = (long)(DurationSeconds * 1_000_000d);

        return Project.Create(
            "导出测试",
            Composition.Create(Width, Height, FrameRate.Fps30, outputDurationUs: durationUs),
            CreateLyrics(),
            audioPath: audioPath);
    }

    private static LyricsDocument CreateLyrics()
    {
        long durationUs = (long)(DurationSeconds * 1_000_000d);

        return LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜",
                new TimeRange(0, durationUs),
                TimingStatus.Edited,
                [
                    LyricToken.Create(
                        "t1",
                        "夜",
                        new TimeRange(0, durationUs),
                        TimingStatus.Edited),
                ]),
        ]);
    }

    /// <summary>把进度回调变成顺序执行的，便于在测试里直接检查列表。</summary>
    private sealed class SynchronousProgress(Action<ExportProgress> onReport) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => onReport(value);
    }
}
