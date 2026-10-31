using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class SceneMeasurementLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 阻塞Worker取消恢复名额且直接Registry停止排水父子身份()
    {
        using TemporaryDirectory directory = new();
        string script = Path.Combine(directory.Path,"hang.py"), marker = Path.Combine(directory.Path,"pid.txt");
        var capabilities = new WorkerSceneMeasurementCapabilities(SceneMeasurementContract.Profile,StaticProjectSnapshotCodec.EngineIdentity,
            [StaticProjectSnapshotCodec.Profile,StaticProjectSnapshotCodec.AnalysisProfile,StaticProjectSnapshotCodec.PresetsProfile,
                PackageProjectSnapshotCodec.Profile,PackagePresetProjectSnapshotCodec.Profile]);
        string capabilityFile = Path.Combine(directory.Path,"cap.json");
        await File.WriteAllBytesAsync(capabilityFile,SceneMeasurementContract.EncodeBounded(capabilities));
        await File.WriteAllTextAsync(script,"""
import sys,json,struct,base64,hashlib,subprocess,time,os
while True:
 p=sys.stdin.buffer.read(4)
 if not p: break
 m=json.loads(sys.stdin.buffer.read(struct.unpack('<I',p)[0]))
 if m['kind']=='hello': r={'kind':'hello','requestId':m['requestId'],'payload':''}
 elif m['kind']=='scene-measurement-capabilities-v1':
  b=open(sys.argv[2],'rb').read();r={'kind':'result','requestId':m['requestId'],'inputSha256':m['inputSha256'],'payload':base64.b64encode(b).decode(),'outputSha256':hashlib.sha256(b).hexdigest()}
 elif m['kind']=='scene-measurement-v1':
  child=subprocess.Popen(['sleep','60'])
  open(sys.argv[1],'w').write(str(os.getpid())+' '+str(child.pid))
  time.sleep(60)
  continue
 elif m['kind']=='shutdown': break
 else: continue
 b=json.dumps(r).encode();sys.stdout.buffer.write(struct.pack('<I',len(b))+b);sys.stdout.buffer.flush()
""");
        var resources = new ResourceStore(new() { ResourceRoot = Path.Combine(directory.Path,"resources") });
        var audio = await resources.UploadAsync("client","song.wav","audio/wav",new MemoryStream([1]));
        var lyrics = await resources.UploadAsync("client","song.lrc","text/plain",new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
        WaveformPeakBuilder peaks = new(); peaks.Append(new float[8000]);
        LoadedWaveform waveform = new(new AudioStreamInfo(1_000_000,8000,1,"pcm","wav"),peaks.Build(8000));
        await using var registry = new ProjectSessionRegistry("host",resources,new((_,_) => Task.FromResult<LoadedWaveform?>(waveform)));
        registry.ConfigureSceneMeasurements(new("python3",[script,marker,capabilityFile],TimeSpan.FromMilliseconds(80)));
        var session = (await registry.CreateAsync("client",new("measure",audio.AssetId,lyrics.AssetId))).Session!;
        using var cancellation = new CancellationTokenSource();
        var request = new SceneMeasurementRequest("0","time","0",64,36,"transparent");
        var first = registry.MeasureSceneAsync("client",session.ProjectSessionId,request,cancellation.Token);
        int[] firstPids = await ReadPidsAsync(marker);
        var quota = await registry.MeasureSceneAsync("client",session.ProjectSessionId,request);
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded,quota.Error!.Code);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.All(firstPids,pid => Assert.False(Alive(pid),$"Canceled measurement member survived: {pid}"));
        File.Delete(marker);
        var next = registry.MeasureSceneAsync("client",session.ProjectSessionId,request);
        int[] secondPids = await ReadPidsAsync(marker);
        await registry.DisposeAsync();
        var stopped = await next;
        Assert.Equal(BackendErrorCode.SessionClosing,stopped.Error!.Code);
        Assert.All(secondPids,pid => Assert.False(Alive(pid),$"Stopped measurement member survived: {pid}"));
        Assert.Equal(BackendErrorCode.SessionClosing,(await registry.MeasureSceneAsync("client",session.ProjectSessionId,request)).Error!.Code);
        output.WriteLine($"Measurement cancel/drain groups: {string.Join(',',firstPids)}; {string.Join(',',secondPids)} all gone; per-client reservation recovered.");
    }

    private static async Task<int[]> ReadPidsAsync(string marker)
    {
        for (int i = 0; i < 400; i++)
        {
            if (File.Exists(marker))
            {
                string value = await File.ReadAllTextAsync(marker);
                if (value.Split(' ',StringSplitOptions.RemoveEmptyEntries) is { Length: 2 } parts)
                    return parts.Select(part => int.Parse(part,CultureInfo.InvariantCulture)).ToArray();
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("测量Worker未进入阻塞点。");
    }
    private static bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
