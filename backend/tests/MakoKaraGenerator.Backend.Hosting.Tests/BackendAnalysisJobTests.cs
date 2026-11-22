using System.Reflection;
using System.Threading.Channels;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAnalysisJobTests
{
    [Fact]
    public async Task 独立分析普通执行失败仍保原通用任务编号()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project("a");
        JobResponse started = await f.Start("a", project);
        (await f.Executor.Next()).Release.SetException(new IOException("controlled analysis failure"));
        JobResponse failed = await f.Terminal("a", started.JobId);
        Assert.Equal("failed", failed.State); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Equal("TaskExecutionFailed", failed.FailureCode); Assert.Null(failed.Error.FailureCode);
        Assert.Null(failed.AnalysisResult); Assert.Equal(1, f.Executor.Drained);
    }

    [Fact]
    public async Task 精确修订与音频身份一次回执完整结果只读且不应用最新工程()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project("a");
        AnalysisJobRequest request = new("0", f.Meta());
        JobResponse job = (await f.Registry.StartAnalysisAsync("a", project.ProjectSessionId, request)).Response!;
        Call call = await f.Executor.Next();
        Assert.Equal(job.JobId, (await f.Registry.StartAnalysisAsync("a", project.ProjectSessionId, request)).Response!.JobId);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.StartAnalysisAsync("a", project.ProjectSessionId, new("1", f.Meta()))).Error!.Code);
        Assert.Null(job.Input); Assert.Null(job.Progress); Assert.NotNull(job.AnalysisInput);
        Assert.Equal(project.AudioAssetId, job.AnalysisInput.Audio.AssetId); Assert.Equal(call.Grant.Sha256, job.AnalysisInput.Audio.Sha256);
        call.Release.SetResult(Canonical(call.Grant.Sha256)); JobResponse terminal = await f.Terminal("a", job.JobId);
        Assert.Equal("succeeded", terminal.State); Assert.Equal("384", terminal.AnalysisResult!.SampleCount);
        Assert.Equal("completed", terminal.AnalysisProgress!.Stage); Assert.Null(terminal.Progress);
        Assert.Equal("0", (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.ProjectRevision);
        Assert.Null(Workspace(project).Project.AudioAnalysis);
        using BackendAnalysisReadLease read = f.Registry.AcquireAnalysisRead("a", job.JobId).Response!;
        Assert.Equal(Canonical(call.Grant.Sha256), await Bytes(read.Stream));
        Assert.Equal(terminal.AnalysisResult.ContentHash, terminal.AnalysisResult.Report.Sha256);
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.AcquireAnalysisRead("b", job.JobId).Error!.Code);
        Assert.Equal(1, f.Executor.Calls); Assert.Equal(1, f.Executor.Drained);
    }
    [Fact]
    public async Task 已有分析的非静态工程可运行音频任务且结果不替换旧分析()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project("a");
        ProjectWorkspace workspace = Workspace(project);
        AudioAnalysisRevision old = new(new string('a', 64), new string('b', 64), 1, [new(0, .3, .2, null, 0)]);
        workspace.Replace(workspace.Project with { AudioAnalysis = ProjectAudioAnalysis.Available(old) });
        JobResponse job = await f.Start("a", project); Call call = await f.Executor.Next();
        call.Release.SetResult(Canonical(call.Grant.Sha256));
        Assert.Equal("succeeded", (await f.Terminal("a", job.JobId)).State);
        Assert.Same(old, workspace.Project.AudioAnalysis!.Data);
    }
    [Fact]
    public async Task 与正式验证共用槽位排队取消不执行且宿主关闭排水()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project("a");
        f.Registry.ConfigureValidationTasks(f.Validator);
        JobResponse active = (await f.Registry.StartValidationAsync("a", project.ProjectSessionId, new("0", 1, f.Meta()))).Response!;
        await f.Validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        JobResponse queued = await f.Start("a", project); Assert.Equal("queued", queued.State);
        f.Registry.CancelJob("a", queued.JobId); Assert.Equal("cancelled", (await f.Terminal("a", queued.JobId)).State);
        Assert.Equal(0, f.Executor.Calls);
        JobResponse again = await f.Start("a", project);
        await f.Registry.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("cancelled", (await f.Terminal("a", active.JobId)).State);
        Assert.Equal("cancelled", (await f.Terminal("a", again.JobId)).State);
        Assert.Equal(0, f.Executor.Calls); Assert.Equal(1, f.Validator.Drained);
    }
    [Fact]
    public async Task deadline与坏完整结果失败但显式取消是取消且没有伪造结果()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project("a");
        JobResponse first = await f.Start("a", project); (await f.Executor.Next()).Release.SetException(new RenderSessionFailureException("WorkerTaskTimedOut", "controlled"));
        JobResponse failed = await f.Terminal("a", first.JobId); Assert.Equal("failed", failed.State); Assert.Equal("WorkerTaskTimedOut", failed.FailureCode); Assert.Null(failed.AnalysisResult);
        JobResponse bad = await f.Start("a", project); (await f.Executor.Next()).Release.SetResult("{}"u8.ToArray());
        JobResponse invalid = await f.Terminal("a", bad.JobId); Assert.Equal("WorkerAnalysisResultInvalid", invalid.FailureCode); Assert.Null(invalid.AnalysisResult);
        JobResponse cancel = await f.Start("a", project); await f.Executor.Next(); f.Registry.CancelJob("a", cancel.JobId);
        Assert.Equal("cancelled", (await f.Terminal("a", cancel.JobId)).State); Assert.Equal(3, f.Executor.Drained);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.State, "analysis-results")));
    }
    [Fact]
    public async Task close策略与重启仅登记旧完整结果授权读取不重跑()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project("a");
        JobResponse done = await f.Start("a", project); Call call = await f.Executor.Next(); byte[] canonical = Canonical(call.Grant.Sha256); call.Release.SetResult(canonical); await f.Terminal("a", done.JobId);
        JobResponse active = await f.Start("a", project); await f.Executor.Next();
        Assert.Equal(BackendErrorCode.ActiveJobs, (await f.Registry.StartCloseAsync("a", project.ProjectSessionId, new("0", "discard", Metadata: f.Meta()))).Error!.Code);
        JobResponse close = (await f.Registry.StartCloseAsync("a", project.ProjectSessionId, new("0", "discard", ActiveJobs: "cancelAndWait", Metadata: f.Meta()))).Response!;
        Assert.Equal("succeeded", (await f.Terminal("a", close.JobId)).State); Assert.Equal("cancelled", (await f.Terminal("a", active.JobId)).State);
        using (BackendAnalysisReadLease live = f.Registry.AcquireAnalysisRead("a", done.JobId).Response!) Assert.Equal(canonical, await Bytes(live.Stream));
        await f.Restart();
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.AcquireAnalysisRead("a", done.JobId).Error!.Code);
        using BackendAnalysisReadLease recovered = f.Registry.AcquireAnalysisRead("new-bootstrap-client", done.JobId, true).Response!;
        Assert.Equal(canonical, await Bytes(recovered.Stream)); Assert.Equal(2, f.Executor.Calls);
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.AcquireAnalysisRead("new-bootstrap-client", active.JobId, true).Error!.Code);
    }
    [Fact]
    public async Task 结果预留包含排队与过期仍读取的文件直到租约释放()
    {
        int limit = Canonical(new string('a', 64)).Length;
        await using Fixture f = new(new() { MaxResultBytes = limit, MaxResultStorageBytes = limit }); ProjectSession project = await f.Project("a");
        JobResponse job = await f.Start("a", project); Call call = await f.Executor.Next();
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, (await f.Registry.StartAnalysisAsync("a", project.ProjectSessionId, new("0", f.Meta()))).Error!.Code);
        call.Release.SetResult(Canonical(call.Grant.Sha256)); JobResponse completed = await f.Terminal("a", job.JobId);
        BackendAnalysisReadLease lease = f.Registry.AcquireAnalysisRead("a", job.JobId).Response!;
        f.Clock.Advance(3601);
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.AcquireAnalysisRead("a", job.JobId).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, (await f.Registry.StartAnalysisAsync("a", project.ProjectSessionId, new("0", f.Meta()))).Error!.Code);
        Assert.Equal(completed.AnalysisResult!.Report.SizeBytes, lease.Stream.Length); Assert.Single(Directory.EnumerateFiles(Path.Combine(f.State, "analysis-results")));
        lease.Dispose(); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.State, "analysis-results")));
        JobResponse next = await f.Start("a", project); Call nextCall = await f.Executor.Next(); nextCall.Release.SetResult(Canonical(nextCall.Grant.Sha256));
        Assert.Equal("succeeded", (await f.Terminal("a", next.JobId)).State);
    }
    [Fact]
    public async Task 来源格式和精确时长预算接受前拒绝且错误结果不能破坏旧报告()
    {
        await using Fixture f = new(); ProjectSession invalid = await f.Project("a", [1, 2, 3]);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.StartAnalysisAsync("a", invalid.ProjectSessionId, new("0", f.Meta()))).Error!.Code);
        ProjectSession tooLong = await f.Project("a", Wave(2_400_001));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.StartAnalysisAsync("a", tooLong.ProjectSessionId, new("0", f.Meta()))).Error!.Code);
        Assert.Equal(0, f.Executor.Calls);
        ProjectSession valid = await f.Project("a"); JobResponse job = await f.Start("a", valid); Call call = await f.Executor.Next(); byte[] good = Canonical(call.Grant.Sha256); call.Release.SetResult(good); JobResponse terminal = await f.Terminal("a", job.JobId);
        string path = Path.Combine(f.State, "analysis-results", terminal.AnalysisResult!.Report.ReportId + ".json"); byte[] bad = [.. good]; bad[0] ^= 1; await File.WriteAllBytesAsync(path, bad);
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.AcquireAnalysisRead("a", job.JobId).Error!.Code);
        await File.WriteAllBytesAsync(path, good); using BackendAnalysisReadLease restored = f.Registry.AcquireAnalysisRead("a", job.JobId).Response!; Assert.Equal(good, await Bytes(restored.Stream));
    }
    private static ProjectWorkspace Workspace(ProjectSession session) => session.Workspace;
    private static byte[] Canonical(string source) => AudioAnalysisJson.Encode(new(source, new string('b', 64), 384, [new(0, .1234567890123456, .5, .25, .8)]));
    private static async Task<byte[]> Bytes(Stream stream) { using MemoryStream output = new(); await stream.CopyToAsync(output); return output.ToArray(); }
    private static byte[] Wave(int samples = 64)
    {
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2); writer.Write(new byte[samples * 2]); return stream.ToArray();
    }
    private sealed record Call(BackendAudioAnalysisGrant Grant, TaskCompletionSource<byte[]> Release);
    private sealed class Executor : IBackendAnalysisExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>(); internal int Calls; internal int Drained;
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        public void ValidateInput(BackendAudioAnalysisGrant grant) { Assert.True(Path.IsPathFullyQualified(grant.Path)); }
        public Task<BackendAnalysisCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackendAnalysisCapabilities(true, true));
        public async Task<byte[]> ExecuteAsync(BackendAudioAnalysisGrant grant, IProgress<BackendAnalysisProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); TaskCompletionSource<byte[]> release = new(TaskCreationOptions.RunContinuationsAsynchronously); _calls.Writer.TryWrite(new(grant, release));
            progress?.Report(new("decoding", "384", null, "8000"));
            try { return await release.Task.WaitAsync(cancellationToken); } finally { Interlocked.Increment(ref Drained); }
        }
    }
    private sealed class Validator : IBackendValidationExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); internal int Drained;
        public async Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, cancellationToken); return new(true, 1, 1, [], []); } finally { Drained++; } }
    }
    private sealed class ClockTime : TimeProvider
    { private DateTimeOffset _now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => _now; internal void Advance(int seconds) => _now = _now.AddSeconds(seconds); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new(); internal string State => _directory.FilePath("state"); internal readonly Executor Executor = new(); internal readonly Validator Validator = new(); internal readonly ClockTime Clock = new();
        internal ProjectSessionRegistry Registry = null!; private ResourceStore _resources = null!; private string _host = "host-one"; private readonly BackendAnalysisOptions? _options; private readonly BackendAnalysisCapabilities _capabilities;
        internal Fixture(BackendAnalysisOptions? options = null, BackendAnalysisCapabilities? capabilities = null) { _options = options; _capabilities = capabilities ?? new(true, true); CreateRegistry(); }
        private void CreateRegistry()
        {
            _resources = new(new() { ResourceRoot = _directory.FilePath("uploads") }, Clock); WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform sample = new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000)); Registry = new(_host, _resources, new((_, _) => Task.FromResult<LoadedWaveform?>(sample)), Clock);
            Registry.ConfigurePersistence(new() { StateRoot = State }); Registry.ConfigureAnalysisTasks(Executor, _capabilities, _options);
        }
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, Clock.GetUtcNow().AddMinutes(1));
        internal async Task<ProjectSession> Project(string client, byte[]? audioBytes = null)
        {
            StoredAsset audio = await _resources.UploadAsync(client, "song.wav", "audio/wav", new MemoryStream(audioBytes ?? Wave()));
            StoredAsset lyric = await _resources.UploadAsync(client, "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
            var result = await Registry.CreateAsync(client, new("analysis", audio.AssetId, lyric.AssetId, new(640, 360, OutputDurationUs: 3000000), Metadata: Meta())); Assert.True(result.Accepted); return result.Session!;
        }
        internal async Task<JobResponse> Start(string client, ProjectSession project)
        { var result = await Registry.StartAnalysisAsync(client, project.ProjectSessionId, new("0", Meta())); Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }
        internal async Task<JobResponse> Terminal(string client, string id)
        { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (true) { JobResponse job = Registry.ReadJob(client, id).Response!; if (job.State is "succeeded" or "failed" or "cancelled" or "unknown") return job; await Task.Delay(10, timeout.Token); } }
        internal async Task Restart() { await Registry.DisposeAsync(); _host = "host-two"; CreateRegistry(); }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
