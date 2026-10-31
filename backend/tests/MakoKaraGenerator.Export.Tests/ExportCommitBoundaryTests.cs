using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class ExportCommitBoundaryTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    public static TheoryData<ExportVideoFormat, string, bool> CancellationCases()
    {
        TheoryData<ExportVideoFormat, string, bool> cases = new();
        foreach (ExportVideoFormat format in Enum.GetValues<ExportVideoFormat>())
        foreach (string boundary in new[] { "Rendering", "Muxing", "Encoded", "Verifying", "ProbeStart", "ProbeEnd", "RenderDispose", "EncoderDispose" })
        foreach (bool existing in new[] { false, true }) cases.Add(format, boundary, existing);
        return cases;
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task 所有提交前取消保持原路径且释放自身产物(ExportVideoFormat format, string boundary, bool existing)
    {
        string output = OutputPath(format);
        using SkiaRenderPipeline pipeline = new();
        VideoExportOptions options = Options(output, format);
        byte[]? original = await PrepareOriginalAsync(pipeline, options, existing);
        using CancellationTokenSource cancellation = new();
        void At(string point) { if (boundary == point) cancellation.Cancel(); }
        TrackingEncoder encoder = new() { AfterComplete = () => At("Encoded"), AfterDispose = () => At("EncoderDispose") };
        TrackingRenderFactory render = new(pipeline, () => At("RenderDispose"));
        CallbackProbe probe = new(async (path, token) =>
        {
            Assert.NotEqual(output, path);
            Assert.True(File.Exists(path));
            AssertOriginal(output, original);
            At("ProbeStart");
            // 故意忽略取消，证明上层提交前再次检查，而不是依赖每个后端合作。
            MediaFileInfo? result = await new FfmpegMediaProbe().ProbeFileAsync(path, CancellationToken.None);
            At("ProbeEnd");
            return result;
        });
        VideoExporter exporter = new(pipeline, probe, encoder, render);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(CreateProject("B"), options,
            new ImmediateProgress(value => At(value.Stage.ToString())), cancellation.Token));

        AssertOriginal(output, original);
        AssertNoTemporaryFiles();
        Assert.Equal(1, encoder.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
    }

    [Theory]
    [InlineData(ExportVideoFormat.OpaqueMp4, false)]
    [InlineData(ExportVideoFormat.OpaqueMp4, true)]
    [InlineData(ExportVideoFormat.TransparentProRes4444, false)]
    [InlineData(ExportVideoFormat.TransparentProRes4444, true)]
    public async Task 提交及完成后的取消不改写成功且通知失败不破坏结果(ExportVideoFormat format, bool notificationFails)
    {
        string output = OutputPath(format);
        using SkiaRenderPipeline pipeline = new();
        VideoExportOptions options = Options(output, format);
        byte[] original = (await PrepareOriginalAsync(pipeline, options, true))!;
        using CancellationTokenSource cancellation = new();
        TrackingEncoder encoder = new();
        TrackingRenderFactory render = new(pipeline, () => { });
        List<ExportStage> stages = [];
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), encoder, render);
        ExportReport report = await exporter.ExportAsync(CreateProject("B"), options, new ImmediateProgress(value =>
        {
            stages.Add(value.Stage);
            if (value.Stage == ExportStage.Committing)
            {
                AssertOriginal(output, original);
                Assert.Equal(1, encoder.DisposeCount);
                Assert.Equal(1, render.DisposeCount);
                cancellation.Cancel();
            }
            if (value.Stage == ExportStage.Completed)
            {
                cancellation.Cancel();
                if (notificationFails) throw new InvalidOperationException("完成观察者失败");
            }
        }), cancellation.Token);

        Assert.True(report.IsVerified);
        Assert.Equal(output, report.OutputPath);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(output));
        Assert.Equal(ExportStage.Completed, stages[^1]);
        Assert.Equal(format.SupportsAlpha(), (await new FfmpegMediaProbe().ProbeFileAsync(output))!.Video!.HasAlpha);
        Assert.Equal(1, encoder.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(ExportVideoFormat.OpaqueMp4, "render")]
    [InlineData(ExportVideoFormat.OpaqueMp4, "encode")]
    [InlineData(ExportVideoFormat.OpaqueMp4, "render-dispose")]
    [InlineData(ExportVideoFormat.OpaqueMp4, "encoder-dispose")]
    [InlineData(ExportVideoFormat.TransparentProRes4444, "render")]
    [InlineData(ExportVideoFormat.TransparentProRes4444, "encode")]
    [InlineData(ExportVideoFormat.TransparentProRes4444, "render-dispose")]
    [InlineData(ExportVideoFormat.TransparentProRes4444, "encoder-dispose")]
    public async Task 渲染编码及资源释放失败保留旧片(ExportVideoFormat format, string failure)
    {
        string output = OutputPath(format);
        using SkiaRenderPipeline pipeline = new();
        VideoExportOptions options = Options(output, format);
        byte[] original = (await PrepareOriginalAsync(pipeline, options, true))!;
        void Fail() => throw new IOException("事务失败：" + failure);
        TrackingEncoder encoder = new()
        {
            AfterFrame = failure == "encode" ? Fail : null,
            AfterDispose = failure == "encoder-dispose" ? Fail : null,
        };
        TrackingRenderFactory render = new(pipeline, failure == "render-dispose" ? Fail : () => { })
        {
            BeforeFrame = failure == "render" ? () => throw new PreparedFrameException("test", "渲染失败") : null,
        };
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), encoder, render);

        Exception? error = await Record.ExceptionAsync(() => exporter.ExportAsync(CreateProject("B"), options));

        Assert.NotNull(error);
        AssertOriginal(output, original);
        AssertNoTemporaryFiles();
        Assert.Equal(1, encoder.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
    }

    [Theory]
    [InlineData(ExportVideoFormat.OpaqueMp4)]
    [InlineData(ExportVideoFormat.TransparentProRes4444)]
    public async Task 真实编码进程失败保留旧片并回收暂存(ExportVideoFormat format)
    {
        string output = OutputPath(format);
        using SkiaRenderPipeline pipeline = new();
        VideoExportOptions options = Options(output, format);
        byte[] original = (await PrepareOriginalAsync(pipeline, options, true))!;
        string brokenAudio = Path.Combine(_directory.Path, "broken.wav");
        await File.WriteAllTextAsync(brokenAudio, "不是音频");
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new BrokenAudioEncoder(brokenAudio));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(CreateProject("B"), options, cancellationToken: deadline.Token));

        AssertOriginal(output, original);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 实际发布时拒绝并发目标或目录且清理暂存(bool directoryTarget)
    {
        string output = OutputPath(ExportVideoFormat.OpaqueMp4);
        using SkiaRenderPipeline pipeline = new();
        byte[] concurrent = [1, 8, 9, 3];
        CallbackProbe probe = new(async (path, token) =>
        {
            MediaFileInfo? info = await new FfmpegMediaProbe().ProbeFileAsync(path, token);
            if (directoryTarget) Directory.CreateDirectory(output);
            else await File.WriteAllBytesAsync(output, concurrent, token);
            return info;
        });
        VideoExporter exporter = new(pipeline, probe, new FfmpegMediaEncoder());

        Exception? error = await Record.ExceptionAsync(() => exporter.ExportAsync(CreateProject(),
            Options(output, ExportVideoFormat.OpaqueMp4) with { Overwrite = false }));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        if (directoryTarget) Assert.True(Directory.Exists(output));
        else AssertOriginal(output, concurrent);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task 探测告警保持原严重性且报告不暴露暂存路径()
    {
        string output = OutputPath(ExportVideoFormat.OpaqueMp4);
        using SkiaRenderPipeline pipeline = new();
        CallbackProbe probe = new((path, _) => throw new IOException("无法校验：" + path));
        VideoExporter exporter = new(pipeline, probe, new FfmpegMediaEncoder());
        ExportReport report = await exporter.ExportAsync(CreateProject(), Options(output, ExportVideoFormat.OpaqueMp4));

        Assert.False(report.IsVerified);
        Assert.True(File.Exists(output));
        Assert.Contains(report.Warnings, warning => warning.Contains("无法校验：" + output, StringComparison.Ordinal));
        Assert.DoesNotContain(".encoding", ExportReportFormatter.Format(report), StringComparison.Ordinal);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task 编码完成转交所有权且会话释放不发布或清理产物()
    {
        string output = OutputPath(ExportVideoFormat.OpaqueMp4);
        byte[] original = [3, 5, 8];
        await File.WriteAllBytesAsync(output, original);
        IVideoEncodingSession session = await new FfmpegMediaEncoder().StartAsync(new VideoEncodingRequest
        {
            OutputPath = output, Width = 64, Height = 36, FrameRate = FrameRate.Fps30, TotalFrames = 1,
        });
        await using (session)
        {
            RenderTarget target = RenderTarget.FromComposition(Composition.Create(64, 36, FrameRate.Fps30));
            await session.WriteFrameAsync(new RenderFrame(new byte[64 * 36 * 4], target, PixelFormat.Bgra8888Premultiplied, 0));
            VideoEncodingResult result = await session.CompleteAsync();
            using (result.Artifact)
            {
                AssertOriginal(output, original);
                await session.DisposeAsync();
                Assert.True(File.Exists(result.Artifact.TemporaryPath));
                Assert.True((await new FfmpegMediaProbe().ProbeFileAsync(result.Artifact.TemporaryPath))!.HasVideo);
            }
        }
        AssertOriginal(output, original);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(ExportVideoFormat.OpaqueMp4)]
    [InlineData(ExportVideoFormat.TransparentProRes4444)]
    public async Task 验证开始取消保持真实旧片且清理暂存(ExportVideoFormat format)
    {
        string output = Path.Combine(_directory.Path, format.SupportsAlpha() ? "old.mov" : "old.mp4");
        using SkiaRenderPipeline pipeline = new();
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        VideoExportOptions options = new() { OutputPath = output, Format = format, IncludeAudio = false };
        await exporter.ExportAsync(CreateProject(), options);
        byte[] original = await File.ReadAllBytesAsync(output);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            CreateProject("B"), options,
            new ImmediateProgress(value =>
            {
                if (value.Stage == ExportStage.Verifying) cancellation.Cancel();
            }), cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal([output], Directory.GetFiles(_directory.Path));
    }

    private static Project CreateProject(string text = "A") => Project.Create("提交边界",
        Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 200_000),
        LyricsDocument.Create([LyricLine.Create("line", text, new TimeRange(0, 200_000),
            TimingStatus.Edited, [LyricToken.Create("token", text, new TimeRange(0, 200_000), TimingStatus.Edited)])]));

    private string OutputPath(ExportVideoFormat format) => Path.Combine(_directory.Path, "output" + format.DefaultExtension());
    private static VideoExportOptions Options(string output, ExportVideoFormat format) =>
        new() { OutputPath = output, Format = format, IncludeAudio = false };

    private static async Task<byte[]?> PrepareOriginalAsync(SkiaRenderPipeline pipeline, VideoExportOptions options, bool existing)
    {
        if (!existing) return null;
        await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(CreateProject(), options);
        return await File.ReadAllBytesAsync(options.OutputPath);
    }

    private static void AssertOriginal(string output, byte[]? original)
    {
        if (original is null) Assert.False(File.Exists(output));
        else Assert.Equal(original, File.ReadAllBytes(output));
    }

    private void AssertNoTemporaryFiles() => Assert.DoesNotContain(Directory.GetFiles(_directory.Path),
        path => Path.GetFileName(path).Contains(".encoding", StringComparison.Ordinal));

    private sealed class ImmediateProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
