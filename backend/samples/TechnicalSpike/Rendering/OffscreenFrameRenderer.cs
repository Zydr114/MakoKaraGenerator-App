using SkiaSharp;

namespace MakoKaraGenerator.Spike.Rendering;

/// <summary>
/// 在没有任何 UI 控件参与的情况下把一帧渲染成位图（SPIKE-04）。
/// </summary>
/// <remarks>
/// <para>
/// 这是 docs/roadmap.md §5 阶段出口要求的关键一条：
/// "一帧渲染可以脱离 UI 控件执行"。导出成片时走的就是这条路径。
/// </para>
/// <para>
/// 因为渲染输入是 <see cref="KaraokeFrame" /> 这样的不可变值，
/// 预览与离屏渲染共用同一个 <see cref="KaraokeFrameRenderer" />，
/// 从而保证"预览和最终输出使用同一套渲染规则"（docs/development.md §19 设计底线）。
/// </para>
/// </remarks>
public static class OffscreenFrameRenderer
{
    /// <summary>把一帧渲染成 RGBA 位图。</summary>
    /// <param name="frame">帧描述，尺寸取自其中的宽高。</param>
    /// <returns>新创建的位图，调用方负责释放。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> 为 <see langword="null"/> 时抛出。</exception>
    public static SKBitmap Render(KaraokeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        int width = (int)MathF.Max(1f, MathF.Round(frame.Width));
        int height = (int)MathF.Max(1f, MathF.Round(frame.Height));

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        SKBitmap bitmap = new(info);

        using (SKCanvas canvas = new(bitmap))
        {
            KaraokeFrameRenderer.Render(canvas, frame);
        }

        return bitmap;
    }

    /// <summary>把一帧渲染并保存为 PNG。</summary>
    /// <param name="frame">帧描述。</param>
    /// <param name="path">输出的 PNG 路径。</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空白时抛出。</exception>
    /// <exception cref="IOException">写入或编码失败时抛出。</exception>
    public static void SavePng(KaraokeFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SKBitmap bitmap = Render(frame);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);

        if (data is null || data.Size == 0)
        {
            throw new IOException($"PNG 编码失败：{path}");
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }
}
