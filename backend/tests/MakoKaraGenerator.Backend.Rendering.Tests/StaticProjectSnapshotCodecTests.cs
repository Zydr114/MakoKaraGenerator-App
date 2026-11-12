using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class StaticProjectSnapshotCodecTests
{
    [Fact]
    public void 全静态源往返保留多来源注音渐变场景和三态覆盖且编码幂等()
    {
        ProjectSnapshot source = StaticSnapshotFixture.Create();
        byte[] input = StaticProjectSnapshotCodec.Encode(source);
        ProjectSnapshot copy = StaticProjectSnapshotCodec.Decode(input);

        Assert.NotSame(source, copy);
        Assert.Equal(source.Name, copy.Name);
        Assert.Equal(source.Composition, copy.Composition);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.LyricPresentation, copy.LyricPresentation);
        Assert.Equal(source.LyricStyle, copy.LyricStyle);
        Assert.Equal(source.EnterAnimation, copy.EnterAnimation);
        Assert.Equal(source.LyricStylePresetId, copy.LyricStylePresetId);
        Assert.Equal(source.Background, copy.Background);
        Assert.Equal(input, StaticProjectSnapshotCodec.Encode(copy));
        Assert.Equal(2, copy.Lyrics.Branches.Length);
        Assert.NotEmpty(copy.Lyrics.Lines[0].RubyAnnotations);
        Assert.Contains(copy.LyricPresentation.Nodes, node => node.Content is CountdownSequenceContent);
        Assert.Contains(copy.LyricPresentation.Nodes, node => node.Content is TextMarkContent);
        LyricStyleOverride style = copy.Lyrics.Lines[0].StyleOverride!;
        Assert.True(style.Shadow.IsSet);
        Assert.Null(style.Shadow.Value);
        Assert.False(style.SungFill.IsSet);
        Assert.True(style.UnsungFill.IsSet);
        Assert.IsType<MaterialSpec.Solid>(style.UnsungFill.Value!.Material.Value);
        Assert.IsType<MaterialSpec.Gradient>(copy.LyricStyle.UnsungFill.Material);
        Assert.IsType<MaterialSpec.Gradient>(copy.LyricStyle.Stroke!.Material);
        Assert.Equal(FrameRate.NtscVideo, copy.Composition.FrameRate);
    }

    [Fact]
    public void 全部十五源字段都有位置且计算属性和兼容访问外观不参与合同()
    {
        JsonObject root = Root();
        JsonObject source = root["snapshot"]!.AsObject();
        Assert.Equal(15, source.Count);
        Assert.Equal(typeof(ProjectSnapshot).GetProperties().Where(property => property.SetMethod is not null && property.Name != nameof(ProjectSnapshot.MediaFeatureInput))
            .Select(property => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order(StringComparer.Ordinal),
            source.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["audioAnalysis", "audioMedia", "audioPath", "background", "composition", "effectInputsFingerprint",
            "effects", "enterAnimation", "lyricPresentation", "lyricStyle", "lyricStylePresetId", "lyrics", "name",
            "presetApplications", "schemaVersion"], source.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["branches", "primaryBranchId", "schemaVersion"], source["lyrics"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        JsonObject style = source["lyricStyle"]!.AsObject();
        Assert.Null(style["fontFamilyName"]);
        Assert.Null(style["unsungColor"]);
        Assert.Null(style["isValid"]);
        Assert.Equal(["material"], style["unsung"]!["fill"]!.AsObject().Select(pair => pair.Key));
        Assert.Null(style["shadow"]); Assert.Null(style["stroke"]);
        Assert.Null(style["unsung"]!["shadow"]?["offsetXPx"]);
        Assert.Null(source["composition"]!["frameRate"]!["value"]);
        Assert.Null(source["background"]!["hasMedia"]);
        Assert.Null(source["lyrics"]!["lines"]);
    }

    [Fact]
    public void 大于JS安全整数的整数微秒端点和有理帧率无损()
    {
        const long start = 9_007_199_254_741_119;
        var range = new TimeRange(start, start + 17);
        var line = LyricLine.Create("long-line", "A", range, TimingStatus.Edited,
            [LyricToken.Create("long-token", "A", range, TimingStatus.Edited)]);
        ProjectSnapshot source = ProjectSnapshot.FromProject(Project.Create("long-time",
            Composition.Create(320, 180, FrameRate.NtscVideo, outputDurationUs: start + 100), LyricsDocument.Create([line])));
        ProjectSnapshot copy = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(source));
        Assert.Equal(start, copy.Lyrics.Lines[0].Time!.Value.StartUs);
        Assert.Equal(start + 17, copy.Lyrics.Lines[0].Tokens[0].Time!.Value.EndUs);
        Assert.Equal(start + 100, copy.Composition.OutputDurationUs);
        Assert.Equal(30000, copy.Composition.FrameRate.Numerator);
        Assert.Equal(1001, copy.Composition.FrameRate.Denominator);
    }

    [Theory]
    [InlineData("contractVersion")]
    [InlineData("profile")]
    [InlineData("engineIdentity")]
    public void 专用结构能力和引擎版本不匹配拒绝(string field)
    {
        JsonObject root = Root(); root[field] = "different";
        AssertCode("SnapshotVersionMismatch", root);
    }

    [Theory]
    [InlineData("effects", "SnapshotEffectsUnsupported")]
    [InlineData("presetApplications", "SnapshotEffectsUnsupported")]
    [InlineData("audioAnalysis", "SnapshotMediaUnsupported")]
    public void 传输非空不支持源拒绝且不静默丢失(string field, string code)
    {
        JsonObject root = Root(); root["snapshot"]![field] = new JsonObject();
        AssertCode(code, root);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("snapshot")]
    [InlineData("style")]
    [InlineData("lyrics")]
    [InlineData("sourceFacade")]
    [InlineData("computed")]
    public void 任一深度未知字段计算属性和可写兼容外观拒绝(string where)
    {
        JsonObject root = Root();
        JsonObject source = root["snapshot"]!.AsObject();
        switch (where)
        {
            case "root": root["extra"] = true; break;
            case "snapshot": source["extra"] = true; break;
            case "style": source["lyricStyle"]!["extra"] = true; break;
            case "lyrics": source["lyrics"]!["lines"] = new JsonArray(); break;
            case "sourceFacade": source["lyricStyle"]!["unsung"]!["fill"]!["color"] = "#FFFFFF"; break;
            case "computed": source["composition"]!["isValid"] = true; break;
        }
        AssertCode("SnapshotInvalid", root);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("nullable")]
    [InlineData("nested")]
    public void 缺失必填源即使原值可空也拒绝(string where)
    {
        JsonObject root = Root();
        switch (where)
        {
            case "source": root["snapshot"]!.AsObject().Remove("composition"); break;
            case "nullable": root["snapshot"]!.AsObject().Remove("audioPath"); break;
            case "nested": root["snapshot"]!["lyricStyle"]!["typography"]!.AsObject().Remove("letterSpacingPx"); break;
        }
        AssertCode("SnapshotInvalid", root);
    }

    [Fact]
    public void 重复字段任意深度拒绝()
    {
        string json = Encoding.UTF8.GetString(StaticProjectSnapshotCodec.Encode(StaticSnapshotFixture.Create()));
        string malformed = json.Replace("\"canvasWidth\":640", "\"canvasWidth\":640,\"canvasWidth\":640", StringComparison.Ordinal);
        Assert.NotEqual(json, malformed);
        Assert.Equal("SnapshotDuplicateField", Assert.Throws<SnapshotContractException>(
            () => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(malformed))).Code);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("enum")]
    [InlineData("nullSource")]
    [InlineData("nullNested")]
    [InlineData("size")]
    [InlineData("time")]
    [InlineData("frameRate")]
    [InlineData("unreducedFrameRate")]
    [InlineData("materialKind")]
    [InlineData("sceneKind")]
    public void 非法领域值和判别类型拒绝(string where)
    {
        JsonObject root = Root();
        JsonNode source = root["snapshot"]!;
        switch (where)
        {
            case "schema": source["schemaVersion"] = 27; break;
            case "enum": source["background"]!["fit"] = "invalid"; break;
            case "nullSource": source["composition"] = null; break;
            case "nullNested": source["lyricStyle"]!["typography"] = null; break;
            case "size": source["composition"]!["canvasWidth"] = 0; break;
            case "time": source["lyrics"]!["branches"]![0]!["lines"]![0]!["time"]!["endUs"] = 0; break;
            case "frameRate": source["composition"]!["frameRate"]!["denominator"] = 0; break;
            case "unreducedFrameRate": source["composition"]!["frameRate"]!["numerator"] = 60000; source["composition"]!["frameRate"]!["denominator"] = 2002; break;
            case "materialKind": source["lyricStyle"]!["unsung"]!["fill"]!["material"]!["kind"] = "future"; break;
            case "sceneKind": source["lyricPresentation"]!["nodes"]!.AsArray().First(node => node!["content"] is not null)!["content"]!["kind"] = "future"; break;
        }
        AssertCode(where == "schema" ? "SnapshotVersionMismatch" : "SnapshotInvalid", root);
    }

    [Fact]
    public void 原输入非法非有限数重复ID和零帧率在编码前拒绝()
    {
        ProjectSnapshot source = StaticSnapshotFixture.Create();
        AssertInvalid(source with { LyricStyle = source.LyricStyle with { Scale = float.NaN } });
        AssertInvalid(source with { Composition = source.Composition with { FrameRate = default } });
        AssertInvalid(source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(source.Lyrics.Branches[0]) } });
    }

    [Fact]
    public void 输入字节和文本上限拒绝且不截断()
    {
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(
            () => StaticProjectSnapshotCodec.Decode(new byte[StaticProjectSnapshotCodec.MaximumBytes + 1])).Code);
        ProjectSnapshot source = StaticSnapshotFixture.Create();
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(
            () => StaticProjectSnapshotCodec.Encode(source with { Name = new string('A', StaticProjectSnapshotCodec.MaximumBytes + 1) })).Code);
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(
            () => StaticProjectSnapshotCodec.Encode(source with { Name = new string('歌', StaticProjectSnapshotCodec.MaximumBytes / 2) })).Code);
    }

    [Fact]
    public void 输入集合对象预算在领域反序列化前拒绝()
    {
        string input = "[" + string.Join(',', Enumerable.Repeat("0", StaticProjectSnapshotCodec.MaximumSourceValues)) + "]";
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(
            () => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(input))).Code);
    }

    [Fact]
    public void 不支持源和预算在编码前明确拒绝()
    {
        ProjectSnapshot source = StaticSnapshotFixture.Create();
        AssertUnsupported(source with { AudioMedia = new(new string('a', 64), 1, "a.wav") }, "SnapshotInvalid");
        AssertUnsupported(source with { AudioAnalysis = ProjectAudioAnalysis.Failed("failed", "real failure") }, "SnapshotMediaUnsupported");
        AssertUnsupported(source with { Background = BackgroundSource.ForVideo("/not-read.mp4") }, "SnapshotMediaUnsupported");
        AssertUnsupported(source with { Background = BackgroundSource.Color with { Path = "/not-read.png" } }, "SnapshotMediaUnsupported");
        AssertUnsupported(source with { EffectInputsFingerprint = "input" }, "SnapshotEffectsUnsupported");
        AssertUnsupported(source with { Effects = new(budgets: new() { DrawCommands = 10 }) }, "SnapshotEffectsUnsupported");
        AssertUnsupported(source with { Effects = new([EffectPackageFixture.Contents()]) }, "SnapshotEffectsUnsupported");
        var template = StyleTemplate.Create("static", "static", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.Scale]);
        AssertUnsupported(source with { PresetApplications = new([new("application", new(ProjectEffectBindingKind.Lines, ids: ["first"]), template)]) }, "SnapshotEffectsUnsupported");
        AssertUnsupported(source with { LyricStyle = source.LyricStyle with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial("package", "sha256:" + new string('a', 64), "material") } } }, "SnapshotEffectsUnsupported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 音频声明原值保留且不存在的路径不被读取或授予文件能力(bool hasIdentity)
    {
        ProjectSnapshot source = StaticSnapshotFixture.Create() with
        {
            AudioPath = "/nonexistent-snapshot-codec-must-not-read/audio.wav",
            AudioMedia = hasIdentity ? new(new string('a', 64), 42, "audio.wav") : null,
        };
        ProjectSnapshot copy = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(source));
        Assert.Equal(source.AudioPath, copy.AudioPath);
        Assert.Equal(source.AudioMedia, copy.AudioMedia);
    }

    [Fact]
    public void 音频身份脱离路径或坏摘要拒绝()
    {
        JsonObject root = Root();
        root["snapshot"]!["audioMedia"] = new JsonObject { ["hash"] = new string('a', 64), ["byteLength"] = 42, ["originalName"] = "audio.wav" };
        AssertCode("SnapshotInvalid", root);
        root["snapshot"]!["audioPath"] = "/must-not-read.wav";
        root["snapshot"]!["audioMedia"]!["hash"] = "bad";
        AssertCode("SnapshotInvalid", root);
    }

    [Fact]
    public void 未设置三态携带值拒绝()
    {
        JsonObject root = Root();
        JsonNode line = root["snapshot"]!["lyrics"]!["branches"]![0]!["lines"]![0]!;
        line["styleOverride"]!["unsung"]!["fill"]!["isSet"] = false;
        AssertCode("SnapshotInvalid", root);
    }

    private static JsonObject Root() => JsonNode.Parse(StaticProjectSnapshotCodec.Encode(StaticSnapshotFixture.Create()))!.AsObject();
    private static void AssertCode(string code, JsonObject root) => Assert.Equal(code,
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(root.ToJsonString()))).Code);
    private static void AssertInvalid(ProjectSnapshot source) => AssertUnsupported(source, "SnapshotInvalid");
    private static void AssertUnsupported(ProjectSnapshot source, string code) => Assert.Equal(code,
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Encode(source)).Code);
}
