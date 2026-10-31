using System.Runtime.InteropServices;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

internal static class SkiaTestReadback
{
    public static byte[] Pixels(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int rowBytes = checked(image.Width * 4);
        byte[] result = new byte[checked(rowBytes * image.Height)];
        GCHandle handle = GCHandle.Alloc(result, GCHandleType.Pinned);
        SKImage? raster = image.ToRasterImage(true);
        try
        {
            if (raster is null || !raster.ReadPixels(info, handle.AddrOfPinnedObject(), rowBytes, 0, 0))
                throw new InvalidOperationException($"Skia test image readback failed ({image.Width}×{image.Height}).");
        }
        finally
        {
            if (raster is not null && !ReferenceEquals(raster, image)) raster.Dispose();
            handle.Free();
        }
        return result;
    }

    public static byte[] Pixel(SKImage image, int x, int y)
    {
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"Pixel coordinate ({x}, {y}) is outside {image.Width}×{image.Height}.");
        byte[] pixels = Pixels(image);
        int offset = checked((y * image.Width + x) * 4);
        return pixels.AsSpan(offset, 4).ToArray();
    }
}
