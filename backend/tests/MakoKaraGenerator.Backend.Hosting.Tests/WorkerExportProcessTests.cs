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

public sealed class WorkerExportProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("cap-valid", true)]
    [InlineData("cap-presets", true)]
    [InlineData("cap-presets-duplicate", false)]
    [InlineData("cap-legacy", false)]
    [InlineData("cap-engine", false)]
    [InlineData("cap-unknown", false)]
    [InlineData("cap-duplicate", false)]
    [InlineData("cap-audio-missing", false)]
    [InlineData("cap-audio-unknown", false)]
    [InlineData("cap-audio-duplicate", false)]
    [InlineData("cap-execution-missing", false)]
    [InlineData("cap-execution-unknown", false)]
    [InlineData("cap-execution-duplicate", false)]
    [InlineData("cap-malformed", false)]
    public async Task 导出能力只接受实际匹配引擎来源及六音频两执行profile集合(string mode, bool enabled)
    {
        using Probe probe = await Probe.CreateAsync(mode);
        BackendExportCapabilities capabilities = await Executor(probe).ProbeAsync();
        Assert.Equal(enabled, capabilities.GuardAvailable); Assert.Equal(enabled, capabilities.OpaqueMp4);
        if (enabled)
        {
            Assert.Equal(mode == "cap-presets" ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets]
                : new[] { BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis }, capabilities.SourceProfiles);
            Assert.Equal([BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg], capabilities.AudioProfiles!);
            Assert.Equal([WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile], capabilities.ExecutionProfiles!);
        }
        if (!enabled) Assert.Equal(mode == "cap-malformed" ? "WorkerExportProtocolInvalid" : "WorkerExportProfileMismatch", capabilities.FailureCode);
        Assert.False(Alive(await probe.PidAsync()));
    }
    [Fact]
    public async Task 协议桩仅有旧来源时不捏造新来源且新快照在启动第二个进程前拒绝()
    {
        using Probe probe = await Probe.CreateAsync("cap-valid");
        var executor = Executor(probe); var capabilities = await executor.ProbeAsync();
        Assert.DoesNotContain(BackendSnapshotProfiles.StaticLyricsScenePresets, capabilities.SourceProfiles!);
        int pid = await probe.PidAsync();
        var source = Snapshot();
        var template = StyleTemplate.Create("static", "static", StyleTemplateKind.Line, source.LyricStyle, [LyricStyleProperty.Opacity]);
        source = source with { PresetApplications = new([new PresetApplication("application", new(Core.Effects.ProjectEffectBindingKind.Project), template)]) };
        var failure = Assert.Throws<RenderSessionFailureException>(() => executor.ValidateInput(source, probe.Input()));
        Assert.Equal("WorkerExportProfileMismatch", failure.Code);
        Assert.Equal(pid, await probe.PidAsync()); Assert.False(Alive(pid));
    }

    [Theory]
    [InlineData("task")] [InlineData("source")] [InlineData("input")] [InlineData("format")] [InlineData("audio")]
    [InlineData("sequence")] [InlineData("count")] [InlineData("stage")] [InlineData("kind")] [InlineData("sha")]
    public async Task 恶意旧来源选项进度与输出哈希不能作为静态导出成功(string mode)
    {
        using Probe probe = await Probe.CreateAsync(mode);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor(probe).ExecuteAsync(Snapshot(), probe.Input()));
        Assert.True(failure.Code is "WorkerExportProtocolInvalid" or "WorkerProtocolInvalid");
        Assert.Null(failure.DiagnosticSnapshot); Assert.False(Alive(await probe.PidAsync()));
        output.WriteLine($"Rejected export {mode}, PID={await probe.PidAsync()}, released=true.");
    }
    [Fact]
    public async Task 接近256KiB实际Error快照通过独立导出失败通道保epoch序号及来源()
    {
        using Probe probe = await Probe.CreateAsync("actual-error");
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor(probe).ExecuteAsync(Snapshot(), probe.Input()));
        Assert.Equal("ActualRuntimeError", failure.Code); Assert.True(failure.DiagnosticSnapshot!.HasError);
        var actual = Assert.Single(failure.DiagnosticSnapshot.ReadAll());
        Assert.Equal(Probe.Epoch, actual.Epoch); Assert.Equal(1, actual.Sequence); Assert.Equal("actual-instance", actual.Diagnostic.InstanceId.Value);
        Assert.Equal("actual-artifact", actual.Diagnostic.ArtifactHash); Assert.Equal("Evaluate", actual.Diagnostic.Stage); Assert.Equal(123, actual.Diagnostic.TimeUs);
        Assert.Equal(250_000, actual.Diagnostic.Diagnostic.Message.Length); Assert.Equal("src/effect.cs", actual.Diagnostic.Diagnostic.Location!.Path);
        Assert.Equal("pkg", actual.Diagnostic.PackageIdentity!.PackageId); Assert.False(Alive(await probe.PidAsync()));
    }
    [Fact]
    public async Task 实际Error快照超预算保主因与secondary且没有伪截断报告()
    {
        using Probe probe = await Probe.CreateAsync("budget-secondary");
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor(probe).ExecuteAsync(Snapshot(), probe.Input()));
        Assert.Equal("ActualRuntimeError", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
        Assert.Equal("WorkerExportReportBudgetExceeded", failure.Data["WorkerExportDiagnosticsFailureCode"]);
    }
    [Fact]
    public async Task Deadline不是用户取消且排水后执行器可重新创建Worker()
    {
        using Probe probe = await Probe.CreateAsync("timeout");
        var executor = Executor(probe, TimeSpan.FromMilliseconds(300));
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(Snapshot(), probe.Input()));
        Assert.Equal("WorkerExportTimedOut", failure.Code);
        int pid = await probe.PidAsync(), child = await probe.ChildAsync(); await AssertDeadAsync(pid); await AssertDeadAsync(child);
        var result = await executor.ExecuteAsync(Snapshot(), probe.Input()); Assert.True(result.StrictlyVerified);
        Assert.NotEqual(pid, await probe.PidAsync()); Assert.False(Alive(await probe.PidAsync()));
        output.WriteLine($"Export deadline PID={pid}, child={child}; both drained, executor reused.");
    }
    [Fact]
    public async Task 取消后的真实进度与终态不能晚到覆盖且所有拥有进程释放()
    {
        using Probe probe = await Probe.CreateAsync("timeout"); using CancellationTokenSource cancellation = new();
        var executor = Executor(probe); int progress = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(Snapshot(), probe.Input(),
            new InlineProgress(_ => { progress++; cancellation.Cancel(); }), cancellation.Token));
        await AssertDeadAsync(await probe.PidAsync()); await AssertDeadAsync(await probe.ChildAsync());
        int finalCount = progress; await Task.Delay(50); Assert.Equal(finalCount, progress);
        Assert.True((await executor.ExecuteAsync(Snapshot(), probe.Input())).StrictlyVerified);
    }
    [Fact]
    public async Task 已准备终态后的旧进度不能发布到已完成调用()
    {
        using Probe probe = await Probe.CreateAsync("late-progress"); List<BackendJobProgressDto> progress = [];
        var result = await Executor(probe).ExecuteAsync(Snapshot(), probe.Input(), new InlineProgress(progress.Add));
        Assert.True(result.StrictlyVerified); Assert.Equal("prepared", progress[^1].Stage); Assert.Equal(2, progress.Count);
        await Task.Delay(50); Assert.Equal(2, progress.Count); Assert.False(Alive(await probe.PidAsync()));
    }
    [Fact]
    public async Task Worker突然退出归中立transport失败而非裸EOF()
    {
        using Probe probe = await Probe.CreateAsync("eof");
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor(probe).ExecuteAsync(Snapshot(), probe.Input()));
        Assert.Equal("WorkerSessionConflict", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
    }
    private static WorkerBackendExportExecutor Executor(Probe probe, TimeSpan? timeout = null)
        => new(probe.Options(), "/usr/bin/ffmpeg", "/usr/bin/ffprobe", timeout,
            new BackendExportOptions { MaxMediaBytes = WorkerExportContract.MaximumMediaBytes });
    private static ProjectSnapshot Snapshot()
    {
        var lyrics = LyricsDocument.Create([LyricLine.Create("line", "a", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "a", new(0, 1_000_000), TimingStatus.Edited)])]);
        return ProjectSnapshot.FromProject(Project.Create("export probe", Composition.Create(320, 180, FrameRate.Fps30, new(0, 0, 0, 255), 1_000_000), lyrics));
    }
    private static bool Alive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task AssertDeadAsync(int pid)
    { for (int i = 0; i < 100 && Alive(pid); i++) await Task.Delay(20); Assert.False(Alive(pid)); }
    private sealed class InlineProgress(Action<BackendJobProgressDto> report) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => report(value); }
    private sealed class Probe : IDisposable
    {
        public static readonly Guid Epoch = Guid.Parse("12345678-1234-4321-1234-123456789abc");
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly TemporaryDirectory _temp = new(); private readonly string _mode;
        private Probe(string mode) { _mode = mode; }
        public static async Task<Probe> CreateAsync(string mode)
        {
            Probe probe = new(mode); await File.WriteAllTextAsync(probe._temp.FilePath("probe.py"), Source);
            var report = new ExportReportDto("opaqueMp4", false, 30, 30, 30, 0, 0, 320, 180, 30, 1, "1000000", "1000000", null,
                "h264", null, "yuv420p", false, 1000, new('a', 64), [], [], true, null, true);
            await File.WriteAllTextAsync(probe._temp.FilePath("report.json"), JsonSerializer.Serialize(report, Json));
            var capabilities = new WorkerExportCapabilitiesDto(true, true, true,
                EngineIdentity: mode == "cap-engine" ? "old-engine" : StaticProjectSnapshotCodec.EngineIdentity,
                SourceProfiles: mode == "cap-presets" ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets]
                    : mode == "cap-presets-duplicate" ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets, BackendSnapshotProfiles.StaticLyricsScenePresets]
                    : mode == "cap-unknown" ? [BackendSnapshotProfiles.StaticLyricsScene, "unknown-profile"]
                    : mode == "cap-duplicate" ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsScene]
                    : [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis],
                AudioProfiles: mode == "cap-audio-missing" ? null
                    : mode == "cap-audio-unknown" ? [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, "unknown"]
                    : mode == "cap-audio-duplicate" ? [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Mp3]
                    : [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                        BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
                ExecutionProfiles: mode == "cap-execution-missing" ? null
                    : mode == "cap-execution-unknown" ? [WorkerExportContract.ExecutionProfile, "unknown"]
                    : mode == "cap-execution-duplicate" ? [WorkerExportContract.ExecutionProfile, WorkerExportContract.ExecutionProfile]
                    : [WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile]);
            string capabilityJson = mode == "cap-legacy" ? "{\"guardAvailable\":true,\"opaqueMp4\":true,\"transparentProRes4444\":true}"
                : mode == "cap-malformed" ? "{\"guardAvailable\":true,\"opaqueMp4\":true,\"transparentProRes4444\":true,\"unknownField\":true}"
                : JsonSerializer.Serialize(capabilities, Json);
            await File.WriteAllTextAsync(probe._temp.FilePath("capabilities.json"), capabilityJson); return probe;
        }
        public BackendExportExecutionInput Input() => new("opaqueMp4", false, _temp.FilePath("candidate.mp4"), null);
        public WorkerProcessOptions Options() => new("python3", [_temp.FilePath("probe.py"), _mode, _temp.FilePath("pid"),
            _temp.FilePath("child"), _temp.FilePath("attempt"), _temp.FilePath("report.json")], TimeSpan.FromMilliseconds(100));
        public async Task<int> PidAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("pid")), CultureInfo.InvariantCulture);
        public async Task<int> ChildAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("child")), CultureInfo.InvariantCulture);
        public void Dispose() => _temp.Dispose();
        private const string Source = """
            import base64, hashlib, json, os, resource, struct, subprocess, sys, time
            mode, pid_file, child_file, attempt, report_file = sys.argv[1:]
            os.setpgid(0,0); resource.setrlimit(resource.RLIMIT_FSIZE,(67108864,67108864))
            with open(pid_file,'w') as target: target.write(str(os.getpid()))
            def send(value):
                body=json.dumps(dict(protocolVersion='worker-1',delayMilliseconds=0,cancelled=False,**value)).encode()
                sys.stdout.buffer.write(struct.pack('<i',len(body))+body); sys.stdout.buffer.flush()
            while True:
                prefix=sys.stdin.buffer.read(4)
                if not prefix: break
                request=json.loads(sys.stdin.buffer.read(struct.unpack('<i',prefix)[0])); kind=request['kind']
                if kind=='hello': send(dict(kind='hello',requestId=request['requestId'],processGroupId=os.getpid(),mediaFileSizeLimitBytes=67108864)); continue
                if kind=='shutdown': break
                if kind=='cancel': continue
                if mode=='eof': sys.exit(0)
                common=dict(requestId=request['requestId'],inputSha256=request['inputSha256'],taskId=request['taskId'],sourceSha256=request['sourceSha256'],
                    exportFormat=request['exportFormat'],exportIncludeAudio=request['exportIncludeAudio'])
                if kind=='task-export-capabilities-v1':
                    with open(os.path.join(os.path.dirname(report_file),'capabilities.json'),'rb') as source: payload=source.read()
                    send(dict(kind='task-export-capabilities-result-v1',payload=base64.b64encode(payload).decode(),outputSha256=hashlib.sha256(payload).hexdigest(),progressSequence=0,**common)); continue
                if mode=='actual-error':
                    diag=dict(occurrences=[dict(epoch='12345678-1234-4321-1234-123456789abc',sequence=1,diagnostic=dict(instanceId='actual-instance',
                        artifactHash='actual-artifact',stage='Evaluate',timeUs=123,severity='Error',code='ActualError',message='x'*250000,
                        location=dict(path='src/effect.cs',line=2,column=3),package=dict(packageId='pkg',revision='sha256:'+'b'*64,exportId='main')))],hasError=True)
                    send(dict(kind='error',error='actual failure',errorCode='ActualRuntimeError',exportDiagnostics=diag,**common)); continue
                if mode=='budget-secondary':
                    send(dict(kind='error',error='actual failure',errorCode='ActualRuntimeError',exportDiagnosticsFailureCode='WorkerExportReportBudgetExceeded',**common)); continue
                progress=dict(stage='rendering',completedFrames=1,totalFrames=30)
                message=dict(kind='task-export-progress-v1',progressSequence=1,exportProgress=progress,**common)
                if mode=='task': message['taskId']='0'*32
                if mode=='source': message['sourceSha256']='f'*64
                if mode=='input': message['inputSha256']='e'*64
                if mode=='format': message['exportFormat']='transparentProRes4444'
                if mode=='audio': message['exportIncludeAudio']=True
                if mode=='sequence': message['progressSequence']=0
                if mode=='count': progress['completedFrames']=31
                if mode=='stage': progress['stage']='committing'
                if mode=='kind': message['kind']='result'
                if mode=='timeout' and not os.path.exists(attempt):
                    with open(attempt,'w') as target: target.write('owned')
                    child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'])
                    with open(child_file,'w') as target: target.write(str(child.pid))
                    send(message); time.sleep(30)
                send(message)
                send(dict(kind='task-export-progress-v1',progressSequence=2,exportProgress=dict(stage='prepared',completedFrames=30,totalFrames=30),**common))
                with open(report_file) as source: report=json.load(source)
                payload=json.dumps(report,separators=(',',':')).encode(); digest=hashlib.sha256(payload).hexdigest()
                if mode=='sha': digest='b'*64
                send(dict(kind='task-export-result-v1',payload=base64.b64encode(payload).decode(),outputSha256=digest,progressSequence=2,**common))
                if mode=='late-progress': send(dict(kind='task-export-progress-v1',progressSequence=3,exportProgress=dict(stage='rendering',completedFrames=29,totalFrames=30),**common))
            """;
    }
}
