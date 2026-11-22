using System.Diagnostics;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>向双阶段预览端口输出合成测试图案（支持 CPU/Vulkan 及 Bgra8888/Rgba8888 双格式精度验收）。</summary>
    internal PreviewRenderFrame RenderSyntheticToPreview(
        RenderTarget target,
        long timeUs,
        IPreviewPort port,
        PreviewPixelFormat? pixelFormat = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(port);
        long startTimestamp = Stopwatch.GetTimestamp();
        lock (_device.SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SKColorType colorType = PreviewTransportSelector.ToSkColorType(pixelFormat ?? port.PoolInfo.PixelFormat);
            SKImageInfo info = new(target.PixelWidth, target.PixelHeight, colorType, SKAlphaType.Premul);
            using IPreviewFrameTarget frameTarget = port.BeginFrame(_device, info, target);
            DrawSyntheticPattern(frameTarget.Surface, target.PixelWidth, target.PixelHeight, frameTarget.FrameSequence);
            long beforeCommit = Stopwatch.GetTimestamp();
            PreviewFrameLease lease = frameTarget.Commit();
            long afterCommit = Stopwatch.GetTimestamp();
            var timings = new PreviewFrameTimings(
                ElapsedMicroseconds(startTimestamp, beforeCommit),
                ElapsedMicroseconds(beforeCommit, afterCommit));
            return new PreviewRenderFrame(target, timeUs, lease, timings);
        }
    }

    private static void DrawSyntheticPattern(SKSurface surface, int width, int height, ulong sequence)
    {
        using var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src };
        SKCanvas canvas = surface.Canvas;
        canvas.ClipRect(new(0, 0, width, height));
        canvas.Clear(SKColors.Black);
        Draw(0, 0, width / 2, height / 2, SKColors.Red);
        Draw(width / 2, 0, width - width / 2, height / 2, SKColors.Lime);
        Draw(0, height / 2, width / 2, height - height / 2, SKColors.Blue);
        Draw(width / 2, height / 2, width - width / 2, height - height / 2, SKColors.White);
        int band = Math.Max(1, height / 8);
        for (int index = 0; index < 8; ++index)
        {
            byte value = (byte)((index * 255 + 3) / 7);
            Draw(index * width / 8, height / 2 - band / 2, (index + 1) * width / 8 - index * width / 8, band, new(value, value, value));
        }

        Draw(width / 4, height * 3 / 4, width / 4, band, new(255, 0, 255, 128));
        Draw(width / 2, height * 3 / 4, width / 4, band, SKColors.Transparent);
        for (int bit = 0; bit < 16; ++bit)
        {
            Draw(bit * width / 16, 0, (bit + 1) * width / 16 - bit * width / 16, Math.Max(1, height / 16),
                ((sequence >> bit) & 1) == 1 ? SKColors.White : SKColors.Black);
        }

        void Draw(int x, int y, int rectangleWidth, int rectangleHeight, SKColor color)
        {
            paint.Color = color;
            canvas.DrawRect(x, y, rectangleWidth, rectangleHeight, paint);
        }
    }
}
