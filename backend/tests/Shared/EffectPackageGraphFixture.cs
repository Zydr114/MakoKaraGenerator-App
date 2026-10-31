using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class EffectPackageGraphFixture
{
    public const string GraphPath = "graphs/probe.graph.json";
    public static JsonObject Capture(bool ruby = true, string region = "fillAndStroke") => new()
    {
        ["includeRuby"] = ruby, ["includeStroke"] = true, ["includeShadow"] = true,
        ["includeDescendantGroups"] = true, ["includeOwnedOverlay"] = false, ["region"] = region,
    };
    public static JsonObject Input(string id = "original", string source = "TargetColor", string type = "color") => new()
    {
        ["id"] = id, ["source"] = source, ["type"] = type, ["availability"] = "required",
        ["backgroundRequirement"] = "none", ["space"] = "canvas", ["capture"] = Capture(),
    };
    public static JsonObject Bounds(double expand = 0, double sample = 0) => new()
    { ["expandPx"] = expand, ["maxExpandPx"] = expand, ["maxSampleOffsetPx"] = sample };
    public static JsonObject Node() => new()
    {
        ["id"] = "probe", ["op"] = "RuntimeShader", ["program"] = EffectPackageShaderFixture.ProgramPath,
        ["children"] = new JsonObject(), ["uniforms"] = new JsonObject { ["amount"] = .25 },
        ["outputType"] = "color", ["bounds"] = Bounds(), ["edge"] = "transparent",
    };
    public static JsonObject Graph() => new()
    {
        ["graphVersion"] = ImageGraphDefinition.CurrentVersion, ["id"] = "probe", ["inputs"] = new JsonArray(Input()),
        ["parameters"] = new JsonArray(), ["nodes"] = new JsonArray(Node()), ["output"] = "node:probe", ["anchorMode"] = "ColorOnly",
        ["bounds"] = new JsonObject { ["space"] = "canvas", ["source"] = "targetRegion", ["maxExpandPx"] = 0 },
        ["composite"] = new JsonObject { ["mode"] = "sourceOver" },
    };
    public static EffectPackageContents Contents(JsonObject? graph = null, JsonObject? program = null, string? source = null,
        Action<JsonObject>? changeManifest = null, Dictionary<string, byte[]>? extraFiles = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [GraphPath] = EffectPackageFixture.Bytes(graph ?? Graph()) };
        if (extraFiles is not null) foreach (var pair in extraFiles) files.Add(pair.Key, pair.Value);
        return EffectPackageShaderFixture.Contents(program, source, manifest =>
        {
            foreach (var entry in manifest["files"]!.AsArray())
            {
                string path = entry!["path"]!.GetValue<string>();
                if (path.EndsWith(".graph.json", StringComparison.Ordinal)) entry["role"] = "graph";
                if (path.EndsWith(".png", StringComparison.Ordinal)) entry["role"] = "image";
            }
            changeManifest?.Invoke(manifest);
        }, files);
    }
}
