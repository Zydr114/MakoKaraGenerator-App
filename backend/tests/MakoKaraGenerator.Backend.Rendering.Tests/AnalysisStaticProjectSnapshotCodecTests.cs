using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class AnalysisStaticProjectSnapshotCodecTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void 原068实际捕获的旧能力全部字节除产品引擎版本外保持不变()
    {
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "static-snapshot-v1.baseline.json"));
        byte[] expected = Encoding.UTF8.GetBytes(fixture.Replace("{{ENGINE_IDENTITY}}", StaticProjectSnapshotCodec.EngineIdentity, StringComparison.Ordinal));
        ProjectSnapshot captured = StaticProjectSnapshotCodec.Decode(expected);
        Assert.Equal(StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.SelectProfile(captured));
        Assert.Equal(expected, StaticProjectSnapshotCodec.Encode(captured));
        Assert.Equal(expected, StaticProjectSnapshotCodec.Encode(captured, StaticProjectSnapshotCodec.Profile));
    }

    [Theory]
    [InlineData("available")]
    [InlineData("failed")]
    [InlineData("noStream")]
    public void 新能力完整保留三种非空状态和所有静态源且原能力始终拒绝(string state)
    {
        ProjectAudioAnalysis analysis = Analysis(state);
        ProjectSnapshot source = StaticSnapshotFixture.Create() with
        { AudioPath = "/not-read/source.wav", AudioMedia = new(Hash, 4096, "source.wav"), AudioAnalysis = analysis };
        Assert.Equal(StaticProjectSnapshotCodec.AnalysisProfile, StaticProjectSnapshotCodec.SelectProfile(source));
        AssertCode("SnapshotMediaUnsupported", () => StaticProjectSnapshotCodec.Encode(source));
        AssertCode("SnapshotMediaUnsupported", () => StaticProjectSnapshotCodec.ValidateSupported(source));
        byte[] encoded = Encode(source);
        ProjectSnapshot copy = StaticProjectSnapshotCodec.Decode(encoded);
        Assert.Equal(analysis, copy.AudioAnalysis);
        Assert.Equal(source.AudioPath, copy.AudioPath);
        Assert.Equal(source.AudioMedia, copy.AudioMedia);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.LyricPresentation, copy.LyricPresentation);
        Assert.Equal(source.LyricStyle, copy.LyricStyle);
        Assert.Equal(source.Composition, copy.Composition);
        Assert.Equal(source.Background, copy.Background);
        Assert.Null(copy.EffectInputsFingerprint);
        Assert.Equal(encoded, Encode(copy));
        if (analysis.Data is { } data)
        {
            using JsonDocument wire = JsonDocument.Parse(encoded);
            JsonElement wrapper = wire.RootElement.GetProperty("snapshot").GetProperty("audioAnalysis");
            Assert.Equal(data.ContentHash, wrapper.GetProperty("contentHash").GetString());
            Assert.Equal(AudioAnalysisJson.Encode(data), Encoding.UTF8.GetBytes(wrapper.GetProperty("data").GetRawText()));
            Assert.Equal(data.Points.ToArray(), copy.AudioAnalysis!.Data!.Points.ToArray());
            Assert.Equal(20_021, copy.AudioAnalysis.Data.DurationUs);
        }
    }

    [Fact]
    public void 空与非空能力互斥且旧封包不能偷带完整分析()
    {
        ProjectSnapshot empty = Source() with { AudioAnalysis = null };
        Assert.Equal(StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.SelectProfile(empty));
        AssertCode("SnapshotAnalysisInvalid", () => Encode(empty));
        JsonObject newWire = Root();
        newWire["profile"] = StaticProjectSnapshotCodec.Profile;
        AssertCode("SnapshotMediaUnsupported", () => Decode(newWire));
        JsonObject oldWire = JsonNode.Parse(StaticProjectSnapshotCodec.Encode(empty))!.AsObject();
        oldWire["profile"] = StaticProjectSnapshotCodec.AnalysisProfile;
        AssertCode("SnapshotAnalysisInvalid", () => Decode(oldWire));
        AssertCode("SnapshotVersionMismatch", () => StaticProjectSnapshotCodec.Encode(Source(), "static-unknown"));
    }

    [Theory]
    [InlineData("available")]
    [InlineData("failed")]
    [InlineData("noStream")]
    public void 新能力要求非空音频路径与固定媒体身份(string state)
    {
        ProjectSnapshot source = Source(state);
        AssertCode("SnapshotAnalysisInvalid", () => Encode(source with { AudioPath = null, AudioMedia = null }));
        AssertCode("SnapshotAnalysisInvalid", () => Encode(source with { AudioMedia = null }));
        JsonObject wire = Root(state);
        wire["snapshot"]!["audioPath"] = null;
        wire["snapshot"]!["audioMedia"] = null;
        AssertCode("SnapshotAnalysisInvalid", () => Decode(wire));
    }

    [Fact]
    public void 成功分析必须绑定同一固定媒体摘要且原始完整数据不能被替换()
    {
        ProjectSnapshot source = Source();
        AssertCode("SnapshotAnalysisInvalid", () => Encode(source with { AudioMedia = new(new string('c', 64), 4096, "source.wav") }));
        JsonObject wire = Root();
        wire["snapshot"]!["audioMedia"]!["hash"] = new string('c', 64);
        AssertCode("SnapshotAnalysisInvalid", () => Decode(wire));
        wire = Root();
        wire["snapshot"]!["audioAnalysis"]!["contentHash"] = new string('0', 64);
        AssertCode("SnapshotAnalysisInvalid", () => Decode(wire));
    }

    [Theory]
    [InlineData("unknownState")]
    [InlineData("extraWrapper")]
    [InlineData("missingState")]
    [InlineData("missingHop")]
    [InlineData("wrongRevision")]
    [InlineData("invalidDouble")]
    [InlineData("failureWithData")]
    public void 非规范未知或不完整分析不能变成成功快照(string kind)
    {
        JsonObject wire = Root(kind == "failureWithData" ? "failed" : "available");
        JsonNode wrapper = wire["snapshot"]!["audioAnalysis"]!;
        switch (kind)
        {
            case "unknownState": wrapper["state"] = "invented"; break;
            case "extraWrapper": wrapper["summaryOnly"] = true; break;
            case "missingState": wrapper.AsObject().Remove("state"); break;
            case "missingHop": wrapper["data"]!["points"]!.AsArray().RemoveAt(1); break;
            case "wrongRevision": wrapper["data"]!["revision"] = "sha256:" + new string('0', 64); break;
            case "invalidDouble": wrapper["data"]!["points"]![0]!["bassLevel"] = "NaN"; break;
            case "failureWithData": wrapper["data"] = new JsonObject(); break;
        }
        AssertCode("SnapshotAnalysisInvalid", () => Decode(wire));
    }

    [Fact]
    public void 规范数据中的空白与重复字段不会被重编码掩盖()
    {
        string wire = Encoding.UTF8.GetString(Encode(Source()));
        string changed = wire.Replace("\"data\":{\"version\":", "\"data\":{ \"version\":", StringComparison.Ordinal);
        Assert.NotEqual(wire, changed);
        AssertCode("SnapshotAnalysisInvalid", () => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(changed)));
        changed = wire.Replace("\"state\":\"available\"", "\"state\":\"available\",\"state\":\"available\"", StringComparison.Ordinal);
        AssertCode("SnapshotDuplicateField", () => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(changed)));
    }

    [Fact]
    public void 完整分析与歌词共用字节预算且精确四MiB仍能往返()
    {
        ProjectSnapshot source = Source() with { Name = "A" };
        int originalLength = Encode(source).Length;
        source = source with { Name = new string('A', StaticProjectSnapshotCodec.MaximumBytes - originalLength + 1) };
        byte[] encoded = Encode(source);
        Assert.Equal(StaticProjectSnapshotCodec.MaximumBytes, encoded.Length);
        Assert.Equal(source.Name, StaticProjectSnapshotCodec.Decode(encoded).Name);
        AssertCode("SnapshotSizeExceeded", () => Encode(source with { Name = source.Name + "A" }));
    }

    [Fact]
    public void 分别合法的最大分析与歌词组合超过总值预算时不得只把分析算一个值()
    {
        AudioAnalysisRevision data = FullAnalysis();
        ProjectSnapshot analyzed = Source() with { AudioAnalysis = ProjectAudioAnalysis.Available(data) };
        byte[] analysisWire = Encode(analyzed);
        Assert.Equal(BoundedAudioAnalysisCodec.MaximumPoints, StaticProjectSnapshotCodec.Decode(analysisWire).AudioAnalysis!.Data!.Points.Length);
        LyricToken[] tokens = [.. Enumerable.Range(0, 19_000).Select(index => LyricToken.Create("t" + index, "A"))];
        LyricLine line = LyricLine.Create("line", new string('A', tokens.Length), tokens: tokens);
        ProjectSnapshot lyrics = analyzed with { AudioAnalysis = null, Lyrics = LyricsDocument.Create([line]) };
        byte[] lyricsWire = StaticProjectSnapshotCodec.Encode(lyrics);
        Assert.Equal(tokens.Length, StaticProjectSnapshotCodec.Decode(lyricsWire).Lyrics.Lines[0].Tokens.Length);
        JsonObject combined = JsonNode.Parse(lyricsWire)!.AsObject();
        combined["profile"] = StaticProjectSnapshotCodec.AnalysisProfile;
        combined["snapshot"]!["audioAnalysis"] = JsonNode.Parse(analysisWire)!["snapshot"]!["audioAnalysis"]!.DeepClone();
        byte[] combinedWire = Encoding.UTF8.GetBytes(combined.ToJsonString());
        Assert.True(combinedWire.Length < StaticProjectSnapshotCodec.MaximumBytes, combinedWire.Length.ToString(CultureInfo.InvariantCulture));
        using JsonDocument document = JsonDocument.Parse(combinedWire);
        Assert.True(CountValues(document.RootElement) > StaticProjectSnapshotCodec.MaximumSourceValues);
        ProjectSnapshot combinedSource = lyrics with { AudioAnalysis = analyzed.AudioAnalysis };
        AssertCode("SnapshotSizeExceeded", () => StaticProjectSnapshotCodec.ValidateSupported(combinedSource, StaticProjectSnapshotCodec.AnalysisProfile));
        AssertCode("SnapshotSizeExceeded", () => Encode(combinedSource));
        AssertCode("SnapshotSizeExceeded", () => StaticProjectSnapshotCodec.Decode(combinedWire));
    }

    [Fact]
    public void 中立完整分析边界复用原规范字节并先拒绝超限采样与点数()
    {
        AudioAnalysisRevision source = Analysis("available").Data!;
        byte[] canonical = AudioAnalysisJson.Encode(source);
        Assert.Equal(canonical, BoundedAudioAnalysisCodec.Encode(source));
        Assert.Equal(source, BoundedAudioAnalysisCodec.Decode(canonical, Hash));
        Assert.Throws<InvalidDataException>(() => BoundedAudioAnalysisCodec.Decode(canonical, new string('c', 64)));
        Assert.Throws<InvalidDataException>(() => BoundedAudioAnalysisCodec.Decode(new byte[BoundedAudioAnalysisCodec.MaximumBytes + 1], Hash));
        AudioAnalysisRevision excessive = new(Hash, new string('b', 64), BoundedAudioAnalysisCodec.MaximumSamples + 1,
            Enumerable.Range(0, BoundedAudioAnalysisCodec.MaximumPoints + 1).Select(index => new AudioAnalysisPoint(index * 20_000L, 0, 0, null, 0)));
        Assert.Throws<InvalidDataException>(() => BoundedAudioAnalysisCodec.Encode(excessive));
        Assert.Throws<InvalidDataException>(() => BoundedAudioAnalysisCodec.Decode(AudioAnalysisJson.Encode(excessive), Hash));
    }

    [Fact]
    public void 实际封包终验按converter真实字段守住同一总值边界()
    {
        ProjectSnapshot analyzed = Source() with { AudioAnalysis = ProjectAudioAnalysis.Available(FullAnalysis()) };
        int CountWire(byte[] bytes)
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return CountValues(document.RootElement);
        }
        ProjectSnapshot Styled(int count)
        {
            LyricToken[] tokens = [.. Enumerable.Range(0, count).Select(index => LyricToken.Create("s" + index, "A", styleOverride: LyricStyleOverride.Empty))];
            return analyzed with { Lyrics = LyricsDocument.Create([LyricLine.Create("line", new string('A', count), tokens: tokens)]) };
        }
        int baseline = CountWire(Encode(Styled(0)));
        int increment = CountWire(Encode(Styled(1))) - baseline;
        Assert.True(increment > 0);
        int maximumTokens = (StaticProjectSnapshotCodec.MaximumSourceValues - baseline) / increment;
        byte[] maximumWire = Encode(Styled(maximumTokens));
        Assert.True(CountWire(maximumWire) <= StaticProjectSnapshotCodec.MaximumSourceValues);
        Assert.Equal(maximumTokens, StaticProjectSnapshotCodec.Decode(maximumWire).Lyrics.Lines[0].Tokens.Length);

        ProjectSnapshot excessive = Styled(maximumTokens + 1);
        // The early object graph deliberately does not duplicate converter field accounting.
        StaticProjectSnapshotCodec.ValidateSupported(excessive, StaticProjectSnapshotCodec.AnalysisProfile);
        JsonObject raw = JsonNode.Parse(maximumWire)!.AsObject();
        JsonNode line = raw["snapshot"]!["lyrics"]!["branches"]![0]!["lines"]![0]!;
        JsonNode token = line["tokens"]![0]!.DeepClone();
        token["id"] = "additional";
        line["tokens"]!.AsArray().Add(token);
        line["text"] = new string('A', maximumTokens + 1);
        byte[] excessiveWire = Encoding.UTF8.GetBytes(raw.ToJsonString());
        Assert.True(excessiveWire.Length < StaticProjectSnapshotCodec.MaximumBytes);
        Assert.True(CountWire(excessiveWire) > StaticProjectSnapshotCodec.MaximumSourceValues);
        AssertCode("SnapshotSizeExceeded", () => Encode(excessive));
        AssertCode("SnapshotSizeExceeded", () => StaticProjectSnapshotCodec.Decode(excessiveWire));
    }

    private static ProjectAudioAnalysis Analysis(string state) => state switch
    {
        "failed" => ProjectAudioAnalysis.Failed("ActualDecodeFailure", "真实失败\n路径只是源说明 /音频/原曲.wav；不改文字。"),
        "noStream" => ProjectAudioAnalysis.NoStream("ActualNoStream", "真实无流\r\n保留理由与Unicode🎵"),
        _ => ProjectAudioAnalysis.Available(new(Hash, new string('b', 64), 961,
            [new(0, .12345678901234567, .4, null, 0), new(20_000, .3, .2, .75, .8)])),
    };

    private static AudioAnalysisRevision FullAnalysis() => new(Hash, new string('b', 64), BoundedAudioAnalysisCodec.MaximumSamples,
        Enumerable.Range(0, BoundedAudioAnalysisCodec.MaximumPoints).Select(index => new AudioAnalysisPoint(index * 20_000L, 0, 0, null, 0)));

    private static ProjectSnapshot Source(string state = "available") => ProjectSnapshot.FromProject(Project.Create("analysis", audioPath: "/not-read/source.wav")
        with { AudioMedia = new(Hash, 4096, "source.wav"), AudioAnalysis = Analysis(state) });

    private static byte[] Encode(ProjectSnapshot source) => StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.AnalysisProfile);
    private static JsonObject Root(string state = "available") => JsonNode.Parse(Encode(Source(state)))!.AsObject();
    private static ProjectSnapshot Decode(JsonObject wire) => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(wire.ToJsonString()));

    private static int CountValues(JsonElement value) => 1 + (value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Sum(property => CountValues(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Sum(CountValues),
        _ => 0,
    });

    private static void AssertCode(string expected, Action operation) => Assert.Equal(expected, Assert.Throws<SnapshotContractException>(operation).Code);
}
