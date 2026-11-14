using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

internal static class V2PackageTestHelper
{
    public const string EffectSource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;

            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                double peak = frame.Parameters.GetNumber("peak");
                foreach (var token in frame.Presentations.TokensInBinding)
                    foreach (var geometry in frame.Geometry)
                        if (geometry.WithRuby == token.WithRuby)
                            output.AppendTransform(token.WithRuby, Affine2.ScaleAt(peak, geometry.BaseGeometry.AdvanceBounds.Center));
            }
        }
        """;

    public static Dictionary<string, byte[]> Files(byte preview = 1, string? source = null) => new(StringComparer.Ordinal)
    {
        ["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source ?? EffectSource),
        ["preview.bin"] = [preview, 2, 3],
        ["README.md"] = Encoding.UTF8.GetBytes("歌词包示例"),
    };

    public static JsonObject Manifest(Dictionary<string, byte[]> files, string[]? requires = null, string entryType = "PackEffect")
    {
        JsonArray list = [];
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            list.Add(new JsonObject
            {
                ["path"] = pair.Key,
                ["role"] = pair.Key.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "asset",
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
            });
        JsonArray capabilityList = [];
        foreach (string capability in requires ?? ["token.affine"]) capabilityList.Add(capability);
        return new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion,
            ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = "demo.v2.closed",
            ["name"] = "DemoV2",
            ["version"] = "2.0.0",
            ["files"] = list,
            ["requires"] = capabilityList,
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 },
            ["emitters"] = new JsonArray(),
            ["parameters"] = new JsonArray(new JsonObject
            {
                ["id"] = "peak", ["type"] = "number", ["default"] = 1.5, ["min"] = 1, ["max"] = 4, ["label"] = "Scale", ["unit"] = "x",
            }),
            ["exports"] = new JsonArray(new JsonObject
            {
                ["id"] = "scale",
                ["kind"] = "controller",
                ["name"] = "Scale",
                ["entryType"] = entryType,
                ["parameters"] = new JsonArray("peak"),
                ["bindingKinds"] = new JsonArray("tokens"),
                ["outputs"] = new JsonArray(new JsonObject
                {
                    ["id"] = "scale",
                    ["kind"] = "VisualTransform",
                    ["target"] = "tokenFamily",
                    ["requires"] = new JsonArray(),
                    ["dependsOn"] = new JsonArray(),
                }),
            }),
        };
    }

    public static JsonObject NineParametersManifest(Dictionary<string, byte[]> files, string[]? requires = null)
    {
        var manifest = Manifest(files, requires);
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

    public static byte[] Bytes(JsonObject manifest) => Encoding.UTF8.GetBytes(manifest.ToJsonString());

    public static IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> Inputs(Dictionary<string, byte[]> files)
        => files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value));

    public static EffectPackageContents Contents(byte preview = 1)
    {
        var files = Files(preview);
        return EffectPackageContents.CreateSupported(Bytes(Manifest(files)), Inputs(files));
    }

    public static EffectPackageDefinition WordEmphasis()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WordEmphasis");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "manifest.json")
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        return EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(
            File.ReadAllBytes(Path.Combine(root, "manifest.json")), Inputs(files)));
    }

    public static EffectPackageContents GroupTransform(string? source = null, string[]? requires = null)
    {
        var files = Files(source: source ?? """
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;

            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;

                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    string node = frame.Parameters.GetText("node");
                    GroupGeometrySample? group = null;
                    foreach (var item in frame.GroupGeometry)
                        if (item.Target.NodeId.Value == node) { group = item; break; }
                    Affine2 inverse;
                    if (group is null || !group.BaseWorldMatrix.TryInverse(out inverse)) return;
                    var local = inverse.Compose(Affine2.Translation(frame.Parameters.GetNumber("offsetX"), frame.Parameters.GetNumber("offsetY")))
                        .Compose(group.BaseWorldMatrix);
                    output.AppendTransform(group.Target, local);
                    if (frame.Parameters.GetBoolean("badWrite"))
                        foreach (var token in frame.Presentations.TokensInBinding)
                            output.AppendTransform(token.WithRuby, Affine2.ScaleAt(1.2, new(0, 0)));
                }
            }
            """);
        var manifest = Manifest(files, requires ?? ["group.affine"]);
        manifest["parameters"] = new JsonArray(
            new JsonObject { ["id"] = "node", ["type"] = "text", ["label"] = "目标组", ["default"] = "" },
            new JsonObject { ["id"] = "offsetX", ["type"] = "number", ["label"] = "水平移动", ["default"] = 30, ["min"] = -300, ["max"] = 300 },
            new JsonObject { ["id"] = "offsetY", ["type"] = "number", ["label"] = "垂直移动", ["default"] = -16, ["min"] = -300, ["max"] = 300 },
            new JsonObject { ["id"] = "badWrite", ["type"] = "boolean", ["label"] = "非法输出探针", ["default"] = false });
        var export = manifest["exports"]![0]!.AsObject();
        export["id"] = "group-transform";
        export["name"] = "Group transform";
        export["parameters"] = new JsonArray("node", "offsetX", "offsetY", "badWrite");
        export["bindingKinds"] = new JsonArray("project", "node", "branch", "lines", "tokens", "clips");
        export["outputs"]![0]!["target"] = "group";
        return EffectPackageContents.CreateSupported(Bytes(manifest), Inputs(files));
    }

    public static void WriteDirectory(string directory, Dictionary<string, byte[]>? files = null)
    {
        files ??= Files();
        Directory.CreateDirectory(directory);
        foreach (var pair in files)
        {
            string target = Path.Combine(directory, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, pair.Value);
        }
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), Bytes(Manifest(files)));
    }

    public static EffectPackageDefinition CreateControllerPackage(string source, string entryType = "Probe",
        string[]? requires = null, string[]? bindingKinds = null, string outputKind = "VisualTransform",
        string outputTarget = "tokenFamily", string exportId = "probe", ResourceCatalog? resources = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/Probe.cs"] = Encoding.UTF8.GetBytes(source),
        };
        var manifest = Manifest(files, requires ?? ["token.affine"], entryType);
        manifest["parameters"] = new JsonArray();
        var export = manifest["exports"]![0]!.AsObject();
        export["id"] = exportId;
        export["name"] = exportId;
        export["parameters"] = new JsonArray();
        JsonArray bindings = [];
        foreach (string kind in bindingKinds ?? ["project", "branch", "lines", "tokens", "clips", "node"]) bindings.Add(kind);
        export["bindingKinds"] = bindings;
        export["outputs"]![0]!["kind"] = outputKind;
        export["outputs"]![0]!["target"] = outputTarget;
        return EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(Bytes(manifest), Inputs(files)));
    }

    public static TextFrame CreateTextFrame(bool independent = true, ResourceCatalog? resources = null,
        BaselineStyles? baseline = null, EffectBinding? binding = null, bool includeLine = true,
        bool includeMark = true, bool includeGroup = true)
    {
        ContentCatalog content = new([], [], [], []);
        SceneCatalog scene = new(content, [], [], [], []);
        var family = new TokenFamilyTarget(new("token"), new("binding"));
        var basic = new BaseTokenTarget(family.TokenId, family.BindingId);
        var line = new FrameLine(new("line"), new("branch"), family.BindingId, new("node"), new("group"), new("program"), null, null, DisplayPhase.Performing, 1);
        var markTarget = new TextMarkTarget(new("mark"), new("cue"), family.BindingId);
        var groupTarget = new GroupTarget(new("group"));
        var geometry = new GeometrySample
        {
            AdvanceBounds = new(10, 20, 50, 80),
            Baseline = new(10, 70),
            BaseWorldMatrix = Affine2.Identity,
            InkBounds = new(12, 23, 48, 58),
            CanTransformIndependently = independent,
            DiagnosticCode = independent ? null : "UnsupportedGlyphPartition",
        };
        TextFrameHostInputs legacy = new(
            1000,
            baseline ?? BaselineStyles.Empty,
            new()
            {
                LinesInBinding = includeLine ? [line] : [],
                TokensInBinding =
                [
                    new()
                    {
                        TokenId = family.TokenId,
                        BranchId = line.BranchId,
                        LineId = line.LineId,
                        BindingId = family.BindingId,
                        SourceText = "x",
                        WithRuby = family,
                        BaseOnly = basic,
                        Performance = new(null, null, null, null, false, false, ProgressSource.Missing),
                    },
                ],
                MarksInBinding = includeMark ? [new FrameMark(markTarget, line, new("node"), "mark", false, 1)] : [],
            },
            [new() { BranchId = line.BranchId, LineId = line.LineId, WithRuby = family, BaseOnly = basic, BaseGeometry = geometry, FamilyGeometry = geometry }],
            includeLine ? [new() { BranchId = line.BranchId, Target = line.Target, BaseGeometry = geometry, ProgressPath = new(0, 50, 55, 40) }] : [],
            includeMark ? [new(markTarget, geometry)] : [],
            includeGroup ? [new() { Target = groupTarget, ReferenceBounds = new(0, 0, 100, 50), BaseWorldMatrix = Affine2.Identity, WorldPivot = new(50, 25), WorldInkCorners = [] }] : [],
            binding ?? new EffectBinding.Project(),
            OutputMode.Opaque);
        var source = new DeclareContext(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed,
            resources ?? ResourceCatalog.Empty, 42, TimelineIndex.Empty, new(1920, 1080), new("test"));
        return new(source, legacy);
    }
}
