using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendPreviewRenderBackendLifecycleTests
{
    [Theory]
    [InlineData("project")]
    [InlineData("template")]
    [InlineData("presetCandidate")]
    [InlineData("editCandidate")]
    [InlineData("authorDraft")]
    public async Task 切换保持原快照和来源且同设备请求释放捕获引用关闭后才释放整组资源(string kind)
    {
        await using Fixture fixture = new();
        Factory cpu = new(), gpu = new();
        CountingLease resource = new();
        BackendPreviewPinnedInput original = Input(cpu, resource, kind);
        PreviewSessionResponse before = fixture.Create(original);
        PreviewFrameMetadataDto oldFrame = await fixture.Render(before, cpu);
        BackendPreviewPinnedInput reader = fixture.Capture(before);
        BackendPreviewPinnedInput selection = fixture.Capture(before) with { Factory = gpu, RenderBackend = "gpu" };

        BackendPreviewSessionResult switched = fixture.Registry.SwitchRenderBackend("client", before.PreviewId,
            before.PreviewGeneration, selection);
        Assert.True(switched.Accepted, switched.Error?.Message);
        PreviewSessionResponse after = switched.Status!.Session;
        Assert.NotEqual(before.PreviewGeneration, after.PreviewGeneration);
        Assert.Equal("gpu", after.RenderBackend);
        Assert.Equal(before.Source, after.Source);
        Assert.Same(original.EditCandidate, after.EditCandidate);
        Assert.Null(after.LatestFrame);
        Assert.Equal(0, resource.Disposals);
        Assert.False(fixture.Frames.TryRead("client", oldFrame.FrameId, DateTimeOffset.UtcNow, out _, out _));

        BackendPreviewPinnedInput noOp = fixture.Capture(after);
        Assert.Same(original.Snapshot, noOp.Snapshot);
        Assert.Same(original.EditCandidate, noOp.EditCandidate);
        Assert.Same(original.PackageClosure, noOp.PackageClosure);
        BackendPreviewSessionResult repeated = fixture.Registry.SwitchRenderBackend("client", after.PreviewId,
            after.PreviewGeneration, noOp);
        Assert.True(repeated.Accepted);
        Assert.Equal(after.PreviewGeneration, repeated.Status!.Session.PreviewGeneration);
        PreviewFrameMetadataDto currentFrame = await fixture.Render(after, gpu);
        Assert.Equal("gpu", currentFrame.RenderBackend);
        Assert.Same(original.Snapshot, Assert.Single(gpu.CreatedSnapshots));
        Assert.Same(original.Snapshot, Assert.Single(gpu.RenderedSnapshots));
        Assert.Equal(0, resource.Disposals);

        Assert.True((await fixture.Close(after)).Accepted);
        Assert.Equal(0, resource.Disposals);
        Assert.Equal(1, cpu.Disposals);
        Assert.Equal(1, gpu.Disposals);
        reader.RetentionLease!.Dispose();
        reader.RetentionLease.Dispose();
        Assert.Equal(1, resource.Disposals);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("source")]
    [InlineData("candidate")]
    [InlineData("backend")]
    public async Task 拒绝替换固定身份或非法后端时保留旧帧代次且捕获租约仍由调用者释放(string mutation)
    {
        await using Fixture fixture = new();
        Factory factory = new();
        CountingLease resource = new();
        BackendPreviewPinnedInput original = Input(factory, resource, "editCandidate");
        PreviewSessionResponse session = fixture.Create(original);
        PreviewFrameMetadataDto frame = await fixture.Render(session, factory);
        BackendPreviewPinnedInput captured = fixture.Capture(session);
        BackendPreviewPinnedInput changed = mutation switch
        {
            "snapshot" => captured with { Snapshot = captured.Snapshot with { Name = "new snapshot" } },
            "source" => captured with { Source = captured.Source with { SourceId = "different-project" } },
            "candidate" => captured with { EditCandidate = captured.EditCandidate! with { BaseProjectRevision = "1" } },
            _ => captured with { RenderBackend = "vulkan" },
        };
        BackendPreviewSessionResult rejected = fixture.Registry.SwitchRenderBackend("client", session.PreviewId,
            session.PreviewGeneration, changed);
        Assert.False(rejected.Accepted);
        Assert.Equal(mutation == "backend" ? BackendErrorCode.InvalidRequest : BackendErrorCode.PreviewGenerationConflict,
            rejected.Error!.Code);
        PreviewSessionResponse current = fixture.Registry.Read("client", session.PreviewId).Status!.Session;
        Assert.Equal(session.PreviewGeneration, current.PreviewGeneration);
        Assert.Equal("cpu", current.RenderBackend);
        Assert.Equal(frame, current.LatestFrame);
        Assert.True(fixture.Frames.TryRead("client", frame.FrameId, DateTimeOffset.UtcNow, out var bytes, out _));
        Assert.Equal(PreviewPngFixture.Create(), bytes!.Content);
        Assert.Single(factory.CreatedSnapshots);
        Assert.True((await fixture.Close(current)).Accepted);
        Assert.Equal(0, resource.Disposals);
        captured.RetentionLease!.Dispose();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task 捕获后被换源的切换冲突不会释放新输入或提前释放旧资源()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        CountingLease oldResource = new(), nextResource = new();
        BackendPreviewPinnedInput original = Input(factory, oldResource);
        PreviewSessionResponse before = fixture.Create(original);
        BackendPreviewPinnedInput stale = fixture.Capture(before);
        BackendPreviewPinnedInput next = Input(factory, nextResource) with
        {
            Source = original.Source with { SourceRevision = "source-2" },
            Snapshot = original.Snapshot with { Name = "next input" },
        };
        BackendPreviewSessionResult updated = fixture.Registry.Update("client", before.PreviewId,
            before.Source.SourceRevision, new(before.PreviewGeneration, next.Source, before.Size, before.AlphaMode), next);
        Assert.True(updated.Accepted, updated.Error?.Message);
        Assert.Equal(0, oldResource.Disposals);
        BackendPreviewSessionResult refused = fixture.Registry.SwitchRenderBackend("client", before.PreviewId,
            before.PreviewGeneration, stale with { RenderBackend = "gpu" });
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict, refused.Error!.Code);
        stale.RetentionLease!.Dispose();
        Assert.Equal(1, oldResource.Disposals);
        Assert.Equal(0, nextResource.Disposals);
        BackendPreviewPinnedInput current = fixture.Capture(updated.Status!.Session);
        Assert.Same(next.Snapshot, current.Snapshot);
        current.RetentionLease!.Dispose();
        Assert.True((await fixture.Close(updated.Status.Session)).Accepted);
        Assert.Equal(1, nextResource.Disposals);
        Assert.Equal(1, oldResource.Disposals);
    }

    [Fact]
    public async Task 捕获后关闭导致切换拒绝且最后一个捕获引用负责释放资源()
    {
        await using Fixture fixture = new();
        CountingLease resource = new();
        PreviewSessionResponse session = fixture.Create(Input(new(), resource));
        BackendPreviewPinnedInput captured = fixture.Capture(session);
        Assert.True((await fixture.Close(session)).Accepted);
        Assert.Equal(0, resource.Disposals);
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound,
            fixture.Registry.CaptureRenderBackendInput("client", session.PreviewId, session.PreviewGeneration).Error!.Code);
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound,
            fixture.Registry.SwitchRenderBackend("client", session.PreviewId, session.PreviewGeneration,
                captured with { RenderBackend = "gpu" }).Error!.Code);
        captured.RetentionLease!.Dispose();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task 切换取消旧在途帧且迟到旧帧不能覆盖新后端代次()
    {
        await using Fixture fixture = new();
        Factory cpu = new(ignoreCancellation: true), gpu = new();
        CountingLease resource = new();
        BackendPreviewPinnedInput original = Input(cpu, resource);
        PreviewSessionResponse before = fixture.Create(original);
        Assert.True(fixture.Request(before).Accepted);
        Call old = await cpu.Next();
        BackendPreviewPinnedInput captured = fixture.Capture(before);
        BackendPreviewSessionResult changed = fixture.Registry.SwitchRenderBackend("client", before.PreviewId,
            before.PreviewGeneration, captured with { Factory = gpu, RenderBackend = "gpu" });
        Assert.True(changed.Accepted);
        PreviewSessionResponse after = changed.Status!.Session;
        Assert.True(old.Token.IsCancellationRequested);
        Assert.True(fixture.Request(after).Accepted);
        old.Release.SetResult();
        Call current = await gpu.Next();
        Assert.Same(original.Snapshot, current.Request.Project);
        Assert.Null(fixture.Registry.Read("client", before.PreviewId).Status!.Session.LatestFrame);
        current.Release.SetResult();
        BackendPreviewSessionStatus ready = await fixture.Wait(after.PreviewId,
            status => status.Session.LatestFrame is not null);
        Assert.Equal(after.PreviewGeneration, ready.Session.LatestFrame!.PreviewGeneration);
        Assert.Equal("gpu", ready.Session.LatestFrame.RenderBackend);
        Assert.Equal(1, fixture.Frames.Count);
        Assert.True((await fixture.Close(ready.Session)).Accepted);
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(1, cpu.Disposals);
        Assert.Equal(1, gpu.Disposals);
    }

    private static BackendPreviewPinnedInput Input(Factory factory, CountingLease resource, string kind = "project")
    {
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(Project.Create("fixed snapshot",
            Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));
        PreviewSourceDto source = new(kind, "source-id", "source-1",
            kind == "authorDraft" ? new(TemplatePreviewLimits.SingleLineSampleId, "token") : null,
            kind == "authorDraft" ? "author-source-1" : null);
        return new(source, snapshot, factory, resource)
        {
            EditCandidate = kind == "editCandidate" ? new("source-id", "0", "source-1", new([], [], [], [], false), []) : null,
        };
    }

    private sealed class CountingLease : IDisposable
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly PreviewFrameHandleStore Frames = new(maxFrames: 4, maxBytes: 4096);
        internal readonly BackendPreviewSessionRegistry Registry;
        private long _sequence;
        internal Fixture() => Registry = new(Frames, new(), new Encoder(), new()
        {
            Limits = new(2, 2, 1, 2, 1024, 30, 8),
        });
        internal PreviewSessionResponse Create(BackendPreviewPinnedInput input)
        {
            BackendPreviewSessionResult result = Registry.Create("client", new(input.Source, new(1, 1),
                PreviewKind: input.Source.Kind, Committable: input.Source.Kind == "project"), input);
            Assert.True(result.Accepted, result.Error?.Message);
            return result.Status!.Session;
        }
        internal BackendPreviewPinnedInput Capture(PreviewSessionResponse session)
        {
            var captured = Registry.CaptureRenderBackendInput("client", session.PreviewId, session.PreviewGeneration);
            Assert.True(captured.Accepted, captured.Error?.Message);
            return captured.Response!;
        }
        internal BackendPreviewFrameRequestResult Request(PreviewSessionResponse session) => Registry.RequestFrame("client",
            session.PreviewId, session.Source.SourceRevision,
            new(session.PreviewGeneration, Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture), "0", "playback"));
        internal async Task<PreviewFrameMetadataDto> Render(PreviewSessionResponse session, Factory factory)
        {
            BackendPreviewFrameRequestResult accepted = Request(session);
            Assert.True(accepted.Accepted, accepted.Error?.Message);
            (await factory.Next()).Release.SetResult();
            return (await Wait(session.PreviewId, status => status.Session.LatestFrame is not null)).Session.LatestFrame!;
        }
        internal async Task<BackendPreviewSessionStatus> Wait(string id, Func<BackendPreviewSessionStatus, bool> predicate)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            while (true)
            {
                BackendPreviewSessionStatus status = Registry.Read("client", id).Status!;
                if (predicate(status)) return status;
                await Task.Delay(10, timeout.Token);
            }
        }
        internal Task<BackendPreviewSessionResult> Close(PreviewSessionResponse session) => Registry.CloseAsync("client",
            session.PreviewId, session.PreviewGeneration, session.Source.SourceRevision);
        public ValueTask DisposeAsync() => Registry.DisposeAsync();
    }

    private sealed class Encoder : IBackendPreviewPngEncoder
    {
        public byte[] Encode(BackendPreviewFrame frame) => PreviewPngFixture.Create();
    }

    private sealed record Call(RenderRequest Request, TaskCompletionSource Release, CancellationToken Token);
    private sealed class Factory(bool ignoreCancellation = false) : IRenderFrameSessionFactory
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal readonly ConcurrentQueue<ProjectSnapshot> CreatedSnapshots = new(), RenderedSnapshots = new();
        internal int Disposals;
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            CreatedSnapshots.Enqueue(snapshot);
            return ValueTask.FromResult<IRenderFrameSession>(new Session(this, ignoreCancellation));
        }
        private sealed class Session(Factory owner, bool ignoreCancellation) : IRenderFrameSession
        {
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                owner.RenderedSnapshots.Enqueue(request.Project);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner._calls.Writer.TryWrite(new(request, release, cancellationToken));
                await release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
                return new([0, 0, 255, 255], request.ResolveTarget(), PixelFormat.Bgra8888Premultiplied, request.TimeUs);
            }
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposals);
                return ValueTask.CompletedTask;
            }
        }
    }
}
