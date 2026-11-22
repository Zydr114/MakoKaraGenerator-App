using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>P3 第 4 步：描边包材质只占最内层一圈，外圈由外圈描边部件原样绘制（P3 设计 §6.3）。</summary>
public sealed partial class PerfFixturePackageTests
{
    [Fact]
    public async Task 描边包材质只占最内层外圈原样绘制()
    {
        var package = await Read("MaterialConstant");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        var outer = StrokeSpec.Create(new(20, 20, 230), 5);
        byte[] actual = await RenderStrokes(package, [new StrokeSpec { Material = material, WidthPx = 5 }, outer]);
        // 对照：最内层换成材质的默认常量色 (0.95, 0.55, 0.2) 的纯色描边。
        byte[] expected = await RenderStrokes(package, [StrokeSpec.Create(new(242, 140, 51), 5), outer]);

        var (inner, outside) = Count(actual);
        var (innerExpected, outsideExpected) = Count(expected);
        Assert.True(innerExpected > 200 && outsideExpected > 200, $"对照：内圈 {innerExpected} 外圈 {outsideExpected}");
        // 两种颜色的内部像素一样多；只有内外圈交界的抗锯齿像素因分开合成而不同。
        Assert.InRange(inner, innerExpected * .95, innerExpected * 1.05);
        Assert.InRange(outside, outsideExpected * .95, outsideExpected * 1.05);

        static (int Inner, int Outer) Count(byte[] bgra)
        {
            int inner = 0, outer = 0;
            for (int i = 0; i < bgra.Length; i += 4)
            {
                if ((bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) == (51, 140, 242, 255)) inner++;
                else if ((bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) == (230, 20, 20, 255)) outer++;
            }
            return (inner, outer);
        }
    }

    private static async Task<byte[]> RenderStrokes(EffectPackageContents package, StrokeLayers strokes)
    {
        var project = Plain(package, []);
        project = project with { LyricStyle = project.LyricStyle with { Unsung = project.LyricStyle.Unsung with { Strokes = strokes } } };
        var snapshot = ProjectSnapshot.FromProject(project);
        if (strokes.All(static layer => layer.Material is not MaterialSpec.PackageMaterial))
        {
            // 对照没有包材质：没有可准备的实例或源材质，直接绘制。
            using var plain = new SkiaRenderPipeline().Render(RenderRequest.At(snapshot, TimeUs) with { TransparentBackground = true });
            return plain.PixelMemory.ToArray();
        }
        var compiled = await Compile(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.Compile(compiledPackage.Definition);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        Assert.Empty(image.MaterialDiagnostics);
        return image.PixelMemory.ToArray();
    }
}
