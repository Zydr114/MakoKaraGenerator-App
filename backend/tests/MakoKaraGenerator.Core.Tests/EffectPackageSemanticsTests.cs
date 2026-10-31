using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests;

public sealed class EffectPackageSemanticsTests
{
    private static (Dictionary<string, byte[]> Files, JsonObject Manifest) Fixture()
    { var files = EffectPackageFixture.Files(); files.Add("assets/image.png", [1]); return (files, EffectPackageFixture.NineParametersManifest(files)); }
    private static EffectPackageDefinition Read(JsonObject manifest, Dictionary<string, byte[]> files)
        => EffectPackageDefinition.Read(EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile));
    private static JsonObject Parameter(JsonObject manifest, string id) => manifest["parameters"]!.AsArray().Single(parameter => parameter!["id"]!.GetValue<string>() == id)!.AsObject();
    [Fact]
    public void 九类参数保留完整整数颜色精度元数据和明确图片角色()
    {
        var (files, manifest) = Fixture(); var package = Read(manifest, files); manifest["parameters"]![0]!["default"] = 4;
        var values = package.ResolveParameters("scale"); Assert.Equal(9, values.Count);
        Assert.Equal(9_007_199_254_740_993L, Assert.IsType<EffectParameterValue.Whole>(values["integer"]).Value);
        Assert.Equal(.123456789, Assert.IsType<EffectParameterValue.Color>(values["color"]).R);
        Assert.True(Assert.IsType<EffectParameterValue.Toggle>(values["boolean"]).Value);
        Assert.Equal("", Assert.IsType<EffectParameterValue.Text>(values["text"]).Value);
        Assert.Equal("assets/image.png", Assert.IsType<EffectParameterValue.Asset>(values["asset"]).Path);
        Assert.Equal(1.5, Assert.IsType<EffectParameterValue.Number>(values["peak"]).Value);
        Assert.Equal("easeOut", Assert.IsType<EffectParameterValue.Curve>(values["curve"]).Value.Easing);
        var option = package.Parameters.Single(parameter => parameter.Id == "enum"); Assert.Equal("本分支", option.Options[0].Label);
        Assert.Equal(["branch", "project"], Assert.IsType<EffectParameterConstraint.Choice>(option.Constraint).Values.ToArray());
        Assert.Equal("us", package.Parameters.Single(parameter => parameter.Id == "duration").Unit);
        Assert.Equal("assets/image.png", Assert.Single(package.ImagePaths));
    }
    [Theory]
    [InlineData("peak", "5")] [InlineData("peak", "\"2\"")] [InlineData("peak", "1e400")]
    [InlineData("integer", "9223372036854775808")] [InlineData("integer", "1.5")]
    [InlineData("duration", "-1")] [InlineData("duration", "1.0")]
    [InlineData("boolean", "1")] [InlineData("text", "null")] [InlineData("enum", "\"unknown\"")]
    [InlineData("color", "\"#FFFFFFFF\"")] [InlineData("color", "{\"r\":1.1,\"g\":0,\"b\":0,\"a\":1}")]
    [InlineData("color", "{\"r\":1,\"g\":0,\"b\":0}")]
    [InlineData("curve", "\"linear\"")] [InlineData("asset", "\"README.md\"")] [InlineData("asset", "\"assets/missing.png\"")]
    public void 坏默认值与显式覆盖共用严格校验而非夹值或类型转换(string id, string json)
    {
        var (files, manifest) = Fixture(); var package = Read(manifest, files); var literal = JsonNode.Parse(json);
        Parameter(manifest, id)["default"] = literal;
        var expected = Assert.Throws<EffectPackageException>(() => Read(manifest, files));
        var actual = Assert.Throws<EffectPackageException>(() => package.ResolveParameters("scale", Encoding.UTF8.GetBytes("{\"" + id + "\":" + json + "}")));
        Assert.Equal(expected.Code, actual.Code); Assert.StartsWith("parameters.", actual.Path, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("number")] [InlineData("integer")] [InlineData("duration")]
    public void 反向区间及负时长区间在读取时失败(string type)
    {
        var (files, manifest) = Fixture(); var parameter = Parameter(manifest, type == "number" ? "peak" : type);
        parameter["min"] = type == "duration" ? -1 : 8; parameter["max"] = 4;
        Assert.Equal("PackageParameterRangeInvalid", Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Theory]
    [InlineData("unknown", "PackageSemanticUnknownField")]
    [InlineData("missing", "PackageSemanticMissingField")]
    [InlineData("duplicate", "PackageParameterDuplicate")]
    [InlineData("choice", "PackageParameterChoiceDuplicate")]
    [InlineData("reference", "PackageParameterMissing")]
    [InlineData("references", "PackageReferenceDuplicate")]
    [InlineData("binding", "PackageBindingKindUnsupported")]
    [InlineData("type", "PackageParameterTypeUnsupported")]
    public void 清单不能静默忽略字段重复身份或无效引用(string change, string code)
    {
        var (files, manifest) = Fixture(); var parameters = manifest["parameters"]!.AsArray(); var export = manifest["exports"]![0]!;
        switch (change)
        {
            case "unknown": parameters[0]!["extra"] = 1; break;
            case "missing": parameters[0]!.AsObject().Remove("label"); break;
            case "duplicate": parameters.Add(parameters[0]!.DeepClone()); break;
            case "choice": Parameter(manifest, "enum")["values"]!.AsArray().Add(Parameter(manifest, "enum")["values"]![0]!.DeepClone()); break;
            case "reference": export["parameters"] = new JsonArray("missing"); break;
            case "references": export["parameters"] = new JsonArray("peak", "peak"); break;
            case "binding": export["bindingKinds"] = new JsonArray("currentSelection"); break;
            case "type": parameters[0]!["type"] = "expression"; break;
        }
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Theory]
    [InlineData("{\"kind\":\"cubicBezier\",\"first\":[0.2,-1],\"second\":[0.8,2]}", "cubicBezier")]
    [InlineData("{\"kind\":\"controlPoints\",\"points\":[[0,0],[0.5,1.5],[1,1]]}", "controlPoints")]
    public void 曲线允许显式有限过冲且保留原控制点(string json, string kind)
    {
        var (files, manifest) = Fixture(); Parameter(manifest, "curve")["default"] = JsonNode.Parse(json); var package = Read(manifest, files);
        var curve = Assert.IsType<EffectParameterValue.Curve>(package.ResolveParameters("scale")["curve"]).Value;
        Assert.Equal(kind, curve.Kind); Assert.True(curve.Points.Any(point => point.Y > 1));
    }
    [Theory]
    [InlineData("{\"kind\":\"easing\",\"easing\":\"unknown\"}")]
    [InlineData("{\"kind\":\"cubicBezier\",\"first\":[-0.1,0],\"second\":[0.8,1]}")]
    [InlineData("{\"kind\":\"controlPoints\",\"points\":[[0,0],[0.8,1],[0.2,1],[1,1]]}")]
    [InlineData("{\"kind\":\"controlPoints\",\"points\":[[0,0],[0,0],[1,1]]}")]
    [InlineData("{\"kind\":\"controlPoints\",\"points\":[[0,0],[1,2]]}")]
    public void 坏曲线不会被排序或修复(string json)
    {
        var (files, manifest) = Fixture(); Parameter(manifest, "curve")["default"] = JsonNode.Parse(json);
        Assert.Equal("PackageCurveInvalid", Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Fact]
    public void 部分覆盖只更新本导出参数且重复JSON与未知键拒绝()
    {
        var (files, manifest) = Fixture(); var package = Read(manifest, files);
        var changed = package.ResolveParameters("scale", "{\"peak\":2.25,\"integer\":9223372036854775807}"u8.ToArray());
        Assert.Equal(2.25, Assert.IsType<EffectParameterValue.Number>(changed["peak"]).Value);
        Assert.Equal(long.MaxValue, Assert.IsType<EffectParameterValue.Whole>(changed["integer"]).Value);
        Assert.Equal(1.5, Assert.IsType<EffectParameterValue.Number>(package.ResolveParameters("scale")["peak"]).Value);
        Assert.Equal("PackageJsonDuplicateKey", Assert.Throws<EffectPackageException>(() => package.ResolveParameters("scale", "{\"peak\":2,\"peak\":3}"u8.ToArray())).Code);
        Assert.Equal("PackageSemanticUnknownField", Assert.Throws<EffectPackageException>(() => package.ResolveParameters("scale", "{\"missing\":1}"u8.ToArray())).Code);
        manifest["exports"]![0]!["parameters"] = new JsonArray("peak");
        Assert.Equal("PackageSemanticUnknownField", Assert.Throws<EffectPackageException>(() => Read(manifest, files).ResolveParameters("scale", "{\"integer\":1}"u8.ToArray())).Code);
    }
    private static JsonObject Output(string id, params string[] dependencies) => new()
    { ["id"] = id, ["kind"] = "VisualTransform", ["target"] = "tokenFamily", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray([.. dependencies.Select(value => (JsonNode?)JsonValue.Create(value))]) };
    [Fact]
    public void 输出前向依赖生成稳定顺序且保留原声明顺序()
    {
        var (files, manifest) = Fixture(); manifest["exports"]![0]!["outputs"] = new JsonArray(Output("consumer", "source"), Output("other"), Output("source"));
        var controller = Read(manifest, files).GetController("scale");
        Assert.Equal(["consumer", "other", "source"], controller.Outputs.Select(output => output.Id).ToArray());
        Assert.Equal(["other", "source", "consumer"], controller.TopologicalOutputs.Select(output => output.Id).ToArray());
    }
    [Theory]
    [InlineData("cycle", "PackageOutputCycle")] [InlineData("missing", "PackageOutputDependencyMissing")]
    [InlineData("duplicate", "PackageOutputDuplicate")] [InlineData("kind", "PackageOutputKindUnsupported")]
    [InlineData("target", "PackageOutputTargetUnsupported")] [InlineData("bypass", "PackageOutputBypassInvalid")]
    public void 输出坏依赖未知类别与错误旁路不能通过(string change, string code)
    {
        var (files, manifest) = Fixture(); JsonArray outputs = new(Output("one"), Output("two")); manifest["exports"]![0]!["outputs"] = outputs;
        if (change == "cycle") { outputs[0]!["dependsOn"] = new JsonArray("two"); outputs[1]!["dependsOn"] = new JsonArray("one"); }
        if (change == "missing") outputs[0]!["dependsOn"] = new JsonArray("absent");
        if (change == "duplicate") outputs[1]!["id"] = "one";
        if (change == "kind") outputs[0]!["kind"] = "SourceMutation";
        if (change == "target") outputs[0]!["target"] = "fakeLine";
        if (change == "bypass") outputs[0]!["bypass"] = new JsonObject();
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Fact]
    public void 含有材质导出时不能只接受控制器部分且取消保留取消语义()
    {
        var (files, manifest) = Fixture(); manifest["exports"]!.AsArray().Add(new JsonObject { ["id"] = "material", ["kind"] = "material" });
        var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        Assert.Equal("PackageSemanticMissingField", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents)).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageDefinition.Read(contents, cancel.Token));
    }
    [Theory]
    [InlineData("file", "PackageFileBudgetExceeded")]
    [InlineData("total", "PackageContentBudgetExceeded")]
    [InlineData("count", "PackageFileCountExceeded")]
    public void R0展开预算包括清单且边界精确不静默跳过资源(string limit, string code)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files));
        long total = manifest.Length + files.Values.Sum(bytes => (long)bytes.Length); long single = Math.Max(manifest.Length, files.Values.Max(bytes => bytes.Length));
        EffectPackageContentBudget exact = new() { MaximumBytes = total, MaximumFileBytes = single, MaximumFiles = files.Count + 1 };
        _ = EffectPackageContents.Create(manifest, EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile, budget: exact);
        var tooSmall = limit switch { "file" => exact with { MaximumFileBytes = single - 1 }, "total" => exact with { MaximumBytes = total - 1 }, _ => exact with { MaximumFiles = files.Count } };
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageContents.Create(manifest, EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile, budget: tooSmall)).Code);
    }
}
