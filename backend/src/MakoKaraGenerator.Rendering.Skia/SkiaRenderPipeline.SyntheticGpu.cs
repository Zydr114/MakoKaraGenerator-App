using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>有限基准合成图案；调用方必须验证独立启动开关，不经过工程准备或作者代码。</summary>
    public GpuRenderFrame RenderSyntheticGpu(RenderTarget target, long timeUs, ulong sequence)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_device.SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is not VulkanSkiaRenderDevice { SupportsGpuFrames: true } gpu)
                throw new GpuPreviewException("GpuPreviewUnavailable", "Synthetic GPU output requires the same shared Vulkan device.");
            // Encode the exact authoritative lease identity. A caller attempt
            // counter can diverge after pool exhaustion or a new stream.
            sequence = gpu.NextGpuFrameSequence;
            using var surface = _device.CreateSurface(new(target.PixelWidth, target.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src };
            var canvas = surface.Canvas;
            canvas.ClipRect(new(0, 0, target.PixelWidth, target.PixelHeight));
            canvas.Clear(SKColors.Black);
            int width = target.PixelWidth, height = target.PixelHeight;
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
                Draw(bit * width / 16, 0, (bit + 1) * width / 16 - bit * width / 16, Math.Max(1, height / 16),
                    ((sequence >> bit) & 1) == 1 ? SKColors.White : SKColors.Black);
            return new(target, timeUs, gpu.CopyGpuFrame(surface, target));

            void Draw(int x, int y, int rectangleWidth, int rectangleHeight, SKColor color)
            {
                paint.Color = color;
                canvas.DrawRect(x, y, rectangleWidth, rectangleHeight, paint);
            }
        }
    }
}
