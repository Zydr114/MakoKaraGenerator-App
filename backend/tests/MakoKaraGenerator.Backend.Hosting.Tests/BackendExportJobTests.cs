using System.Buffers.Binary;
using System.Globalization;
using System.Threading.Channels;
using System.Text.Json;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendExportJobTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task 独立导出普通执行失败仍保原通用任务编号()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse started = await f.Start(id);
        (await f.Exporter.Next()).Release.SetException(new IOException("controlled export failure"));
        JobResponse failed = await f.Terminal("a", started.JobId);
        Assert.Equal("failed", failed.State); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Equal("TaskExecutionFailed", failed.FailureCode); Assert.Null(failed.Error.FailureCode);
        Assert.Null(failed.ExportResult); Assert.Null(failed.FailureReportRef); Assert.Equal(1, f.Exporter.Drained);
    }

    [Fact]
    public async Task 部署媒体限额支持小于旧固定值和空值但拒绝非正数()
    {
        await using Fixture fixture = await Fixture.Create();
        fixture.ConfigureMediaLimit(1024);
        fixture.ConfigureMediaLimit(null);
        Assert.Throws<WorkerProtocolException>(() => fixture.ConfigureMediaLimit(0));
        Assert.Throws<WorkerProtocolException>(() => fixture.ConfigureMediaLimit(-1));
    }

    [Fact]
    public async Task 固定来源默认音频授权统一回执且纯视频不给任何音频授予()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        ExportJobRequest request = new("0", Metadata: f.Meta());
        JobResponse first = (await f.Registry.StartExportAsync("a", id, request)).Response!;
        Call call = await f.Exporter.Next(); Assert.NotNull(call.Input.Audio);
        Assert.Equal(first.ExportInput!.Audio!.Sha256, call.Input.Audio!.Sha256);
        Assert.Equal(first.JobId, (await f.Registry.StartExportAsync("a", id, request)).Response!.JobId);
        call.Release.TrySetResult(); JobResponse terminal = await f.Terminal("a", first.JobId);
        Assert.Equal("succeeded", terminal.State);
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.ReadExportResult("b", first.JobId).Error!.Code);
        using BackendExportReadLease read = f.Registry.AcquireExportRead("a", first.JobId).Response!;
        Assert.True(await read.VerifyHashAsync()); Assert.Equal(Executor.Bytes, await Read(read.Stream));
        JobResponse video = (await f.Registry.StartExportAsync("a", id, new("0", IncludeAudio: false, Metadata: f.Meta()))).Response!;
        Call pure = await f.Exporter.Next(); Assert.Null(pure.Input.Audio); Assert.False(pure.Input.IncludeAudio);
        pure.Release.TrySetResult(); Assert.Equal("succeeded", (await f.Terminal("a", video.JobId)).State);
        Assert.Equal(2, f.Exporter.Calls);
    }

    [Fact]
    public async Task 实际音频不足或畸形与超计划输入在接受前拒绝并释放全部预留()
    {
        await using Fixture malformed = await Fixture.Create(); string bad = await malformed.Project("a", validWave: false);
        Assert.Equal(BackendErrorCode.ExportAudioUnsupported, (await malformed.Registry.StartExportAsync("a", bad, new("0", Metadata: malformed.Meta()))).Error!.Code);
        Assert.Equal(0, malformed.Exporter.Calls);
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.StartExportAsync("a", id, new("1", Metadata: f.Meta()))).Error!.Code);
        Assert.Equal(BackendErrorCode.CommandNotSupported, (await f.Registry.StartExportAsync("a", id, new("0", "webm", Metadata: f.Meta()))).Error!.Code);
        f.Exporter.RejectInput = true;
        Assert.Equal(BackendErrorCode.ExportInputRejected, (await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Error!.Code);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.State, "jobs")));
        f.Exporter.RejectInput = false;
        JobResponse accepted = (await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Response!;
        (await f.Exporter.Next()).Release.TrySetResult(); Assert.Equal("succeeded", (await f.Terminal("a", accepted.JobId)).State);
    }

    [Fact]
    public async Task 导出和验证共用客户端执行许可队列取消不开始且关闭批取消全部排水()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse validation = (await f.Registry.StartValidationAsync("a", id, new("0", 3, f.Meta()))).Response!;
        await f.Validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        JobResponse queued = (await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Response!;
        Assert.Equal("queued", f.Registry.ReadJob("a", queued.JobId).Response!.State); Assert.Equal(0, f.Exporter.Calls);
        f.Registry.CancelJob("a", queued.JobId); Assert.Equal("cancelled", (await f.Terminal("a", queued.JobId)).State);
        JobResponse queuedAgain = (await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Response!;
        JobResponse close = (await f.Registry.StartCloseAsync("a", id, new("0", "discard", ActiveJobs: "cancelAndWait", Metadata: f.Meta()))).Response!;
        Assert.Equal("succeeded", (await f.Terminal("a", close.JobId)).State);
        Assert.Equal("cancelled", (await f.Terminal("a", validation.JobId)).State);
        Assert.Equal("cancelled", (await f.Terminal("a", queuedAgain.JobId)).State); Assert.Equal(0, f.Exporter.Calls);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
    }

    [Fact]
    public async Task 截短实际帧报告或SHA不符拒绝原子发布且已存在产物保持()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        JobResponse first = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult(); JobResponse success = await f.Terminal("a", first.JobId);
        byte[] original = f.Registry.ReadExportResult("a", first.JobId).Response!;
        f.Exporter.BadFrames = true; JobResponse broken = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult();
        Assert.Equal("failed", (await f.Terminal("a", broken.JobId)).State);
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.ReadExportResult("a", broken.JobId).Error!.Code);
        Assert.Equal(original, f.Registry.ReadExportResult("a", first.JobId).Response);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts"), "artifact-*"));
        string media = Path.Combine(f.State, "export-artifacts", success.ExportResult!.Artifact.ArtifactId, "media.mp4");
        byte[] bytes = await File.ReadAllBytesAsync(media); byte[] damaged = [.. bytes]; damaged[0] ^= 1; await File.WriteAllBytesAsync(media, damaged);
        using BackendExportReadLease read = f.Registry.AcquireExportRead("a", first.JobId).Response!; Assert.False(await read.VerifyHashAsync());
    }

    [Fact]
    public async Task 过期仍在读取的包占预算直到租约释放且重启只读已登记旧产物()
    {
        await using Fixture f = await Fixture.Create(new() { MaxReportBytes = 2048, MaxMediaBytes = Executor.Bytes.Length,
            MaxArtifactStorageBytes = Executor.Bytes.Length + 2048 });
        string id = await f.Project("a"); JobResponse first = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult();
        JobResponse completed = await f.Terminal("a", first.JobId);
        BackendExportReadLease active = f.Registry.AcquireExportRead("a", first.JobId).Response!;
        f.Clock.Advance(3601);
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.AcquireExportRead("a", first.JobId).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, (await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Error!.Code);
        Assert.True(await active.VerifyHashAsync()); active.Dispose();
        JobResponse next = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult(); await f.Terminal("a", next.JobId);
        byte[] report = f.Registry.ReadExportResult("a", next.JobId).Response!;
        await f.Restart();
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.ReadExportResult("a", next.JobId).Error!.Code);
        Assert.Equal(report, f.Registry.ReadExportResult("new-bootstrap", next.JobId, true).Response);
        using BackendExportReadLease recovered = f.Registry.AcquireExportRead("new-bootstrap", next.JobId, true).Response!;
        Assert.True(await recovered.VerifyHashAsync()); Assert.Equal(Executor.Bytes, await Read(recovered.Stream));
        Assert.NotNull(completed.ExportResult);
    }

    [Fact]
    public async Task 真实失败发生完整报告保来源且无媒体并支持明确重启读取()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        Guid epoch = Guid.NewGuid(); RuntimeDiagnostic diagnostic = new(new("actual-instance"), "actual-artifact", "render", 123,
            new(DiagnosticSeverity.Error, "ActualError", "actual failure", new("source.cs", 7, 11)));
        RuntimeDiagnosticSnapshot snapshot = RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 1, diagnostic), new(epoch, 2, diagnostic)]);
        f.Exporter.Failure = new("ActualFailure", "internal paths never become HTTP error", snapshot);
        JobResponse job = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult();
        JobResponse terminal = await f.Terminal("a", job.JobId);
        Assert.Equal("failed", terminal.State); Assert.Equal("ActualFailure", terminal.FailureCode);
        Assert.Null(terminal.ExportResult); Assert.NotNull(terminal.FailureReportRef); Assert.Null(terminal.Diagnostics);
        byte[] bytes = f.Registry.ReadExportResult("a", job.JobId).Response!;
        ExportFailureReportDto report = JsonSerializer.Deserialize<ExportFailureReportDto>(bytes, Json)!;
        Assert.Equal("exportFailure", report.Kind); Assert.Equal(terminal.Input, report.Source); Assert.Equal(terminal.ExportInput, report.Input);
        Assert.Equal(2, report.Diagnostics.Length); Assert.Equal(epoch, report.Diagnostics[0].Occurrence!.Epoch);
        Assert.Equal("2", report.Diagnostics[1].Occurrence!.Sequence); Assert.Equal(new BackendDiagnosticLocationDto("source.cs", 7, 11), report.Diagnostics[0].Occurrence!.Location);
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.AcquireExportRead("a", job.JobId).Error!.Code);
        ExportDiagnosticsPageDto page = f.Registry.ReadExportDiagnostics("a", job.JobId, null, 1).Response!;
        Assert.NotNull(page.NextCursor); Assert.Single(f.Registry.ReadExportDiagnostics("a", job.JobId, page.NextCursor, 1).Response!.Diagnostics);
        string bundle = Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts"), "artifact-*"));
        Assert.Equal("report.json", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(bundle))));
        await f.Restart(); Assert.Equal(bytes, f.Registry.ReadExportResult("new-bootstrap", job.JobId, true).Response);
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.AcquireExportRead("new-bootstrap", job.JobId, true).Error!.Code);
    }

    [Fact]
    public async Task 用户取消已赢闸门时实际失败不发布报告且原primary保留()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        RuntimeDiagnostic actual = new(new("actual-instance"), "artifact", "render", 0, new(DiagnosticSeverity.Error, "ActualError", "actual"));
        f.Exporter.Failure = new("ActualFailure", "controlled", RuntimeDiagnosticSnapshot.FromArray([actual]));
        f.Exporter.IgnoreCancellation = true;
        JobResponse job = await f.Start(id); Call call = await f.Exporter.Next();
        Assert.True(f.Registry.CancelJob("a", job.JobId).Accepted); call.Release.TrySetResult();
        JobResponse failed = await f.Terminal("a", job.JobId);
        Assert.Equal("failed", failed.State); Assert.True(failed.CancellationRequested); Assert.Equal("ActualFailure", failed.FailureCode);
        Assert.Null(failed.FailureReportRef); Assert.Null(failed.ExportResult);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts"), "artifact-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
    }

    [Theory]
    [InlineData(0)] [InlineData(513)]
    public async Task 无实际发生或超预算不伪造截断失败报告且原cause不被报告失败覆盖(int count)
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        Guid epoch = Guid.NewGuid(); RuntimeDiagnostic diagnostic = new(new("actual-instance"), "actual-artifact", "render", 123,
            new(DiagnosticSeverity.Error, "ActualError", "actual failure"));
        RuntimeDiagnosticSnapshot snapshot = count == 0 ? RuntimeDiagnosticSnapshot.Empty : RuntimeDiagnosticSnapshot.FromOccurrences(
            Enumerable.Range(1, count).Select(sequence => new RuntimeDiagnosticOccurrence(epoch, sequence, diagnostic)));
        RenderSessionFailureException primary = new("ActualFailure", "controlled primary", snapshot); f.Exporter.Failure = primary;
        JobResponse job = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult(); JobResponse terminal = await f.Terminal("a", job.JobId);
        Assert.Equal("failed", terminal.State); Assert.Equal("ActualFailure", terminal.FailureCode); Assert.Null(terminal.FailureReportRef); Assert.Null(terminal.ExportResult);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts"), "artifact-*"));
        if (count > 512) Assert.True(primary.Data.Contains("ExportFailureReportFailure"));
    }

    [Fact]
    public async Task 宿主关闭取消导出并等待执行器释放再清理候选()
    {
        await using Fixture f = await Fixture.Create(); string id = await f.Project("a");
        await f.Start(id); await f.Exporter.Next(); await f.Start(id);
        await f.Registry.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, f.Exporter.Calls); Assert.Equal(1, f.Exporter.Drained);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
    }

    private static async Task<byte[]> Read(Stream input) { using MemoryStream output = new(); await input.CopyToAsync(output); return output.ToArray(); }
    private sealed record Call(BackendExportExecutionInput Input, ProjectSnapshot Snapshot, TaskCompletionSource Release);
    private sealed class Executor : IBackendExportExecutor
    {
        // Transport seam fixture bytes; independent real Worker/media acceptance is a separate probe.
        internal static readonly byte[] Bytes = "controlled-export-media"u8.ToArray();
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal int Calls; internal int Drained; internal bool RejectInput; internal bool BadFrames; internal RenderSessionFailureException? Failure; internal bool IgnoreCancellation;
        internal string SourceAudioDuration = "3000000";
        public Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackendExportCapabilities(true, true, true));
        public void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input)
        { if (RejectInput) throw new WorkerProtocolException("controlled dynamic input budget"); }
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        public async Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Writer.TryWrite(new(input, snapshot, release));
            try
            {
                await release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
                await File.WriteAllBytesAsync(input.CandidatePath, Bytes, IgnoreCancellation ? CancellationToken.None : cancellationToken);
                if (Failure is not null) throw Failure;
                var c = snapshot.Composition; var plan = CompositionFramePlan.Create(c.OutputDurationUs!.Value, c.FrameRate);
                bool alpha = input.Format == "transparentProRes4444";
                string duration = plan.VideoDurationUs.ToString(CultureInfo.InvariantCulture);
                return new(input.Format, input.IncludeAudio, plan.TotalFrames, BadFrames ? plan.TotalFrames - 1 : plan.TotalFrames,
                    plan.TotalFrames, 0, 0, c.CanvasWidth, c.CanvasHeight, c.FrameRate.Numerator, c.FrameRate.Denominator,
                    duration, duration, input.IncludeAudio ? SourceAudioDuration : null, alpha ? "prores" : "h264", input.IncludeAudio ? alpha ? "pcm_s16le" : "aac" : null,
                    alpha ? "yuva444p12le" : "yuv420p", alpha, Bytes.Length, WorkerProtocol.ComputeSha256(Bytes), [], [], true,
                    input.IncludeAudio ? duration : null, true);
            }
            finally { Interlocked.Increment(ref Drained); }
        }
    }
    private sealed class Validator : IBackendValidationExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return new(true, framesPerOutput, framesPerOutput, [], []); }
    }
    private sealed class Time : TimeProvider
    { private DateTimeOffset _now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => _now; internal void Advance(int seconds) => _now = _now.AddSeconds(seconds); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new(); internal string State => _directory.FilePath("state");
        internal readonly Executor Exporter = new(); internal readonly Validator Validator = new(); internal readonly Time Clock = new();
        internal ProjectSessionRegistry Registry = null!; internal ResourceStore Resources = null!; private string _host = "host-one"; private BackendExportOptions? _options;
        private BackendExportCapabilities? _capabilities;
        internal static Task<Fixture> Create(BackendExportOptions? options = null, BackendExportCapabilities? capabilities = null)
        { Fixture f = new() { _options = options, _capabilities = capabilities }; f.CreateRegistry(); return Task.FromResult(f); }
        private void CreateRegistry()
        {
            Resources = new(new() { ResourceRoot = _directory.FilePath("uploads") }, Clock);
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]); LoadedWaveform sample = new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            Registry = new(_host, Resources, new((_, _) => Task.FromResult<LoadedWaveform?>(sample)), Clock);
            Registry.ConfigurePersistence(new() { StateRoot = State }); Registry.ConfigureValidationTasks(Validator);
            Registry.ConfigureExportTasks(Exporter, _capabilities ?? new(true, true, true), _options);
        }
        internal void ConfigureMediaLimit(long? maximum)
        {
            ResourceStore resources = new(new() { ResourceRoot = _directory.FilePath("invalid-uploads") }, Clock);
            using ProjectSessionRegistry registry = new("invalid-host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(null)), Clock);
            registry.ConfigurePersistence(new() { StateRoot = _directory.FilePath("invalid-state") });
            registry.ConfigureExportTasks(Exporter, new(true, true, true), new() { MaxMediaBytes = maximum });
        }
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, Clock.GetUtcNow().AddMinutes(1));
        internal async Task<string> Project(string client, bool validWave = true, byte[]? audioBytes = null)
        {
            byte[] wave = new byte[44 + 48000]; "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 8000); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 16000);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
            "data"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), 48000);
            StoredAsset audio = await Resources.UploadAsync(client, "song.wav", "audio/wav", new MemoryStream(audioBytes ?? (validWave ? wave : [1, 2, 3])));
            StoredAsset lyrics = await Resources.UploadAsync(client, "song.lrc", "text/plain", new MemoryStream("[00:00.00]<00:00.00>hello<00:03.00>\n"u8.ToArray()));
            var created = await Registry.CreateAsync(client, new("export", audio.AssetId, lyrics.AssetId, new(16, 16, OutputDurationUs: 3000000), ImporterId: "lrc.enhanced", Metadata: Meta()));
            Assert.True(created.Accepted);
            return created.Session!.ProjectSessionId;
        }
        internal async Task<JobResponse> Start(string id) { var result = await Registry.StartExportAsync("a", id, new("0", Metadata: Meta())); Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }
        internal async Task<JobResponse> Terminal(string client, string id)
        { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (true) { JobResponse job = Registry.ReadJob(client, id).Response!; if (job.State is "succeeded" or "failed" or "cancelled" or "unknown") return job; await Task.Delay(10, timeout.Token); } }
        internal async Task Restart() { await Registry.DisposeAsync(); _host = "host-two"; CreateRegistry(); }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
