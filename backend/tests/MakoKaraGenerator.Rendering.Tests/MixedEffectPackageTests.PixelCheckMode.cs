using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    // H3：同一准备帧经材质图、区域约束与场景合成，关闭逐像素检查后不为检查读回或同步提交，主画面逐字节相同。
    [Theory]
    [InlineData("masked", false)] [InlineData("unmasked", false)]
    [InlineData("masked", true)] [InlineData("unmasked", true)]
    public async Task 关闭图材质逐像素检查后不读回中间产物_主画面与开启时逐字节相同(string mode, bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        string? shader = mode == "unmasked" ? "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(amount,0,original.eval(p).a*.001,1); }" : null;
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => manifest["exports"]![1]!["coverageMode"] = mode, shaderSource: shader));
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var snapshot = Snapshot(); var effect = package.CreateEffect("controller", new("material"), new Fx.EffectBinding.Project(), 1);
        await using var session = await new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var output = new RenderTarget { PixelWidth = 640, PixelHeight = 360 };
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, output);
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true, Target = output };

        var on = Render(true); var off = Render(false);
        Assert.True(on.ImageReads > 0);
        Assert.Equal(0, off.ImageReads);
        Assert.True(off.Submits < on.Submits, $"关闭 {off.Submits} 次提交，开启 {on.Submits} 次");
        Assert.Contains(on.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 0);
        Assert.Equal(on.Pixels, off.Pixels);

        (byte[] Pixels, int ImageReads, int Submits) Render(bool checks)
        {
            using var device = new GraphPixelCheckModeTests.CountingDevice(BaseMaterialRuntimeTests.Device(gpu));
            using var native = CompiledPackageGraphExports.CompileWithDevice(package.Definition, null, device, checks);
            using var resources = MaterialResources(native, package, new(0, 0, 640, 360));
            using var budget = new ImageSurfaceBudgetSession();
            using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false, graphPixelChecks: checks);
            using var frame = renderer.Render(request);
            Assert.Empty(frame.MaterialDiagnostics);
            return (frame.PixelMemory.ToArray(), device.ImageReads, device.Submits);
        }
    }
}
