using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerValidationTests(ITestOutputHelper output)
{
    [Fact]
    public void 封包预算扣除header且输入SHA覆盖计划与固定快照()
    {
        Guid id = Guid.NewGuid(); byte[] snapshot = [1, 2, 3];
        byte[] payload = WorkerValidationContract.EncodeInput(id, 3, snapshot);
        WorkerValidationInput input = WorkerValidationContract.DecodeInput(payload);
        Assert.Equal(4194272, WorkerValidationContract.MaximumSnapshotBytes);
        Assert.Equal(id, input.TaskId); Assert.Equal(3, input.FramesPerOutput); Assert.Equal(snapshot, input.SnapshotBytes);
        string hash = WorkerProtocol.ComputeSha256(payload);
        payload[24] = 4;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(WorkerValidationContract.RequestKind, "task", hash, payload)));
        snapshot[0] = 7; Assert.Equal(1, input.SnapshotBytes[0]);
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.EncodeInput(id, 3, new byte[WorkerValidationContract.MaximumSnapshotBytes + 1]));
    }

    [Theory]
    [InlineData("header")] [InlineData("version")] [InlineData("reserved")] [InlineData("zero-id")]
    [InlineData("zero-plan")] [InlineData("overflow-plan")] [InlineData("short")]
    public void 畸形header和计划统一为协议拒绝(string mode)
    {
        byte[] bytes = WorkerValidationContract.EncodeInput(Guid.NewGuid(), 3, [1]);
        switch (mode)
        {
            case "header": bytes[0] = 0; break;
            case "version": bytes[4] = 9; break;
            case "reserved": bytes[31] = 1; break;
            case "zero-id": bytes.AsSpan(8, 16).Clear(); break;
            case "zero-plan": bytes.AsSpan(24, 4).Clear(); break;
            case "overflow-plan": bytes.AsSpan(24, 4).Fill(255); break;
            case "short": bytes = [1, 2]; break;
        }
        Assert.Equal("WorkerValidationInputRejected", Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.DecodeInput(bytes)).Code);
    }

    [Theory]
    [InlineData(0, 30, 1, 1_000_000L, 320, 180)]
    [InlineData(601, 30, 1, 60_000_000L, 320, 180)]
    [InlineData(3, 30, 1, 1L, 320, 180)]
    [InlineData(3, 30, 1, 1_000_000L, 1921, 1080)]
    [InlineData(3, 30, 1, 1_000_000L, 1920, 1081)]
    [InlineData(3, 0, 1, 1_000_000L, 320, 180)]
    public void 固定帧计划尺寸时长在执行前拒绝(int frames, int numerator, int denominator, long duration, int width, int height)
        => Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.ValidateGeometryAndTiming(width, height, duration, numerator, denominator, frames));

    [Fact]
    public void FullHD本地像素预算独立于IPC且最大分母不会溢出帧时间()
    {
        Assert.True(1920L * 1080 * 4 > WorkerProtocol.MaxPayloadBytes);
        WorkerValidationContract.ValidateGeometryAndTiming(1920, 1080, long.MaxValue, 1, int.MaxValue, 600);
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.ValidateGeometryAndTiming(320, 180, null, 30, 1, 3));
        var huge = new ValidationReportDto(false, 0, 0, [new(false, "0", "Big", new('x', WorkerValidationContract.MaximumReportBytes), true)], []);
        Assert.Equal("WorkerValidationReportBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.EncodeReport(huge, 3)).Code);
    }

    [Theory]
    [InlineData("counts")] [InlineData("fake-success")] [InlineData("fake-failure")]
    [InlineData("epoch")] [InlineData("sequence")] [InlineData("missing-source")] [InlineData("budget")] [InlineData("package")]
    public void 错结果与发生身份及完整报告预算拒绝(string mode)
    {
        ValidationReportDto report = ErrorReport();
        report = mode switch
        {
            "counts" => report with { OpaqueFrames = 4 },
            "fake-success" => report with { Succeeded = true, OpaqueFrames = 3, TransparentFrames = 3 },
            "fake-failure" => new(false, 0, 0, [], []),
            "epoch" => report with { Diagnostics = [report.Diagnostics[0] with { Diagnostic = report.Diagnostics[0].Diagnostic with
                { Occurrence = report.Diagnostics[0].Diagnostic.Occurrence! with { Epoch = Guid.Empty } } }] },
            "sequence" => report with { Diagnostics = [report.Diagnostics[0] with { Diagnostic = report.Diagnostics[0].Diagnostic with
                { Occurrence = report.Diagnostics[0].Diagnostic.Occurrence! with { Sequence = "2" } } }] },
            "missing-source" => report with { Diagnostics = [report.Diagnostics[0] with { Diagnostic = report.Diagnostics[0].Diagnostic with { Occurrence = null } }] },
            "package" => report with { Diagnostics = [report.Diagnostics[0] with { Diagnostic = report.Diagnostics[0].Diagnostic with
                { Occurrence = report.Diagnostics[0].Diagnostic.Occurrence! with { Package = new("pkg", "malformed-revision", "main") } } }] },
            "budget" => report with { Diagnostics = Enumerable.Repeat(report.Diagnostics[0], 513).ToArray() },
            _ => throw new ArgumentException(mode),
        };
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.ValidateReport(report, 3));
    }

    [Theory]
    [InlineData("task")] [InlineData("source")] [InlineData("plan")] [InlineData("input")] [InlineData("kind")]
    [InlineData("sequence")] [InlineData("completed")] [InlineData("mode")] [InlineData("time")]
    [InlineData("bad-count")] [InlineData("error-success")] [InlineData("output-sha")]
    public async Task 恶意旧进度和终态拒绝后拥有的进程被回收(string mode)
    {
        using Probe probe = await Probe.CreateAsync(mode);
        var executor = new WorkerBackendValidationExecutor(probe.Options());
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(Snapshot(), 3));
        Assert.Equal("WorkerProtocolInvalid", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
        int pid = await probe.PidAsync(); Assert.False(Alive(pid));
        output.WriteLine($"Rejected {mode}; probe PID={pid}, released=true.");
    }

    [Fact]
    public async Task 完整Error报告保每输出发生及所有实际来源不伪造成功()
    {
        using Probe probe = await Probe.CreateAsync("actual-error");
        ValidationReportDto result = await new WorkerBackendValidationExecutor(probe.Options()).ExecuteAsync(Snapshot(), 3);
        Assert.False(result.Succeeded); Assert.Equal(0, result.OpaqueFrames); Assert.True(Assert.Single(result.Issues).IsError);
        ValidationDiagnosticDto diagnostic = Assert.Single(result.Diagnostics);
        Assert.False(diagnostic.Transparent); Assert.Equal(ErrorReport().Diagnostics[0], diagnostic);
        Assert.False(Alive(await probe.PidAsync()));
    }

    [Theory]
    [InlineData("late-progress")] [InlineData("late-result")]
    public async Task 终态后晚到的上一任务消息不能作为新任务进度或结果复活(string mode)
    {
        using Probe probe = await Probe.CreateAsync(mode);
        await using WorkerProcessProxy proxy = new(probe.Options());
        List<BackendJobProgressDto> progress = [];
        byte[] bytes = StaticProjectSnapshotCodec.Encode(Snapshot());
        Assert.True((await proxy.ValidateStaticProjectAsync(bytes, 3, TimeSpan.FromSeconds(5), new InlineProgress(progress.Add))).Succeeded);
        Assert.Equal(2, progress.Count);
        int pid = Assert.IsType<int>(proxy.ProcessId);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => proxy.ValidateStaticProjectAsync(bytes, 3, TimeSpan.FromSeconds(5), new InlineProgress(progress.Add)));
        Assert.Equal(2, progress.Count); Assert.Null(proxy.ProcessId); Assert.False(Alive(pid));
        output.WriteLine($"Late {mode}, PID={pid}; previous terminal retained, new request rejected and released.");
    }

    [Fact]
    public async Task 任务Deadline为稳定失败而非用户取消且杀树后原执行器可重用()
    {
        using Probe probe = await Probe.CreateAsync("timeout");
        var executor = new WorkerBackendValidationExecutor(probe.Options(), TimeSpan.FromMilliseconds(1500));
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(Snapshot(), 3));
        Assert.Equal("WorkerTaskTimedOut", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
        int pid = await probe.PidAsync(); int child = await probe.ChildAsync();
        Assert.False(Alive(pid)); await AssertDeadAsync(child);
        ValidationReportDto recovered = await executor.ExecuteAsync(Snapshot(), 3);
        Assert.True(recovered.Succeeded); Assert.NotEqual(pid, await probe.PidAsync());
        output.WriteLine($"Deadline PID={pid}, descendant={child}; killed and executor reuse passed.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 包验证能力握手计入任务期限且保留用户取消语义并排水(bool userCancel)
    {
        using Probe probe = await Probe.CreateAsync("package-handshake-timeout");
        await using WorkerProcessProxy proxy = new(probe.Options());
        using CancellationTokenSource cancellation = new();
        if (userCancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(1000));
        Task<ValidationReportDto> operation = proxy.ValidateProjectAsync(PackageProjectSnapshotCodec.Encode(Snapshot()), 1,
            TimeSpan.FromMilliseconds(userCancel ? 5000 : 1500), null, PackageProjectSnapshotCodec.Profile, cancellation.Token);
        if (userCancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
        {
            var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => operation);
            Assert.Equal("WorkerTaskTimedOut", failure.Code);
        }
        Assert.True(probe.HandshakeStarted); Assert.Null(proxy.ProcessId); Assert.False(Alive(await probe.PidAsync()));
    }

    [Fact]
    public async Task 真实收到进度后用户取消并等待拥有进程树释放后重用代理()
    {
        using Probe probe = await Probe.CreateAsync("timeout");
        await using WorkerProcessProxy proxy = new(probe.Options());
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.ValidateStaticProjectAsync(StaticProjectSnapshotCodec.Encode(Snapshot()),
            3, TimeSpan.FromSeconds(5), new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
        int pid = await probe.PidAsync(); Assert.Null(proxy.ProcessId); Assert.False(Alive(pid)); await AssertDeadAsync(await probe.ChildAsync());
        Assert.True((await proxy.ValidateStaticProjectAsync(StaticProjectSnapshotCodec.Encode(Snapshot()), 3, TimeSpan.FromSeconds(5))).Succeeded);
        int restarted = await probe.PidAsync(); await proxy.StopAsync(); Assert.False(Alive(restarted));
    }

    private static ValidationReportDto ErrorReport() => new(false, 0, 0,
        [new(false, "0", "Actual", "actual failure", true)],
        [new(false, new("Actual", "error", "真实错误", null, new(new("0c95dc85-89bb-4536-908a-4f59347b49f6"), "1", "actual-instance", new('a', 64),
            "Evaluate", "0", new("src/effect.cs", 2, 3), new("pkg", "sha256:" + new string('b', 64), "main")), "runtime"))]);
    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(Project.Create("validation", Composition.Create(8, 4, FrameRate.Fps30,
        outputDurationUs: 1_000_000), LyricsDocument.Create([])));
    private static bool Alive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task AssertDeadAsync(int pid)
    { for (int i = 0; i < 100 && Alive(pid); i++) await Task.Delay(20); Assert.False(Alive(pid)); }
    private sealed class InlineProgress(Action<BackendJobProgressDto> report) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => report(value); }
    private sealed class Probe : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly TemporaryDirectory _temp = new();
        private readonly string _mode;
        private Probe(string mode) { _mode = mode; }
        public static async Task<Probe> CreateAsync(string mode)
        {
            Probe probe = new(mode); await File.WriteAllTextAsync(probe._temp.FilePath("probe.py"), Source);
            await File.WriteAllTextAsync(probe._temp.FilePath("error.json"), JsonSerializer.Serialize(ErrorReport(), Json));
            return probe;
        }
        public WorkerProcessOptions Options() => HostingTestPlatformHelpers.PythonOptions(_temp.FilePath("probe.py"), [_mode, _temp.FilePath("pid"),
            _temp.FilePath("child"), _temp.FilePath("attempt"), _temp.FilePath("error.json")], TimeSpan.FromMilliseconds(100));
        public bool HandshakeStarted => File.Exists(_temp.FilePath("attempt"));
        public async Task<int> PidAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("pid")), CultureInfo.InvariantCulture);
        public async Task<int> ChildAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("child")), CultureInfo.InvariantCulture);
        public void Dispose() => _temp.Dispose();
        private const string Source = """
            import base64, hashlib, json, os, struct, subprocess, sys, time
            mode, pid_file, child_file, attempt, error_file = sys.argv[1:]
            if mode=='package-handshake-timeout' and hasattr(os, 'setsid'): os.setsid()
            with open(pid_file, 'w') as target: target.write(str(os.getpid()))
            def send(value):
                body=json.dumps(dict(protocolVersion='worker-1', delayMilliseconds=0, cancelled=False, **value)).encode()
                sys.stdout.buffer.write(struct.pack('<i',len(body))+body); sys.stdout.buffer.flush()
            while True:
                prefix=sys.stdin.buffer.read(4)
                if not prefix: break
                request=json.loads(sys.stdin.buffer.read(struct.unpack('<i',prefix)[0]))
                kind=request['kind']
                if kind=='hello':
                    hello=dict(kind='hello',requestId=request['requestId'])
                    if mode=='package-handshake-timeout': hello['processGroupId']=os.getpgrp() if hasattr(os, 'getpgrp') else os.getpid()
                    send(hello); continue
                if kind=='shutdown': break
                if kind=='cancel': continue
                if mode=='package-handshake-timeout' and kind=='package-validation-capabilities-v1':
                    with open(attempt, 'w') as target: target.write('handshake')
                    time.sleep(30); continue
                frames=request['validationFramesPerOutput']
                common=dict(requestId=request['requestId'],inputSha256=request['inputSha256'],taskId=request['taskId'],
                    sourceSha256=request['sourceSha256'],validationFramesPerOutput=frames)
                if mode=='actual-error':
                    with open(error_file) as source: report=json.load(source)
                    sequence=0
                else:
                    progress=dict(stage='rendering',completedFrames=1,totalFrames=frames*2,timeUs='0',transparent=False)
                    progress_message=dict(kind='task-validation-progress-v1',progressSequence=1,validationProgress=progress,**common)
                    if mode=='task': progress_message['taskId']='0'*32
                    if mode=='source': progress_message['sourceSha256']='f'*64
                    if mode=='plan': progress_message['validationFramesPerOutput']=frames+1
                    if mode=='input': progress_message['inputSha256']='e'*64
                    if mode=='kind': progress_message['kind']='result'
                    if mode=='sequence': progress_message['progressSequence']=0
                    if mode=='completed': progress['completedFrames']=0
                    if mode=='mode': progress['transparent']=True
                    if mode=='time': progress['timeUs']='17'
                    send(progress_message)
                    if mode=='timeout' and not os.path.exists(attempt):
                        with open(attempt,'w') as target: target.write('owned')
                        child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'])
                        with open(child_file,'w') as target: target.write(str(child.pid))
                        time.sleep(30)
                    report=dict(succeeded=True,opaqueFrames=frames,transparentFrames=frames,issues=[],diagnostics=[])
                    sequence=2*((frames-1)//10+1+(1 if (frames-1)%10 else 0))
                    send(dict(kind='task-validation-progress-v1',progressSequence=sequence,validationProgress=dict(stage='rendering',
                        completedFrames=frames*2,totalFrames=frames*2,timeUs=str((frames-1)*1000000//30),transparent=True),**common))
                    if mode=='bad-count': report['opaqueFrames']=frames-1
                    if mode=='error-success':
                        with open(error_file) as source: report=json.load(source)
                        report.update(succeeded=True,opaqueFrames=frames,transparentFrames=frames)
                payload=json.dumps(report,separators=(',',':'),ensure_ascii=False).encode()
                digest=hashlib.sha256(payload).hexdigest()
                if mode=='output-sha': digest='a'*64
                send(dict(kind='task-validation-result-v1',payload=base64.b64encode(payload).decode(),outputSha256=digest,
                    progressSequence=sequence,**common))
                if mode=='late-progress': send(dict(kind='task-validation-progress-v1',progressSequence=sequence,validationProgress=dict(stage='rendering',
                    completedFrames=frames*2,totalFrames=frames*2,timeUs=str((frames-1)*1000000//30),transparent=True),**common))
                if mode=='late-result': send(dict(kind='task-validation-result-v1',payload=base64.b64encode(payload).decode(),outputSha256=digest,
                    progressSequence=sequence,**common))
            """;
    }
}
