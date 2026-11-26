using System.Collections;
using System.Reflection;
using System.Threading.Channels;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendRuntimeExportJobTests
{
    [Theory]
    [InlineData(BackgroundKind.Image)]
    [InlineData(BackgroundKind.Video)]
    public async Task 固定媒体导出复用原任务与报告并保持私有副本直到执行器排水(BackgroundKind kind)
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: kind);
        string expectedSha = ProjectRuntimeInputCodec.ComputeFixedSourceSha256(ProjectSnapshot.FromProject(f.Session.Workspace.Project));
        JobResponse started = await f.Start();
        Call call = await f.Executor.Next();
        Assert.Equal(ProjectRuntimeInputCodec.Profile, started.Input!.SourceProfile);
        Assert.Equal(expectedSha, started.Input.SourceSha256);
        Assert.Null(started.ExportInput!.TaskProfile);
        var source = ProjectRuntimeInputCodec.Decode(call.EncodedImage);
        Assert.Equal(kind, source.Grant!.Kind);
        Assert.NotEqual(f.Session.Workspace.Project.Background.Path, source.Grant.FilePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source.Grant.FilePath));
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.Equal(f.BaselineReferenceCount + 1, f.ReferenceCount);
        call.Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(started.JobId)).State);
        Assert.False(File.Exists(source.Grant.FilePath));
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        byte[] report = f.Registry.ReadExportResult("client", started.JobId).Response!;
        await f.Registry.DisposeAsync();
        f.Restart();
        Assert.Equal(report, f.Registry.ReadExportResult("local-bootstrap", started.JobId, true).Response);
        f.AssertScheduler(0, 0);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("close")]
    [InlineData("identity")]
    [InlineData("owner")]
    public async Task 固定媒体导出Writer准入复核工程变更且拒绝后释放全部租约(string mutation)
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: BackgroundKind.Video);
        await f.Session.Writer.WaitAsync();
        Task<BackendOperationResult<JobResponse>> pending = Task.Run(f.StartOperation);
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
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Accepted);
            Assert.Equal(mutation switch { "revision" => BackendErrorCode.RevisionConflict,
                "close" => BackendErrorCode.SessionClosing, _ => BackendErrorCode.SessionNotFound }, result.Error!.Code);
            Assert.Equal(0, f.Executor.Calls);
            Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
            Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
            Assert.Equal(0, f.ArtifactUsedBytes);
            f.AssertScheduler(0, 0);
        }
        finally
        {
            f.RestoreSessionIdentity(); SetOwner(f.Session, "client"); SetProjectRevision(f.Session, 0); f.Session.ClosePending = false;
        }
    }

    [Fact]
    public async Task 固定媒体导出异步准备不阻塞202且支持准备期取消与完成后导出()
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: BackgroundKind.Video);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Session.PrepareBarrierForTests = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        // 1. 准备被长时间阻塞时，StartExportAsync 必须立刻返回 202，不得卡住 HTTP 准入；且支持准备期取消。
        JobResponse cancelledJob = await f.StartOperation().WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t =>
        {
            Assert.True(t.Result.Accepted, t.Result.Error?.Message);
            return t.Result.Response!;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Executor.Calls);
        Assert.True(f.Registry.CancelJob("client", cancelledJob.JobId).Accepted);
        Assert.Equal("cancelled", (await f.Terminal(cancelledJob.JobId)).State);
        Assert.Equal(0, f.Executor.Calls);
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.Equal(0, f.ArtifactUsedBytes);
        f.AssertScheduler(0, 0);

        // 2. 再次发起导出：返回 202 后即使外部修订前进，释放准备闸门后仍按原固定快照完成导出。
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Session.PrepareBarrierForTests = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        JobResponse accepted = await f.StartOperation().WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t =>
        {
            Assert.True(t.Result.Accepted, t.Result.Error?.Message);
            return t.Result.Response!;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SetProjectRevision(f.Session, 1);
        release.TrySetResult();
        Call call = await f.Executor.Next();
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        call.Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(accepted.JobId)).State);
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        f.AssertScheduler(0, 0);
    }

    [Fact]
    public async Task 固定媒体导出排队取消和关闭须等待执行器排水才删除副本()
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: BackgroundKind.Video);
        f.Executor.HoldCancellationDrain = true;
        JobResponse started = await f.Start();
        Call active = await f.Executor.Next();
        string file = ProjectRuntimeInputCodec.Decode(active.EncodedImage).Grant!.FilePath;
        JobResponse queued = await f.Start();
        Assert.Equal("queued", queued.State);
        Assert.True(f.Registry.CancelJob("client", queued.JobId).Accepted);
        Assert.Equal("cancelled", (await f.Terminal(queued.JobId)).State);
        Assert.Equal(1, f.Executor.Calls);
        var close = await f.Registry.StartCloseAsync("client", f.SessionId,
            new("0", "discard", ActiveJobs: "cancelAndWait", Metadata: f.Meta()));
        Assert.True(close.Accepted);
        await active.CancellationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(file));
        Assert.Equal("running", f.Registry.ReadJob("client", started.JobId).Response!.State);
        active.Drain.TrySetResult();
        Assert.Equal("cancelled", (await f.Terminal(started.JobId)).State);
        Assert.Equal("succeeded", (await f.Terminal(close.Response!.JobId)).State);
        Assert.False(File.Exists(file));
        Assert.Equal(0, f.ReferenceCount);
        f.AssertScheduler(0, 0);
    }

    // M13 C1：导出入口（固定媒体运行输入、纯色通用）对实际使用 SDK v2 的工程，
    // 未选 GPU 时都在登记任务、取输入和调用执行器之前拒绝。
    [Theory]
    [InlineData("runtime")]
    [InlineData("generic")]
    public async Task SDK2工程未选GPU的导出在登记任务与取输入前拒绝(string path)
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: BackgroundKind.Image);
        Project project = SdkV2ProjectFixture.WithEnabledV2Instance(f.Session.Workspace.Project);
        if (path == "generic") project = project with { Background = new BackgroundSource() };
        f.Session.Workspace.Replace(project);
        BackendOperationResult<JobResponse> result = await f.StartOperation();
        Assert.False(result.Accepted);
        Assert.Equal(BackendErrorCode.CommandNotSupported, result.Error!.Code);
        Assert.Equal("renderBackend", result.Error.Field);
        Assert.Equal("PackageBackendUnsupported", result.Error.FailureCode);
        Assert.Equal(0, f.Executor.Calls);
        Assert.Empty((await f.Registry.ReadClientStateAsync("client")).Jobs!);
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.Equal(0, f.ArtifactUsedBytes);
        f.AssertScheduler(0, 0);
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetOwner(ProjectSession session, string owner) =>
        typeof(ProjectSession).GetField("<OwnerClientId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, owner);

    private static void SetProjectRevision(ProjectSession session, long value) =>
        session.ProjectRevision = value;

    private static ExportReportDto Pass() => new("opaqueMp4", false, 90, 90, 90, 0, 0, 640, 360, 30, 1,
        "3000000", "3000000", null, "h264", null, "yuv420p", false, Executor.Bytes.Length,
        WorkerProtocol.ComputeSha256(Executor.Bytes), [], [], true, null, true);

    private sealed record Call(TaskCompletionSource<ExportReportDto> Release, byte[] ImageBytes, byte[] EncodedImage,
        TaskCompletionSource CancellationReceived, TaskCompletionSource Drain, BackendExportExecutionInput Input);

    private sealed class Executor : IBackendExportExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal int Calls;
        internal int Drained;
        internal bool HoldCancellationDrain;
        internal static readonly byte[] Bytes = "controlled-image-export"u8.ToArray();
        internal bool RuntimeEnabled;
        public Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackendExportCapabilities(true, true, true));
        public void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input)
        {
            if (!RuntimeEnabled) throw new InvalidOperationException("图片不得进入通用输入校验。");
            Assert.Equal(ProjectRuntimeInputCodec.Profile, ProjectRuntimeInputCodec.Decode(input.RuntimeSource).SourceProfile);
        }
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!RuntimeEnabled) throw new InvalidOperationException("图片导出不得进入通用执行器。");
            var source = ProjectRuntimeInputCodec.Decode(input.RuntimeSource);
            Assert.True(File.Exists(source.Grant!.FilePath));
            Interlocked.Increment(ref Calls);
            TaskCompletionSource<ExportReportDto> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] bytes = input.RuntimeSource.ToArray();
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Writer.TryWrite(new(release, bytes, bytes, cancelled, drain, input));
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
        internal long ArtifactUsedBytes => (long)Field(Field(Registry, "_exportArtifacts")!, "_reservedBytes")!;
        internal void Restart() { _host = "host-two"; RegistryOptions(this, null); }
        internal int ReferenceCount { get { lock (ResourceGate) return ReadReferences().Values.Sum(); } }
        internal object ResourceGate => typeof(ResourceStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Resources)!;
        internal ResourceStore Resources = null!;
        private ProjectSession? _replacement;
        private string _host = "host-one";

        internal static async Task<Fixture> Create(BackendExportOptions? options = null, bool runtime = true, BackgroundKind kind = BackgroundKind.Image)
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
                    ProjectRuntimeInputCodec.Profile] : null), options);
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
