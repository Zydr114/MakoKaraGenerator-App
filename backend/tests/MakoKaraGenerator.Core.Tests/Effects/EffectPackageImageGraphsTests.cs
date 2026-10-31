using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests.Effects;

public sealed class EffectPackageImageGraphsTests
{
    [Fact]
    public void 每个输入独立保存完整配方不受原JSON后改影响()
    {
        var graph = EffectPackageGraphFixture.Graph(); var second = EffectPackageGraphFixture.Input("withoutRuby");
        second["capture"] = EffectPackageGraphFixture.Capture(false, "stroke"); graph["inputs"]!.AsArray().Add(second);
        var contents = EffectPackageGraphFixture.Contents(graph); second["capture"]!["includeRuby"] = true;
        var actual = EffectPackageImageGraphs.Read(contents)[EffectPackageGraphFixture.GraphPath];
        Assert.True(actual.Captures["original"].IncludeRuby); Assert.False(actual.Captures["withoutRuby"].IncludeRuby);
        Assert.Equal(EffectCaptureRegion.Stroke, actual.Captures["withoutRuby"].Region);
        Assert.Single(EffectPackageDefinition.Read(contents).ImageGraphs); Assert.Single(actual.Plan.Nodes);
    }

    [Theory]
    [InlineData("capture", "PackageSemanticMissingField")]
    [InlineData("overlay", "PackageSemanticMissingField")]
    [InlineData("boolean", "PackageParameterTypeMismatch")]
    [InlineData("offset", "PackageSemanticMissingField")]
    [InlineData("unknown", "PackageSemanticUnknownField")]
    [InlineData("version", "PackageGraphVersionMismatch")]
    [InlineData("space", "PackageGraphSpaceUnsupported")]
    [InlineData("source", "PackageGraphSourceUnsupported")]
    [InlineData("bounds-source", "PackageGraphBoundsSourceUnsupported")]
    [InlineData("reference", "PackageGraphReferenceInvalid")]
    [InlineData("cycle", "GraphCycle")]
    [InlineData("bypass", "InvalidGraphBypass")]
    [InlineData("parameter", "GraphUniformBindingMismatch")]
    [InlineData("expansion", "PackageGraphExpansionInvalid")]
    public void 严格缺失字段类型拓扑及旁路错误携带实际图路径(string change, string code)
    {
        var graph = EffectPackageGraphFixture.Graph(); var input = graph["inputs"]![0]!.AsObject(); var node = graph["nodes"]![0]!.AsObject();
        switch (change)
        {
            case "capture": input.Remove("capture"); break;
            case "overlay": input["capture"]!.AsObject().Remove("includeOwnedOverlay"); break;
            case "boolean": input["capture"]!["includeRuby"] = 1; break;
            case "offset": node["bounds"]!.AsObject().Remove("maxSampleOffsetPx"); break;
            case "unknown": node["file"] = "probe.sksl"; break;
            case "version": graph["graphVersion"] = ImageGraphDefinition.CurrentVersion + 1; break;
            case "space": input["space"] = "0"; break;
            case "source": input["source"] = "targetcolor"; break;
            case "bounds-source": graph["bounds"]!["source"] = "canvas"; break;
            case "reference": graph["output"] = "probe"; break;
            case "cycle": graph["nodes"] = new JsonArray(new JsonObject { ["id"] = "probe", ["op"] = "Blur", ["input"] = "node:probe", ["sigmaPx"] = 0,
                ["bounds"] = EffectPackageGraphFixture.Bounds(), ["edge"] = "transparent" }); break;
            case "bypass": graph["bypassInput"] = "input:original"; input["availability"] = "optional"; break;
            case "parameter": node["uniforms"]!["amount"] = "parameter:missing"; break;
            case "expansion": node["bounds"]!["expandPx"] = "Math.Ceiling(peak)"; break;
        }
        var error = Assert.Throws<EffectPackageException>(() => EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(graph)));
        Assert.Equal(code, error.Code); Assert.StartsWith(EffectPackageGraphFixture.GraphPath, error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void 五种节点矩阵和完整参数区间使用唯一编译器且不猜边界()
    {
        var graph = EffectPackageGraphFixture.Graph(); graph["inputs"]!.AsArray().Add(EffectPackageGraphFixture.Input("coverage", "GlyphCoverage", "coverage"));
        var blur = new JsonObject { ["id"] = "blur", ["op"] = "Blur", ["input"] = "node:probe", ["sigmaPx"] = "parameter:peak",
            ["bounds"] = new JsonObject { ["expandPx"] = "ceil(3 * parameter:peak)", ["maxExpandPx"] = 12, ["maxSampleOffsetPx"] = 12 }, ["edge"] = "clamp" };
        graph["nodes"]!.AsArray().Add(blur); graph["parameters"] = new JsonArray("peak"); graph["bounds"]!["maxExpandPx"] = 12;
        graph["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "mask", ["op"] = "Mask", ["color"] = "node:blur", ["coverage"] = "input:coverage", ["bounds"] = EffectPackageGraphFixture.Bounds(), ["edge"] = "transparent" });
        var transform = new JsonObject { ["id"] = "move", ["op"] = "Transform", ["input"] = "node:mask", ["matrix"] = new JsonArray(1, 0, 0, 1, 2, 3),
            ["outputBounds"] = new JsonArray(0, 0, 16, 16), ["bounds"] = EffectPackageGraphFixture.Bounds(), ["edge"] = "transparent" };
        graph["nodes"]!.AsArray().Add(transform);
        graph["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "blend", ["op"] = "Blend", ["foreground"] = "node:move", ["background"] = "input:original", ["mode"] = "sourceOver", ["bounds"] = EffectPackageGraphFixture.Bounds(), ["edge"] = "transparent" });
        graph["output"] = "node:blend";
        var actual = EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(graph))[EffectPackageGraphFixture.GraphPath];
        Assert.Equal(5, actual.Plan.Nodes.Length); Assert.Equal(2, Assert.IsType<ImageGraphNode.Transform>(actual.Plan.Nodes[3]).Matrix.Tx);
        transform["matrix"] = new JsonArray(0, 0, 0, 0, 2, 3);
        Assert.Equal("InvalidGraphTransform", Assert.Throws<EffectPackageException>(() => EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(graph))).Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 七类完整字面量包含长度一数组及颜色精度(bool arrays)
    {
        string[] kinds = ["float", "float2", "float3", "float4", "int", "color", "matrix3x3"];
        JsonNode[] values = [JsonValue.Create(.123456789), new JsonArray(1, 2), new JsonArray(1, 2, 3), new JsonArray(1, 2, 3, 4), JsonValue.Create(7),
            new JsonObject { ["r"] = .123456789, ["g"] = .2, ["b"] = .3, ["a"] = .4 }, new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1)];
        var program = EffectPackageShaderFixture.Program(); var declarations = new JsonArray(); var uniforms = new JsonObject();
        for (int i = 0; i < kinds.Length; i++)
        {
            var declaration = new JsonObject { ["name"] = "v" + i, ["type"] = kinds[i] }; if (arrays) declaration["arrayLength"] = 1;
            declarations.Add(declaration); uniforms["v" + i] = arrays && values[i] is not JsonArray ? new JsonArray(values[i]) : values[i];
        }
        program["uniforms"] = declarations; var graph = EffectPackageGraphFixture.Graph(); graph["nodes"]![0]!["uniforms"] = uniforms;
        var node = Assert.IsType<ImageGraphNode.RuntimeShader>(Assert.Single(EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(graph, program))[EffectPackageGraphFixture.GraphPath].Plan.Nodes));
        var color = Assert.IsType<GraphUniformBinding.Literal>(node.Uniforms["v5"]).Value;
        Assert.Equal(.123456789, color.Numbers[0]); Assert.Equal(arrays ? 1 : (int?)null, color.ArrayLength);
        Assert.Equal(7, Assert.IsType<GraphUniformBinding.Literal>(node.Uniforms["v4"]).Value.Integers[0]);
    }

    [Fact]
    public void 未引用坏图也拒绝条件选项必须覆盖清单且取消不产出目录()
    {
        var graph = EffectPackageGraphFixture.Graph(); var input = graph["inputs"]![0]!.AsObject(); input.Remove("capture");
        input["source"] = "parameter:mode"; input["choices"] = new JsonObject { ["media"] = "BackgroundMedia", ["backdrop"] = "BackdropBelow" };
        input["backgroundRequirement"] = "opaqueCanvas"; graph["parameters"] = new JsonArray("mode"); graph["output"] = "input:original";
        void Parameters(JsonObject manifest) => manifest["parameters"]!.AsArray().Add(new JsonObject { ["id"] = "mode", ["type"] = "enum", ["label"] = "Source", ["default"] = "media",
            ["values"] = new JsonArray(new JsonObject { ["value"] = "media", ["label"] = "Media" }, new JsonObject { ["value"] = "backdrop", ["label"] = "Below" }) });
        var contents = EffectPackageGraphFixture.Contents(graph, changeManifest: Parameters);
        Assert.Equal(2, EffectPackageImageGraphs.Read(contents)[EffectPackageGraphFixture.GraphPath].Plan.Definition.Inputs[0].Choices.Count);
        input["choices"]!.AsObject().Remove("backdrop");
        Assert.Equal("InvalidGraphInputChoice", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(EffectPackageGraphFixture.Contents(graph, changeManifest: Parameters))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageImageGraphs.Read(contents, cancellationToken: cancel.Token));
    }

    [Theory]
    [InlineData("image", "PackageGraphImageMissing")]
    [InlineData("resource-capture", "PackageSemanticUnknownField")]
    [InlineData("literal", "PackageParameterTypeMismatch")]
    [InlineData("matrix", "PackageGraphComponentCountInvalid")]
    public void 图片角色字段和分量编码不允许隐式替代(string change, string code)
    {
        var graph = EffectPackageGraphFixture.Graph(); var input = graph["inputs"]![0]!.AsObject();
        if (change is "image" or "resource-capture")
        {
            input["source"] = "ResourceImage"; input["resource"] = "README.md";
            if (change == "image") input.Remove("capture");
        }
        if (change == "literal") graph["nodes"]![0]!["uniforms"]!["amount"] = new JsonArray(1, 2);
        if (change == "matrix")
            graph["nodes"] = new JsonArray(new JsonObject { ["id"] = "probe", ["op"] = "Transform", ["input"] = "input:original",
                ["matrix"] = new JsonArray(1, 0, 0, 1), ["outputBounds"] = new JsonArray(0, 0, 16, 16), ["bounds"] = EffectPackageGraphFixture.Bounds(), ["edge"] = "transparent" });
        var error = Assert.Throws<EffectPackageException>(() => EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(graph)));
        Assert.Equal(code, error.Code);
    }
}
