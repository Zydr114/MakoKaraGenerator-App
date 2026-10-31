using System.Threading.Channels;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorValidationTests
{
    [Fact]
    public async Task 作者验证固定完整输入身份并复用报告诊断及恢复读取()
    {
        await using Fixture f = await Fixture.Create();
        AuthorValidationJobRequest request = f.Request();
        BackendOperationResult<JobResponse> startResult = await f.Registry.StartAuthorValidationAsync("client", f.Draft.DraftId, request);
        Assert.True(startResult.Accepted, startResult.Error?.Code + ":" + startResult.Error?.Message + ":" + startResult.Error?.FailureCode);
        JobResponse started = startResult.Response!;
        Call call = await f.Executor.Next();
        Assert.Null(started.ProjectSessionId);
        Assert.Equal("authorDraft", started.Input!.SourceKind);
        Assert.Equal(ProjectSessionRegistry.AuthorValidationProfile, started.Input.SourceProfile);
        Assert.Equal(WorkerProtocol.ComputeSha256(call.Bytes), started.Input.SourceSha256);
        Assert.Equal(f.Draft.DraftId, started.AuthorValidationInput!.DraftId);
        Assert.Equal(f.Draft.DraftRevision, started.AuthorValidationInput.DraftRevision);
        Assert.Equal(TemplatePreviewLimits.SingleLineSampleId, started.AuthorValidationInput.SampleId);
        Assert.Equal("token", started.AuthorValidationInput.TargetKey);
        Assert.Equal(started.JobId, (await f.Registry.StartAuthorValidationAsync("client", f.Draft.DraftId, request)).Response!.JobId);
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.ReadJob("other", started.JobId).Error!.Code);
        Assert.True(File.Exists(call.MediaPath));
        call.Release.SetResult(ErrorReport());
        JobResponse terminal = await f.Terminal(started.JobId);
        Assert.Equal("failed", terminal.State);
        Assert.Equal("ValidationFailed", terminal.FailureCode);
        byte[] report = f.Registry.ReadValidationResult("client", started.JobId).Response!;
        Assert.Equal(WorkerValidationContract.EncodeReport(ErrorReport(), 360), report);
        ValidationDiagnosticsPageDto page = f.Registry.ReadValidationDiagnostics("client", started.JobId, null, 1).Response!;
        Assert.Equal(ErrorReport().Diagnostics, page.Diagnostics);
        Assert.Equal(terminal.ValidationResult!.Report.Sha256, page.ReportSha256);
        await Fixture.WaitReleased(call.MediaPath);
        await f.Registry.DisposeAsync();
        f.Registry = f.NewRegistry(new Executor());
        Assert.Equal(report, f.Registry.ReadRecoveredValidationResult("client", started.JobId).Response);
    }

    [Fact]
    public async Task 作者验证拒绝旧修订异主样本目标和不合法材质槽位()
    {
        await using Fixture f = await Fixture.Create();
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.StartAuthorValidationAsync("client", f.Draft.DraftId,
            f.Request() with { ExpectedDraftRevision = "0" })).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, (await f.Registry.StartAuthorValidationAsync("other", f.Draft.DraftId, f.Request())).Error!.Code);
        Assert.Equal("sampleId", (await f.Registry.StartAuthorValidationAsync("client", f.Draft.DraftId, f.Request() with { SampleId = "current-song" })).Error!.Field);
        Assert.Equal("targetKey", (await f.Registry.StartAuthorValidationAsync("client", f.Draft.DraftId, f.Request() with { TargetKey = "line" })).Error!.Field);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.StartAuthorValidationAsync("client", f.Draft.DraftId,
            f.Request() with { MaterialSlot = "stroke" })).Error!.Code);
        Assert.Equal(0, f.Executor.Calls);
    }

    [Fact]
    public async Task 作者验证排队取消不执行且已接收任务在删除草稿后仍使用固定闭包并等待取消排水()
    {
        await using Fixture f = await Fixture.Create();
        BackendOperationResult<JobResponse> firstResult = await f.Start();
        Assert.True(firstResult.Accepted, firstResult.Error?.Code + ":" + firstResult.Error?.Message + ":" + firstResult.Error?.FailureCode);
        JobResponse first = firstResult.Response!;
        Call running = await f.Executor.Next();
        JobResponse queued = (await f.Start()).Response!;
        Assert.Equal("queued", f.Registry.ReadJob("client", queued.JobId).Response!.State);
        Assert.True(f.Registry.CancelJob("client", queued.JobId).Accepted);
        Assert.Equal("cancelled", (await f.Terminal(queued.JobId)).State);
        Assert.Equal(1, f.Executor.Calls);
        Assert.True((await f.Registry.DeleteAuthorDraftAsync("client", f.Draft.DraftId,
            new(f.Draft.DraftRevision, f.Meta()))).Accepted);
        f.Executor.HoldDrain = true;
        Assert.True(f.Registry.CancelJob("client", first.JobId).Accepted);
        await running.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("running", f.Registry.ReadJob("client", first.JobId).Response!.State);
        Assert.True(File.Exists(running.MediaPath));
        running.Drain.TrySetResult();
        Assert.Equal("cancelled", (await f.Terminal(first.JobId)).State);
        await Fixture.WaitReleased(running.MediaPath);
    }

    private static ValidationReportDto ErrorReport() => new(false, 0, 0,
        [new(false, "0", "Actual", "actual failure", true)],
        [new(false, new("Actual", "error", "真实错误", null, new(new("0c95dc85-89bb-4536-908a-4f59347b49f6"), "1", "actual-instance", new('a', 64),
            "Evaluate", "0", new("src/effect.cs", 2, 3), new("pkg", "sha256:" + new string('b', 64), "main")), "runtime"))]);

    private sealed record Call(byte[] Bytes, string MediaPath, TaskCompletionSource<ValidationReportDto> Release,
        TaskCompletionSource Cancelled, TaskCompletionSource Drain);
    private sealed class Executor : IBackendValidationExecutor, IBackendRuntimeValidationExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal int Calls;
        internal bool HoldDrain;
        public bool RuntimeValidationAvailable => true;
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("author validation must use Runtime input");
        public async Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(360, framesPerOutput);
            Interlocked.Increment(ref Calls);
            var decoded = ProjectRuntimeInputCodec.Decode(sourceBytes);
            Call call = new(sourceBytes.ToArray(), decoded.Grant!.FilePath, new(TaskCreationOptions.RunContinuationsAsynchronously),
                new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            await _calls.Writer.WriteAsync(call, cancellationToken);
            try { return await call.Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                call.Cancelled.TrySetResult();
                if (HoldDrain) await call.Drain.Task.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                throw;
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ResourceStore Resources = null!;
        internal Executor Executor = new();
        internal AuthorDraftResponse Draft = null!;
        internal static async Task<Fixture> Create(IBackendValidationExecutor? executor = null)
        {
            Fixture f = new();
            string archive = f._directory.FilePath("demo.mkfx");
            await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
            f.Resources = new(new() { ResourceRoot = f._directory.FilePath("resources") });
            StoredAsset asset = await f.Resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", File.OpenRead(archive));
            f.Registry = f.NewRegistry(executor ?? f.Executor);
            var created = await f.Registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "author", f.Meta()));
            Assert.True(created.Accepted, created.Error?.Message);
            f.Draft = created.Response!;
            return f;
        }
        internal ProjectSessionRegistry NewRegistry(IBackendValidationExecutor executor)
        {
            ProjectSessionRegistry registry = new("author-validation-" + Guid.NewGuid().ToString("N"), Resources, NewProjectPreparationService.CreateDefault());
            registry.ConfigurePersistence(new() { StateRoot = _directory.FilePath("state") });
            registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            registry.ConfigureValidationTasks(executor, new() { MaxRunning = 1, MaxQueued = 2, MaxQueuedPerClient = 2 });
            return registry;
        }
        internal RequestMetadataDto Meta() => new("author-validation-" + Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Registry.ServerTimeUtc.AddMinutes(1));
        internal AuthorValidationJobRequest Request() => new(Draft.DraftRevision, TemplatePreviewLimits.SingleLineSampleId, "token", Metadata: Meta());
        internal Task<BackendOperationResult<JobResponse>> Start() => Registry.StartAuthorValidationAsync("client", Draft.DraftId, Request());
        internal async Task<JobResponse> Terminal(string id, int timeout = 10)
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(timeout));
            while (true)
            {
                JobResponse job = Registry.ReadJob("client", id).Response!;
                if (job.State is "succeeded" or "failed" or "cancelled") return job;
                await Task.Delay(20, stop.Token);
            }
        }
        internal static async Task WaitReleased(string path)
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
            while (File.Exists(path)) await Task.Delay(20, stop.Token);
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
