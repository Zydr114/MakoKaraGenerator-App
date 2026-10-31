using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerAnalysisCapabilitiesTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("legacy")]
    [InlineData("engine")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task 真实私有IPC能力必须当前engine及精确格式来源集合且终态排水(string mode)
    {
        using TemporaryDirectory temp = new();
        var caps = new WorkerAnalysisCapabilitiesDto(true, true, null, StaticProjectSnapshotCodec.EngineIdentity,
            [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
            [BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource]);
        caps = mode switch
        {
            "legacy" => new(true, true),
            "engine" => caps with { EngineIdentity = "old-engine" },
            "unknown" => caps with { SourceProfiles = [BackendAudioInputProfiles.LegacyAnalysisSource, "unknown"] },
            "duplicate" => caps with { AudioProfiles = [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Mp3] },
            _ => caps,
        };
        await File.WriteAllBytesAsync(temp.FilePath("cap.json"), WorkerAnalysisContract.EncodeJson(caps, 4096));
        await File.WriteAllTextAsync(temp.FilePath("probe.py"), Source);
        var executor = new WorkerBackendAnalysisExecutor(new WorkerProcessOptions("python3",
            [temp.FilePath("probe.py"), temp.FilePath("cap.json"), temp.FilePath("pid")]), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        var actual = await executor.ProbeAsync();
        if (mode == "valid")
        {
            Assert.True(actual.GuardAvailable); Assert.True(actual.ToolStartupVerified); Assert.Null(actual.FailureCode);
            Assert.Equal(caps.AudioProfiles, actual.AudioProfiles); Assert.Equal(caps.SourceProfiles, actual.SourceProfiles);
        }
        else
        {
            Assert.False(actual.GuardAvailable); Assert.False(actual.ToolStartupVerified);
            Assert.Equal("WorkerAnalysisProfileMismatch", actual.FailureCode); Assert.Empty(actual.AudioProfiles!); Assert.Empty(actual.SourceProfiles!);
        }
        int pid = int.Parse(await File.ReadAllTextAsync(temp.FilePath("pid")), CultureInfo.InvariantCulture);
        Assert.False(Alive(pid));
    }
    private static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private const string Source = """
        import base64,hashlib,json,os,struct,sys
        cap,pid=sys.argv[1:]
        os.setpgid(0,0)
        with open(pid,'w') as target: target.write(str(os.getpid()))
        def send(value):
            body=json.dumps(dict(protocolVersion='worker-1',delayMilliseconds=0,cancelled=False,**value)).encode()
            sys.stdout.buffer.write(struct.pack('<i',len(body))+body);sys.stdout.buffer.flush()
        while True:
            prefix=sys.stdin.buffer.read(4)
            if not prefix: break
            request=json.loads(sys.stdin.buffer.read(struct.unpack('<i',prefix)[0]))
            if request['kind']=='hello': send(dict(kind='hello',requestId=request['requestId'],processGroupId=os.getpid()));continue
            if request['kind']=='shutdown': break
            raw=open(cap,'rb').read()
            send(dict(kind='task-audio-analysis-capabilities-result-v1',requestId=request['requestId'],taskId=request['taskId'],
                inputSha256=request['inputSha256'],payload=base64.b64encode(raw).decode(),outputSha256=hashlib.sha256(raw).hexdigest(),progressSequence=0))
        """;
}
