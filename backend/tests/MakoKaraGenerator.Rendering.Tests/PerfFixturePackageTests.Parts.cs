using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>F3：必然为空的部件先判空再分配，像素不变，表面与材质执行减少。</summary>
public sealed partial class PerfFixturePackageTests
{
    /// <remarks>夹具行没有注音：注音部件与注音填充材质一律跳过；无装饰时描边、阴影部件也跳过。
    /// 关闭像素检查（生产配置），否则检查模式会照常捕获来核对判空。</remarks>
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task 必然为空的部件不捕获不执行材质且像素不变(bool gpu, bool decorated)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var (full, fullSurfaces) = await RenderParts(decorated, gpu, false);
        var (skipped, skippedSurfaces) = await RenderParts(decorated, gpu, true);
        Assert.Contains(full, value => value != 0);
        Assert.Equal(full, skipped);
        // 实测 41 → 27（无装饰）／41 → 31（有装饰）：省下注音部件的捕获与两次注音填充材质执行，
        // 无装饰时再省行与元素两个成员的描边、阴影捕获。
        Assert.True(fullSurfaces - skippedSurfaces >= (decorated ? 10 : 14), $"表面 {fullSurfaces} → {skippedSurfaces}");
    }

    private static async Task<(byte[] Pixels, int Surfaces)> RenderParts(bool decorated, bool gpu, bool skip)
    {
        var package = await Read("MaterialConstant");
        var project = Plain(package, []);
        var style = project.LyricStyle;
        if (decorated)
            style = style with
            {
                Stroke = new StrokeSpec { WidthPx = 6, Color = new RgbaColor(20, 40, 200) },
                Shadow = new ShadowSpec { DistancePx = 14, AngleDegrees = 135, BlurRadiusPx = 4, Color = new RgbaColor(0, 0, 0, 200) },
            };
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        project = project with { LyricStyle = style with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var device = new GraphPixelCheckModeTests.CountingDevice(BaseMaterialRuntimeTests.Device(gpu));
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiledPackage.Definition, null, device, pixelChecks: false);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false, graphPixelChecks: false) { SkipKnownEmptyParts = skip };
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        Assert.Empty(image.MaterialDiagnostics);
        return (image.PixelMemory.ToArray(), device.Surfaces);
    }
}
