using System.Globalization;
using CliWrap;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerProjectImageExportIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("static", "opaqueMp4", null)]
    [InlineData("static", "transparentProRes4444", null)]
    [InlineData("analysis", "opaqueMp4", null)]
    [InlineData("analysis", "transparentProRes4444", null)]
    [InlineData("package", "opaqueMp4", null)]
    [InlineData("package", "transparentProRes4444", null)]
    [InlineData("static", "opaqueMp4", "wav")]
    [InlineData("analysis", "transparentProRes4444", "wav")]
    [InlineData("static", "opaqueMp4", "mp3")]
    [InlineData("analysis", "transparentProRes4444", "flac")]
    public async Task 真实Worker三种图片基础来源复用严格导出并保完整视频及音轨(string kind, string format, string? audioFormat)
    {
        using TemporaryDirectory temp = new();
        byte[] bytes = Png();
        ProjectSnapshot snapshot = Snapshot(kind, bytes);
        BackendExportAudioGrant? grant = null;
        if (audioFormat is not null)
        {
            string path = temp.FilePath("audio." + audioFormat);
            await Cli.Wrap(TestMediaToolPaths.Ffmpeg).WithArguments(["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                "-c:a", audioFormat == "wav" ? "pcm_s16le" : audioFormat == "mp3" ? "libmp3lame" : "flac", path]).ExecuteAsync();
            byte[] audio = await File.ReadAllBytesAsync(path);
            grant = new(path, audio.Length, WorkerProtocol.ComputeSha256(audio), audioFormat == "wav" ? BackendAudioInputProfiles.Pcm16Wave
                : audioFormat == "mp3" ? BackendAudioInputProfiles.Mp3 : BackendAudioInputProfiles.Flac);
            snapshot = PackageWorkerRuntimeFixture.Fingerprint(snapshot with { AudioPath = "audio-source-declaration.wav",
                AudioMedia = new(grant.Sha256, grant.Size, "original." + audioFormat) });
        }
        string candidate = temp.FilePath(format == "opaqueMp4" ? "candidate.mp4" : "candidate.mov");
        WorkerBackendExportExecutor executor = Executor(kind == "package" ? "gpu" : null);
        BackendExportCapabilities capabilities = await executor.ProbeAsync();
        Assert.True(capabilities.ImageExportAvailable); Assert.True(executor.ImageExportAvailable);
        List<BackendJobProgressDto> progress = [];
        ExportReportDto report;
        try { report = await executor.ExecuteImageAsync(snapshot, bytes, new(format, grant is not null, candidate, grant), new Progress(progress.Add)); }
        catch (RenderSessionFailureException failure) { output.WriteLine($"{kind}/{format}/{audioFormat}: {failure.Code}; {string.Join(',', progress.Select(p => p.Stage))}"); throw; }
        Assert.Equal(3, report.DecodedFrames); Assert.Equal(3, report.FramesWritten); Assert.True(report.StrictlyVerified);
        Assert.Equal(format != "opaqueMp4", report.HasAlpha); Assert.Equal(grant is not null, report.AudioIncluded);
        Assert.Equal(new FileInfo(candidate).Length, report.MediaSize);
        Assert.Equal(WorkerProtocol.ComputeSha256(await File.ReadAllBytesAsync(candidate)), report.Sha256);
        Assert.Equal("prepared", progress[^1].Stage); Assert.Equal(3, progress[^1].CompletedFrames);
        using MemoryStream raw = new();
        await Cli.Wrap(TestMediaToolPaths.Ffmpeg).WithArguments(["-v", "error", "-i", candidate, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"])
            .WithStandardOutputPipe(PipeTarget.ToStream(raw)).ExecuteAsync();
        Assert.Equal(3 * 320 * 180 * 4, raw.Length);
        if (grant is not null)
        {
            long samples = await new FfmpegPcmReader(TestMediaToolPaths.Ffmpeg).ReadMonoSamplesAtRateAsync(candidate, 48000, _ => ValueTask.CompletedTask);
            Assert.InRange(samples, 14400 - 1024, 14400 + 1024);
        }
        output.WriteLine($"Image export {kind}/{format}/{audioFormat ?? "none"}: 3 complete BGRA frames, {report.MediaSize} bytes; strict report true.");
    }

    [Theory]
    [InlineData("corrupt", "PackageImageDecodeFailed")]
    [InlineData("dimension", "PackageImageBudgetExceeded")]
    [InlineData("decoded", "PackageImageBudgetExceeded")]
    public async Task 真实图片解码失败不产生候选且同执行器后续仍健康(string kind, string code)
    {
        using TemporaryDirectory temp = new();
        byte[] bytes = kind == "corrupt" ? [1, 2, 3] : kind == "dimension" ? Png(4097, 1) : Png(2049, 2049);
        string candidate = temp.FilePath("bad.mp4");
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor().ExecuteImageAsync(Snapshot("static", bytes), bytes,
            new("opaqueMp4", false, candidate, null)));
        Assert.Equal(code, failure.Code); Assert.False(File.Exists(candidate));
    }

    [Fact]
    public async Task 图片取消等待真实Worker排水且透明后续导出成功()
    {
        using TemporaryDirectory temp = new(); byte[] bytes = Png();
        ProjectSnapshot snapshot = Snapshot("static", bytes);
        snapshot = PackageWorkerRuntimeFixture.Fingerprint(snapshot with { Composition = snapshot.Composition with
            { OutputDurationUs = 10_000_000, CanvasWidth = 1920, CanvasHeight = 1080, FrameRate = FrameRate.Fps60 } });
        var executor = Executor(); using CancellationTokenSource cancellation = new();
        string canceled = temp.FilePath("cancel.mp4");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteImageAsync(snapshot, bytes,
            new("opaqueMp4", false, canceled, null), new Progress(p => { if (p.Stage == "rendering") cancellation.Cancel(); }), cancellation.Token));
        Assert.False(File.Exists(canceled));
        var report = await executor.ExecuteImageAsync(Snapshot("static", bytes), bytes,
            new("transparentProRes4444", false, temp.FilePath("retry.mov"), null));
        Assert.True(report.StrictlyVerified); Assert.Equal(3, report.DecodedFrames);
    }

    private static WorkerBackendExportExecutor Executor(string? renderBackend = null) => new(new WorkerProcessOptions(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = renderBackend },
        TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe, exportOptions: new BackendExportOptions { MaxMediaBytes = WorkerExportContract.MaximumMediaBytes });
    private static ProjectSnapshot Snapshot(string kind, byte[] bytes)
    {
        ProjectSnapshot snapshot = PackageWorkerRuntimeFixture.Create("scale");
        snapshot = snapshot with { Composition = snapshot.Composition with { OutputDurationUs = 300_000, FrameRate = new(10, 1) } };
        if (kind != "package") snapshot = snapshot with { Effects = ProjectEffects.Empty, AudioPath = null, AudioMedia = null, AudioAnalysis = null };
        if (kind == "analysis") snapshot = snapshot with { AudioPath = "declared.wav", AudioMedia = new(new string('a', 64), 44, "original.wav"),
            AudioAnalysis = ProjectAudioAnalysis.Failed("ActualFailure", "retained analysis") };
        MediaFileRevision media = new(WorkerProtocol.ComputeSha256(bytes), bytes.Length, "actual.png");
        return PackageWorkerRuntimeFixture.Fingerprint(snapshot with { Background = new()
        { Kind = BackgroundKind.Image, Path = media.RelativePath, Media = media, Fit = BackgroundFit.Contain } });
    }
    private static byte[] Png(int width = 7, int height = 5)
    {
        using SKBitmap bitmap = new(width, height); bitmap.Erase(new SKColor(240, 20, 120, 180)); bitmap.SetPixel(0, 0, new(0, 240, 70, 230));
        using SKImage image = SKImage.FromBitmap(bitmap); using SKData data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private sealed class Progress(Action<BackendJobProgressDto> action) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => action(value); }
}
