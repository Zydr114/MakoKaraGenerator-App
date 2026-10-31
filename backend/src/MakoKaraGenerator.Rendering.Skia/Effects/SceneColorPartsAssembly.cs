using System.Collections.Immutable;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>显式顺序重组的彩色部件；拥有独立颜色图，不拥有输入部件，共同 alpha 尚未应用。</summary>
public sealed class SceneColorPartsAssembly : IDisposable
{
    private readonly SkiaGraphSurface _surface;
    private bool _disposed;
    internal SceneColorPartsAssembly(SceneCaptureTarget target, ImmutableArray<SceneColorPart> parts, SkiaGraphSurface surface,
        long timeUs, double commonOpacity, byte opacityByte)
    { Target = target; Parts = parts; _surface = surface; TimeUs = timeUs; CommonOpacity = commonOpacity; OpacityByte = opacityByte; }
    /// <summary>同一真实目标。</summary>
    public SceneCaptureTarget Target { get; }
    /// <summary>宿主显式指定的绘制顺序，前项在下方；不提供隐藏默认。</summary>
    public ImmutableArray<SceneColorPart> Parts { get; }
    /// <summary>独立产物，可在输入部件释放后继续作为 TargetColor。</summary>
    public SkiaGraphSurface Surface { get { ObjectDisposedException.ThrowIf(_disposed, this); return _surface; } }
    /// <summary>固定捕获时刻。</summary>
    public long TimeUs { get; }
    /// <summary>尚未应用的原共同透明度。</summary>
    public double CommonOpacity { get; }
    /// <summary>与生产共同图层相同的量化 alpha。</summary>
    public byte OpacityByte { get; }
    /// <summary>在最终组合上使用的实际透明度。</summary>
    public double QuantizedOpacity => OpacityByte / 255d;
    /// <summary>幂等释放独立产物，不夺取输入。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _surface.Dispose(); }
}
