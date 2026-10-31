using System.Collections;
using System.Reflection;
using System.Threading.Channels;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendRuntimeValidationJobTests
{
    [Fact]
    public async Task 统一Runtime验证只进入独立执行器并在完成后释放图片音频引用与输入快照()
    {
        await using Fixture f = await Fixture.Create();
        int baseline = f.ReferenceCount;
        JobResponse started = await f.Start();
        Call call = await f.Executor.Next();
        Assert.Equal(ProjectRuntimeInputCodec.Profile, started.Input!.SourceProfile);
        Assert.Equal(WorkerProtocol.ComputeSha256(call.EncodedImage), started.Input.SourceSha256);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(call.MediaPath));
        Assert.Equal(baseline + 1, f.ReferenceCount);
        call.Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(started.JobId)).State);
        Assert.False(File.Exists(call.MediaPath));
        Assert.Equal(baseline, f.ReferenceCount);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        f.AssertScheduler(0, 0);
    }

    [Fact]
    public async Task 统一Runtime验证音频获取失败释放已取得图片引用且不登记任务()
    {
        await using Fixture f = await Fixture.Create();
        string original = f.Session.AudioAssetId;
        SetAudioAssetId(f.Session, "missing-audio");
        try
        {
            BackendOperationResult<JobResponse> result = await f.Registry.StartValidationAsync("client", f.SessionId,
                new("0", 3, f.Meta()));
            Assert.False(result.Accepted);
            Assert.Equal(BackendErrorCode.ResourceNotFound, result.Error!.Code);
            Assert.Equal(0, f.Executor.Calls);
            Assert.Equal(f.ReferenceCount, f.BaselineReferenceCount);
            Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        }
        finally { SetAudioAssetId(f.Session, original); }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("close")]
    [InlineData("identity")]
    [InlineData("owner")]
    public async Task 统一Runtime验证二次准入在读取期间发现会话变化时拒绝且无泄漏(string mutation)
    {
        await using Fixture f = await Fixture.Create();
        (Task<BackendOperationResult<JobResponse>> pending, Task writerWaiter) = f.StartBehindCaptureGate();
        await writerWaiter.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            switch (mutation)
            {
                case "revision": SetProjectRevision(f.Session, 1); break;
                case "close": f.Session.ClosePending = true; break;
                case "identity": f.ReplaceSessionIdentity(); break;
                case "owner": SetOwner(f.Session, "foreign"); break;
            }
        }
        finally { f.Session.Writer.Release(); }
        try
        {
            BackendOperationResult<JobResponse> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Accepted);
            Assert.Equal(mutation switch
            {
                "revision" => BackendErrorCode.RevisionConflict,
                "close" => BackendErrorCode.SessionClosing,
                _ => BackendErrorCode.SessionNotFound,
            }, result.Error!.Code);
            Assert.Equal(0, f.Executor.Calls);
            Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
            Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
            f.AssertScheduler(0, 0);
        }
        finally
        {
            f.RestoreSessionIdentity();
            SetOwner(f.Session, "client");
            SetProjectRevision(f.Session, 0);
            f.Session.ClosePending = false;
            await pending.IgnoreFailureAsync();
        }
    }

    [Fact]
    public async Task 统一Runtime验证排队取消与关闭均等待执行器排水并释放租约()
    {
        await using Fixture f = await Fixture.Create(new() { MaxRunning = 1 });
        JobResponse first = await f.Start();
        Call running = await f.Executor.Next();
        JobResponse queued = await f.Start();
        Assert.Equal("queued", f.Registry.ReadJob("client", queued.JobId).Response!.State);
        Assert.Equal(f.BaselineReferenceCount + 2, f.ReferenceCount);
        f.AssertScheduler(1, 1);
        Assert.True(f.Registry.CancelJob("client", queued.JobId).Accepted);
        Assert.Equal("cancelled", (await f.Terminal(queued.JobId)).State);
        Assert.Equal(f.BaselineReferenceCount + 1, f.ReferenceCount);
        f.AssertScheduler(1, 0);
        Assert.Equal(1, f.Executor.Calls);
        running.Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(first.JobId)).State);
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        f.AssertScheduler(0, 0);

        f.Executor.HoldCancellationDrain = true;
        JobResponse active = await f.Start();
        Call activeCall = await f.Executor.Next();
        BackendOperationResult<JobResponse> close = await f.Registry.StartCloseAsync("client", f.SessionId,
            new("0", "discard", ActiveJobs: "cancelAndWait", Metadata: f.Meta()));
        Assert.True(close.Accepted);
        await activeCall.CancellationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("running", f.Registry.ReadJob("client", active.JobId).Response!.State);
        Assert.False(f.Registry.ReadJob("client", close.Response!.JobId).Response!.State is "succeeded" or "failed" or "cancelled");
        Assert.Equal(f.BaselineReferenceCount + 1, f.ReferenceCount);
        Assert.True(Directory.Exists(f.ImageDirectory));
        f.AssertScheduler(1, 0);
        activeCall.Drain.TrySetResult();
        Assert.Equal("cancelled", (await f.Terminal(active.JobId)).State);
        Assert.Equal("succeeded", (await f.Terminal(close.Response.JobId)).State);
        Assert.Equal(2, f.Executor.Drained);
        Assert.Equal(0, f.ReferenceCount);
        Assert.False(Directory.Exists(f.ImageDirectory));
        f.AssertScheduler(0, 0);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task 统一Runtime验证实际执行finally清空固定图片快照音频和调度引用(string outcome)
    {
        await using Fixture f = await Fixture.Create();
        object work = f.CreateWork();
        using BackendJob job = new("client", "validation", f.SessionId, DateTimeOffset.UtcNow.AddMinutes(1),
            _ => { }, TimeProvider.System, TimeSpan.FromMinutes(1), new object(), _ => { },
            failureMapper: _ => new(BackendErrorCode.TaskExecutionFailed, "controlled"));
        MethodInfo run = typeof(ProjectSessionRegistry).GetMethod("RunValidationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        job.Start(current => (Task)run.Invoke(f.Registry, [work, current])!, deferRunning: true);
        Call call = await f.Executor.Next();
        Assert.NotNull(Field(work, "Snapshot"));
        Assert.NotNull(Field(work, "RuntimeInput"));
        Assert.True(File.Exists(call.MediaPath));
        Assert.NotNull(Field(work, "_audio"));
        Assert.NotNull(Field(work, "Reservation"));
        Assert.Equal(f.BaselineReferenceCount + 1, f.ReferenceCount);
        if (outcome == "cancel") Assert.True(job.Cancel().Accepted);
        else if (outcome == "failure") call.Release.TrySetException(new IOException("controlled"));
        else call.Release.TrySetResult(Pass());
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(outcome switch { "success" => "succeeded", "failure" => "failed", _ => "cancelled" }, job.Read().State);
        Assert.Null(Field(work, "Snapshot"));
        Assert.Null(Field(work, "RuntimeInput"));
        Assert.False(File.Exists(call.MediaPath));
        Assert.Null(Field(work, "_audio"));
        Assert.Null(Field(work, "Reservation"));
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        f.AssertScheduler(0, 0);
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetOwner(ProjectSession session, string owner) =>
        typeof(ProjectSession).GetField("<OwnerClientId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, owner);

    private static ValidationReportDto Pass() => new(true, 3, 3, [], []);

    private static void SetAudioAssetId(ProjectSession session, string value) =>
        typeof(ProjectSession).GetProperty(nameof(ProjectSession.AudioAssetId), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(session, value);

    private static void SetProjectRevision(ProjectSession session, long value) =>
        typeof(ProjectSession).GetProperty("ProjectRevision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, value);

    private sealed record Call(TaskCompletionSource<ValidationReportDto> Release, string MediaPath, byte[] EncodedImage,
        TaskCompletionSource CancellationReceived, TaskCompletionSource Drain);

    private sealed class Executor : IBackendValidationExecutor, IBackendRuntimeValidationExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal int Calls;
        internal int Drained;
        internal bool HoldCancellationDrain;
        public bool RuntimeValidationAvailable => true;
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("统一Runtime验证不得进入通用执行器。");
        public async Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes,
            int framesPerOutput, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            TaskCompletionSource<ValidationReportDto> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] bytes = sourceBytes.ToArray();
            var input = ProjectRuntimeInputCodec.Decode(bytes);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Writer.TryWrite(new(release, input.Grant!.FilePath, bytes, cancelled, drain));
            try { return await release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                if (HoldCancellationDrain) await drain.Task.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                throw;
            }
            finally { Interlocked.Increment(ref Drained); }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly TemporaryDirectory _imageDirectory = new();
        internal readonly Executor Executor = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal string SessionId => Session.ProjectSessionId;
        internal int BaselineReferenceCount { get; private set; }
        internal string ImageDirectory => _imageDirectory.Path;
        internal int ReferenceCount { get { lock (ResourceGate) return ReadReferences().Values.Sum(); } }
        internal object ResourceGate => typeof(ResourceStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Resources)!;
        internal ResourceStore Resources = null!;
        private ProjectSession? _replacement;
        private string _host = "host-one";

        internal static async Task<Fixture> Create(BackendValidationOptions? options = null)
        {
            Fixture f = new();
            f.Resources = new(new() { ResourceRoot = f._directory.FilePath("uploads") });
            RegistryOptions(f, options);
            await f.CreateProject();
            f.BaselineReferenceCount = f.ReferenceCount;
            return f;
        }

        private static void RegistryOptions(Fixture f, BackendValidationOptions? options)
        {
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform sample = new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new(f._host, f.Resources, new((_, _) => Task.FromResult<LoadedWaveform?>(sample)));
            f.Registry.ConfigurePersistence(new() { StateRoot = f._directory.FilePath("state") });
            f.Registry.ConfigureValidationTasks(f.Executor, options);
        }

        private async Task CreateProject()
        {
            StoredAsset audio = await Resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            StoredAsset lyric = await Resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
            ProjectSessionCreateResult created = await Registry.CreateAsync("client",
                new("image", audio.AssetId, lyric.AssetId, new(640, 360, OutputDurationUs: 3000000), Metadata: Meta()));
            Assert.True(created.Accepted);
            Assert.True(Registry.TryGet("client", created.Session!.ProjectSessionId, out Session));
            byte[] image = [1, 2, 3];
            MediaFileRevision media = new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)), image.Length, "image.png");
            string path = Path.Combine(_imageDirectory.Path, media.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, image);
            Session.PinnedInputDirectory = _imageDirectory.Path;
            Session.Workspace.Replace(Session.Workspace.Project with
            { Background = new BackgroundSource { Kind = BackgroundKind.Image, Path = path, Media = media } });
        }

        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<BackendOperationResult<JobResponse>> StartOperation() => Registry.StartValidationAsync("client", SessionId, new("0", 3, Meta()));
        internal async Task<JobResponse> Start()
        {
            BackendOperationResult<JobResponse> started = await StartOperation();
            Assert.True(started.Accepted, started.Error?.Message);
            return started.Response!;
        }

        internal (Task<BackendOperationResult<JobResponse>> Pending, Task Writer) StartBehindCaptureGate()
        {
            lock (ResourceGate)
            {
                // AcquireReference blocks while the first capture owns Writer and the image lease.
                // Queue this waiter before returning that Writer; it must run before re-admission.
                Task<BackendOperationResult<JobResponse>> pending = Task.Run(StartOperation);
                Assert.True(SpinWait.SpinUntil(() => !Session.DrainPinnedInputReadsAsync().IsCompleted, TimeSpan.FromSeconds(5)));
                Task writer = Session.Writer.WaitAsync();
                Assert.False(writer.IsCompleted);
                return (pending, writer);
            }
        }

        private BackendTaskScheduler Scheduler =>
            (BackendTaskScheduler)Field(Registry, "_taskScheduler")!;
        internal void AssertScheduler(int running, int queued)
        { Assert.Equal(running, Scheduler.Running); Assert.Equal(queued, Scheduler.Queued); }
        internal object CreateWork()
        {
            Type type = typeof(ProjectSessionRegistry).GetNestedType("ValidationWork", BindingFlags.NonPublic)!;
            var snapshot = ProjectSnapshot.FromProject(Session.Workspace.Project);
            using var lease = Session.AcquireProjectRuntimeInput(snapshot);
            var prepared = lease.PrepareAsync().GetAwaiter().GetResult().Response!;
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [snapshot, ProjectRuntimeInputCodec.Profile, 3, Resources.AcquireReference("client", Session.AudioAssetId),
                    Scheduler.TryReserve("client", SessionId)!, Executor, null, prepared, Executor], null)!;
        }
        internal async Task<JobResponse> Terminal(string id)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            while (true)
            {
                JobResponse response = Registry.ReadJob("client", id).Response!;
                if (response.State is "succeeded" or "failed" or "cancelled" or "unknown")
                {
                    BackendJob job = (BackendJob)((IDictionary)Field(Registry, "_jobs")!)[id]!;
                    await job.Completion.WaitAsync(timeout.Token);
                    return response;
                }
                await Task.Delay(10, timeout.Token);
            }
        }

        internal void ReplaceSessionIdentity()
        {
            _replacement = new(Session.ProjectSessionId, _host, "client", ProjectWorkspace.Adopt(Session.Workspace.Project), Session.AudioAssetId,
                Session.AudioDurationUs, TimeProvider.System, new(), new("replacement", 100)) { PinnedInputDirectory = Session.PinnedInputDirectory };
            FieldInfo field = typeof(ProjectSessionRegistry).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            IDictionary dictionary = (IDictionary)field.GetValue(Registry)!;
            dictionary[Session.ProjectSessionId] = _replacement;
        }

        internal void RestoreSessionIdentity()
        {
            if (_replacement is null) return;
            FieldInfo field = typeof(ProjectSessionRegistry).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            IDictionary dictionary = (IDictionary)field.GetValue(Registry)!;
            dictionary[Session.ProjectSessionId] = Session; _replacement = null;
        }

        private Dictionary<string, int> ReadReferences() =>
            (Dictionary<string, int>)typeof(ResourceStore).GetField("_references", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Resources)!;

        public async ValueTask DisposeAsync()
        {
            RestoreSessionIdentity();
            if (Registry is not null) await Registry.DisposeAsync();
            _directory.Dispose(); _imageDirectory.Dispose();
        }
    }
}
