using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Effects;

public sealed class MixedEffectPackageTests
{
    [Theory]
    [InlineData("unmasked", EffectMaterialCoverageMode.Unmasked)]
    [InlineData("masked", EffectMaterialCoverageMode.Masked)]
    public void 材质覆盖声明强类型保存并进入包修订(string mode, EffectMaterialCoverageMode expected)
    {
        var contents = MixedEffectPackageFixture.Contents(manifest => manifest["exports"]![1]!["coverageMode"] = mode);
        var definition = EffectPackageDefinition.Read(contents);
        Assert.Equal(expected, definition.GetMaterial("ink").CoverageMode);
        if (mode == "unmasked") Assert.NotEqual(MixedEffectPackageFixture.Contents().Revision, contents.Revision);
    }

    [Theory]
    [InlineData("missing", "PackageSemanticMissingField")]
    [InlineData("auto", "PackageMaterialCoverageModeInvalid")]
    [InlineData("Masked", "PackageMaterialCoverageModeInvalid")]
    [InlineData("number", "PackageParameterTypeMismatch")]
    public void 材质不猜测覆盖模式或转换旧清单(string change, string code)
    {
        var contents = MixedEffectPackageFixture.Contents(manifest =>
        {
            var material = manifest["exports"]![1]!.AsObject();
            if (change == "missing") material.Remove("coverageMode");
            else if (change == "number") material["coverageMode"] = 1;
            else material["coverageMode"] = change;
        });
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents)).Code);
    }

    [Fact]
    public void 三类导出共用参数真值及独立身份而不把文件路径冒充句柄()
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents());
        Assert.Equal(["controller", "ink", "tint"], definition.Exports.Select(export => export.Id));
        Assert.Equal("PackEffect", Assert.Single(definition.Controllers).EntryType);
        Assert.Equal(["fill", "stroke"], Assert.Single(definition.Materials).Regions.ToArray());
        Assert.Equal(EffectPackageGraphFixture.GraphPath, Assert.Single(definition.Graphs).GraphPath);
        foreach (string id in new[] { "controller", "ink", "tint" })
        {
            Assert.Equal(.5, Assert.IsType<EffectParameterValue.Number>(definition.ResolveParameters(id)["amount"]).Value);
            Assert.Equal(.75, Assert.IsType<EffectParameterValue.Number>(definition.ResolveParameters(id, "{\"amount\":0.75}"u8.ToArray())["amount"]).Value);
        }
        Assert.Equal("PackageExportKindMismatch", Assert.Throws<EffectPackageException>(() => definition.GetGraph("ink")).Code);
        Assert.Equal("PackageExportKindMismatch", Assert.Throws<EffectPackageException>(() => definition.GetController("tint")).Code);
        Assert.Equal("PackageExportMissing", Assert.Throws<EffectPackageException>(() => definition.GetExport(EffectPackageGraphFixture.GraphPath)).Code);
    }

    [Fact]
    public void 纯资源包无控制器且完整渐变与半透明颜色保持原值()
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true, fallback: MixedEffectPackageFixture.Gradient()));
        Assert.Empty(definition.Controllers);
        var fallback = Assert.IsType<EffectMaterialFallback.Gradient>(definition.GetMaterial("ink").Fallback).Value;
        Assert.Equal(GradientKind.Linear, fallback.Kind); Assert.Equal(15, fallback.AngleDegrees);
        Assert.Equal(.4, fallback.CenterX); Assert.Equal(.6, fallback.CenterY);
        Assert.Equal(.35, fallback.RadiusX); Assert.Equal(.45, fallback.RadiusY);
        Assert.Equal(GradientCoordinateSpace.Line, fallback.CoordinateSpace);
        Assert.Equal(GradientColorSpace.Hsv, fallback.ColorSpace); Assert.Equal(GradientHuePath.Longer, fallback.HuePath);
        Assert.Equal([0d, .5, 1], fallback.Stops.Select(stop => stop.Offset));
        Assert.Equal(new RgbaColor(255, 0, 0, 64), fallback.Stops[0].Color);
        var solid = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents()).GetMaterial("ink").Fallback;
        Assert.Equal(new RgbaColor(51, 128, 204, 160), Assert.IsType<EffectMaterialFallback.Solid>(solid).Color);
    }

    [Theory]
    [InlineData("duplicate", "PackageExportDuplicate")]
    [InlineData("path", "PackageExportGraphMissing")]
    [InlineData("parameters", "PackageExportGraphParameterMissing")]
    [InlineData("region", "PackageMaterialRegionInvalid")]
    [InlineData("emptyRegion", "PackageMaterialRegionInvalid")]
    [InlineData("materialOutput", "PackageMaterialOutputInvalid")]
    [InlineData("materialBypass", "PackageMaterialOutputInvalid")]
    [InlineData("graphOutput", "PackageGraphOutputInvalid")]
    [InlineData("graphBypass", "PackageGraphBypassInvalid")]
    [InlineData("mapping", "PackageBypassMappingUnsupported")]
    [InlineData("extra", "PackageSemanticUnknownField")]
    public void 混合包的未使用材质或图不能被忽略(string change, string code)
    {
        var contents = MixedEffectPackageFixture.Contents(manifest =>
        {
            var material = manifest["exports"]![1]!; var graph = manifest["exports"]![2]!;
            switch (change)
            {
                case "duplicate": material["id"] = "controller"; break;
                case "path": material["graph"] = "README.md"; break;
                case "parameters": material["parameters"] = new JsonArray(); break;
                case "region": material["regions"] = new JsonArray("shadow"); break;
                case "emptyRegion": material["regions"] = new JsonArray(); break;
                case "materialOutput": material["outputs"]![0]!["target"] = "line"; break;
                case "materialBypass": material["outputs"]![0]!.AsObject().Remove("bypass"); break;
                case "graphOutput": graph["outputs"]![0]!["kind"] = "Overlay"; break;
                case "graphBypass": graph["outputs"]![0]!["bypass"] = new JsonObject { ["kind"] = "materialFallback", ["mapping"] = "identity" }; break;
                case "mapping": material["outputs"]![0]!["bypass"]!["mapping"] = "estimated"; break;
                case "extra": material["fallback"]!["silentDefault"] = true; break;
            }
        });
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents)).Code);
    }

    [Theory]
    [InlineData("#FFFFFF")] [InlineData("#FFFFFF00 ")] [InlineData("#00FF00GG")]
    public void 回退颜色不补透明度或修剪错误字面量(string color)
    {
        var contents = MixedEffectPackageFixture.Contents(fallback: new() { ["kind"] = "solid", ["color"] = color });
        Assert.Equal("PackageMaterialFallbackInvalid", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents)).Code);
    }

    [Theory]
    [InlineData("missing", "PackageSemanticMissingField")]
    [InlineData("order", "PackageMaterialFallbackInvalid")]
    [InlineData("endpoint", "PackageMaterialFallbackInvalid")]
    [InlineData("radius", "PackageMaterialFallbackInvalid")]
    [InlineData("enum", "PackageMaterialFallbackInvalid")]
    [InlineData("numericEnum", "PackageParameterTypeMismatch")]
    public void 渐变不补字段不排序不修正端点或枚举(string change, string code)
    {
        var fallback = MixedEffectPackageFixture.Gradient(kind: "Radial"); var gradient = fallback["gradient"]!;
        switch (change)
        {
            case "missing": gradient.AsObject().Remove("huePath"); break;
            case "order": gradient["stops"]![1]!["offset"] = -.1; break;
            case "endpoint": gradient["stops"]![0]!["offset"] = .1; break;
            case "radius": gradient["radiusX"] = 0; break;
            case "enum": gradient["coordinateSpace"] = "line"; break;
            case "numericEnum": gradient["kind"] = 1; break;
        }
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(fallback: fallback))).Code);
    }

    [Theory]
    [InlineData(false, "PackageGraphBypassMissing")]
    [InlineData(true, "PackageGraphBypassMismatch")]
    public void 背景依赖图必须显式旁路到图根所声明的原输入(bool mismatch, string code)
    {
        var contents = MixedEffectPackageFixture.Contents(manifest =>
        {
            var output = manifest["exports"]![2]!["outputs"]![0]!;
            if (mismatch) output["bypass"]!["input"] = "input:background"; else output.AsObject().Remove("bypass");
        }, background: true);
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents)).Code);
    }

    [Fact]
    public void 合法透明旁路固定原输入且取消保留取消语义()
    {
        var contents = MixedEffectPackageFixture.Contents(background: true);
        var definition = EffectPackageDefinition.Read(contents);
        Assert.Equal("original", Assert.IsType<EffectOutputBypass.GraphInput>(definition.GetGraph("tint").Outputs[0].Bypass).InputId);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageDefinition.Read(contents, cancel.Token));
    }
}
