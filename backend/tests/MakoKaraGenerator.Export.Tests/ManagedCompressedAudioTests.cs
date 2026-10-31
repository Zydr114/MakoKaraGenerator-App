using System.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Export.Tests;

public sealed class ManagedCompressedAudioTests
{
    [Theory]
    [InlineData("mp3", false)]
    [InlineData("mp3", true)]
    [InlineData("flac", false)]
    [InlineData("flac", true)]
    public async Task 受信压缩覆盖真实request和原报告并保留有理尾帧与完整非静音音轨(string inputFormat, bool transparent)
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        string audio = await CreateAudioAsync(directory, inputFormat, deadline.Token);
        FrameRate rate = new(30_000, 1001);
        using SkiaRenderPipeline pipeline = new();
        FfmpegMediaProbe probe = new();
        CapturingEncoder encoder = new(inputFormat);
        AudioEncodingPlan selected = new(false, "aac", "固定压缩音频重编码为 aac。");
        ExportReport report = await new VideoExporter(pipeline, probe, encoder).ExportAsync(ProjectFor(audio, rate), new()
        {
            OutputPath = directory.FilePath(transparent ? "candidate.mov" : "candidate.mp4"),
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4,
            AudioEncodingPlanOverride = selected,
        }, cancellationToken: deadline.Token);
        Assert.NotNull(encoder.Request); Assert.Same(selected, encoder.Request.AudioPlan);
        Assert.False(encoder.Request.AudioPlan!.Copy); Assert.Equal("aac", encoder.Request.AudioPlan.CodecName);
        Assert.False(report.AudioCopied); Assert.Equal(transparent ? "pcm_s16le" : "aac", report.AudioCodecName);
        Assert.True(report.IsVerified); Assert.Equal(60, report.FramesWritten);
        Assert.Equal(2_002_000, report.VideoDurationUs);
        Assert.Contains(report.Warnings, warning => warning.Contains("音轨按计划视频时长裁切", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("-shortest", StringComparison.Ordinal));
        if (!transparent) Assert.Contains(selected.Reason, report.Warnings);
        MediaFileInfo? actual = await probe.ProbeFileAsync(report.OutputPath, true, "mov", deadline.Token);
        Assert.NotNull(actual); Assert.NotNull(actual.Video); Assert.NotNull(actual.Audio);
        Assert.Equal(60L, actual.Video.DecodedFrameCount); Assert.Equal(30_000, actual.Video.FrameRateNumerator);
        Assert.Equal(1001, actual.Video.FrameRateDenominator); Assert.Equal(transparent, actual.Video.HasAlpha);
        Assert.Equal(transparent ? "pcm_s16le" : "aac", actual.Audio.CodecName);
        Assert.InRange(Math.Abs(actual.Audio.DurationUs - 2_002_000), 0, 50_000);
        double energy = 0;
        long decoded = await new FfmpegPcmReader().ReadMonoSamplesAsync(report.OutputPath, samples =>
        {
            foreach (float sample in samples.Span) energy += sample * sample;
            return ValueTask.CompletedTask;
        }, deadline.Token);
        Assert.True(decoded > 80_000); Assert.InRange(Math.Sqrt(energy / decoded), .3, .5);
    }

    [Fact]
    public async Task 默认MP3复制和原时长差文案保持而覆盖不得污染后续默认导出()
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        string audio = await CreateAudioAsync(directory, "mp3", deadline.Token);
        using SkiaRenderPipeline pipeline = new();
        FfmpegMediaProbe probe = new();
        CapturingEncoder encoder = new(null);
        VideoExporter exporter = new(pipeline, probe, encoder);
        ExportReport overridden = await exporter.ExportAsync(ProjectFor(audio, FrameRate.Fps30), new()
        {
            OutputPath = directory.FilePath("overridden.mp4"), AudioEncodingPlanOverride = new(false, "aac", "explicit AAC"),
        }, cancellationToken: deadline.Token);
        Assert.False(overridden.AudioCopied); Assert.Equal("aac", overridden.AudioCodecName);
        Assert.DoesNotContain(overridden.Warnings, warning => warning.Contains("-shortest", StringComparison.Ordinal));
        ExportReport original = await exporter.ExportAsync(ProjectFor(audio, FrameRate.Fps30), new()
        {
            OutputPath = directory.FilePath("original.mp4"),
        }, cancellationToken: deadline.Token);
        Assert.True(encoder.Request!.AudioPlan!.Copy); Assert.Equal("mp3", encoder.Request.AudioPlan.CodecName);
        Assert.True(original.AudioCopied); Assert.Equal("mp3", original.AudioCodecName);
        Assert.Contains(original.Warnings, warning => warning.Contains("-shortest", StringComparison.Ordinal));
        Assert.DoesNotContain(original.Warnings, warning => warning.Contains("音轨按计划视频时长裁切", StringComparison.Ordinal));
        Assert.Equal("mp3", (await probe.ProbeFileAsync(original.OutputPath, deadline.Token))!.Audio!.CodecName);
    }

    private static Project ProjectFor(string audio, FrameRate rate) => Project.Create("managed audio policy",
        Composition.Create(128, 72, rate, outputDurationUs: 2_000_000),
        LyricsDocument.Create([LyricLine.Create("line", "A", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "A", new(0, 2_000_000), TimingStatus.Edited)])]), audioPath: audio);

    private static async Task<string> CreateAudioAsync(TemporaryDirectory directory, string format, CancellationToken token)
    {
        const int rate = 44_100;
        string source = directory.FilePath("source.wav");
        WavWriter.Write(source, WavWriter.BuildSegmentedTone(rate, (3, .6f)), rate);
        string encoded = directory.FilePath("owned.input");
        ProcessStartInfo start = new(FfmpegPcmReader.DefaultExecutableName)
        { RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "-nostdin", "-v", "error", "-i", source, "-c:a",
            format == "mp3" ? "libmp3lame" : "flac", "-f", format, encoded }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await errors); return encoded;
    }

    private sealed class CapturingEncoder(string? inputFormat) : IMediaEncoder
    {
        public VideoEncodingRequest? Request { get; private set; }
        public Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return new FfmpegMediaEncoder().StartAsync(request with { AudioInputFormat = inputFormat }, cancellationToken);
        }
    }
}
