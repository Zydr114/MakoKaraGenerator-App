using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerAudioPreparationProxyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 已验证Worker输入拒绝返回原媒体输入异常且不泄露Worker内部详情()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        WorkerBackendAudioPreparationExecutor executor = Executor(directory, "input-rejected");
        IOException failure = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(Grant(directory)));
        Assert.Equal("音频资源探测或解码失败。", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task 已验证Worker无音流仍返回空结果供原调用方分类()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        Assert.Null(await Executor(directory, "no-stream").ExecuteAsync(Grant(directory)));
    }

    [Theory]
    [InlineData("WorkerAudioPreparationTimedOut")]
    [InlineData("WorkerAudioPreparationTransportFailed")]
    [InlineData("WorkerAudioPreparationSourceChanged")]
    public async Task 已关联Worker错误回执映射仍保原稳定编号(string code)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() =>
            Executor(directory, code).ExecuteAsync(Grant(directory)));
        Assert.Equal(code, failure.Code);
        Assert.DoesNotContain("controlled", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("profile")]
    [InlineData("families")]
    [InlineData("missing-families")]
    [InlineData("buckets")]
    [InlineData("bucket-samples")]
    [InlineData("bytes")]
    [InlineData("missing-budget")]
    public async Task actual握手能力来源profile完整集合与固定预算缺失错配均disabled(string damage)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new(); WorkerBackendAudioPreparationExecutor executor = Executor(directory, damage);
        BackendAudioPreparationCapabilities result = await executor.ProbeAsync();
        Assert.False(result.GuardAvailable); Assert.False(result.ToolStartupVerified); Assert.Equal("WorkerAudioPreparationProfileMismatch", result.FailureCode);
    }

    [Fact]
    public async Task 无实际私有group的cap回执不能宣称guardAvailable()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new(); WorkerBackendAudioPreparationExecutor executor = Executor(directory, "no-group");
        BackendAudioPreparationCapabilities result = await executor.ProbeAsync(); Assert.False(result.GuardAvailable);
        Assert.Equal("WorkerAudioPreparationUnavailable", result.FailureCode);
    }

    [Theory]
    [InlineData("wrong-task")]
    [InlineData("wrong-source")]
    [InlineData("wrong-input")]
    [InlineData("unexpected-progress")]
    public async Task 错关联的Worker错误不能作为这次音频的真实输入拒绝(string damage)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new(); WorkerBackendAudioPreparationExecutor executor = Executor(directory, damage);
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(Grant(directory)));
        Assert.Equal("WorkerAudioPreparationProtocolInvalid", failure.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 未响应cancel的准备进程必须强制排水实际private成员并区分deadline与用户取消(bool userCancel)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new(); WorkerBackendAudioPreparationExecutor executor = Executor(directory, "hang", TimeSpan.FromSeconds(1));
        using CancellationTokenSource caller = new(); Task pending = executor.ExecuteAsync(Grant(directory), caller.Token);
        string identities = directory.FilePath("identities.json"); await WaitUntil(() => File.Exists(identities));
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(identities));
        (int Pid, long Start)[] members = [.. document.RootElement.EnumerateArray().Select(value => (value[0].GetInt32(), value[1].GetInt64()))];
        Assert.Equal(2, members.Length); Assert.All(members, identity => Assert.True(IsSameProcess(identity.Pid, identity.Start)));
        if (userCancel) caller.Cancel();
        if (userCancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Equal("WorkerAudioPreparationTimedOut", (await Assert.ThrowsAsync<RenderSessionFailureException>(() => pending)).Code);
        Assert.All(members, identity => Assert.False(IsSameProcess(identity.Pid, identity.Start)));
        foreach (var member in members) output.WriteLine($"Controlled private preparation member PID={member.Pid}, startTicks={member.Start}, gone=true, userCancel={userCancel}");
    }

    [Fact]
    public async Task 真实Worker能力与完整WAV波形还原保源采样数及原Info()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        string worker = WorkerBinaryLocator.LocateBackendWorker();
        Assert.True(File.Exists(worker)); string audio = directory.FilePath("wave.wav"); WavWriter.Write(audio, WavWriter.BuildSegmentedTone(8000, (0.15, 0.8f)), 8000);
        byte[] bytes = File.ReadAllBytes(audio); BackendAudioPreparationGrant grant = new(audio, bytes.LongLength, WorkerProtocol.ComputeSha256(bytes));
        WorkerBackendAudioPreparationExecutor executor = new(new(worker), TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        BackendAudioPreparationCapabilities capabilities = await executor.ProbeAsync(); Assert.True(capabilities.GuardAvailable, capabilities.FailureCode); Assert.True(capabilities.ToolStartupVerified);
        var expected = (await MakoKaraGenerator.Media.AudioWaveformLoader.CreateForSelfContainedFiles(TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe).LoadAsync(audio))!;
        var actual = (await executor.ExecuteAsync(grant))!;
        Assert.Equal(expected.Info, actual.Info); Assert.Equal(expected.Peaks.SampleRate, actual.Peaks.SampleRate);
        Assert.Equal(expected.Peaks.SampleCount, actual.Peaks.SampleCount); Assert.Equal(expected.Peaks.DurationUs, actual.Peaks.DurationUs);
        Assert.Equal(expected.Peaks.LevelCount, actual.Peaks.LevelCount);
        for (int level = 0; level < expected.Peaks.LevelCount; level++)
        {
            Assert.Equal(expected.Peaks.BucketCount(level), actual.Peaks.BucketCount(level));
            for (int index = 0; index < expected.Peaks.BucketCount(level); index++)
            {
                var left = expected.Peaks.GetBucketPeak(level, index); var right = actual.Peaks.GetBucketPeak(level, index);
                Assert.Equal(BitConverter.SingleToInt32Bits(left.Min), BitConverter.SingleToInt32Bits(right.Min));
                Assert.Equal(BitConverter.SingleToInt32Bits(left.Max), BitConverter.SingleToInt32Bits(right.Max));
            }
        }
    }

    internal static WorkerBackendAudioPreparationExecutor Executor(TemporaryDirectory directory, string damage, TimeSpan? timeout = null)
    {
        string script = directory.FilePath("worker.py"); File.WriteAllText(script, ControlledWorker);
        return new(HostingTestPlatformHelpers.PythonOptions(script, [damage, directory.FilePath("identities.json"), StaticProjectSnapshotCodec.EngineIdentity]),
            HostingTestPlatformHelpers.ContractFfmpegPath, HostingTestPlatformHelpers.ContractFfprobePath, timeout);
    }
    private static BackendAudioPreparationGrant Grant(TemporaryDirectory directory)
    { string source = directory.FilePath("source.wav"); File.WriteAllBytes(source, [1, 2, 3]); return new(source, 3, WorkerProtocol.ComputeSha256([1, 2, 3])); }
    private static async Task WaitUntil(Func<bool> condition)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private static bool IsSameProcess(int pid, long start) => HostingTestPlatformHelpers.IsSameProcess(pid, start);
    private static string RepositoryRoot()
    { DirectoryInfo? current = new(AppContext.BaseDirectory); while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx"))) current = current.Parent; return current!.FullName; }
    private const string ControlledWorker = """
        import base64, hashlib, json, os, struct, subprocess, sys, time
        mode, identity_path, engine = sys.argv[1:]
        if mode != 'no-group' and hasattr(os, 'setsid'): os.setsid()
        def send(value, payload=b''):
            body = json.dumps(dict(protocolVersion='worker-1', payload=base64.b64encode(payload).decode(), delayMilliseconds=0, cancelled=False, **value)).encode()
            sys.stdout.buffer.write(struct.pack('<i', len(body)) + body); sys.stdout.buffer.flush()
        def identity(pid):
            if sys.platform == 'win32':
                import ctypes
                from ctypes import wintypes
                k32 = ctypes.WinDLL('kernel32', use_last_error=True)
                h = k32.OpenProcess(0x1000, False, pid)
                c, e, k, u = wintypes.FILETIME(), wintypes.FILETIME(), wintypes.FILETIME(), wintypes.FILETIME()
                try:
                    k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
                    return [pid, (c.dwHighDateTime << 32) | c.dwLowDateTime]
                finally:
                    if h: k32.CloseHandle(h)
            stat = open('/proc/%d/stat' % pid).read(); fields = stat[stat.rfind(')')+2:].split(); return [pid, int(fields[19])]
        while True:
            prefix = sys.stdin.buffer.read(4)
            if not prefix: break
            message = json.loads(sys.stdin.buffer.read(struct.unpack('<i', prefix)[0])); kind = message['kind']
            if kind == 'hello':
                hello = dict(kind='hello', requestId=message['requestId'])
                if mode != 'no-group': hello['processGroupId'] = os.getpgrp() if hasattr(os, 'getpgrp') else os.getpid()
                send(hello)
            elif kind == 'shutdown': break
            elif kind == 'task-audio-prepare-capabilities-v1':
                cap = dict(guardAvailable=True, toolStartupVerified=True, engineIdentity=engine, profile='self-contained-native-waveform-v1', families=['wav','mp3','flac','aac','mov','ogg'], maximumBaseBuckets=500000, baseBucketSamples=64, maximumResultBytes=4194304)
                if mode == 'engine': cap['engineIdentity'] = 'old-engine'
                if mode == 'profile': cap['profile'] = 'unknown'
                if mode == 'families': cap['families'] = ['wav'] * 6
                if mode == 'missing-families': del cap['families']
                if mode == 'buckets': cap['maximumBaseBuckets'] = 500001
                if mode == 'bucket-samples': cap['baseBucketSamples'] = 128
                if mode == 'bytes': cap['maximumResultBytes'] = 4194305
                if mode == 'missing-budget': del cap['maximumResultBytes']
                payload = json.dumps(cap).encode()
                send(dict(kind='task-audio-prepare-capabilities-result-v1', requestId=message['requestId'], taskId=message['taskId'], inputSha256=message['inputSha256'], outputSha256=hashlib.sha256(payload).hexdigest()), payload)
            elif kind == 'task-audio-prepare-v1':
                if mode == 'hang':
                    child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    tmp_identity = identity_path + '.tmp'
                    with open(tmp_identity, 'w') as out: json.dump([identity(os.getpid()), identity(child.pid)], out)
                    os.replace(tmp_identity, identity_path)
                    time.sleep(60)
                else:
                    reply = dict(kind='error', requestId=message['requestId'], taskId=message['taskId'], inputSha256=message['inputSha256'], sourceSha256=message['sourceSha256'], errorCode='WorkerAudioPreparationInputRejected', error='controlled')
                    if mode == 'no-stream': reply['errorCode'] = 'WorkerAudioPreparationNoAudioStream'
                    if mode.startswith('WorkerAudioPreparation'): reply['errorCode'] = mode
                    if mode == 'wrong-task': reply['taskId'] = '1' * 32
                    if mode == 'wrong-source': reply['sourceSha256'] = '1' * 64
                    if mode == 'wrong-input': reply['inputSha256'] = '1' * 64
                    if mode == 'unexpected-progress': reply['progressSequence'] = 1
                    send(reply)
        """;
}
