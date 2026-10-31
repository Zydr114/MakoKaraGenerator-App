using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerAnalysisProcessTests
{
    [Theory]
    [InlineData("task")] [InlineData("source")] [InlineData("input")] [InlineData("sequence")] [InlineData("samples")]
    [InlineData("time")] [InlineData("stage")] [InlineData("old-frame")] [InlineData("output-sha")] [InlineData("bad-result")]
    public async Task 旧来源和畸形实际进度或结果不能成为完整分析(string mode)
    {
        using Probe probe = await Probe.CreateAsync(mode);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor(probe).ExecuteAsync(probe.Grant()));
        Assert.True(failure.Code is "WorkerAnalysisProtocolInvalid" or "WorkerProtocolInvalid" or "WorkerAnalysisResultInvalid");
        Assert.Null(failure.DiagnosticSnapshot); Assert.False(Alive(await probe.PidAsync()));
    }
    [Fact]
    public async Task 分析deadline为稳定失败且拥有子进程全部释放后执行器可重用()
    {
        using Probe probe = await Probe.CreateAsync("timeout"); var executor = Executor(probe, TimeSpan.FromMilliseconds(300));
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(probe.Grant()));
        Assert.Equal("WorkerAnalysisTimedOut", failure.Code); await AssertDeadAsync(await probe.PidAsync()); await AssertDeadAsync(await probe.ChildAsync());
        Assert.NotEmpty(await executor.ExecuteAsync(probe.Grant())); Assert.False(Alive(await probe.PidAsync()));
    }
    [Fact]
    public async Task 实际进度后用户取消与晚到消息不覆盖终态并可重新创建()
    {
        using Probe probe = await Probe.CreateAsync("timeout"); var executor = Executor(probe); using CancellationTokenSource cancellation = new();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(probe.Grant(),
            new InlineProgress(_ => { calls++; cancellation.Cancel(); }), cancellation.Token));
        await AssertDeadAsync(await probe.PidAsync()); await AssertDeadAsync(await probe.ChildAsync());
        int terminal = calls; await Task.Delay(40); Assert.Equal(terminal, calls);
        Assert.NotEmpty(await executor.ExecuteAsync(probe.Grant()));
    }
    [Fact]
    public async Task 完整终态后的晚到进度不复活且IO失败没有伪造Runtime发生()
    {
        using Probe probe = await Probe.CreateAsync("late"); List<BackendAnalysisProgressDto> progress = [];
        Assert.NotEmpty(await Executor(probe).ExecuteAsync(probe.Grant(), new InlineProgress(progress.Add)));
        Assert.Equal(2, progress.Count); Assert.Equal("prepared", progress[^1].Stage); Assert.False(Alive(await probe.PidAsync()));
        using Probe failed = await Probe.CreateAsync("error");
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor(failed).ExecuteAsync(failed.Grant()));
        Assert.Equal("AudioAnalysisFailed", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
    }
    private static WorkerBackendAnalysisExecutor Executor(Probe probe, TimeSpan? timeout = null)
        => new(probe.Options(), "/usr/bin/ffmpeg", "/usr/bin/ffprobe", timeout);
    private static bool Alive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task AssertDeadAsync(int pid)
    { for (int i = 0; i < 100 && Alive(pid); i++) await Task.Delay(20); Assert.False(Alive(pid)); }
    private sealed class InlineProgress(Action<BackendAnalysisProgressDto> callback) : IProgress<BackendAnalysisProgressDto>
    { public void Report(BackendAnalysisProgressDto value) => callback(value); }
    private sealed class Probe : IDisposable
    {
        private readonly TemporaryDirectory _temp = new(); private readonly string _mode;
        private Probe(string mode) { _mode = mode; }
        public static async Task<Probe> CreateAsync(string mode)
        {
            Probe probe = new(mode); await File.WriteAllTextAsync(probe._temp.FilePath("probe.py"), Source);
            var data = new AudioAnalysisRevision(new('a', 64), new('b', 64), 960, [new(0, .5, .25, null, 0)]);
            await File.WriteAllBytesAsync(probe._temp.FilePath("result.json"), AudioAnalysisJson.Encode(data)); return probe;
        }
        public BackendAudioAnalysisGrant Grant() => new(_temp.FilePath("source.wav"), 44, new('a', 64));
        public WorkerProcessOptions Options() => new("python3", [_temp.FilePath("probe.py"), _mode, _temp.FilePath("pid"),
            _temp.FilePath("child"), _temp.FilePath("attempt"), _temp.FilePath("result.json")], TimeSpan.FromMilliseconds(100));
        public async Task<int> PidAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("pid")), CultureInfo.InvariantCulture);
        public async Task<int> ChildAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("child")), CultureInfo.InvariantCulture);
        public void Dispose() => _temp.Dispose();
        private const string Source = """
            import base64, hashlib, json, os, struct, subprocess, sys, time
            mode,pid_file,child_file,attempt,result_file=sys.argv[1:]
            os.setpgid(0,0)
            with open(pid_file,'w') as target: target.write(str(os.getpid()))
            def send(value):
                body=json.dumps(dict(protocolVersion='worker-1',delayMilliseconds=0,cancelled=False,**value)).encode()
                sys.stdout.buffer.write(struct.pack('<i',len(body))+body);sys.stdout.buffer.flush()
            while True:
                prefix=sys.stdin.buffer.read(4)
                if not prefix: break
                request=json.loads(sys.stdin.buffer.read(struct.unpack('<i',prefix)[0]));kind=request['kind']
                if kind=='hello': send(dict(kind='hello',requestId=request['requestId'],processGroupId=os.getpid()));continue
                if kind=='shutdown': break
                if kind=='cancel': continue
                common=dict(requestId=request['requestId'],inputSha256=request['inputSha256'],taskId=request['taskId'],sourceSha256=request['sourceSha256'])
                if mode=='error': send(dict(kind='error',error='decoder failed',errorCode='AudioAnalysisFailed',**common));continue
                progress=dict(stage='decoding',completedSamples='480',timeUs='10000')
                message=dict(kind='task-audio-analysis-progress-v1',progressSequence=1,analysisProgress=progress,**common)
                if mode=='task': message['taskId']='0'*32
                if mode=='source': message['sourceSha256']='f'*64
                if mode=='input': message['inputSha256']='e'*64
                if mode=='sequence': message['progressSequence']=0
                if mode=='samples': progress['completedSamples']='14400001'
                if mode=='time': progress['timeUs']='17'
                if mode=='stage': progress['stage']='committing'
                if mode=='old-frame': message['frameWidth']=320
                if mode=='timeout' and not os.path.exists(attempt):
                    with open(attempt,'w') as target: target.write('owned')
                    child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)'])
                    with open(child_file,'w') as target: target.write(str(child.pid))
                    send(message);time.sleep(30)
                send(message)
                send(dict(kind='task-audio-analysis-progress-v1',progressSequence=2,analysisProgress=dict(stage='prepared',completedSamples='960',totalSamples='960',timeUs='20000'),**common))
                with open(result_file,'rb') as source: payload=source.read()
                if mode=='bad-result': payload=payload.replace(b'"sampleCount":960',b'"sampleCount":959')
                digest=hashlib.sha256(payload).hexdigest()
                if mode=='output-sha': digest='b'*64
                send(dict(kind='task-audio-analysis-result-v1',payload=base64.b64encode(payload).decode(),outputSha256=digest,progressSequence=2,**common))
                if mode=='late': send(dict(kind='task-audio-analysis-progress-v1',progressSequence=3,analysisProgress=progress,**common))
            """;
    }
}
