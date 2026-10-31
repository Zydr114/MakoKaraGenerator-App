using System.Runtime.InteropServices;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Skia;

/// <summary>Encodes the real premultiplied BGRA frame as a straight-alpha PNG.</summary>
/// <remarks>Native encoding scratch is serialized and bounded independently of retained HTTP frame leases.</remarks>
public sealed class SkiaBackendPreviewPngEncoder : IBackendPreviewPngEncoder
{
    private readonly object _gate = new();
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly long _maxPixelBytes;
    private readonly long _maxEncodedBytes;

    /// <summary>Creates an encoder with hard pixel and encoded-output byte budgets.</summary>
    public SkiaBackendPreviewPngEncoder(int maxWidth = 1920, int maxHeight = 1080,
        long maxPixelBytes = PreviewFrameContract.MaxFrameBytes, long maxEncodedBytes = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixelBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedBytes);
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _maxPixelBytes = Math.Min(maxPixelBytes, PreviewFrameContract.MaxFrameBytes);
        _maxEncodedBytes = Math.Min(maxEncodedBytes, PreviewFrameContract.MaxFrameBytes);
    }

    /// <inheritdoc />
    public byte[] Encode(BackendPreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Target != frame.Request.RenderRequest.ResolveTarget())
            throw new ArgumentException("The rendered dimensions do not match the requested output.", nameof(frame));
        long stride = (long)frame.Target.PixelWidth * 4;
        return EncodePixels(frame.Pixels, frame.Target.PixelWidth, frame.Target.PixelHeight, stride, frame.Format);
    }

    internal byte[] EncodePixels(byte[] pixels, int width, int height, long stride, PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || width > _maxWidth || height > _maxHeight)
            throw new ArgumentException("The rendered dimensions exceed the encoder limits.", nameof(width));
        if (format != PixelFormat.Bgra8888Premultiplied || stride != (long)width * 4)
            throw new ArgumentException("PNG encoding requires tightly packed premultiplied BGRA pixels.", nameof(format));
        long required = stride * height;
        if (required > _maxPixelBytes || required != pixels.LongLength)
            throw new ArgumentException("The rendered byte count does not match the bounded dimensions and stride.", nameof(pixels));

        lock (_gate)
        {
            SKImageInfo info = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKBitmap bitmap = new(info);
            if (bitmap.GetPixels() == IntPtr.Zero || bitmap.RowBytes != stride)
                throw new InvalidOperationException("Unable to allocate the bounded PNG pixel buffer.");
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Skia did not produce a PNG for the rendered frame.");
            if (encoded.Size <= 0 || encoded.Size > _maxEncodedBytes)
                throw new InvalidOperationException("The encoded PNG exceeds the configured frame byte limit.");
            return encoded.ToArray();
        }
    }
}
