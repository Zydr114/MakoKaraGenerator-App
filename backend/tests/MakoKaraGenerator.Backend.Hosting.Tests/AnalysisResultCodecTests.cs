using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class AnalysisResultCodecTests
{
    private const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static byte[] Canonical() => AudioAnalysisJson.Encode(new(Source, new string('b', 64), 961,
        [new(0, .1234567890123456, .5, .25, .8), new(20000, .2, .1, null, 0)]));
    [Fact]
    public void 完整原规范精度及双哈希保持且源不符拒绝()
    {
        byte[] bytes = Canonical(); var result = AnalysisResultCodec.Decode(bytes, Source);
        Assert.Equal(bytes, AudioAnalysisJson.Encode(result));
        Assert.Equal(.1234567890123456, result.Points[0].BassLevel);
        Assert.Equal(WorkerProtocol.ComputeSha256(bytes), result.ContentHash);
        Assert.Equal("WorkerAnalysisResultInvalid", Assert.Throws<WorkerProtocolException>(() => AnalysisResultCodec.Decode(bytes, new string('c', 64))).Code);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("grid")]
    [InlineData("fraction")]
    [InlineData("whitespace")]
    public void 原Core字段数值及规范异常都映为稳定失败(string mutation)
    {
        string value = Encoding.UTF8.GetString(Canonical());
        value = mutation switch
        {
            "missing" => value.Replace("\"version\":1,", "", StringComparison.Ordinal),
            "duplicate" => value.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            "unknown" => value.Replace("\"version\":1", "\"extra\":0,\"version\":1", StringComparison.Ordinal),
            "grid" => value.Replace("\"timeUs\":20000", "\"timeUs\":20001", StringComparison.Ordinal),
            "fraction" => value.Replace("\"sampleCount\":961", "\"sampleCount\":961.5", StringComparison.Ordinal),
            _ => value + " ",
        };
        Assert.Equal("WorkerAnalysisResultInvalid", Assert.Throws<WorkerProtocolException>(() => AnalysisResultCodec.Decode(Encoding.UTF8.GetBytes(value), Source)).Code);
    }
    [Fact]
    public void 超字节与样本预算前置拒绝不解析为领域结果()
    {
        Assert.Throws<WorkerProtocolException>(() => AnalysisResultCodec.Decode(new byte[AnalysisResultCodec.MaximumResultBytes + 1], Source));
        byte[] bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Canonical()).Replace("\"sampleCount\":961", "\"sampleCount\":14400001", StringComparison.Ordinal));
        Assert.Throws<WorkerProtocolException>(() => AnalysisResultCodec.Decode(bytes, Source));
    }
}
