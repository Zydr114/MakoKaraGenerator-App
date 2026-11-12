using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    [Theory]
    [InlineData("solid")] [InlineData("linear")] [InlineData("radial")] [InlineData("glyph")]
    public async Task 包内显式回退投影与静态规范外观真实像素一致(string kind)
    {
        JsonObject? json = kind == "solid" ? null : MixedEffectPackageFixture.Gradient(space: kind == "glyph" ? "Glyph" : "Line",
            kind: kind == "radial" ? "Radial" : "Linear");
        var compiled = await Compile(MixedEffectPackageFixture.Contents(pure: true, fallback: json));
        var value = compiled.GetMaterialFallback(compiled.Resources.GetMaterial("ink"));
        var fallback = compiled.Definition.GetMaterial("ink").Fallback;
        var fill = fallback switch
        {
            EffectMaterialFallback.Solid solid => new FillSpec { Color = solid.Color },
            EffectMaterialFallback.Gradient gradient => new FillSpec { Color = gradient.Value.Stops[0].Color, Gradient = gradient.Value },
            _ => throw new InvalidOperationException(),
        };
        var snapshot = Snapshot(); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 1_000_000);
        var target = Assert.Single(scene.TokenGeometries).WithRuby;
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, [],
            [new(target, new(unsung: value, sung: value))]);
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        using var expected = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null,
            LyricStyle = snapshot.LyricStyle with { UnsungFill = fill, SungFill = fill } }, scene.TimeUs) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Contains(actual.PixelMemory.ToArray().Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        // 这是回退的显式消费入口；正常模式包材质仍由后续区域适配实施，不能自动当回退使用。
        Assert.Equal("MaterialOwnershipRequired", Assert.Throws<PreparedFrameException>(() => new PreparedRenderFrame(snapshot, scene.TimeUs,
            Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, [],
            [new(target, new(sung: Fx.MaterialValue.PackageExport(compiled.Resources.GetMaterial("ink"), compiled.ResolveParameters("ink"))))])).Code);
    }

    [Theory]
    [InlineData("Glyph", "Oklch")] [InlineData("Line", "Oklch")]
    public async Task 生产未支持的回退不能部分启用混合控制器(string space, string colorSpace)
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(fallback: MixedEffectPackageFixture.Gradient(space, colorSpace)));
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => Compile(definition.Contents));
        Assert.Equal("PackageMaterialFallbackUnsupported", error.Code); Assert.Equal("exports.ink.fallback", error.Path);
        Assert.Equal(error.Code, Assert.Throws<EffectPackageException>(() => CompiledPackageGraphExports.Compile(definition)).Code);
    }

    [Fact]
    public void 回退渐变的进度坐标已删去而在读取时拒绝()
    {
        // 0.112.74 起 Core 删去进度坐标（P3 D-P3-7）；此前它能读取、编译时才报不支持。
        var error = Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(
            MixedEffectPackageFixture.Contents(fallback: MixedEffectPackageFixture.Gradient("Progress", "Hsv"))));
        Assert.Equal("PackageMaterialFallbackInvalid", error.Code); Assert.EndsWith(".fallback.gradient.coordinateSpace", error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 混合包编译失败不公开资源回退或部分控制器且未导出源码仍必须编译()
    {
        var failed = await Compile(MixedEffectPackageFixture.Contents(controllerSource: "invalid !"));
        Assert.False(failed.Succeeded); Assert.False(failed.Artifact.Succeeded);
        Assert.Throws<KeyNotFoundException>(() => failed.Resources.GetMaterial("ink"));
        var untrusted = new Fx.ResourceCatalog(failed.Definition.Contents.PackageId, failed.Definition.Contents.Revision, ["ink"], ["tint"], []);
        Assert.Equal("PackageCompilationFailed", Assert.Throws<EffectPackageException>(() => failed.GetMaterialFallback(untrusted.GetMaterial("ink"))).Code);
        var unused = await Compile(MixedEffectPackageFixture.Contents(manifest => manifest["exports"]!.AsArray().RemoveAt(0), controllerSource: "invalid !"));
        Assert.Empty(unused.Definition.Controllers); Assert.False(unused.Succeeded); Assert.NotNull(unused.ControllerArtifact);
        var pureWithController = MixedEffectPackageFixture.Contents(manifest => manifest["exports"]!.AsArray().Insert(0, new JsonObject
        { ["id"] = "missing", ["kind"] = "controller", ["name"] = "Missing", ["entryType"] = "PackEffect", ["parameters"] = new JsonArray(),
            ["bindingKinds"] = new JsonArray("project"), ["outputs"] = new JsonArray() }), pure: true);
        Assert.Equal("PackageCSharpMissing", (await Assert.ThrowsAsync<EffectPackageException>(() => Compile(pureWithController))).Code);
    }

    [Fact]
    public async Task 没有控制器的包也严格检查原生源码且取消不变为失败包()
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true, shaderSource: "bad shader"));
        Assert.Equal("ShaderCompilationFailed", (await Assert.ThrowsAsync<EffectPackageException>(() => Compile(definition.Contents))).Code);
        Assert.Equal("ShaderCompilationFailed", Assert.Throws<EffectPackageException>(() => CompiledPackageGraphExports.Compile(definition)).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CompiledEffectPackage.CompileAsync(definition, Compiler(), new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator(), cancel.Token));
    }
}
