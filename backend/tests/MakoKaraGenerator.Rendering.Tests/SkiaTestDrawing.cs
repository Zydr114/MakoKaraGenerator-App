using System.Runtime.InteropServices;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

internal static class SkiaTestDrawing
{
    // Keep the reference draw independent from SkiaRenderPipeline, while using the
    // backend selected for this test run so exact AA checks compare like renderers.
    internal static byte[] DrawOnSelectedBackend(byte[] basis, int width, int height, Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var device = SkiaRenderDeviceFactory.Create();
        using var surface = device.CreateSurface(info);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(basis, 0, bitmap.GetPixels(), basis.Length);
        using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
            surface.Canvas.DrawBitmap(bitmap, 0, 0, copy);
        draw(surface.Canvas);
        device.FlushAndSubmit(surface);

        byte[] result = new byte[basis.Length];
        GCHandle pin = GCHandle.Alloc(result, GCHandleType.Pinned);
        try { Assert.True(device.ReadPixels(surface, info, pin.AddrOfPinnedObject(), info.RowBytes)); }
        finally { pin.Free(); }
        return result;
    }
}
