using System.Collections.Concurrent;
using System.Threading.Channels;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendPreviewCoordinatorTests
{
    private static ProjectSnapshot Snapshot(string name = "preview") =>
        ProjectSnapshot.FromProject(Project.Create(name,
            Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));

    private static BackendPreviewRequest Request(
        Factory factory, ProjectSnapshot snapshot, long generation, string revision, long timeUs,
        CancellationToken cancellationToken = default) => new()
        {
            SessionFactory = factory,
            PreviewGeneration = generation,
            SourceRevision = revision,
            RenderRequest = RenderRequest.At(snapshot, timeUs) with { CancellationToken = cancellationToken },
        };

    private static async Task<Call> Read(Channel<Call> calls) =>
        await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task 同代次播放只保留最新待处理时间且帧带显式来源与序号()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => updates.Writer.TryWrite(update);

        coordinator.Submit(Request(factory, snapshot, 4, "revision-7", 1));
        Call first = await Read(factory.Calls);
        for (int time = 2; time <= 100; time++)
            coordinator.Submit(Request(factory, snapshot, 4, "revision-7", time));

        Assert.False(first.Token.IsCancellationRequested);
        first.Release.TrySetResult();
        Call latest = await Read(factory.Calls);
        Assert.Equal(100, latest.Request.TimeUs);
        latest.Release.TrySetResult();

        BackendPreviewUpdate ready = await ReadReady(updates, 100);
        Assert.Equal(4, ready.Request.PreviewGeneration);
        Assert.Equal("revision-7", ready.Request.SourceRevision);
        Assert.Equal(100, ready.Frame!.ActualTimeUs);
        Assert.Equal(100, ready.Frame.RequestSequence);
        Assert.Equal(new long[] { 1, 100 }, factory.Times.ToArray());
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task 同固定输入换代取消后复用可恢复会话并隔离旧帧()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new(recoverable: true);
        ConcurrentQueue<BackendPreviewUpdate> published = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { published.Enqueue(update); updates.Writer.TryWrite(update); };
        coordinator.Submit(Request(factory, snapshot, 1, "revision", 90));
        Call old = await Read(factory.Calls);
        coordinator.Submit(Request(factory, snapshot, 2, "revision", 10));
        Assert.True(old.Token.IsCancellationRequested);
        Call current = await Read(factory.Calls);
        current.Release.TrySetResult();
        BackendPreviewUpdate ready = await ReadReady(updates, 10);
        Assert.Equal(2, ready.Request.PreviewGeneration);
        Assert.Equal(1, factory.Created);
        Assert.Equal(0, factory.Disposed);
        Assert.DoesNotContain(published, update => update.State == BackendPreviewUpdateState.Ready && update.Request.PreviewGeneration == 1);
    }

    [Fact]
    public async Task 同固定输入seek期间迟到成功帧不可跨代发布()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new(ignoreCancellation: true, recoverable: true);
        ConcurrentQueue<BackendPreviewUpdate> published = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { published.Enqueue(update); updates.Writer.TryWrite(update); };
        coordinator.Submit(Request(factory, snapshot, 1, "revision", 90));
        Call old = await Read(factory.Calls);
        coordinator.Submit(Request(factory, snapshot, 2, "revision", 10));
        Assert.True(old.Token.IsCancellationRequested);
        old.Release.TrySetResult();
        Call next = await Read(factory.Calls);
        next.Release.TrySetResult();
        BackendPreviewUpdate ready = await ReadReady(updates, 10);
        Assert.Equal(2, ready.Request.PreviewGeneration);
        Assert.Equal(1, factory.Created);
        Assert.DoesNotContain(published, update => update.State == BackendPreviewUpdateState.Ready && update.Request.PreviewGeneration == 1);
    }

    [Fact]
    public async Task 同固定输入旧代硬失败后释放会话且新代成功()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new(ignoreCancellation: true, recoverable: true);
        ConcurrentQueue<BackendPreviewUpdate> published = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { published.Enqueue(update); updates.Writer.TryWrite(update); };

        coordinator.Submit(Request(factory, snapshot, 1, "revision", 90));
        Call old = await Read(factory.Calls);
        coordinator.Submit(Request(factory, snapshot, 2, "revision", 10));
        Assert.True(old.Token.IsCancellationRequested);
        old.Release.TrySetException(new InvalidOperationException("obsolete failure"));

        Call current = await Read(factory.Calls);
        current.Release.TrySetResult();
        Assert.Equal(2, (await ReadReady(updates, 10)).Request.PreviewGeneration);
        Assert.Equal(2, factory.Created);
        Assert.Equal(1, factory.Disposed);
        Assert.DoesNotContain(published, update => update.State == BackendPreviewUpdateState.Failed);
    }

    [Fact]
    public async Task 创建失败携带未排水会话后关闭会重试清理()
    {
        ProjectSnapshot snapshot = Snapshot();
        using FailedCreationFactory factory = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => updates.Writer.TryWrite(update);
        coordinator.Submit(new BackendPreviewRequest { SessionFactory = factory, PreviewGeneration = 1,
            SourceRevision = "revision", RenderRequest = RenderRequest.At(snapshot, 0) });
        BackendPreviewUpdate failed = await ReadState(updates, BackendPreviewUpdateState.Failed);
        Assert.Equal("CreateFailure", failed.FailureCode);
        Assert.Equal(0, factory.Session.Disposed);
        await coordinator.DisposeAsync();
        Assert.Equal(1, factory.Session.Disposed);
    }

    private sealed class FailedCreationFactory : IRenderFrameSessionFactory, IDisposable
    {
        internal FailedSession Session = new();
        public void Dispose() { }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken token = default)
        {
            var error = new RenderSessionFailureException("CreateFailure", "create failed");
            BackendPreviewFailedCreation.Attach(error, Session);
            return ValueTask.FromException<IRenderFrameSession>(error);
        }
    }

    private sealed class FailedSession : IRenderFrameSession
    {
        internal int Disposed;
        public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task 多次排水失败仍报告原始Worker错误并阻止新会话()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new() { DisposeFailures = 2 };
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => updates.Writer.TryWrite(update);
        coordinator.Submit(Request(factory, snapshot, 1, "revision", 1));
        Call first = await Read(factory.Calls);
        first.Release.SetException(new RenderSessionFailureException("OriginalWorkerFailure", "original worker failure"));
        BackendPreviewUpdate failed = await ReadState(updates, BackendPreviewUpdateState.Failed);
        Assert.Equal("OriginalWorkerFailure", failed.FailureCode);
        coordinator.Submit(Request(factory, snapshot, 2, "revision", 2));
        BackendPreviewUpdate retry = await ReadState(updates, BackendPreviewUpdateState.Failed);
        Assert.Equal("OriginalWorkerFailure", retry.FailureCode);
        Assert.Equal("original worker failure", retry.Error);
        Assert.Equal(1, factory.Created);
        Assert.Equal(2, factory.Disposed);
        coordinator.Submit(Request(factory, snapshot, 3, "revision", 3));
        Call recovered = await Read(factory.Calls);
        recovered.Release.SetResult();
        await ReadReady(updates, 3);
        Assert.Equal(3, factory.Disposed);
        Assert.Equal(2, factory.Created);
    }

    [Fact]
    public async Task 旧代释放失败后重试成功才创建新会话()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new(failFirstDispose: true);
        ConcurrentQueue<BackendPreviewUpdate> published = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { published.Enqueue(update); updates.Writer.TryWrite(update); };
        coordinator.Submit(Request(factory, snapshot, 1, "revision", 90));
        Call old = await Read(factory.Calls);
        coordinator.Submit(Request(factory, snapshot, 2, "revision", 10));
        Assert.True(old.Token.IsCancellationRequested);
        Call current = await Read(factory.Calls);
        current.Release.TrySetResult();
        Assert.Equal(2, (await ReadReady(updates, 10)).Request.PreviewGeneration);
        Assert.Equal(2, factory.Created);
        Assert.Equal(2, factory.Disposed);
        Assert.Equal(1, factory.CreatedAtSuccessfulDispose);
        Assert.DoesNotContain(published, update => update.State == BackendPreviewUpdateState.Failed);
    }

    [Fact]
    public async Task 代次或来源改变时取消旧工作并拒绝迟到帧与错误()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new(ignoreCancellation: true);
        ConcurrentQueue<BackendPreviewUpdate> published = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { published.Enqueue(update); updates.Writer.TryWrite(update); };

        coordinator.Submit(Request(factory, snapshot, 1, "revision-a", 90));
        Call old = await Read(factory.Calls);
        _ = await ReadPreparing(updates);
        coordinator.Submit(Request(factory, snapshot, 2, "revision-b", 10));
        Assert.True(old.Token.IsCancellationRequested);
        old.Release.TrySetException(new InvalidOperationException("obsolete error"));

        Call current = await Read(factory.Calls);
        current.Release.TrySetResult();
        BackendPreviewUpdate ready = await ReadReady(updates, 10);
        Assert.Equal(2, ready.Request.PreviewGeneration);
        Assert.Equal("revision-b", ready.Request.SourceRevision);
        Assert.DoesNotContain(published, update =>
            update.State == BackendPreviewUpdateState.Failed
            || update.State == BackendPreviewUpdateState.Ready && update.Request.PreviewGeneration == 1);
        Assert.Equal(2, factory.Created);
        Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task 同代次倒退时间的迟到帧被序号门禁拒绝()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new(ignoreCancellation: true);
        ConcurrentQueue<BackendPreviewUpdate> published = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { published.Enqueue(update); updates.Writer.TryWrite(update); };

        coordinator.Submit(Request(factory, snapshot, 5, "revision", 90));
        Call old = await Read(factory.Calls);
        _ = await ReadPreparing(updates);
        coordinator.Submit(Request(factory, snapshot, 5, "revision", 10));
        old.Release.TrySetResult();

        Call current = await Read(factory.Calls);
        current.Release.TrySetResult();
        BackendPreviewUpdate ready = await ReadReady(updates, 10);
        Assert.Equal(10, ready.Frame!.ActualTimeUs);
        Assert.DoesNotContain(published, update =>
            update.State == BackendPreviewUpdateState.Ready && update.Request.RenderRequest.TimeUs == 90);
    }

    [Fact]
    public async Task 同来源失败可缓存而新来源修订清除失败并重建会话()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new();
        Channel<BackendPreviewUpdate> updates = Channel.CreateUnbounded<BackendPreviewUpdate>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => updates.Writer.TryWrite(update);

        coordinator.Submit(Request(factory, snapshot, 1, "old", 1));
        Call first = await Read(factory.Calls);
        first.Release.TrySetException(new InvalidOperationException("compile failed"));
        BackendPreviewUpdate failed = await ReadState(updates, BackendPreviewUpdateState.Failed);
        Assert.Equal("compile failed", failed.Error);

        coordinator.Submit(Request(factory, snapshot, 1, "old", 2));
        BackendPreviewUpdate cached = await ReadState(updates, BackendPreviewUpdateState.Failed);
        Assert.Equal("compile failed", cached.Error);
        Assert.Single(factory.Times);

        coordinator.Submit(Request(factory, snapshot, 1, "new", 3));
        Call recovery = await Read(factory.Calls);
        recovery.Release.TrySetResult();
        BackendPreviewUpdate ready = await ReadReady(updates, 3);
        Assert.Equal("new", ready.Request.SourceRevision);
        Assert.Equal(2, factory.Created);
        Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task 通知在锁外发布且回调可以同步提交新请求()
    {
        ProjectSnapshot snapshot = Snapshot();
        Factory factory = new();
        await using BackendPreviewCoordinator coordinator = new();
        int submitted = 0;
        coordinator.Updated += update =>
        {
            if (update.State != BackendPreviewUpdateState.Preparing || Interlocked.Exchange(ref submitted, 1) != 0)
                return;
            Task submit = Task.Run(() => coordinator.Submit(Request(factory, snapshot, 1, "revision", 2)));
            Assert.True(submit.Wait(TimeSpan.FromSeconds(2)), "通知回调不能在协调器锁内阻塞新请求提交。");
        };

        coordinator.Submit(Request(factory, snapshot, 1, "revision", 1));
        Call first = await Read(factory.Calls);
        first.Release.TrySetResult();
        Call second = await Read(factory.Calls);
        Assert.Equal(2, second.Request.TimeUs);
        second.Release.TrySetResult();
        Assert.Equal(new long[] { 1, 2 }, factory.Times.ToArray());
    }

    private static async Task<BackendPreviewUpdate> ReadReady(Channel<BackendPreviewUpdate> updates, long timeUs)
    {
        while (true)
        {
            BackendPreviewUpdate update = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (update.State == BackendPreviewUpdateState.Ready && update.Request.RenderRequest.TimeUs == timeUs)
                return update;
        }
    }

    private static async Task<BackendPreviewUpdate> ReadPreparing(Channel<BackendPreviewUpdate> updates)
    {
        while (true)
        {
            BackendPreviewUpdate update = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (update.State == BackendPreviewUpdateState.Preparing) return update;
        }
    }

    private static async Task<BackendPreviewUpdate> ReadState(Channel<BackendPreviewUpdate> updates, BackendPreviewUpdateState state)
    {
        while (true)
        {
            BackendPreviewUpdate update = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (update.State == state) return update;
        }
    }

    private sealed record Call(RenderRequest Request, TaskCompletionSource Release, CancellationToken Token);

    private sealed class Factory(bool ignoreCancellation = false, bool recoverable = false, bool failFirstDispose = false) : IRenderFrameSessionFactory
    {
        public readonly Channel<Call> Calls = Channel.CreateUnbounded<Call>();
        public readonly ConcurrentQueue<long> Times = new();
        public readonly bool FailFirstDispose = failFirstDispose;
        public int Created;
        public int Disposed;
        public int CreatedAtSuccessfulDispose;
        public int DisposeFailures;

        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Created);
            return ValueTask.FromResult<IRenderFrameSession>(new Session(this, ignoreCancellation, recoverable));
        }

        private sealed class Session(Factory owner, bool ignoreCancellation, bool recoverable) : IRenderFrameSession, IBackendPreviewCancellationRecoverable
        {
            public bool CanReuseAfterCancellation => recoverable;
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                owner.Times.Enqueue(request.TimeUs);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Calls.Writer.TryWrite(new(request, release, cancellationToken));
                if (ignoreCancellation)
                    await release.Task.ConfigureAwait(false);
                else
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                RenderTarget target = request.ResolveTarget();
                byte[] pixels = new byte[target.PixelWidth * target.PixelHeight * 4];
                pixels[0] = (byte)request.TimeUs;
                return new RenderFrame(pixels, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs);
            }

            public ValueTask DisposeAsync()
            {
                int count = Interlocked.Increment(ref owner.Disposed);
                if (owner.FailFirstDispose && count == 1) throw new InvalidOperationException("dispose failed");
                if (count <= owner.DisposeFailures) throw new InvalidOperationException("dispose failed");
                owner.CreatedAtSuccessfulDispose = owner.Created;
                return ValueTask.CompletedTask;
            }
        }
    }
}
