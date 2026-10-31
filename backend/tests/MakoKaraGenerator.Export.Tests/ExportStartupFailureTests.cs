using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export.Tests;

public sealed class ExportStartupFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 编码启动首失败或取消不会被像素会话清理覆盖(bool cancelled)
    {
        using TemporaryDirectory directory = new();
        Exception primary = cancelled ? new OperationCanceledException("primary startup cancellation")
            : new IOException("primary encoder open failure");
        CleanupFactory factory = new();
        VideoExporter exporter = new(new UnusedPipeline(), new UnusedProbe(), new FailingEncoder(primary), factory);
        Exception? observed = await Record.ExceptionAsync(() => exporter.ExportAsync(ProjectFor(),
            new() { OutputPath = directory.FilePath("output.mp4"), IncludeAudio = false }));
        Assert.Same(primary, observed);
        Assert.IsType<IOException>(primary.Data["RenderSessionCleanupFailure"]);
        Assert.Equal(1, factory.DisposeCount);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task 覆盖拒绝保持原片并保留首错误与清理信息()
    {
        using TemporaryDirectory directory = new();
        string output = directory.FilePath("old.mp4");
        byte[] original = [1, 2, 3]; await File.WriteAllBytesAsync(output, original);
        CleanupFactory factory = new();
        VideoExporter exporter = new(new UnusedPipeline(), new UnusedProbe(), new FailingEncoder(new IOException("must not start")), factory);
        IOException error = await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(ProjectFor(),
            new() { OutputPath = output, IncludeAudio = false, Overwrite = false }));
        Assert.Contains("输出文件已存在", error.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(error.Data["RenderSessionCleanupFailure"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal(1, factory.DisposeCount);
    }

    private static Project ProjectFor() => Project.Create("startup guard",
        Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 100_000),
        LyricsDocument.Create([LyricLine.Create("line", "A", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("token", "A", new(0, 100_000), TimingStatus.Edited)])]));
    private sealed class CleanupFactory : IRenderFrameSessionFactory
    {
        public int DisposeCount { get; private set; }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRenderFrameSession>(new Session(this));
        private sealed class Session(CleanupFactory owner) : IRenderFrameSession
        {
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Rendering must not start.");
            public ValueTask DisposeAsync()
            { owner.DisposeCount++; throw new IOException("secondary render cleanup"); }
        }
    }
    private sealed class FailingEncoder(Exception primary) : IMediaEncoder
    {
        public Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default) => throw primary;
    }
    private sealed class UnusedProbe : IMediaProbe
    {
        public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class UnusedPipeline : IRenderPipeline
    {
        public string BackendName => "not used";
        public RenderFrame Render(RenderRequest request) => throw new InvalidOperationException();
    }
}
