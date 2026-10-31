using System.Threading.Channels;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendValidationJobTests
{
    [Fact]
    public async Task 正式验证普通执行失败仍保原通用任务编号()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse started = await f.Start("a", id);
        (await f.Executor.Next()).Release.SetException(new IOException("controlled validation failure"));
        JobResponse failed = await f.Terminal("a", started.JobId);
        Assert.Equal("failed", failed.State); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Equal("TaskExecutionFailed", failed.FailureCode); Assert.Null(failed.Error.FailureCode);
        Assert.Null(failed.ValidationResult); Assert.Equal(1, f.Executor.Drained);
    }

    [Fact]
    public async Task 同客户端排队不占其他客户端名额且取消排队不执行()
    {
        await using Fixture f = await Fixture.Create();
        string a = await f.Project("a"); string b = await f.Project("b");
        JobResponse first = await f.Start("a", a); Call runningA = await f.Executor.Next();
        JobResponse queued = await f.Start("a", a);
        Assert.Equal("queued", f.Registry.ReadJob("a", queued.JobId).Response!.State);
        JobResponse second = await f.Start("b", b); Call runningB = await f.Executor.Next();
        Assert.Equal("running", f.Registry.ReadJob("b", second.JobId).Response!.State);
        f.Registry.CancelJob("a", queued.JobId);
        Assert.Equal("cancelled", (await f.Terminal("a", queued.JobId)).State);
        runningA.Release.SetResult(Pass()); runningB.Release.SetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal("a", first.JobId)).State);
        Assert.Equal("succeeded", (await f.Terminal("b", second.JobId)).State);
        Assert.Equal(2, f.Executor.Calls);
    }

    [Fact]
    public async Task 回执重复只登记一次修订与计划预算拒绝不执行且排队独立限额()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        ValidationJobRequest request = new("0", 3, f.Meta());
        JobResponse first = (await f.Registry.StartValidationAsync("a", id, request)).Response!;
        Call call = await f.Executor.Next();
        Assert.Equal(first.JobId, (await f.Registry.StartValidationAsync("a", id, request)).Response!.JobId);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.StartValidationAsync("a", id, new("1", 3, f.Meta()))).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.StartValidationAsync("a", id, new("0", 601, f.Meta()))).Error!.Code);
        List<JobResponse> queued = [];
        for (int i = 0; i < 4; i++) queued.Add(await f.Start("a", id));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, (await f.Registry.StartValidationAsync("a", id, new("0", 3, f.Meta()))).Error!.Code);
        foreach (JobResponse job in queued) f.Registry.CancelJob("a", job.JobId);
        call.Release.SetResult(Pass()); await f.Terminal("a", first.JobId);
        Assert.Equal(1, f.Executor.Calls);
    }

    [Fact]
    public async Task 未通过报告仍原子登记失败终态完整来源诊断分页与哈希损坏拒绝()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse job = await f.Start("a", id); Call call = await f.Executor.Next();
        BackendDiagnosticDto diagnostic = new("actual", "error", "actual failure", null, new(Guid.NewGuid(), "1", "runtime", "", "render", "0", null, null), "runtime");
        call.Release.SetResult(new(false, 1, 0, [new(false, "0", "actual", "actual failure", true)], [new(false, diagnostic)]));
        JobResponse terminal = await f.Terminal("a", job.JobId);
        Assert.Equal("failed", terminal.State); Assert.Equal("ValidationFailed", terminal.FailureCode);
        Assert.NotNull(terminal.ValidationResult); Assert.False(terminal.ValidationResult.Passed);
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.ReadValidationResult("b", job.JobId).Error!.Code);
        byte[] bytes = f.Registry.ReadValidationResult("a", job.JobId).Response!;
        Assert.Equal(terminal.ValidationResult.Report.Sha256, WorkerProtocol.ComputeSha256(bytes));
        Assert.Equal("actual", Assert.Single(f.Registry.ReadValidationDiagnostics("a", job.JobId, null, 1).Response!.Diagnostics).Diagnostic.Code);
        Assert.Equal(BackendErrorCode.DiagnosticCursorExpired, f.Registry.ReadValidationDiagnostics("a", job.JobId, "foreign", 1).Error!.Code);
        string path = Path.Combine(f.State, "validation-reports", terminal.ValidationResult.Report.ReportId + ".json");
        byte[] damaged = [.. bytes]; damaged[0] ^= 1; await File.WriteAllBytesAsync(path, damaged);
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.ReadValidationResult("a", job.JobId).Error!.Code);
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(bytes, f.Registry.ReadValidationResult("a", job.JobId).Response);
    }

    [Fact]
    public async Task 执行超时是失败明确取消才取消且终态前执行器已经排水()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse timeout = await f.Start("a", id); Call first = await f.Executor.Next();
        first.Release.SetException(new RenderSessionFailureException("WorkerTaskTimedOut", "deadline"));
        JobResponse failed = await f.Terminal("a", timeout.JobId);
        Assert.Equal("failed", failed.State); Assert.Equal("WorkerTaskTimedOut", failed.FailureCode);
        Assert.Null(failed.ValidationResult); Assert.Equal(1, f.Executor.Drained);
        JobResponse cancelled = await f.Start("a", id); await f.Executor.Next();
        f.Registry.CancelJob("a", cancelled.JobId);
        Assert.Equal("cancelled", (await f.Terminal("a", cancelled.JobId)).State);
        Assert.Equal(2, f.Executor.Drained);
    }

    [Fact]
    public async Task 关闭默认拒绝已登记任务cancelAndWait排水且重启只读旧报告不复活live权限()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse succeeded = await f.Start("a", id); (await f.Executor.Next()).Release.SetResult(Pass());
        JobResponse terminal = await f.Terminal("a", succeeded.JobId);
        JobResponse active = await f.Start("a", id); await f.Executor.Next();
        JobResponse queued = await f.Start("a", id);
        Assert.Equal(BackendErrorCode.ActiveJobs, (await f.Registry.StartCloseAsync("a", id, new("0", "discard", Metadata: f.Meta()))).Error!.Code);
        JobResponse close = (await f.Registry.StartCloseAsync("a", id, new("0", "discard", ActiveJobs: "cancelAndWait", Metadata: f.Meta()))).Response!;
        Assert.Equal("succeeded", (await f.Terminal("a", close.JobId)).State);
        Assert.Equal("cancelled", (await f.Terminal("a", active.JobId)).State);
        Assert.Equal("cancelled", (await f.Terminal("a", queued.JobId)).State);
        Assert.Equal(2, f.Executor.Calls);
        Assert.NotNull(f.Registry.ReadValidationResult("a", succeeded.JobId).Response);
        await f.Restart();
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.ReadValidationResult("a", succeeded.JobId).Error!.Code);
        Assert.Equal(terminal.ValidationResult!.Report.Sha256, WorkerProtocol.ComputeSha256(f.Registry.ReadRecoveredValidationResult("new-bootstrap-client", succeeded.JobId).Response!));
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.ReadRecoveredValidationResult("new-bootstrap-client", active.JobId).Error!.Code);
    }

    [Fact]
    public async Task 报告发布配额失败不登记假结果且终态任务不持有固定快照()
    {
        await using Fixture quota = await Fixture.Create(new() { MaxReportStorageBytes = 1 });
        string id = await quota.Project("a"); JobResponse job = await quota.Start("a", id);
        (await quota.Executor.Next()).Release.SetResult(Pass());
        JobResponse failed = await quota.Terminal("a", job.JobId);
        Assert.Equal("failed", failed.State); Assert.Equal("ValidationReportStorageQuotaExceeded", failed.FailureCode);
        Assert.Null(failed.ValidationResult);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(quota.State, "validation-reports")));
        await using Fixture normal = await Fixture.Create();
        string project = await normal.Project("a"); JobResponse success = await normal.Start("a", project);
        (await normal.Executor.Next()).Release.SetResult(Pass()); await normal.Terminal("a", success.JobId);
        // The retained ledger entry must retain metadata only, not the completed async input graph.
        for (int i = 0; i < 5; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(20); }
        Assert.False(normal.Executor.Snapshot!.TryGetTarget(out _));
        Assert.Equal("succeeded", normal.Registry.ReadJob("a", success.JobId).Response!.State);
    }

    [Fact]
    public async Task 宿主关闭主动取消验证并等待执行器排水()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        await f.Start("a", id); await f.Executor.Next(); await f.Start("a", id);
        await f.Registry.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, f.Executor.Drained); Assert.Equal(1, f.Executor.Calls);
    }

    private static ValidationReportDto Pass() => new(true, 3, 3, [], []);
    private sealed record Call(TaskCompletionSource<ValidationReportDto> Release);
    private sealed class Executor : IBackendValidationExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal int Calls; internal int Drained;
        internal WeakReference<ProjectSnapshot>? Snapshot;
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        public async Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Snapshot = new(snapshot); Interlocked.Increment(ref Calls); TaskCompletionSource<ValidationReportDto> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Writer.TryWrite(new(release));
            try { return await release.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Increment(ref Drained); }
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string State => _directory.FilePath("state");
        internal readonly Executor Executor = new();
        internal ProjectSessionRegistry Registry = null!; private ResourceStore _resources = null!; private string _host = "host-one";
        internal static Task<Fixture> Create(BackendValidationOptions? options = null) { Fixture f = new(); f.CreateRegistry(options); return Task.FromResult(f); }
        private void CreateRegistry(BackendValidationOptions? options = null)
        {
            _resources = new(new() { ResourceRoot = _directory.FilePath("uploads") });
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform sample = new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            Registry = new(_host, _resources, new((_, _) => Task.FromResult<LoadedWaveform?>(sample)));
            Registry.ConfigurePersistence(new() { StateRoot = State }); Registry.ConfigureValidationTasks(Executor, options);
        }
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, DateTimeOffset.UtcNow.AddMinutes(1));
        internal async Task<string> Project(string client)
        {
            StoredAsset audio = await _resources.UploadAsync(client, "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            StoredAsset lyric = await _resources.UploadAsync(client, "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
            var project = await Registry.CreateAsync(client, new("validation", audio.AssetId, lyric.AssetId, new(640, 360, OutputDurationUs: 3000000), Metadata: Meta()));
            Assert.True(project.Accepted); return project.Session!.ProjectSessionId;
        }
        internal async Task<JobResponse> Start(string client, string project) => (await Registry.StartValidationAsync(client, project, new("0", 3, Meta()))).Response!;
        internal async Task<JobResponse> Terminal(string client, string id)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            while (true) { JobResponse job = Registry.ReadJob(client, id).Response!; if (job.State is "succeeded" or "failed" or "cancelled" or "unknown") return job; await Task.Delay(10, timeout.Token); }
        }
        internal async Task Restart() { await Registry.DisposeAsync(); _host = "host-two"; CreateRegistry(); }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
