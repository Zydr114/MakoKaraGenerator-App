using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerAnalysisContractTests
{
    [Theory]
    [InlineData("header")] [InlineData("version")] [InlineData("reserved")] [InlineData("task")] [InlineData("length")]
    public void 分析封包畸形身份预算和inputSHA拒绝(string mode)
    {
        var meta = new WorkerAnalysisMetadata(WorkerAnalysisContract.Profile, new("/owned/input.wav", 44, new('a', 64)), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        byte[] bytes = WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), meta); string sha = WorkerProtocol.ComputeSha256(bytes);
        switch (mode) { case "header": bytes[0] = 0; break; case "version": bytes[4] = 9; break; case "reserved": bytes[31] = 1; break;
            case "task": bytes.AsSpan(8, 16).Clear(); break; case "length": bytes.AsSpan(24, 4).Fill(255); break; }
        Assert.Throws<WorkerProtocolException>(() => WorkerAnalysisContract.DecodeInput(bytes));
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(WorkerAnalysisContract.RequestKind, "task", sha, bytes)));
    }
    [Fact]
    public void profile授权路径及重复字段不能被静默忽略()
    {
        var meta = new WorkerAnalysisMetadata(WorkerAnalysisContract.Profile, new("relative.wav", 44, new('a', 64)), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        Assert.Throws<WorkerProtocolException>(() => WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), meta));
        Assert.Throws<WorkerProtocolException>(() => WorkerAnalysisContract.DecodeJson<WorkerAnalysisCapabilitiesDto>(
            "{\"guardAvailable\":true,\"guardAvailable\":false,\"toolStartupVerified\":true}"u8, 4096));
        Assert.Throws<WorkerProtocolException>(() => WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), meta with { Audio = null }));
    }
    [Fact]
    public void 完整点预算保守ASCII上界小于既有payload上限且短尾只在prepared向上取整()
    {
        var data = new AudioAnalysisRevision(new('a', 64), new('b', 64), WorkerAnalysisContract.MaximumSamples,
            Enumerable.Range(0, WorkerAnalysisContract.MaximumPoints).Select(index =>
                new AudioAnalysisPoint((long)index * 20000, 2.2250738585072014E-308, .9999999999999999, .12345678901234567, .9999999999999999)));
        byte[] bytes = AudioAnalysisJson.Encode(data);
        Assert.True(bytes.Length < WorkerProtocol.MaxPayloadBytes);
        Assert.Equal(15000, AnalysisResultCodec.Decode(bytes, new('a', 64)).Points.Length);
        var decoding = new WorkerMessage(WorkerAnalysisContract.ProgressKind, "task", progressSequence: 1,
            analysisProgress: new("decoding", "48625", TimeUs: "1013020"));
        Assert.Equal(2, WorkerAnalysisContract.ValidateProgress(decoding, 0, 0, -1));
        var prepared = decoding with { ProgressSequence = 2, AnalysisProgress = new("prepared", "48625", "48625", "1013021") };
        Assert.Equal(4, WorkerAnalysisContract.ValidateProgress(prepared, 1, 48625, 2));
        Assert.Throws<WorkerProtocolException>(() => WorkerAnalysisContract.ValidateProgress(prepared with
            { AnalysisProgress = prepared.AnalysisProgress! with { TimeUs = "1013020" } }, 1, 48625, 2));
    }
}
