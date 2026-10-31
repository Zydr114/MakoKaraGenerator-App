using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class PackageProjectSnapshotCodecTests
{
    [Fact]
    public void 资源闭包摘要保留manifest文件身份大小摘要和实际role状态()
    {
        EffectPackageContents validPackage = EffectPackageFixture.Contents();
        PackageClosureInspection valid = PackageProjectSnapshotCodec.InspectResourceClosure(validPackage);
        Assert.True(valid.IsValidated);
        Assert.True(valid.IdentityValid);
        Assert.True(valid.RoleReferencesValid);
        Assert.Equal(validPackage.PackageId, valid.PackageId);
        Assert.Equal(validPackage.Revision, valid.PackageRevision);
        Assert.Equal(validPackage.Files.Length, valid.FileCount);
        Assert.Equal(validPackage.Manifest.Length + validPackage.Files.Sum(file => file.Content.Length), valid.TotalSizeBytes);
        Assert.Empty(valid.UnsupportedPaths);

        Dictionary<string, byte[]> files = EffectPackageFixture.Files();
        JsonObject manifest = EffectPackageFixture.Manifest(files);
        manifest["files"]!.AsArray().First(item => item!["path"]!.GetValue<string>() == "README.md")!["role"] = "font";
        EffectPackageContents unsupportedPackage = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        PackageClosureInspection unsupported = PackageProjectSnapshotCodec.InspectResourceClosure(unsupportedPackage);
        Assert.False(unsupported.IsValidated);
        Assert.Contains("README.md", unsupported.UnsupportedPaths);
        Assert.Equal("font", unsupportedPackage.Files.Single(file => file.Path == "README.md").Role);
    }

    [Fact]
    public void 资源闭包摘要拒绝图对未声明role图片的隐藏引用()
    {
        JsonObject graph = EffectPackageGraphFixture.Graph();
        JsonObject input = EffectPackageGraphFixture.Input("image", "ResourceImage"); input.Remove("capture"); input["resource"] = "README.md";
        graph["inputs"]!.AsArray().Add(input);
        EffectPackageContents package = EffectPackageGraphFixture.Contents(graph: graph);
        PackageClosureInspection inspection = PackageProjectSnapshotCodec.InspectResourceClosure(package);
        Assert.False(inspection.IsValidated);
        Assert.True(inspection.IdentityValid);
        Assert.False(inspection.RoleReferencesValid);
        Assert.Equal("PackageGraphImageMissing", inspection.FailureCode);
    }

    [Fact]
    public void 十五源字段与完整包原字节全部直接实例和预算无损往返()
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        byte[] encoded = PackageProjectSnapshotCodec.Encode(source);
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(encoded);
        Assert.NotSame(source, copy);
        Assert.Equal(source.Name, copy.Name);
        Assert.Equal(source.SchemaVersion, copy.SchemaVersion);
        Assert.Equal(source.Composition, copy.Composition);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.LyricPresentation, copy.LyricPresentation);
        Assert.Equal(source.LyricStyle, copy.LyricStyle);
        Assert.Equal(source.LyricStylePresetId, copy.LyricStylePresetId);
        Assert.Equal(source.EnterAnimation, copy.EnterAnimation);
        Assert.Equal(source.Background, copy.Background);
        Assert.Equal(source.AudioPath, copy.AudioPath);
        Assert.Equal(source.AudioMedia, copy.AudioMedia);
        Assert.Equal(source.AudioAnalysis, copy.AudioAnalysis);
        Assert.Equal(source.PresetApplications, copy.PresetApplications);
        Assert.Equal(source.EffectInputsFingerprint, copy.EffectInputsFingerprint);
        Assert.Equal(source.Effects.Budgets, copy.Effects.Budgets);
        Assert.Equal(source.Effects.Instances.ToArray(), copy.Effects.Instances.ToArray());
        Assert.Equal(["first-instance", "second-instance", "disabled-instance"], copy.Effects.Instances.Select(instance => instance.Id));
        Assert.Equal(ulong.MaxValue, copy.Effects.Instances[0].Seed);
        Assert.Equal(9_007_199_254_740_993UL, copy.Effects.Instances[1].Seed);
        Assert.False(copy.Effects.Instances[2].Enabled);
        Assert.Equal(source.Effects.Packages.Length, copy.Effects.Packages.Length);
        for (int index = 0; index < source.Effects.Packages.Length; index++)
        {
            EffectPackageContents before = source.Effects.Packages[index], after = copy.Effects.Packages[index];
            Assert.NotSame(before, after);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.Profile, after.Profile);
            Assert.Equal(before.Manifest.ToArray(), after.Manifest.ToArray());
            Assert.Equal(before.Files.Select(file => (file.Path, file.Role, file.Sha256)), after.Files.Select(file => (file.Path, file.Role, file.Sha256)));
            for (int file = 0; file < before.Files.Length; file++)
                Assert.Equal(before.Files[file].Content.ToArray(), after.Files[file].Content.ToArray());
        }
        Assert.Equal(encoded, PackageProjectSnapshotCodec.Encode(copy));
        JsonObject wire = JsonNode.Parse(encoded)!.AsObject();
        Assert.Equal("package-lyrics-scene-v1", wire["profile"]!.GetValue<string>());
        Assert.Equal(typeof(ProjectSnapshot).GetProperties().Where(property => property.SetMethod is not null)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order(StringComparer.Ordinal),
            wire["snapshot"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("available")]
    [InlineData("failed")]
    [InlineData("noStream")]
    public void 完整分析三态与音频身份参与同一个精确指纹(string state)
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create(state);
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(PackageProjectSnapshotCodec.Encode(source));
        Assert.Equal(source.AudioPath, copy.AudioPath);
        Assert.Equal(source.AudioMedia, copy.AudioMedia);
        Assert.Equal(source.AudioAnalysis, copy.AudioAnalysis);
        Assert.Equal(source.EffectInputsFingerprint, copy.EffectInputsFingerprint);
        Assert.NotEqual(source.Effects.InputsFingerprint, copy.EffectInputsFingerprint);
        if (source.AudioAnalysis!.Data is { } data)
        {
            Assert.Equal(AudioAnalysisJson.Encode(data), AudioAnalysisJson.Encode(copy.AudioAnalysis!.Data!));
            Assert.Equal(data.Points.ToArray(), copy.AudioAnalysis.Data!.Points.ToArray());
        }
        AssertRejected(source with { AudioMedia = null });
        AssertRejected(source with { AudioPath = null });
        AssertRejected(source with { AudioMedia = new(new string('c', 64), 4096, "other.wav") });
        AssertRejected(source with { AudioAnalysis = PackageSnapshotContractFixture.Analysis(state == "available" ? "failed" : "available") });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("falseNull")]
    [InlineData("wrong")]
    [InlineData("effectsOnly")]
    [InlineData("changedSeed")]
    [InlineData("changedBudget")]
    [InlineData("changedOrder")]
    public void 指纹缺失伪空或旧输入绝不能静默跳过或错配脚本(string kind)
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create("available");
        JsonObject wire = Root(source);
        JsonObject snapshot = wire["snapshot"]!.AsObject();
        switch (kind)
        {
            case "missing": snapshot.Remove("effectInputsFingerprint"); break;
            case "falseNull": snapshot["effectInputsFingerprint"] = null; break;
            case "wrong": snapshot["effectInputsFingerprint"] = "sha256:" + new string('0', 64); break;
            case "effectsOnly": snapshot["effectInputsFingerprint"] = source.Effects.InputsFingerprint; break;
            case "changedSeed": snapshot["effects"]!["instances"]![0]!["seed"] = 1; break;
            case "changedBudget": snapshot["effects"]!["budgets"]!["drawCommands"] = 1; break;
            case "changedOrder":
                JsonArray instances = snapshot["effects"]!["instances"]!.AsArray();
                JsonNode first = instances[0]!.DeepClone(); instances.RemoveAt(0); instances.Add(first); break;
        }
        SnapshotContractException failure = Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(wire.ToJsonString())));
        Assert.Equal(kind == "missing" ? "SnapshotInvalid" : "SnapshotFingerprintMismatch", failure.Code);
        Assert.Equal("SnapshotFingerprintMismatch", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Encode(source with { EffectInputsFingerprint = null })).Code);
        Assert.Equal("SnapshotFingerprintMismatch", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Encode(source with { EffectInputsFingerprint = "sha256:" + new string('0', 64) })).Code);
    }

    [Fact]
    public void 空实例仍完整保留未使用包及自定义预算且无伪造指纹()
    {
        ProjectSnapshot basis = PackageSnapshotContractFixture.Create();
        ProjectSnapshot source = PackageSnapshotContractFixture.Fingerprint(basis with
        { Effects = new(basis.Effects.Packages, budgets: basis.Effects.Budgets) });
        Assert.Null(source.EffectInputsFingerprint);
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(PackageProjectSnapshotCodec.Encode(source));
        Assert.Empty(copy.Effects.Instances);
        Assert.Equal(2, copy.Effects.Packages.Length);
        Assert.Equal(source.Effects.Budgets, copy.Effects.Budgets);
        Assert.Null(copy.EffectInputsFingerprint);
        AssertRejected(source with { EffectInputsFingerprint = "sha256:" + new string('0', 64) });
    }

    [Theory]
    [InlineData(ProjectEffectBindingKind.Project)]
    [InlineData(ProjectEffectBindingKind.Branch)]
    [InlineData(ProjectEffectBindingKind.Lines)]
    [InlineData(ProjectEffectBindingKind.Tokens)]
    [InlineData(ProjectEffectBindingKind.Clips)]
    [InlineData(ProjectEffectBindingKind.Node)]
    public void 六种绑定的可空身份集合顺序与节点后代开关均保留(ProjectEffectBindingKind kind)
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        ProjectEffectBinding binding = kind switch
        {
            ProjectEffectBindingKind.Branch => new(kind, "translation"),
            ProjectEffectBindingKind.Lines => new(kind, ids: ["chorus", "first"]),
            ProjectEffectBindingKind.Tokens => new(kind, ids: ["first-b", "first-a"]),
            ProjectEffectBindingKind.Clips => new(kind, ids: source.LyricPresentation.Programs[0].Clips.Select(clip => clip.Id).Reverse()),
            ProjectEffectBindingKind.Node => new(kind, source.LyricPresentation.Nodes[0].Id, includeDescendants: true),
            _ => new(kind),
        };
        source = PackageSnapshotContractFixture.Fingerprint(source with
        { Effects = new(source.Effects.Packages, [source.Effects.Instances[0] with { Binding = binding }], source.Effects.Budgets) });
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(PackageProjectSnapshotCodec.Encode(source));
        Assert.Equal(binding, Assert.Single(copy.Effects.Instances).Binding);
        Assert.Equal(binding.Ids.ToArray(), copy.Effects.Instances[0].Binding.Ids.ToArray());
    }

    [Fact]
    public void 参数原规范数字精度与重复字段规则不能被double中转改变()
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        // Disabled source remains editable and must preserve arbitrary canonical parameter text.
        var instance = source.Effects.Instances[2] with
        { Parameters = EffectParameterOverrides.Parse("{\"z\":18446744073709551615,\"a\":9007199254740993,\"nested\":{\"x\":0.12345678901234567890123456789}}") };
        source = PackageSnapshotContractFixture.Fingerprint(source with
        { Effects = new(source.Effects.Packages, source.Effects.Instances.SetItem(2, instance), source.Effects.Budgets) });
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(PackageProjectSnapshotCodec.Encode(source));
        Assert.Equal(instance.Parameters.Json, copy.Effects.Instances[2].Parameters.Json);
        JsonObject wire = Root();
        string json = wire.ToJsonString();
        string malformed = json.Replace("\"amount\":0.12345678901234567", "\"amount\":0.12345678901234567,\"amount\":0.5", StringComparison.Ordinal);
        Assert.NotEqual(json, malformed);
        Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(malformed)));
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("line")]
    [InlineData("token")]
    [InlineData("branch")]
    [InlineData("mark")]
    public void 包材质原引用与参数不依赖控制器实例且参与指纹(string owner)
    {
        ProjectSnapshot basis = PackageSnapshotContractFixture.Create();
        EffectPackageContents package = basis.Effects.Packages[0];
        MaterialSpec material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink",
            EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"));
        ProjectSnapshot source = basis with { Effects = new([package], budgets: basis.Effects.Budgets) };
        var primary = source.Lyrics.PrimaryBranch!;
        switch (owner)
        {
            case "baseline": source = source with { LyricStyle = source.LyricStyle with { UnsungFill = new() { Material = material } } }; break;
            case "branch": source = source with { Lyrics = source.Lyrics.ReplaceBranch(primary with { StyleOverride = new() { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = material }) } }) }; break;
            case "line": source = source with { Lyrics = source.Lyrics.ReplaceBranch(primary with { Lines = primary.Lines.SetItem(0, primary.Lines[0] with { StyleOverride = new() { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = material }) } }) }) }; break;
            case "token":
                var line = primary.Lines[0];
                source = source with { Lyrics = source.Lyrics.ReplaceBranch(primary with { Lines = primary.Lines.SetItem(0, line with { Tokens = line.Tokens.SetItem(0, line.Tokens[0] with { StyleOverride = new() { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = material }) } }) }) }) }; break;
            case "mark":
                var node = source.LyricPresentation.Nodes.First(item => item.Content is TextMarkContent);
                var mark = (TextMarkContent)node.Content!;
                source = source with { LyricPresentation = source.LyricPresentation with { Nodes = source.LyricPresentation.Nodes.Replace(node, node with { Content = mark with { AppearanceOverride = new() { PendingFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = material }) } } }) } }; break;
        }
        source = PackageSnapshotContractFixture.Fingerprint(source);
        Assert.NotNull(source.EffectInputsFingerprint);
        Assert.Null(source.Effects.InputsFingerprint);
        byte[] bytes = PackageProjectSnapshotCodec.Encode(source);
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(bytes);
        Assert.Equal(source.LyricStyle, copy.LyricStyle);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.LyricPresentation, copy.LyricPresentation);
        Assert.Equal(source.EffectInputsFingerprint, copy.EffectInputsFingerprint);
        Assert.Equal(bytes, PackageProjectSnapshotCodec.Encode(copy));
        AssertRejected(source with { EffectInputsFingerprint = null });
    }

    [Fact]
    public void 新codec不扩大原双静态公共profile与实际静态拒绝范围()
    {
        Assert.False(BackendSnapshotProfiles.IsSupported(PackageProjectSnapshotCodec.Profile));
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScene, StaticProjectSnapshotCodec.SelectProfile(source));
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Encode(source));
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.ValidateSupported(source));
        source = PackageSnapshotContractFixture.Create("available");
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, StaticProjectSnapshotCodec.SelectProfile(source));
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.AnalysisProfile));
        byte[] packageWire = PackageProjectSnapshotCodec.Encode(source);
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Decode(packageWire));
        string oldFixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "static-snapshot-v1.baseline.json"));
        byte[] oldBytes = Encoding.UTF8.GetBytes(oldFixture.Replace("{{ENGINE_IDENTITY}}", StaticProjectSnapshotCodec.EngineIdentity, StringComparison.Ordinal));
        Assert.Equal(oldBytes, StaticProjectSnapshotCodec.Encode(StaticProjectSnapshotCodec.Decode(oldBytes)));
    }

    private static JsonObject Root(ProjectSnapshot? source = null) => JsonNode.Parse(PackageProjectSnapshotCodec.Encode(source ?? PackageSnapshotContractFixture.Create()))!.AsObject();
    private static void AssertRejected(ProjectSnapshot source) => Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Encode(source));
    private static void AssertRejected(JsonObject wire) => Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(wire.ToJsonString())));
}
