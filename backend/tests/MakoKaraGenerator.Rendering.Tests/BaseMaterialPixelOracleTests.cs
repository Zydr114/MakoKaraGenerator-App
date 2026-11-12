using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class BaseMaterialPixelOracleTests
{
    [Fact]
    public void 半透明逐图元重叠135与先合并白mask后着色80是不同算子()
    {
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(24, 16, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        using var colored = SKSurface.Create(info)!; using var mask = SKSurface.Create(info)!;
        colored.Canvas.Clear(SKColors.Transparent); mask.Canvas.Clear(SKColors.Transparent);
        using var tint = new SKPaint { Color = new(26, 64, 102, 80), IsAntialias = false };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = false };
        foreach (var rectangle in new[] { new SKRect(2, 2, 16, 14), new SKRect(8, 2, 22, 14) })
        { colored.Canvas.DrawRect(rectangle, tint); mask.Canvas.DrawRect(rectangle, white); }
        using var direct = colored.Snapshot(); using var raw = mask.Snapshot();
        Assert.Equal(135, SkiaTestReadback.Pixel(direct, 12, 8)[3]);
        var once = BaseMaterialPixelOracle.Tint(SkiaTestReadback.Pixels(raw), .5, .5);
        Assert.Equal(80, once[(8 * 24 + 12) * 4 + 3]);
        Assert.Equal(80, once[(8 * 24 + 4) * 4 + 3]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 两态材质分别着色且半唱完整图像匹配raw几何加CPU颜色oracle(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package, mode: 11), BaseMaterialPackageFixture.Instance(package, "opacity", 2)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot); Assert.True(compiled.Succeeded);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition, null, device);
        using var resources = BaseMaterialRuntimeTests.Resources(native);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false);
        foreach (long time in new long[] { 500_000, 2_000_000, 3_500_000 })
        {
            var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot, time);
            var token = Assert.Single(frame.FinalGeometry);
            MaterialValue.PackageMaterial Material(double amount) => (MaterialValue.PackageMaterial)MaterialValue.PackageExport(
                native.Resources.GetMaterial("ink"), new([new("amount", new ParameterValue.Number(amount))]));
            var unsung = Material(.25); var sung = Material(.75);
            var bindings = frame.BaseMaterials.Select(binding => binding with
            { Material = binding.Slot == PreparedMaterialSlot.SungFill ? sung : unsung }).ToImmutableArray();
            var separate = BaseMaterialPackageFixture.Parent(frame).WithTextPartitions(frame.BaseTransforms,
                [new(token.BaseOnly, TestStyle.Update(unsung, sung, opacityFactor: .5))], frame.ClusterTransforms, bindings);
            byte[] expected = BaseMaterialPixelOracle.Expected(renderer, snapshot, separate, .25, .75, .5);
            using var actual = BaseMaterialRuntimeTests.Render(renderer, snapshot, separate);
            BaseMaterialPixelOracle.Compare(expected, actual, 2);
            if (time == 2_000_000)
            {
                byte[] swapped = BaseMaterialPixelOracle.Expected(renderer, snapshot, separate, .75, .25, .5);
                Assert.True(expected.Where((value, offset) => Math.Abs(value - swapped[offset]) > 2).Any(), "半唱必须对两侧amount交换敏感。");
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真正Warp的raw覆盖与独立整数平移参考一致(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package, mode: 11)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot); var token = Assert.Single(frame.FinalGeometry);
        Rect2 domain = new(-100, -200, 800, 400); const int shiftX = 5, shiftY = 3;
        var warp = new GridWarp(domain, 1, 1, [new(domain.Left + shiftX, domain.Top + shiftY), new(domain.Right + shiftX, domain.Top + shiftY),
            new(domain.Left + shiftX, domain.Bottom + shiftY), new(domain.Right + shiftX, domain.Bottom + shiftY)]);
        var warped = frame.WithTextWarps([new(new("oracle-warp"), "oracle-warp", null, new TextTarget.Base(token.BaseOnly), warp)]);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        using var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
        byte[] before = Raw(frame), after = Raw(warped), shifted = new byte[before.Length];
        for (int y = shiftY; y < 360; y++) for (int x = shiftX; x < 640; x++)
            before.AsSpan(((y - shiftY) * 640 + x - shiftX) * 4, 4).CopyTo(shifted.AsSpan((y * 640 + x) * 4, 4));
        BaseMaterialPixelOracle.Compare(shifted, after, 640, 2);
        byte[] Raw(PreparedRenderFrame value)
        {
            using var capture = renderer.CaptureSceneTarget(BaseMaterialRuntimeTests.Request(snapshot, value), new SceneCaptureTarget.Base(token.BaseOnly),
                new(false, false, false, false, SceneCaptureRegion.Fill), GraphInputSource.GlyphCoverage,
                BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default);
            return SkiaTestReadback.Pixels(capture.Image);
        }
    }
}
