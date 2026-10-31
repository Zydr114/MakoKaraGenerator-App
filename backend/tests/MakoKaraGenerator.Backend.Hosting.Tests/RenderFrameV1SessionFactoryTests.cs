using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class RenderFrameV1SessionFactoryTests
{
    [Fact]
    public async Task 固定快照和尺寸预算在真实会话前门禁()
    {
        ProjectSnapshot snapshot = Snapshot(64, 36);
        var inner = new RecordingFactory();
        var adapter = new RenderFrameV1SessionFactory(inner, new(64, 36, 64L * 36 * 4));
        await using IRenderFrameSession session = await adapter.CreateAsync(snapshot);

        var mismatch = await Assert.ThrowsAsync<PreparedFrameException>(() =>
            session.RenderAsync(RenderRequest.At(snapshot with { }, 0)).AsTask());
        Assert.Equal("RenderFrameV1InputsMismatch", mismatch.Code);
        var tooWide = await Assert.ThrowsAsync<PreparedFrameException>(() =>
            session.RenderAsync(RenderRequest.At(snapshot, 0) with { Target = new RenderTarget { PixelWidth = 65, PixelHeight = 36 } }).AsTask());
        Assert.Equal("RenderFrameV1SizeExceeded", tooWide.Code);
        Assert.Equal(0, inner.RenderCount);
    }

    [Fact]
    public async Task 请求取消沿适配器边界传到底层会话()
    {
        ProjectSnapshot snapshot = Snapshot(4, 4);
        var inner = new RecordingFactory { WaitForCancellation = true };
        var adapter = new RenderFrameV1SessionFactory(inner, new(4, 4, 4L * 4 * 4));
        await using IRenderFrameSession session = await adapter.CreateAsync(snapshot);
        using CancellationTokenSource cancellation = new();

        Task pending = session.RenderAsync(RenderRequest.At(snapshot, 0), cancellation.Token).AsTask();
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(inner.SeenCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task 输出不符合合同会先释放帧并拒绝所有权转移()
    {
        ProjectSnapshot snapshot = Snapshot(4, 4);
        var inner = new RecordingFactory { WrongTarget = true };
        var adapter = new RenderFrameV1SessionFactory(inner, new(4, 4, 4L * 4 * 4));
        await using IRenderFrameSession session = await adapter.CreateAsync(snapshot);

        var error = await Assert.ThrowsAsync<PreparedFrameException>(() =>
            session.RenderAsync(RenderRequest.At(snapshot, 0)).AsTask());
        Assert.Equal("RenderFrameV1OutputMismatch", error.Code);
        Assert.NotNull(inner.LastFrame);
        Assert.True(inner.LastFrame!.IsDisposed);
    }

    private static ProjectSnapshot Snapshot(int width, int height) =>
        ProjectSnapshot.FromProject(Project.Create(
            "render-frame-v1",
            Composition.Create(width, height, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([])));

    private sealed class RecordingFactory : IRenderFrameSessionFactory
    {
        public int RenderCount;
        public bool WrongTarget { get; init; }
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken SeenCancellation { get; private set; }
        public RenderFrame? LastFrame { get; private set; }

        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRenderFrameSession>(new Session(this));

        private sealed class Session(RecordingFactory owner) : IRenderFrameSession
        {
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.RenderCount);
                owner.SeenCancellation = cancellationToken;
                if (owner.WaitForCancellation)
                {
                    owner.Entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                RenderTarget target = request.ResolveTarget();
                if (owner.WrongTarget)
                    target = new RenderTarget { PixelWidth = 1, PixelHeight = 1 };
                RenderFrame frame = new(new byte[target.PixelWidth * target.PixelHeight * 4], target,
                    PixelFormat.Bgra8888Premultiplied, request.TimeUs);
                owner.LastFrame = frame;
                return frame;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
