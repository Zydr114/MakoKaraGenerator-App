using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>逐字节固定背景像素；返回的不可变位图由本帧作用域独立拥有。</summary>
    /// <remarks>
    /// 保留来源的颜色类型、alpha 类型与色彩空间，不经过 shader 绘制或预乘往返转换。
    /// 视频来源是 straight-alpha BGRA，因此半透明和全透明像素也保留解码器的原始字节。
    /// 只复制每行有效像素，不读取源行尾 padding；调用期间来源不得被其他线程修改或释放。
    /// </remarks>
    internal static unsafe SKBitmap CreateBackgroundSnapshot(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(source.Handle == IntPtr.Zero, source);
        // Info 的色彩空间 wrapper 可与调用方共享；这里不能把它当独占租约 Dispose。
        SKImageInfo info = source.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.BytesPerPixel <= 0)
            throw new InvalidOperationException("无法固定没有有效像素的本帧背景位图。");

        int pixelRowBytes = checked(info.Width * info.BytesPerPixel);
        int sourceRowBytes = source.RowBytes;
        IntPtr sourcePixels = source.GetPixels(out IntPtr sourceLength);
        long sourceRequiredBytes = checked((long)(info.Height - 1) * sourceRowBytes + pixelRowBytes);
        if (sourceRowBytes < pixelRowBytes || sourcePixels == IntPtr.Zero || sourceLength.ToInt64() < sourceRequiredBytes)
            throw new InvalidOperationException("无法读取本帧背景位图的完整像素行。");

        var snapshot = new SKBitmap();
        try
        {
            if (!snapshot.TryAllocPixels(info))
                throw new InvalidOperationException("无法分配本帧背景位图快照。");

            int targetRowBytes = snapshot.RowBytes;
            IntPtr targetPixels = snapshot.GetPixels(out IntPtr targetLength);
            long targetRequiredBytes = checked((long)(info.Height - 1) * targetRowBytes + pixelRowBytes);
            if (targetRowBytes < pixelRowBytes || targetPixels == IntPtr.Zero || targetLength.ToInt64() < targetRequiredBytes)
                throw new InvalidOperationException("无法写入本帧背景位图的完整像素行。");

            if (sourceRowBytes == pixelRowBytes && targetRowBytes == pixelRowBytes)
            {
                Buffer.MemoryCopy((void*)sourcePixels, (void*)targetPixels, targetLength.ToInt64(), targetRequiredBytes);
            }
            else
            {
                for (int y = 0; y < info.Height; y++)
                    Buffer.MemoryCopy((byte*)sourcePixels + (long)y * sourceRowBytes,
                        (byte*)targetPixels + (long)y * targetRowBytes, pixelRowBytes, pixelRowBytes);
            }

            snapshot.SetImmutable();
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
        finally { GC.KeepAlive(source); }
    }
}
