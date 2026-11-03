using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class PresetStaticProjectSnapshotCodecTests
{
    [Theory]
    [InlineData(null)] [InlineData("available")] [InlineData("failed")] [InlineData("noStream")]
    public void 新能力完整保六目标顺序固定快照且分析可选(string? state)
    {
        ProjectSnapshot source = PresetSnapshotFixture.Create(state);
        Assert.Equal(StaticProjectSnapshotCodec.PresetsProfile, StaticProjectSnapshotCodec.SelectProfile(source));
        byte[] encoded = Encode(source);
        ProjectSnapshot copy = StaticProjectSnapshotCodec.Decode(encoded);
        Assert.Equal(source.PresetApplications, copy.PresetApplications);
        Assert.Equal(source.AudioAnalysis, copy.AudioAnalysis);
        Assert.Equal(source.AudioMedia, copy.AudioMedia);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.LyricPresentation, copy.LyricPresentation);
        Assert.Equal(encoded, Encode(copy));
        Assert.Null(copy.EffectInputsFingerprint);
        Assert.Empty(copy.Effects.Packages); Assert.Empty(copy.Effects.Instances);
        Assert.Equal(6, copy.PresetApplications.Items.Select(item => item.Target.Kind).Distinct().Count());
        Assert.False(copy.PresetApplications.Items[^1].Enabled);
    }

    [Theory]
    [InlineData(StyleTemplateKind.Line)] [InlineData(StyleTemplateKind.Token)] [InlineData(StyleTemplateKind.Composite)]
    public void 三类纯静态模板保原类别而非统一重建为Line(StyleTemplateKind kind)
    {
        var source = PresetSnapshotFixture.Create();
        var template = StyleTemplate.Create("fixed", "固定", kind, source.LyricStyle, [LyricStyleProperty.Opacity]);
        var target = kind == StyleTemplateKind.Token ? new ProjectEffectBinding(ProjectEffectBindingKind.Tokens, ids: ["first-a"])
            : new ProjectEffectBinding(ProjectEffectBindingKind.Project);
        source = source with { PresetApplications = new([new PresetApplication("application", target, template)]) };
        Assert.Equal(kind, StaticProjectSnapshotCodec.Decode(Encode(source)).PresetApplications.Items[0].Snapshot.Kind);
    }

    [Fact]
    public void 原默认与旧两能力始终拒绝固定预设且空集合继续选旧能力()
    {
        var source = PresetSnapshotFixture.Create();
        Code("SnapshotEffectsUnsupported", () => StaticProjectSnapshotCodec.Encode(source));
        Code("SnapshotEffectsUnsupported", () => StaticProjectSnapshotCodec.ValidateSupported(source));
        Code("SnapshotEffectsUnsupported", () => StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.AnalysisProfile));
        Code("SnapshotPresetInvalid", () => Encode(source with { PresetApplications = PresetApplications.Empty }));
        var empty = source with { PresetApplications = PresetApplications.Empty };
        Assert.Equal(StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.SelectProfile(empty));
        Assert.Equal(StaticProjectSnapshotCodec.AnalysisProfile, StaticProjectSnapshotCodec.SelectProfile(empty with
        { AudioAnalysis = PackageSnapshotContractFixture.Analysis("available") }));
        var root = Root(); root["profile"] = StaticProjectSnapshotCodec.Profile;
        Code("SnapshotEffectsUnsupported", () => Decode(root));
        root["profile"] = StaticProjectSnapshotCodec.AnalysisProfile;
        Code("SnapshotEffectsUnsupported", () => Decode(root));
    }

    [Fact]
    public void 保全部二十一属性及未选完整样式而非只传当前外观()
    {
        var source = PresetSnapshotFixture.Create(); var first = source.PresetApplications.Items[0];
        var properties = LyricStylePropertyExtensions.All.Reverse().ToImmutableArray();
        var snapshot = StyleTemplate.Create("all", "全部", StyleTemplateKind.Line, first.Snapshot.Style with
        { Typography = first.Snapshot.Style.Typography with { FontFamilyName = null, FallbackFamilyNames = [] }, Stroke = null, Shadow = null }, properties);
        source = source with { PresetApplications = new(source.PresetApplications.Items.SetItem(0, ReplaceSnapshot(first, snapshot))) };
        var actual = StaticProjectSnapshotCodec.Decode(Encode(source)).PresetApplications.Items[0].Snapshot;
        Assert.Equal(properties.ToArray(), actual.Properties.ToArray());
        Assert.Equal(snapshot.Style, actual.Style);
        var json = Root(source)["snapshot"]!["presetApplications"]!["items"]![0]!["snapshot"]!["style"]!;
        Assert.True(json.AsObject().ContainsKey("stroke")); Assert.Null(json["stroke"]);
        Assert.True(json["typography"]!.AsObject().ContainsKey("fontFamilyName")); Assert.Null(json["typography"]!["fontFamilyName"]);
    }

    [Theory]
    [InlineData("missing-style")] [InlineData("null-style")] [InlineData("missing-opacity")]
    [InlineData("missing-stroke")] [InlineData("missing-font")] [InlineData("unknown-style")]
    [InlineData("wrong-case-style")] [InlineData("wrong-case-enum")] [InlineData("numeric-enum")]
    [InlineData("unknown-app")] [InlineData("missing-enabled")] [InlineData("null-enabled")]
    [InlineData("null-properties")] [InlineData("duplicate-property")] [InlineData("wrong-case-property")]
    [InlineData("missing-identities")] [InlineData("unknown-target")] [InlineData("wrong-case-target")]
    [InlineData("missing-node-flag")] [InlineData("multiple-line-ids")] [InlineData("duplicate-id")]
    [InlineData("duplicate-target")] [InlineData("empty-apps")] [InlineData("null-apps")]
    [InlineData("unknown-kind")] [InlineData("wrong-version")] [InlineData("trimmed-name")]
    [InlineData("numeric-string")] [InlineData("color-overflow")] [InlineData("unknown-gradient")]
    [InlineData("gradient-enum-case")] [InlineData("null-gradient-stop")] [InlineData("null-target")]
    [InlineData("unknown-template")] [InlineData("missing-recipes")]
    public void 固定源严格必填空值大小写与身份不被持久化默认补全(string mutation)
    {
        var root = Root(); var applications = root["snapshot"]!["presetApplications"]!;
        var items = applications["items"]!.AsArray(); var first = items[0]!;
        var snapshot = first["snapshot"]!; var style = snapshot["style"]!;
        switch (mutation)
        {
            case "missing-style": snapshot.AsObject().Remove("style"); break;
            case "null-style": snapshot["style"] = null; break;
            case "missing-opacity": style.AsObject().Remove("opacity"); break;
            case "missing-stroke": style.AsObject().Remove("stroke"); break;
            case "missing-font": style["typography"]!.AsObject().Remove("fontFamilyName"); break;
            case "unknown-style": style["extra"] = true; break;
            case "wrong-case-style": style["Opacity"] = style["opacity"]!.DeepClone(); style.AsObject().Remove("opacity"); break;
            case "wrong-case-enum": style["horizontalAlignment"] = "center"; break;
            case "numeric-enum": style["horizontalAlignment"] = 0; break;
            case "unknown-app": first["extra"] = 1; break;
            case "missing-enabled": first.AsObject().Remove("enabled"); break;
            case "null-enabled": first["enabled"] = null; break;
            case "null-properties": snapshot["properties"] = null; break;
            case "duplicate-property": snapshot["properties"]!.AsArray().Add("opacity"); break;
            case "wrong-case-property": snapshot["properties"]![0] = "Opacity"; break;
            case "missing-identities": first.AsObject().Remove("identities"); break;
            case "unknown-target": first["target"]!["extra"] = true; break;
            case "wrong-case-target": first["target"]!["kind"] = "Project"; break;
            case "missing-node-flag": items[5]!["target"]!.AsObject().Remove("includeDescendants"); break;
            case "multiple-line-ids": items[2]!["target"]!["lineIds"]!.AsArray().Add("second"); break;
            case "duplicate-id": items[1]!["id"] = first["id"]!.DeepClone(); break;
            case "duplicate-target": items[1]!["target"] = first["target"]!.DeepClone(); break;
            case "empty-apps": applications["items"] = new JsonArray(); break;
            case "null-apps": root["snapshot"]!["presetApplications"] = null; break;
            case "unknown-kind": snapshot["kind"] = "line"; break;
            case "wrong-version": applications["templateSchemaVersion"] = StyleTemplateLibrary.CurrentSchemaVersion + 1; break;
            case "trimmed-name": snapshot["name"] = " trailing "; break;
            case "numeric-string": style["opacity"] = "0.8"; break;
            case "color-overflow": style["sungFill"]!["material"]!["color"]!["red"] = 256; break;
            case "unknown-gradient": style["unsungFill"]!["material"]!["value"]!["extra"] = true; break;
            case "gradient-enum-case": style["unsungFill"]!["material"]!["value"]!["kind"] = "linear"; break;
            case "null-gradient-stop": style["unsungFill"]!["material"]!["value"]!["stops"]![0] = null; break;
            case "null-target": first["target"] = null; break;
            case "unknown-template": snapshot["extra"] = 1; break;
            case "missing-recipes": snapshot.AsObject().Remove("effectRecipes"); break;
        }
        Assert.Throws<SnapshotContractException>(() => Decode(root));
    }

    [Theory]
    [InlineData("recipes")] [InlineData("packages")] [InlineData("identities")] [InlineData("inputs")]
    public void 禁用应用也拒绝全部高级存储字段(string mutation)
    {
        var root = Root(); var last = root["snapshot"]!["presetApplications"]!["items"]!.AsArray()[^1]!;
        Assert.False(last["enabled"]!.GetValue<bool>());
        if (mutation == "inputs") last["branchInputs"]!["advanced"] = "primary";
        else if (mutation == "identities") last["identities"]!.AsArray().Add(new JsonObject());
        else last["snapshot"]![mutation == "recipes" ? "effectRecipes" : "packages"]!.AsArray().Add(new JsonObject());
        Code("SnapshotEffectsUnsupported", () => Decode(root));
    }

    [Fact]
    public void 禁用未选包材质和工程残留包仍不能被静态能力忽略()
    {
        var source = PresetSnapshotFixture.Create(); var last = source.PresetApplications.Items[^1];
        var package = MixedEffectPackageFixture.Contents(pure: true);
        var style = last.Snapshot.Style with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } };
        var advanced = StyleTemplate.Create("advanced", "高级", StyleTemplateKind.Line, style, [LyricStyleProperty.FontSizePx], packages: [package]);
        source = source with { PresetApplications = new(source.PresetApplications.Items.SetItem(source.PresetApplications.Items.Length - 1, ReplaceSnapshot(last, advanced))) };
        Code("SnapshotEffectsUnsupported", () => Encode(source));
        Code("SnapshotEffectsUnsupported", () => Encode(PresetSnapshotFixture.Create() with { Effects = new([package]) }));
        Code("SnapshotEffectsUnsupported", () => Encode(PresetSnapshotFixture.Create() with { Effects = new(budgets: new() { LiveParticles = 1 }) }));
    }

    [Fact]
    public void 全图重复字段和四MiB字节预算不按应用重置()
    {
        string json = Encoding.UTF8.GetString(Encode(PresetSnapshotFixture.Create()));
        string duplicate = json.Replace("\"opacity\":0.8", "\"opacity\":0.8,\"opacity\":0.8", StringComparison.Ordinal);
        Assert.NotEqual(json, duplicate);
        Code("SnapshotDuplicateField", () => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(duplicate)));
        var source = PresetSnapshotFixture.Create(); var item = source.PresetApplications.Items[0];
        var huge = item.Snapshot with { Name = new string('x', StaticProjectSnapshotCodec.MaximumBytes) };
        Code("SnapshotSizeExceeded", () => Encode(source with { PresetApplications = new([ReplaceSnapshot(item, huge)]) }));
        var utf8 = source with { PresetApplications = new([ReplaceSnapshot(item, huge with { Name = new string('歌', 700_000) })]) };
        Code("SnapshotSizeExceeded", () => StaticProjectSnapshotCodec.ValidateSupported(utf8, StaticProjectSnapshotCodec.PresetsProfile));
    }

    [Fact]
    public void 模板与普通源共用二十万值预算且不可通过每部分均未超限绕过()
    {
        var source = PresetSnapshotFixture.Create();
        var names = Enumerable.Range(0, 110_000).Select(index => "font" + index).ToImmutableArray();
        var style = source.LyricStyle with { Typography = source.LyricStyle.Typography with { FallbackFamilyNames = names } };
        var item = source.PresetApplications.Items[0];
        var template = StyleTemplate.Create("many-fonts", "full legacy fallback", StyleTemplateKind.Line, style, [LyricStyleProperty.Opacity]);
        // Each component alone fits; the complete envelope must fail before rendering.
        var apps = new PresetApplications([ReplaceSnapshot(item, template)]);
        Assert.NotEmpty(Encode(source with { PresetApplications = apps }));
        Assert.NotEmpty(StaticProjectSnapshotCodec.Encode(source with { LyricStyle = style, PresetApplications = PresetApplications.Empty }));
        Code("SnapshotSizeExceeded", () => Encode(source with { LyricStyle = style, PresetApplications = apps }));
    }

    [Fact]
    public void 完整分析要求同源且高级包profile不能借用此预设入口()
    {
        var source = PresetSnapshotFixture.Create("available");
        Code("SnapshotAnalysisInvalid", () => Encode(source with { AudioMedia = null }));
        Code("SnapshotAnalysisInvalid", () => Encode(source with { AudioMedia = new(new string('f', 64), 4096, "other.wav") }));
        Code("SnapshotEffectsUnsupported", () => PackageProjectSnapshotCodec.Encode(source));
    }

    private static PresetApplication ReplaceSnapshot(PresetApplication item, StyleTemplate snapshot)
        => new(item.Id, item.Target, snapshot, item.Identities, item.BranchInputs, item.Enabled);
    private static byte[] Encode(ProjectSnapshot source) => StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.PresetsProfile);
    private static JsonObject Root(ProjectSnapshot? source = null) => JsonNode.Parse(Encode(source ?? PresetSnapshotFixture.Create()))!.AsObject();
    private static ProjectSnapshot Decode(JsonObject root) => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(root.ToJsonString()));
    private static void Code(string expected, Action action) => Assert.Equal(expected, Assert.Throws<SnapshotContractException>(action).Code);
}
