using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GpuRenderOutputTests
{
    [Fact]
    public void 默认Cpu拒绝Gpu输出且不破坏原像素入口()
    {
        using var device = new CpuSkiaRenderDevice();
        using var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
        var target = new RenderTarget { PixelWidth = 96, PixelHeight = 64 };
        var snapshot = new ProjectSnapshot
        {
            Name = "CPU output remains explicit",
            Composition = new() { CanvasWidth = 96, CanvasHeight = 64, BackgroundColor = new(17, 34, 51) },
        };
        var request = RenderRequest.At(snapshot, 42);

        Assert.Equal("GpuPreviewUnavailable", Assert.Throws<GpuPreviewException>(() => renderer.RenderGpu(request)).Code);
        Assert.Equal("GpuPreviewUnavailable", Assert.Throws<GpuPreviewException>(() => renderer.RenderSyntheticGpu(target, 0, 1)).Code);

        using var frame = renderer.Render(request);
        Assert.Equal(42, frame.TimeUs);
        Assert.Equal(target, frame.Target);
        Assert.True(frame.TryGetPixel(0, 0, out var color));
        Assert.Equal(new RgbaColor(17, 34, 51), color);
        Assert.Empty(frame.ImageEffectDiagnostics);
        Assert.Empty(frame.MaterialDiagnostics);
    }
}
