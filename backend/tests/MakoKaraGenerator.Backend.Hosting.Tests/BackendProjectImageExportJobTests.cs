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

public sealed partial class BackendProjectImageExportJobTests
{
    [Fact]
    public async Task 图片导出只进入独立执行器并在完成后释放图片音频引用与输入快照()
    {
        await using Fixture f = await Fixture.Create();
        int baseline = f.ReferenceCount;
        JobResponse started = await f.Start();
        Call call = await f.Executor.Next();
        Assert.Equal(ProjectImageExportWorkerContract.ExportProfile, started.Input!.SourceProfile);
        Assert.Equal(WorkerProtocol.ComputeSha256(call.EncodedImage), started.Input.SourceSha256);
        Assert.Equal([1, 2, 3], call.ImageBytes);
        Assert.Equal(baseline + 1, f.ReferenceCount);
        Assert.Equal(ProjectImageExportWorkerContract.ExportProfile, started.ExportInput!.TaskProfile);
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScene, started.ExportInput.BaseSourceProfile);
        call.Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(started.JobId)).State);
        Assert.Equal(baseline, f.ReferenceCount);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        f.AssertScheduler(0, 0);
    }

    [Fact]
    public async Task 图片导出音频获取失败释放已取得图片引用且不登记任务()
    {
        await using Fixture f = await Fixture.Create();
        string original = f.Session.AudioAssetId;
        SetAudioAssetId(f.Session, "missing-audio");
        try
        {
            BackendOperationResult<JobResponse> result = await f.Registry.StartExportAsync("client", f.SessionId,
                new("0", IncludeAudio: false, Metadata: f.Meta()));
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
    public async Task 图片导出二次准入在读取期间发现会话变化时拒绝且无泄漏(string mutation)
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
    public async Task 图片导出排队取消与关闭均等待执行器排水并释放租约()
    {
        await using Fixture f = await Fixture.Create();
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
    [InlineData("source")]
    [InlineData("input")]
    public async Task 图片导出读取或任务封包拒绝全部归还预留并允许后续工作(string phase)
    {
        await using Fixture f = await Fixture.Create();
        if (phase == "source") await File.WriteAllBytesAsync(f.Session.Workspace.Project.Background.Path!, [3, 2, 1]);
        else f.Executor.RejectInput = true;
        var result = await f.StartOperation();
        Assert.False(result.Accepted); Assert.Equal(0, f.Executor.Calls);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount); f.AssertScheduler(0, 0);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
        Assert.Equal(0L, f.ArtifactUsedBytes);
        await File.WriteAllBytesAsync(f.Session.Workspace.Project.Background.Path!, [1, 2, 3]);
        f.Executor.RejectInput = false;
        JobResponse next = await f.Start(); (await f.Executor.Next()).Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(next.JobId)).State);
    }

    [Fact]
    public async Task 图片导出唯一账本恢复保留任务与基础源并拒绝缺失或矛盾新字段()
    {
        await using Fixture f = await Fixture.Create();
        JobResponse started = await f.Start(); (await f.Executor.Next()).Release.TrySetResult(Pass());
        await f.Terminal(started.JobId);
        byte[] bytes = f.Registry.ReadExportResult("client", started.JobId).Response!;
        await f.Registry.DisposeAsync();
        f.Restart();
        var recovered = Assert.Single(f.Registry.ReadRecoveredJobs("local-bootstrap")).Job;
        Assert.Equal(started.Input, recovered.Input); Assert.Equal(started.ExportInput, recovered.ExportInput);
        Assert.Equal(bytes, f.Registry.ReadExportResult("local-bootstrap", started.JobId, true).Response);
        string path = Path.Combine(f.State, "jobs", started.JobId + ".json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["Job"]!["ExportInput"]!["BaseSourceProfile"] = BackendSnapshotProfiles.StaticLyricsScenePresets;
        await File.WriteAllTextAsync(path, node.ToJsonString());
        Assert.Equal(BackendErrorCode.TaskExecutionFailed,
            f.Registry.ReadExportResult("local-bootstrap", started.JobId, true).Error!.Code);
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetOwner(ProjectSession session, string owner) =>
        typeof(ProjectSession).GetField("<OwnerClientId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, owner);

    private static ExportReportDto Pass() => new("opaqueMp4", false, 90, 90, 90, 0, 0, 640, 360, 30, 1,
        "3000000", "3000000", null, "h264", null, "yuv420p", false, Executor.Bytes.Length,
        WorkerProtocol.ComputeSha256(Executor.Bytes), [], [], true, null, true);

    private static void SetAudioAssetId(ProjectSession session, string value) =>
        typeof(ProjectSession).GetProperty(nameof(ProjectSession.AudioAssetId), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(session, value);

    private static void SetProjectRevision(ProjectSession session, long value) =>
        typeof(ProjectSession).GetProperty("ProjectRevision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, value);

    private sealed record Call(TaskCompletionSource<ExportReportDto> Release, byte[] ImageBytes, byte[] EncodedImage,
        TaskCompletionSource CancellationReceived, TaskCompletionSource Drain);

    private sealed class Executor : IBackendExportExecutor, IBackendImageExportExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal int Calls;
        internal int Drained;
        internal bool HoldCancellationDrain;
        internal static readonly byte[] Bytes = "controlled-image-export"u8.ToArray();
        internal bool RejectInput;
        internal bool RuntimeEnabled;
        public bool ImageExportAvailable => true;
        public Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackendExportCapabilities(true, true, true));
        public void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input)
        {
            if (!RuntimeEnabled) throw new InvalidOperationException("图片不得进入通用输入校验。");
            if (RejectInput) throw new WorkerProtocolException("controlled budget", "WorkerSnapshotBudgetExceeded");
            Assert.Equal(ProjectRuntimeInputCodec.Profile, ProjectRuntimeInputCodec.Decode(input.RuntimeSource).SourceProfile);
        }
        public void ValidateImageInput(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes, BackendExportExecutionInput input)
        { if (RejectInput) throw new WorkerProtocolException("controlled budget", "WorkerSnapshotBudgetExceeded"); }
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!RuntimeEnabled) throw new InvalidOperationException("图片导出不得进入通用执行器。");
            var source = ProjectRuntimeInputCodec.Decode(input.RuntimeSource);
            Assert.True(File.Exists(source.Grant!.FilePath));
            await ExecuteImageAsync(snapshot, input.RuntimeSource, input, progress, cancellationToken);
            return Pass();
        }
        public async Task<ExportReportDto> ExecuteImageAsync(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes,
            BackendExportExecutionInput input, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            TaskCompletionSource<ExportReportDto> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] bytes = imageBytes.ToArray();
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Writer.TryWrite(new(release, bytes, RuntimeEnabled ? bytes : ProjectImagePreviewSnapshotCodec.Encode(snapshot, bytes), cancelled, drain));
            try
            {
                ExportReportDto result = await release.Task.WaitAsync(cancellationToken);
                await File.WriteAllBytesAsync(input.CandidatePath, Bytes, cancellationToken);
                return result;
            }
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
        internal string State => _directory.FilePath("state");
        internal long ArtifactUsedBytes => (long)Field(Field(Registry, "_exportArtifacts")!, "_reservedBytes")!;
        internal void Restart() { _host = "host-two"; RegistryOptions(this, null); }
        internal int ReferenceCount { get { lock (ResourceGate) return ReadReferences().Values.Sum(); } }
        internal object ResourceGate => typeof(ResourceStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Resources)!;
        internal ResourceStore Resources = null!;
        private ProjectSession? _replacement;
        private string _host = "host-one";

        internal static async Task<Fixture> Create(BackendExportOptions? options = null, bool runtime = false, BackgroundKind kind = BackgroundKind.Image)
        {
            Fixture f = new();
            f.Executor.RuntimeEnabled = runtime;
            f.Resources = new(new() { ResourceRoot = f._directory.FilePath("uploads") });
            RegistryOptions(f, options);
            await f.CreateProject(kind);
            f.BaselineReferenceCount = f.ReferenceCount;
            return f;
        }

        private static void RegistryOptions(Fixture f, BackendExportOptions? options)
        {
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform sample = new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new(f._host, f.Resources, new((_, _) => Task.FromResult<LoadedWaveform?>(sample)));
            f.Registry.ConfigurePersistence(new() { StateRoot = f._directory.FilePath("state") });
            f.Registry.ConfigureValidationTasks(null, new() { MaxRunning = 1 });
            f.Registry.ConfigureExportTasks(f.Executor, new(true, true, true,
                SourceProfiles: f.Executor.RuntimeEnabled ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                    BackendSnapshotProfiles.StaticLyricsScenePresets, BackendSnapshotProfiles.PackageLyricsScene, BackendSnapshotProfiles.PackageLyricsScenePresets,
                    ProjectRuntimeInputCodec.Profile] : null,
                ImageExportAvailable: true, ImageBaseProfiles: [BackendSnapshotProfiles.StaticLyricsScene,
                    BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene]), options);
        }

        private async Task CreateProject(BackgroundKind kind)
        {
            StoredAsset audio = await Resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            StoredAsset lyric = await Resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]<00:00.00>hello<00:03.00>\n"u8.ToArray()));
            ProjectSessionCreateResult created = await Registry.CreateAsync("client",
                new("image", audio.AssetId, lyric.AssetId, new(640, 360, OutputDurationUs: 3000000), ImporterId: "lrc.enhanced", Metadata: Meta()));
            Assert.True(created.Accepted);
            Assert.True(Registry.TryGet("client", created.Session!.ProjectSessionId, out Session));
            byte[] image = [1, 2, 3];
            MediaFileRevision media = new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)), image.Length, "image.png");
            string path = Path.Combine(_imageDirectory.Path, media.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, image);
            Session.PinnedInputDirectory = _imageDirectory.Path;
            Session.Workspace.Replace(Session.Workspace.Project with
            { Background = new BackgroundSource { Kind = kind, Path = path, Media = media } });
        }

        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<BackendOperationResult<JobResponse>> StartOperation() => Registry.StartExportAsync("client", SessionId, new("0", IncludeAudio: false, Metadata: Meta()));
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
