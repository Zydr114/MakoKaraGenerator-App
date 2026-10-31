namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>相同真实字形／逐字裁切／矩阵生成的 raw 区域和包基础外观；不含共同 alpha。</summary>
public sealed class SceneMaterialRegionCapture : IDisposable
{
    private bool _disposed;
    internal SceneMaterialRegionCapture(SkiaGraphSurface region, SkiaGraphSurface fallback) { Region = region; TargetColor = fallback; }
    /// <summary>纯几何覆盖，不读取原填充透明度。</summary>
    public SkiaGraphSurface Region { get; }
    /// <summary>包基础外观在本区域的真实预乘图像，没有旧填充或自反馈。</summary>
    public SkiaGraphSurface TargetColor { get; }
    /// <summary>幂等释放两个独立表面。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; try { TargetColor.Dispose(); } finally { Region.Dispose(); } }
}
