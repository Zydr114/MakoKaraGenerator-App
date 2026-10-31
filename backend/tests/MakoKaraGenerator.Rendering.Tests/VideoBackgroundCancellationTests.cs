using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VideoBackgroundCancellationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 正式渲染和真实背景捕获均把取消传到打开或逐帧读取(bool capture, bool duringOpen)
    {
        using var cancellation = new CancellationTokenSource(); using var provider = new CancellingProvider(cancellation, duringOpen);
        using var resolver = new BackgroundFrameSourceResolver(provider); using var pipeline = new SkiaRenderPipeline(backgroundResolver: resolver);
        var project = Project.Create("video cancellation", Composition.Create(16, 8, FrameRate.Fps30, RgbaColor.Black),
            background: BackgroundSource.ForVideo("/controlled/video.mp4"));
        var request = RenderRequest.At(ProjectSnapshot.FromProject(project), 123_456) with { CancellationToken = cancellation.Token };
        var error = Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (capture) { using var surface = pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, new(0, 0, 16, 8), GraphExecutionBudget.Default); }
            else { using var frame = pipeline.Render(request); }
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancellation.Token, provider.OpenToken);
        if (!duringOpen) Assert.Equal(cancellation.Token, provider.Source.ReadToken);
        // The cancelled open must not poison the resolver with a MissingVideo entry.
        if (duringOpen)
        {
            provider.Cancel = false;
            Assert.NotNull(resolver.Resolve(project.Background, CancellationToken.None));
            Assert.Equal(2, provider.Opens);
        }
    }

    private sealed class CancellingProvider(CancellationTokenSource cancellation, bool duringOpen) : IVideoFrameProvider
    {
        internal bool Cancel { get; set; } = true;
        internal int Opens { get; private set; }
        internal CancellationToken OpenToken { get; private set; }
        internal CancellingSource Source { get; } = new(cancellation);
        public IVideoFrameSource? Open(string path, out string? failureReason) => throw new InvalidOperationException("legacy open used");
        public IVideoFrameSource? Open(string path, out string? failureReason, CancellationToken cancellationToken)
        {
            Opens++; OpenToken = cancellationToken; failureReason = null;
            if (duringOpen && Cancel) cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Source;
        }
        public void Dispose() { }
    }
    private sealed class CancellingSource(CancellationTokenSource cancellation) : IVideoFrameSource
    {
        internal CancellationToken ReadToken { get; private set; }
        public long DurationUs => 1_000_000;
        public double FrameRate => 30;
        public int Width => 1;
        public int Height => 1;
        public string? LastError => null;
        public bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame) => throw new InvalidOperationException("legacy read used");
        public bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame, CancellationToken cancellationToken)
        {
            ReadToken = cancellationToken; frame = null; cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        public void Dispose() { }
    }
}
