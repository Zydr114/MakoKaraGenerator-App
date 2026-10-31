namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>调用者拥有的真实彩色部件及尚未应用的共同合成透明度。</summary>
public sealed class SceneColorPartResult : IDisposable
{
    private readonly SkiaGraphSurface _surface;
    private bool _disposed;
    internal SceneColorPartResult(RenderRequest request, SceneCaptureTarget target, SceneColorPart part, SkiaGraphSurface surface, double commonOpacity, byte opacityByte)
    { Request = request; Target = target; Part = part; _surface = surface; CommonOpacity = commonOpacity; OpacityByte = opacityByte; }
    internal RenderRequest Request { get; }
    /// <summary>本帧真实目标，不包含组后代。</summary>
    public SceneCaptureTarget Target { get; }
    /// <summary>实际彩色部件类别。</summary>
    public SceneColorPart Part { get; }
    /// <summary>真实捕获时刻，整数微秒。</summary>
    public long TimeUs => Request.TimeUs;
    /// <summary>拥有的真实 sRGB 表面，已释放时失败。</summary>
    public SkiaGraphSurface Surface { get { ObjectDisposedException.ThrowIf(_disposed, this); return _surface; } }
    /// <summary>尚未应用的原共同透明度；行入场或标记整体透明度。</summary>
    public double CommonOpacity { get; }
    /// <summary>与生产 SaveLayer 相同的 8 bit 量化透明度，组合后应用一次。</summary>
    public byte OpacityByte { get; }
    /// <summary>归一化的实际生产透明度。</summary>
    public double QuantizedOpacity => OpacityByte / 255d;
    /// <summary>幂等释放拥有的部件表面，不夺取外部资源。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _surface.Dispose(); }
}
