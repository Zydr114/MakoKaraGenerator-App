using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class EffectPackageFixture
{
    public static readonly EffectPackageProfile Profile = new(2, "1.0.0", ShaderProgramDefinition.RasterProfile);
    public const string EffectSource = """
        using MakoKaraGenerator.Scripting;
        public sealed class PackEffect : EffectBase
        {
            public override void Evaluate(FrameContext frame, FrameWriter output)
            {
                foreach (var token in frame.Presentations.TokensInBinding)
                    output.Transforms.ScaleTokenFamily(token.WithRuby, frame.Parameters.GetNumber("peak"), Pivot.BaseAdvanceCenter);
            }
        }
        """;
    public static Dictionary<string, byte[]> Files(byte preview = 1) => new(StringComparer.Ordinal)
    {
        ["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(EffectSource), ["preview.bin"] = [preview, 2, 3],
        ["README.md"] = Encoding.UTF8.GetBytes("歌词包示例"),
    };
    public static JsonObject Manifest(Dictionary<string, byte[]> files)
    {
        JsonArray list = [];
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) list.Add(new JsonObject
        {
            ["path"] = pair.Key, ["role"] = pair.Key.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "asset",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
        });
        return new()
        {
            ["packageVersion"] = Profile.PackageVersion, ["sdkVersion"] = Profile.SdkVersion, ["shaderProfile"] = Profile.ShaderProfile,
            ["packageId"] = "demo.closed", ["name"] = "Demo", ["version"] = "1.0.0", ["files"] = list,
            ["parameters"] = new JsonArray(new JsonObject { ["id"] = "peak", ["type"] = "number", ["default"] = 1.5, ["min"] = 1, ["max"] = 4, ["label"] = "Scale", ["unit"] = "x" }),
            ["exports"] = new JsonArray(new JsonObject { ["id"] = "scale", ["kind"] = "controller", ["name"] = "Scale", ["entryType"] = "PackEffect",
                ["parameters"] = new JsonArray("peak"), ["bindingKinds"] = new JsonArray("tokens"),
                ["outputs"] = new JsonArray(new JsonObject { ["id"] = "scale", ["kind"] = "VisualTransform", ["target"] = "tokenFamily", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() }) }),
        };
    }
    public static byte[] Bytes(JsonObject manifest) => Encoding.UTF8.GetBytes(manifest.ToJsonString());
    public static IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> Inputs(Dictionary<string, byte[]> files)
        => files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value));
    public static EffectPackageContents Contents(byte preview = 1)
    { var files = Files(preview); return EffectPackageContents.Create(Bytes(Manifest(files)), Inputs(files), Profile); }
    public static EffectPackageDefinition WordEmphasis()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "effect-packages", "WordEmphasis");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "manifest.json")
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        return EffectPackageDefinition.Read(EffectPackageContents.Create(File.ReadAllBytes(Path.Combine(root, "manifest.json")), Inputs(files), Profile));
    }
    public static EffectPackageContents GroupTransform(string? source = null)
    {
        var files = Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source ?? """
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    var group = frame.GroupGeometry.Single(item => item.Target.NodeId.Value == frame.Parameters.GetText("node"));
                    if (!group.BaseWorldMatrix.TryInverse(out var inverse)) throw new Exception("InvalidGroupMatrix");
                    var local = inverse.Compose(Affine2.Translation(frame.Parameters.GetNumber("offsetX"), frame.Parameters.GetNumber("offsetY")))
                        .Compose(group.BaseWorldMatrix);
                    output.Transforms.ApplyAffine(group.Target, local);
                    if (frame.Parameters.GetBoolean("badWrite"))
                        foreach (var token in frame.Presentations.TokensInBinding)
                            output.Transforms.ScaleTokenFamily(token.WithRuby, 1.2, Pivot.BaseAdvanceCenter);
                }
            }
            """);
        var manifest = Manifest(files);
        manifest["parameters"] = new JsonArray(
            new JsonObject { ["id"] = "node", ["type"] = "text", ["label"] = "目标组", ["default"] = "" },
            new JsonObject { ["id"] = "offsetX", ["type"] = "number", ["label"] = "水平移动", ["default"] = 30, ["min"] = -300, ["max"] = 300 },
            new JsonObject { ["id"] = "offsetY", ["type"] = "number", ["label"] = "垂直移动", ["default"] = -16, ["min"] = -300, ["max"] = 300 },
            new JsonObject { ["id"] = "badWrite", ["type"] = "boolean", ["label"] = "非法输出探针", ["default"] = false });
        var export = manifest["exports"]![0]!; export["id"] = "group-transform"; export["name"] = "Group transform";
        export["parameters"] = new JsonArray("node", "offsetX", "offsetY", "badWrite");
        export["bindingKinds"] = new JsonArray("project", "node", "branch", "lines", "tokens", "clips");
        export["outputs"]![0]!["target"] = "group";
        return EffectPackageContents.Create(Bytes(manifest), Inputs(files), Profile);
    }
    public static EffectPackageContents GroupOpacity(string? source = null, bool declaresGroup = true)
    {
        var files = Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source ?? """
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    var target = frame.GroupGeometry.Single(item => item.Target.NodeId.Value == frame.Parameters.GetText("node")).Target;
                    if (frame.Parameters.GetBoolean("lateError") && frame.TimeUs >= 300000) target = new(new("missing"));
                    output.Style.MultiplyOpacity(target, frame.Parameters.GetNumber("factor"));
                }
            }
            """);
        var manifest = Manifest(files);
        manifest["parameters"] = new JsonArray(
            new JsonObject { ["id"] = "node", ["type"] = "text", ["label"] = "目标组", ["default"] = "" },
            new JsonObject { ["id"] = "factor", ["type"] = "number", ["label"] = "共同透明度", ["default"] = .5, ["min"] = 0, ["max"] = 3 },
            new JsonObject { ["id"] = "lateError", ["type"] = "boolean", ["label"] = "失败探针", ["default"] = false });
        var export = manifest["exports"]![0]!; export["id"] = "group-opacity"; export["name"] = "Group opacity";
        export["parameters"] = new JsonArray("node", "factor", "lateError");
        export["bindingKinds"] = new JsonArray("project", "node", "branch", "lines", "tokens", "clips");
        export["outputs"]![0]!["kind"] = "StyleModulation";
        export["outputs"]![0]!["target"] = declaresGroup ? "group" : "tokenFamily";
        return EffectPackageContents.Create(Bytes(manifest), Inputs(files), Profile);
    }
    public static JsonObject NineParametersManifest(Dictionary<string, byte[]> files)
    {
        var manifest = Manifest(files);
        foreach (var entry in manifest["files"]!.AsArray())
            if (entry!["path"]!.GetValue<string>() == "assets/image.png") entry["role"] = "image";
        var parameters = manifest["parameters"]!.AsArray();
        parameters.Add(new JsonObject { ["id"] = "integer", ["type"] = "integer", ["label"] = "整数", ["default"] = 9_007_199_254_740_993L, ["min"] = long.MinValue, ["max"] = long.MaxValue });
        parameters.Add(new JsonObject { ["id"] = "duration", ["type"] = "duration", ["label"] = "时长", ["default"] = 120_000L, ["min"] = 0, ["max"] = long.MaxValue, ["unit"] = "us" });
        parameters.Add(new JsonObject { ["id"] = "boolean", ["type"] = "boolean", ["label"] = "开关", ["default"] = true });
        parameters.Add(new JsonObject { ["id"] = "text", ["type"] = "text", ["label"] = "文本", ["default"] = "" });
        parameters.Add(new JsonObject { ["id"] = "enum", ["type"] = "enum", ["label"] = "选项", ["default"] = "branch", ["values"] = new JsonArray(
            new JsonObject { ["value"] = "branch", ["label"] = "本分支" }, new JsonObject { ["value"] = "project", ["label"] = "全工程" }) });
        parameters.Add(new JsonObject { ["id"] = "color", ["type"] = "color", ["label"] = "颜色", ["default"] = new JsonObject { ["r"] = .123456789, ["g"] = .25, ["b"] = .75, ["a"] = .8 } });
        parameters.Add(new JsonObject { ["id"] = "curve", ["type"] = "curve", ["label"] = "曲线", ["default"] = new JsonObject { ["kind"] = "easing", ["easing"] = "easeOut" } });
        parameters.Add(new JsonObject { ["id"] = "asset", ["type"] = "asset", ["label"] = "图片", ["default"] = "assets/image.png" });
        manifest["exports"]![0]!["parameters"] = new JsonArray([.. parameters.Select(parameter => (JsonNode?)JsonValue.Create(parameter!["id"]!.GetValue<string>()))]);
        return manifest;
    }
    public static void WriteDirectory(string directory, Dictionary<string, byte[]>? files = null)
    {
        files ??= Files(); Directory.CreateDirectory(directory);
        foreach (var pair in files)
        {
            string target = Path.Combine(directory, pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, pair.Value);
        }
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), Bytes(Manifest(files)));
    }
}
