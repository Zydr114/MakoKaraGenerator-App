using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class PackageProjectSnapshotCodecRejectionTests
{
    [Theory]
    [InlineData("contractVersion")]
    [InlineData("profile")]
    [InlineData("engineIdentity")]
    public void 不匹配的合同版本能力与引擎拒绝(string field)
    {
        JsonObject wire = Root(); wire[field] = "different";
        Assert.Equal("SnapshotVersionMismatch", Assert.Throws<SnapshotContractException>(() =>
            PackageProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(wire.ToJsonString()))).Code);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("effects")]
    [InlineData("instance")]
    [InlineData("budget")]
    [InlineData("binding")]
    [InlineData("subscription")]
    [InlineData("origin")]
    [InlineData("package")]
    [InlineData("file")]
    public void 任一深度未知字段与缺失源字段拒绝(string target)
    {
        JsonObject wire = Root();
        JsonObject node = Node(wire, target);
        node["extra"] = true;
        Reject(wire);
        wire = Root(); node = Node(wire, target);
        string first = node.First().Key;
        Assert.True(node.Remove(first));
        Reject(wire);
    }

    [Fact]
    public void 重复字段不被反序列化吞掉()
    {
        string wire = Encoding.UTF8.GetString(PackageProjectSnapshotCodec.Encode(PackageSnapshotContractFixture.Create()));
        foreach (string key in new[] { "\"drawCommands\":4567", "\"seed\":18446744073709551615", "\"id\":\"first-instance\"" })
        {
            Assert.Contains(key, wire, StringComparison.Ordinal);
            string malformed = wire.Replace(key, key + "," + key, StringComparison.Ordinal);
            Assert.Equal("SnapshotDuplicateField", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(malformed))).Code);
        }
    }

    [Theory]
    [InlineData("instanceNull")]
    [InlineData("packageNull")]
    [InlineData("budgetNull")]
    [InlineData("bindingNull")]
    [InlineData("subscriptionNull")]
    [InlineData("negativeSeed")]
    [InlineData("oversizedSeed")]
    [InlineData("invalidEnum")]
    [InlineData("numericEnum")]
    [InlineData("zeroBudget")]
    [InlineData("duplicateInstance")]
    [InlineData("duplicatePackage")]
    [InlineData("duplicateBinding")]
    [InlineData("wrongOrigin")]
    [InlineData("unknownSubscription")]
    [InlineData("wrongSchema")]
    [InlineData("badFrameRate")]
    public void 非法领域结构与源值不得降级默认值(string target)
    {
        JsonObject wire = Root();
        JsonNode source = wire["snapshot"]!, effects = source["effects"]!, instance = effects["instances"]![0]!;
        switch (target)
        {
            case "instanceNull": effects["instances"]![0] = null; break;
            case "packageNull": effects["packages"]![0] = null; break;
            case "budgetNull": effects["budgets"] = null; break;
            case "bindingNull": instance["binding"] = null; break;
            case "subscriptionNull": instance["subscription"] = null; break;
            case "negativeSeed": instance["seed"] = -1; break;
            case "oversizedSeed": instance["seed"] = JsonNode.Parse("18446744073709551616"); break;
            case "invalidEnum": instance["overlayLayer"] = "future"; break;
            case "numericEnum": instance["overlayLayer"] = 0; break;
            case "zeroBudget": effects["budgets"]!["surfaceMemoryBytes"] = 0; break;
            case "duplicateInstance": effects["instances"]!.AsArray().Add(instance.DeepClone()); break;
            case "duplicatePackage": effects["packages"]!.AsArray().Add(effects["packages"]![0]!.DeepClone()); break;
            case "duplicateBinding": instance["binding"]!["ids"] = new JsonArray("first-a", "first-a"); break;
            case "wrongOrigin": effects["instances"]![1]!["templateOrigin"]!["ownerId"] = "not-owner"; break;
            case "unknownSubscription": instance["subscription"]!["kind"] = "unknown"; break;
            case "wrongSchema": source["schemaVersion"] = 27; break;
            case "badFrameRate": source["composition"]!["frameRate"]!["denominator"] = 0; break;
        }
        Reject(wire);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("fileChanged")]
    [InlineData("fileMissing")]
    [InlineData("fileExtra")]
    [InlineData("fileDuplicate")]
    [InlineData("pathEscape")]
    [InlineData("badBase64")]
    public void 包闭包身份或原字节异常明确拒绝(string target)
    {
        JsonObject wire = Root(); JsonNode package = wire["snapshot"]!["effects"]!["packages"]![0]!;
        JsonArray files = package["files"]!.AsArray();
        switch (target)
        {
            case "revision": package["revision"] = "sha256:" + new string('0', 64); break;
            case "fileChanged": files[0]!["content"] = Convert.ToBase64String([1, 2, 3, 4]); break;
            case "fileMissing": files.RemoveAt(0); break;
            case "fileExtra":
                JsonNode extra = files[0]!.DeepClone(); extra["path"] = "extra.bin"; files.Add(extra); break;
            case "fileDuplicate": files.Add(files[0]!.DeepClone()); break;
            case "pathEscape": files[0]!["path"] = "../escape"; break;
            case "badBase64": files[0]!["content"] = "!not-base64!"; break;
        }
        Reject(wire);
    }

    [Theory]
    [InlineData("wrongSource")]
    [InlineData("wrongHash")]
    [InlineData("wrongRevision")]
    [InlineData("missingHop")]
    [InlineData("unknownState")]
    [InlineData("unknownAnalysisField")]
    public void 完整分析原规范和同源绑定不被新codec旁路(string target)
    {
        JsonObject wire = JsonNode.Parse(PackageProjectSnapshotCodec.Encode(PackageSnapshotContractFixture.Create("available")))!.AsObject();
        JsonNode snapshot = wire["snapshot"]!, analysis = snapshot["audioAnalysis"]!;
        switch (target)
        {
            case "wrongSource": snapshot["audioMedia"]!["hash"] = new string('c', 64); break;
            case "wrongHash": analysis["contentHash"] = new string('0', 64); break;
            case "wrongRevision": analysis["data"]!["revision"] = "sha256:" + new string('0', 64); break;
            case "missingHop": analysis["data"]!["points"]!.AsArray().RemoveAt(1); break;
            case "unknownState": analysis["state"] = "invented"; break;
            case "unknownAnalysisField": analysis["summaryOnly"] = true; break;
        }
        Reject(wire);
    }

    [Fact]
    public void 改变原清单的包profile不能作为同一包被接受()
    {
        foreach (string field in new[] { "packageVersion", "sdkVersion", "shaderProfile" })
        {
            JsonObject wire = Root(); JsonNode package = wire["snapshot"]!["effects"]!["packages"]![0]!;
            JsonObject manifest = JsonNode.Parse(Convert.FromBase64String(package["manifest"]!.GetValue<string>()))!.AsObject();
            manifest[field] = field == "packageVersion" ? JsonValue.Create(999) : JsonValue.Create("different");
            package["manifest"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Reject(wire);
        }
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("packageVersion")]
    [InlineData("sdkVersion")]
    [InlineData("shaderProfile")]
    public void 自定义合法包profile与v1在创建及解码阶段也必须拒绝(string field)
    {
        EffectPackageProfile current = EffectPackageProfiles.V2;
        EffectPackageProfile unsupported = field switch
        {
            "v1" => new(2, "1.0.0", ShaderProgramDefinition.RasterProfile),
            "packageVersion" => current with { PackageVersion = current.PackageVersion + 1 },
            "sdkVersion" => current with { SdkVersion = "unsupported-sdk" },
            _ => current with { ShaderProfile = "unsupported-shader" },
        };
        var files = EffectPackageFixture.Files();
        JsonObject manifest = EffectPackageFixture.Manifest(files);
        if (field == "v1")
        {
            manifest.Remove("requires"); manifest.Remove("lineExit"); manifest.Remove("emitters");
            manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray();
        }
        manifest["packageVersion"] = unsupported.PackageVersion;
        manifest["sdkVersion"] = unsupported.SdkVersion;
        manifest["shaderProfile"] = unsupported.ShaderProfile;
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), unsupported)).Code);
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files))).Code);
        JsonObject wire = Root(); JsonNode package = wire["snapshot"]!["effects"]!["packages"]![0]!;
        package["manifest"] = Convert.ToBase64String(EffectPackageFixture.Bytes(manifest));
        Reject(wire);
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("order")]
    [InlineData("bom")]
    public void 相同内容修订不能掩盖传输清单非规范原字节(string target)
    {
        byte[] valid = PackageProjectSnapshotCodec.Encode(PackageSnapshotContractFixture.Create());
        JsonObject wire = JsonNode.Parse(valid)!.AsObject();
        JsonNode package = wire["snapshot"]!["effects"]!["packages"]![0]!;
        byte[] canonical = Convert.FromBase64String(package["manifest"]!.GetValue<string>());
        byte[] changed = target switch
        {
            "whitespace" => Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(canonical)),
            "bom" => [0xef, 0xbb, 0xbf, .. canonical],
            _ => ReverseProperties(canonical),
        };
        Assert.False(canonical.AsSpan().SequenceEqual(changed));
        // Core's importer deliberately normalizes these forms to the same revision.
        // A fixed IPC source must reject that normalization rather than lose original bytes.
        JsonArray files = package["files"]!.AsArray();
        EffectPackageContents normalized = EffectPackageContents.Create(changed,
            files.Select(file => KeyValuePair.Create(file!["path"]!.GetValue<string>(),
                (ReadOnlyMemory<byte>)Convert.FromBase64String(file["content"]!.GetValue<string>()))), EffectPackageFixture.Profile);
        Assert.Equal(package["revision"]!.GetValue<string>(), normalized.Revision);
        Assert.Equal(canonical, normalized.Manifest.ToArray());
        package["manifest"] = Convert.ToBase64String(changed);
        Assert.Equal("SnapshotPackageInvalid", Assert.Throws<SnapshotContractException>(() =>
            PackageProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(wire.ToJsonString()))).Code);

        static byte[] ReverseProperties(byte[] bytes)
        {
            JsonObject before = JsonNode.Parse(bytes)!.AsObject();
            JsonObject after = new();
            foreach (var pair in before.Reverse()) after.Add(pair.Key, pair.Value?.DeepClone());
            return Encoding.UTF8.GetBytes(after.ToJsonString());
        }
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("packageId")]
    public void 包源缺失与源身份不匹配拒绝(string target)
    {
        JsonObject wire = Root(); JsonObject package = wire["snapshot"]!["effects"]!["packages"]![0]!.AsObject();
        if (target == "manifest") package["manifest"] = null;
        else package["packageId"] = "different.package";
        Reject(wire);
    }

    [Theory]
    [InlineData("packageId")]
    [InlineData("revision")]
    [InlineData("exportId")]
    [InlineData("parameters")]
    public void 包材质getter源字段也必须显式存在并拒绝未知字段(string field)
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        EffectPackageContents package = source.Effects.Packages[0];
        source = PackageSnapshotContractFixture.Fingerprint(source with
        {
            Effects = new([package]),
            LyricStyle = source.LyricStyle with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } },
        });
        byte[] valid = PackageProjectSnapshotCodec.Encode(source);
        JsonObject wire = JsonNode.Parse(valid)!.AsObject();
        JsonObject material = wire["snapshot"]!["lyricStyle"]!["unsung"]!["fill"]!["material"]!.AsObject();
        Assert.True(material.Remove(field));
        Reject(wire);
        wire = JsonNode.Parse(valid)!.AsObject();
        wire["snapshot"]!["lyricStyle"]!["unsung"]!["fill"]!["material"]!["extra"] = true;
        Reject(wire);
    }

    [Fact]
    public void 预设应用与背景媒体仍严格拒绝且路径声明零IO()
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        var template = StyleTemplate.Create("static", "static", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.Scale]);
        Reject(source with { PresetApplications = new([new("application", new(ProjectEffectBindingKind.Lines, ids: ["first"]), template)]) });
        Reject(source with { Background = BackgroundSource.ForVideo("/never-read/video.mp4") });
        Reject(source with { Background = BackgroundSource.Color with { Path = "/never-read/image.png" } });
        JsonObject wire = Root();
        wire["snapshot"]!["presetApplications"] = new JsonObject();
        Reject(wire);
        wire = Root();
        wire["snapshot"]!["background"]!["path"] = "/never-read/image.png";
        Reject(wire);
        ProjectSnapshot declaration = PackageSnapshotContractFixture.Fingerprint(source with { AudioPath = "/does-not-exist/audio.wav" });
        Assert.Equal(declaration.AudioPath, PackageProjectSnapshotCodec.Decode(PackageProjectSnapshotCodec.Encode(declaration)).AudioPath);
    }

    [Fact]
    public void 总字节与源对象数量超限在领域读取前拒绝()
    {
        Assert.Equal(4 * 1024 * 1024, PackageProjectSnapshotCodec.MaximumBytes);
        Assert.Equal(200_000, PackageProjectSnapshotCodec.MaximumSourceValues);
        RejectBytes(new byte[PackageProjectSnapshotCodec.MaximumBytes + 1]);
        string values = "[" + string.Join(',', Enumerable.Repeat("0", PackageProjectSnapshotCodec.MaximumSourceValues)) + "]";
        RejectBytes(Encoding.UTF8.GetBytes(values));
        ProjectSnapshot source = PackageSnapshotContractFixture.Create();
        Reject(source with { Name = new string('A', PackageProjectSnapshotCodec.MaximumBytes + 1) });
        Reject(source with { LyricStyle = source.LyricStyle with { Scale = float.NaN } });
        Reject(source with { Lyrics = LyricsDocument.Create([LyricLine.Create("many", new string('A', 30_000), tokens:
            Enumerable.Range(0, 30_000).Select(index => LyricToken.Create("t" + index, "A")).ToArray())]) });
    }

    [Fact]
    public void 已合法存在的大包不得先Base64扩张再靠最终stream预算失败()
    {
        Dictionary<string, byte[]> files = EffectPackageFixture.Files();
        files["large.bin"] = new byte[PackageProjectSnapshotCodec.MaximumBytes];
        EffectPackageContents package = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        ProjectSnapshot source = PackageSnapshotContractFixture.Fingerprint(StaticSnapshotFixture.Create() with { Effects = new([package]) });
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.ValidateSupported(source)).Code);
        Reject(source);
    }

    [Fact]
    public void 总wire精确四MiB仍成功再多一个字节拒绝且不裁切包()
    {
        ProjectSnapshot source = PackageSnapshotContractFixture.Create() with { Name = "A" };
        int initialBytes = PackageProjectSnapshotCodec.Encode(source).Length;
        source = source with { Name = new string('A', PackageProjectSnapshotCodec.MaximumBytes - initialBytes + 1) };
        byte[] encoded = PackageProjectSnapshotCodec.Encode(source);
        Assert.Equal(PackageProjectSnapshotCodec.MaximumBytes, encoded.Length);
        ProjectSnapshot copy = PackageProjectSnapshotCodec.Decode(encoded);
        Assert.Equal(source.Name, copy.Name);
        Assert.Equal(source.Effects.Packages[0].Manifest.ToArray(), copy.Effects.Packages[0].Manifest.ToArray());
        Reject(source with { Name = source.Name + "A" });
    }

    [Fact]
    public void 分别合法的歌词与参数深图必须合并计算同一200k源预算()
    {
        ProjectSnapshot basis = PackageSnapshotContractFixture.Create();
        var primary = basis.Lyrics.PrimaryBranch!;
        LyricToken[] tokens = [.. Enumerable.Range(0, 8_000).Select(index => LyricToken.Create("dense-" + index, "A"))];
        var line = LyricLine.Create("dense-line", new string('A', tokens.Length), tokens: tokens);
        ProjectSnapshot lyrics = PackageSnapshotContractFixture.Fingerprint(basis with
        { Lyrics = basis.Lyrics.ReplaceBranch(primary with { Lines = primary.Lines.Add(line) }) });
        byte[] lyricsWire = PackageProjectSnapshotCodec.Encode(lyrics);
        int lyricValues = CountValues(lyricsWire);
        Assert.True(lyricValues < PackageProjectSnapshotCodec.MaximumSourceValues);
        // A genuinely nested parameter graph contributes values even though its owner is one instance.
        int parameterValues = PackageProjectSnapshotCodec.MaximumSourceValues - lyricValues + 1_000;
        Assert.True(parameterValues > 0);
        string json = "{\"dense\":{\"nested\":[" + string.Join(',', Enumerable.Repeat("0", parameterValues)) + "]}}";
        ProjectEffectInstance disabled = basis.Effects.Instances[2] with { Parameters = EffectParameterOverrides.Parse(json) };
        ProjectEffects effects = new(basis.Effects.Packages, basis.Effects.Instances.SetItem(2, disabled), basis.Effects.Budgets);
        ProjectSnapshot parameters = PackageSnapshotContractFixture.Fingerprint(basis with { Effects = effects });
        byte[] parameterWire = PackageProjectSnapshotCodec.Encode(parameters);
        Assert.True(CountValues(parameterWire) < PackageProjectSnapshotCodec.MaximumSourceValues);
        Assert.Equal(json, PackageProjectSnapshotCodec.Decode(parameterWire).Effects.Instances[2].Parameters.Json);
        Assert.Equal(tokens.Length, PackageProjectSnapshotCodec.Decode(lyricsWire).Lyrics.PrimaryBranch!.Lines[^1].Tokens.Length);

        ProjectSnapshot combined = PackageSnapshotContractFixture.Fingerprint(lyrics with { Effects = effects });
        JsonObject raw = JsonNode.Parse(lyricsWire)!.AsObject();
        raw["snapshot"]!["effects"] = JsonNode.Parse(parameterWire)!["snapshot"]!["effects"]!.DeepClone();
        raw["snapshot"]!["effectInputsFingerprint"] = combined.EffectInputsFingerprint;
        byte[] combinedWire = Encoding.UTF8.GetBytes(raw.ToJsonString());
        Assert.True(combinedWire.Length < PackageProjectSnapshotCodec.MaximumBytes);
        Assert.True(CountValues(combinedWire) > PackageProjectSnapshotCodec.MaximumSourceValues);
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.ValidateSupported(combined)).Code);
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Encode(combined)).Code);
        Assert.Equal("SnapshotSizeExceeded", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(combinedWire)).Code);
    }

    private static int CountValues(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        return Count(document.RootElement);
        static int Count(JsonElement value) => 1 + (value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Sum(property => Count(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Sum(Count),
            _ => 0,
        });
    }

    private static JsonObject Root() => JsonNode.Parse(PackageProjectSnapshotCodec.Encode(PackageSnapshotContractFixture.Create()))!.AsObject();
    private static JsonObject Node(JsonObject wire, string target)
    {
        JsonNode source = wire["snapshot"]!, effects = source["effects"]!, instance = effects["instances"]![0]!;
        return (target switch
        {
            "snapshot" => source, "effects" => effects, "instance" => instance, "budget" => effects["budgets"]!,
            "binding" => instance["binding"]!, "subscription" => instance["subscription"]!,
            "origin" => effects["instances"]![1]!["templateOrigin"]!, "package" => effects["packages"]![0]!,
            _ => effects["packages"]![0]!["files"]![0]!,
        }).AsObject();
    }
    private static void Reject(JsonObject wire) => RejectBytes(Encoding.UTF8.GetBytes(wire.ToJsonString()));
    private static void Reject(ProjectSnapshot source) => Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Encode(source));
    private static void RejectBytes(byte[] bytes) => Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(bytes));
}
