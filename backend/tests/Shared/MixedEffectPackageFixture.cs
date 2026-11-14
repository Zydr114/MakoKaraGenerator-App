using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class MixedEffectPackageFixture
{
    public const string Source = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                foreach (var line in frame.Presentations.LinesInBinding)
                    output.BindGraph(line.Target, new(frame.Resources.GetGraph("tint"),
                        new([new("amount", new ParameterValue.Number(frame.Parameters.GetNumber("amount")))]),
                        GraphUniformValues.Empty, GraphTransformValues.Empty));
            }
        }
        """;
    public const string MaterialSource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var material = MaterialValue.PackageExport(frame.Resources.GetMaterial("ink"),
                    new([new("amount", new ParameterValue.Number(frame.Parameters.GetNumber("amount")))]));
                foreach (var token in frame.Presentations.TokensInBinding)
                    output.SetFill(token.WithRuby, TextState.Both, material);
            }
        }
        """;
    public static EffectPackageContents MaterialContents(Action<JsonObject>? manifestChange = null, bool background = false,
        bool backgroundColor = false, JsonObject? fallback = null, string? controllerSource = null, Action<JsonObject>? graphChange = null,
        string? shaderSource = null, Action<JsonObject>? programChange = null)
        => Contents(manifest =>
        {
            manifest["requires"] = new JsonArray("style.fill");
            manifest["exports"]![0]!["outputs"] = new JsonArray(new JsonObject { ["id"]="style", ["kind"]="StyleModulation",
                ["target"]="tokenFamily", ["requires"]=new JsonArray("style.fill"), ["dependsOn"]=new JsonArray() });
            manifestChange?.Invoke(manifest);
        }, background:background, backgroundColor: backgroundColor, fallback:fallback, controllerSource:controllerSource ?? MaterialSource,
            graphChange:graphChange, shaderSource:shaderSource,
            programChange: programChange);
    public static JsonObject Output(string target, bool material = false, bool background = false) => new()
    {
        ["id"] = "color", ["kind"] = "ImageGraph", ["target"] = target, ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray(),
        ["bypass"] = material ? new JsonObject { ["kind"] = "materialFallback", ["mapping"] = "identity" }
            : background ? new JsonObject { ["kind"] = "graphInput", ["input"] = "input:original", ["mapping"] = "identity" } : null,
    };
    public static JsonObject GraphExport(bool background = false)
    {
        var output = Output("line", background: background); if (!background) output.Remove("bypass");
        return new() { ["id"] = "tint", ["kind"] = "graph", ["name"] = "Tint", ["graph"] = EffectPackageGraphFixture.GraphPath,
            ["parameters"] = new JsonArray("amount"), ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"),
            ["outputs"] = new JsonArray(output) };
    }
    public static JsonObject Material(JsonObject? fallback = null) => new()
    {
        ["id"] = "ink", ["kind"] = "material", ["name"] = "Ink", ["graph"] = EffectPackageGraphFixture.GraphPath,
        ["parameters"] = new JsonArray("amount"), ["regions"] = new JsonArray("fill", "stroke"),
        ["coverageMode"] = "masked",
        ["fallback"] = fallback ?? new JsonObject { ["kind"] = "solid", ["color"] = "#3380CCA0" },
        ["outputs"] = new JsonArray(Output("region", material: true)),
    };
    public static JsonObject Gradient(string space = "Line", string colorSpace = "Hsv", string kind = "Linear") => new()
    {
        ["kind"] = "gradient", ["gradient"] = new JsonObject
        {
            ["kind"] = kind, ["angleDegrees"] = 15, ["centerX"] = .4, ["centerY"] = .6, ["radiusX"] = .35, ["radiusY"] = .45,
            ["coordinateSpace"] = space, ["colorSpace"] = colorSpace, ["huePath"] = "Longer",
            ["stops"] = new JsonArray(new JsonObject { ["offset"] = 0, ["color"] = "#FF000040" }, new JsonObject { ["offset"] = .5, ["color"] = "#00FF0080" },
                new JsonObject { ["offset"] = 1, ["color"] = "#0000FFC0" }),
        },
    };
    public static EffectPackageContents Contents(Action<JsonObject>? manifestChange = null, bool pure = false, bool background = false,
        bool backgroundColor = false, JsonObject? fallback = null, string? controllerSource = null, Action<JsonObject>? graphChange = null,
        string? shaderSource = null, Action<JsonObject>? programChange = null)
    {
        var graph = EffectPackageGraphFixture.Graph(); graph["parameters"] = new JsonArray("amount");
        graph["nodes"]![0]!["uniforms"]!["amount"] = "parameter:amount";
        graph["nodes"]![0]!["children"] = new JsonObject { ["original"] = "input:original" };
        if (background)
        {
            var input = EffectPackageGraphFixture.Input("background", backgroundColor ? "BackgroundColor" : "BackgroundMedia");
            input.Remove("capture"); input["backgroundRequirement"] = "media";
            graph["inputs"]!.AsArray().Add(input); graph["bypassInput"] = "input:original";
            if (!backgroundColor) graph["output"] = "input:background";
            else graph["nodes"]![0]!["children"]!["background"] = "input:background";
        }
        graphChange?.Invoke(graph);
        var program = EffectPackageShaderFixture.Program();
        program["children"] = backgroundColor
            ? new JsonArray(new JsonObject { ["name"] = "original", ["type"] = "color" }, new JsonObject { ["name"] = "background", ["type"] = "color" })
            : new JsonArray(new JsonObject { ["name"] = "original", ["type"] = "color" });
        programChange?.Invoke(program);
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(controllerSource ?? Source);
        files[EffectPackageShaderFixture.ProgramPath] = EffectPackageFixture.Bytes(program);
        string defaultShader = backgroundColor
            ? "uniform float amount; uniform shader original; uniform shader background; half4 main(float2 p) { half4 c=original.eval(p); half4 b=background.eval(p); return half4(c.rgb*amount+b.rgb*0,c.a); }"
            : "uniform float amount; uniform shader original; half4 main(float2 p) { half4 c=original.eval(p); return half4(c.rgb*amount,c.a); }";
        files[EffectPackageShaderFixture.SourcePath] = Encoding.UTF8.GetBytes(shaderSource ?? defaultShader);
        files[EffectPackageGraphFixture.GraphPath] = EffectPackageFixture.Bytes(graph);
        if (pure) files.Remove("src/PackEffect.cs");
        var manifest = EffectPackageShaderFixture.Manifest(files);
        foreach (var file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>() == EffectPackageGraphFixture.GraphPath) file["role"] = "graph";
        manifest["requires"] = pure ? new JsonArray() : new JsonArray("image.graph");
        manifest["parameters"] = new JsonArray(new JsonObject { ["id"] = "amount", ["type"] = "number", ["label"] = "亮度", ["default"] = .5, ["min"] = 0, ["max"] = 1 });
        JsonArray exports = [];
        if (!pure)
        {
            var output = Output("line"); output.Remove("bypass"); output["requires"] = new JsonArray("image.graph");
            exports.Add(new JsonObject { ["id"] = "controller", ["kind"] = "controller", ["name"] = "Controller", ["entryType"] = "PackEffect",
                ["parameters"] = new JsonArray("amount"), ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"), ["outputs"] = new JsonArray(output) });
        }
        exports.Add(Material(fallback)); exports.Add(GraphExport(background)); manifest["exports"] = exports;
        manifestChange?.Invoke(manifest);
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }
}
