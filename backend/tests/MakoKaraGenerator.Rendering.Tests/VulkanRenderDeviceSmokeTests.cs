using System.Runtime.InteropServices;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VulkanRenderDeviceSmokeTests
{
    [Fact]
    public void VulkanDeviceCreatesSurfaceFlushesAndReadsPixelsWhenEnabled()
    {
        // GPU smoke is opt-in because the normal rendering test suite must remain
        // runnable on hosts without a Vulkan loader or graphics queue.
        if (!string.Equals(Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE"), "1", StringComparison.Ordinal))
            return;

        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        Assert.Equal("vulkan", device.Info.Backend);

        var imageInfo = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = device.CreateSurface(imageInfo);
        surface.Canvas.Clear(SKColors.Red);
        using (var paint = new SKPaint { Color = SKColors.Blue, IsAntialias = true })
            surface.Canvas.DrawCircle(32, 32, 12, paint);

        device.FlushAndSubmit(surface);

        byte[] pixels = new byte[imageInfo.BytesSize];
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.True(device.ReadPixels(surface, imageInfo, handle.AddrOfPinnedObject(), imageInfo.RowBytes));
        }
        finally
        {
            handle.Free();
        }

        Assert.Equal((byte)0, pixels[0]);
        Assert.Equal((byte)0, pixels[1]);
        Assert.Equal((byte)255, pixels[2]);
        Assert.Equal((byte)255, pixels[3]);

        int center = (32 * imageInfo.Width + 32) * 4;
        Assert.Equal((byte)255, pixels[center]);
        Assert.Equal((byte)0, pixels[center + 1]);
        Assert.Equal((byte)0, pixels[center + 2]);
        Assert.Equal((byte)255, pixels[center + 3]);
    }
}
