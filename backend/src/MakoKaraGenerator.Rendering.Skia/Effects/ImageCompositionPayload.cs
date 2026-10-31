using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>可传递的独立前景与下层擦除量；在下层 B 上应用 Color + B × (1 − Erasure)。</summary>
/// <remarks>拥有两个独立表面，不含已经合成的背景，不借用原图产物；不属于脚本公开 SDK。</remarks>
public sealed class ImageCompositionPayload : IDisposable
{
    private readonly SkiaGraphSurface _color, _erasure;
    private readonly Action? _releaseDeviceLease;
    private bool _disposed;
    internal ImageCompositionPayload(SkiaGraphSurface color, SkiaGraphSurface erasure, ImageGraphFrameContext context, Action? releaseDeviceLease = null)
    { _color = color; _erasure = erasure; Context = context; _releaseDeviceLease = releaseDeviceLease; }
    internal ImageGraphFrameContext Context { get; }
    /// <summary>已经应用共同透明度的 sRGB 预乘前景，仍未与真实下层合成。</summary>
    public SkiaGraphSurface Color { get { ObjectDisposedException.ThrowIf(_disposed, this); return _color; } }
    /// <summary>已经应用共同透明度的 raw alpha 擦除量；可能大于前景 alpha。</summary>
    public SkiaGraphSurface Erasure { get { ObjectDisposedException.ThrowIf(_disposed, this); return _erasure; } }
    /// <summary>幂等释放拥有的前景和擦除表面，不释放原图输入或结果。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _color.Dispose(); }
        finally
        {
            try { _erasure.Dispose(); }
            finally { _releaseDeviceLease?.Invoke(); }
        }
    }
}
