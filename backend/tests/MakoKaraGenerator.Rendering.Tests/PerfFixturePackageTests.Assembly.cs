using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>F4：相邻的无历史层在同一张表面上依次绘制，共享空层跳过；像素逐字节不变，表面减少。</summary>
public sealed partial class PerfFixturePackageTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task 层组装单遍绘制与逐层复制像素一致且表面减少(bool gpu, bool decorated)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var (perLayer, perLayerSurfaces) = await RenderAssembly(decorated, gpu, false);
        var (single, singleSurfaces) = await RenderAssembly(decorated, gpu, true);
        Assert.Contains(perLayer, value => value != 0);
        Assert.Equal(perLayer, single);
        // 这里的层都没有历史：逐层时每层整张复制一次，单遍只画一张。实测 27 → 15（无装饰）／36 → 20（描边、阴影、辉光）。
        Assert.True(perLayerSurfaces - singleSurfaces >= (decorated ? 16 : 12), $"表面 {perLayerSurfaces} → {singleSurfaces}");
    }

    private static async Task<(byte[] Pixels, int Surfaces)> RenderAssembly(bool decorated, bool gpu, bool singlePass)
    {
        var package = await Read("MaterialConstant");
        var project = Plain(package, []);
        var style = project.LyricStyle;
        if (decorated)
            style = style with
            {
                Stroke = new StrokeSpec { WidthPx = 6, Color = new RgbaColor(20, 40, 200) },
                Shadow = new ShadowSpec { DistancePx = 14, AngleDegrees = 135, BlurRadiusPx = 4, Color = new RgbaColor(0, 0, 0, 200) },
                Glow = new GlowSpec { SpreadPx = 3, BlurRadiusPx = 5, Color = new RgbaColor(255, 220, 0, 180) },
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
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false, graphPixelChecks: false) { SinglePassLayerAssembly = singlePass };
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        Assert.Empty(image.MaterialDiagnostics);
        return (image.PixelMemory.ToArray(), device.Surfaces);
    }
}
