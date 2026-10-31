using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Export.Tests;

public sealed class ExportNeutralFailureTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task 单帧或最后帧的Worker中立失败拒绝提交并保留旧片与实际诊断(int frames, bool transparent)
    {
        using TemporaryDirectory directory = new();
        ExportVideoFormat format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4;
        string output = directory.FilePath("existing" + format.DefaultExtension());
        byte[] original = [3, 1, 4, 1, 5];
        await File.WriteAllBytesAsync(output, original);
        RuntimeDiagnosticSnapshot actual = RuntimeDiagnosticSnapshot.FromArray(
            [new(new("worker-instance"), "actual-artifact", "render", 123,
                new(DiagnosticSeverity.Error, "ActualWorkerDiagnostic", "controlled actual occurrence"))]);
        RenderSessionFailureException primary = new("WorkerFrameInvalid", "controlled typed frame failure", actual);
        ControlledRenderer renderer = new(frames, primary);
        ControlledEncoder encoder = new();
        List<ExportStage> stages = [];
        VideoExporter exporter = new(new UnusedPipeline(), new ControlledProbe(frames, transparent), encoder, renderer);

        Exception? observed = await Record.ExceptionAsync(() =>
            exporter.ExportAsync(ProjectFor(frames), new() { OutputPath = output, Format = format, IncludeAudio = false },
                new InlineProgress(progress => stages.Add(progress.Stage))));
        if (observed is null)
        {
            // Baseline reproduction: the old catch filter encoded a fallback,
            // completed and replaced the original destination before this fails.
            Assert.True(encoder.Completed);
            Assert.Equal(frames, encoder.Session!.FramesWritten);
            Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(output));
            Assert.Contains(ExportStage.Completed, stages);
        }
        RenderSessionFailureException error = Assert.IsType<RenderSessionFailureException>(observed);

        Assert.Same(primary, error);
        Assert.Equal("WorkerFrameInvalid", error.Code);
        Assert.Same(actual, error.DiagnosticSnapshot);
        Assert.Equal("ActualWorkerDiagnostic", Assert.Single(error.DiagnosticSnapshot!.ReadAll()).Diagnostic.Diagnostic.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal(frames - 1, encoder.Session!.FramesWritten);
        Assert.False(encoder.Completed);
        Assert.Equal(1, renderer.Disposed);
        Assert.Equal(1, encoder.Disposed);
        Assert.DoesNotContain(ExportStage.Committing, stages);
        Assert.DoesNotContain(ExportStage.Completed, stages);
        Assert.Equal([output], Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 原非脚本普通绘制错误仍可背景回退完成并报告(bool transparent)
    {
        using TemporaryDirectory directory = new();
        ExportVideoFormat format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4;
        string output = directory.FilePath("fallback" + format.DefaultExtension());
        ControlledRenderer renderer = new(1, new InvalidOperationException("ordinary drawing failure"));
        ControlledEncoder encoder = new();
        VideoExporter exporter = new(new UnusedPipeline(), new ControlledProbe(1, transparent), encoder, renderer);

        ExportReport report = await exporter.ExportAsync(ProjectFor(1),
            new() { OutputPath = output, Format = format, IncludeAudio = false });

        Assert.True(report.IsVerified);
        Assert.Equal(1, report.FailedFrames);
        Assert.Contains(report.Warnings, warning => warning.Contains("ordinary drawing failure", StringComparison.Ordinal));
        Assert.True(encoder.Completed);
        Assert.Equal(1, encoder.Session!.FramesWritten);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(output));
        Assert.Equal([output], Directory.GetFiles(directory.Path));
        Assert.Equal(1, renderer.Disposed);
        Assert.Equal(1, encoder.Disposed);
    }

    private static Project ProjectFor(int frames)
    {
        long duration = frames * 1_000_000L / 30;
        return Project.Create("neutral export compatibility", Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: duration),
            LyricsDocument.Create([LyricLine.Create("line", "A", new(0, duration), TimingStatus.Edited,
                [LyricToken.Create("token", "A", new(0, duration), TimingStatus.Edited)])]));
    }

    private sealed class InlineProgress(Action<ExportProgress> action) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => action(value); }
    private sealed class UnusedPipeline : IRenderPipeline
    {
        public string BackendName => "must use fixed render session";
        public RenderFrame Render(RenderRequest request) => throw new InvalidOperationException("The session factory must own rendering.");
    }
    private sealed class ControlledRenderer(int failAt, Exception failure) : IRenderFrameSessionFactory
    {
        public int Disposed { get; private set; }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRenderFrameSession>(new Session(this, failAt, failure));
        private sealed class Session(ControlledRenderer owner, int failAt, Exception failure) : IRenderFrameSession
        {
            private int _calls;
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                if (++_calls == failAt) throw failure;
                RenderTarget target = request.ResolveTarget();
                return ValueTask.FromResult(new RenderFrame(new byte[target.PixelWidth * target.PixelHeight * 4], target,
                    PixelFormat.Bgra8888Premultiplied, request.TimeUs));
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
    private sealed class ControlledProbe(int frames, bool alpha) : IMediaProbe
    {
        public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioStreamInfo?>(null);
        public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaFileInfo?>(new(alpha ? "mov" : "mp4", frames * 1_000_000L / 30,
                [new("video", alpha ? "prores" : "h264", 2, 2, 30, frames * 1_000_000L / 30)
                    { PixelFormat = alpha ? "yuva444p10le" : "yuv420p" }]));
    }
    private sealed class ControlledEncoder : IMediaEncoder
    {
        public SessionState? Session { get; private set; }
        public bool Completed { get; private set; }
        public int Disposed { get; private set; }
        public Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default)
        {
            Session = new(this, request.OutputPath + ".owned.encoding");
            return Task.FromResult<IVideoEncodingSession>(Session);
        }
        internal sealed class SessionState(ControlledEncoder owner, string temporary) : IVideoEncodingSession
        {
            public int FramesWritten { get; private set; }
            public Task WriteFrameAsync(RenderFrame frame, CancellationToken cancellationToken = default)
            { using (frame) FramesWritten++; return Task.CompletedTask; }
            public async Task<VideoEncodingResult> CompleteAsync(CancellationToken cancellationToken = default)
            {
                owner.Completed = true;
                await File.WriteAllBytesAsync(temporary, [9, 8, 7], cancellationToken);
                return new(0, FramesWritten, 0, 3, [], new(temporary));
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
