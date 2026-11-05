using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Media;

/// <summary>一个矩形（图片源区域或画布目标区域），单位是像素。</summary>
/// <param name="X">左上角横坐标。</param>
/// <param name="Y">左上角纵坐标。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public readonly record struct BackgroundRect(float X, float Y, float Width, float Height);

/// <summary>
/// 背景图在画布上的落位：把"画哪一块"与"画到哪里"算清楚。
/// </summary>
/// <remarks>
/// <para>
/// 这里是<b>纯几何</b>，不引用任何绘图库（docs/development.md §10.1 的分层要求），
/// 因此三种适配方式都能直接用单元测试钉住，渲染侧只负责把两个矩形交给后端。
/// </para>
/// <para>
/// 三种适配统一成同一段算法：先按比例得到"完整显示时的尺寸"，再只保留画布可见的部分。
/// 于是"超出画布"不是靠裁剪区（clip）实现的，而是把源矩形一起缩小——
/// 覆盖档的裁切因此是精确的，也不会在缩放后的预览里出现半像素接缝。
/// </para>
/// </remarks>
public sealed record BackgroundPlacement(BackgroundRect Source, BackgroundRect Destination)
{
    /// <summary>没有可画的像素（尺寸退化）。</summary>
    public bool IsEmpty => Destination.Width <= 0f || Destination.Height <= 0f;

    /// <summary>计算背景图在画布上的落位。</summary>
    /// <param name="imageWidth">图片像素宽度，必须为正。</param>
    /// <param name="imageHeight">图片像素高度，必须为正。</param>
    /// <param name="canvasWidth">画布宽度，必须为正。</param>
    /// <param name="canvasHeight">画布高度，必须为正。</param>
    /// <param name="fit">适配方式。</param>
    /// <returns>源矩形与目标矩形。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一尺寸非正时抛出。</exception>
    public static BackgroundPlacement Compute(
        int imageWidth,
        int imageHeight,
        int canvasWidth,
        int canvasHeight,
        BackgroundFit fit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasHeight);

        double scale = fit switch
        {
            BackgroundFit.Contain => Math.Min(canvasWidth / (double)imageWidth, canvasHeight / (double)imageHeight),
            BackgroundFit.Cover => Math.Max(canvasWidth / (double)imageWidth, canvasHeight / (double)imageHeight),
            BackgroundFit.Original => 1d,
            _ => Math.Min(canvasWidth / (double)imageWidth, canvasHeight / (double)imageHeight),
        };

        double displayedWidth = imageWidth * scale;
        double displayedHeight = imageHeight * scale;
        double left = (canvasWidth - displayedWidth) / 2d;
        double top = (canvasHeight - displayedHeight) / 2d;

        // 画布之外的部分不画：目标矩形缩到可见范围，源矩形同步缩到对应的那一块。
        double visibleWidth = Math.Min(displayedWidth, canvasWidth);
        double visibleHeight = Math.Min(displayedHeight, canvasHeight);

        return new BackgroundPlacement(
            new BackgroundRect(
                (float)((displayedWidth - visibleWidth) / 2d / scale),
                (float)((displayedHeight - visibleHeight) / 2d / scale),
                (float)(visibleWidth / scale),
                (float)(visibleHeight / scale)),
            new BackgroundRect(
                (float)(left + ((displayedWidth - visibleWidth) / 2d)),
                (float)(top + ((displayedHeight - visibleHeight) / 2d)),
                (float)visibleWidth,
                (float)visibleHeight));
    }

    /// <summary>Computes the same centered fit from positive finite display metrics without integer SAR rounding.</summary>
    /// <param name="imageWidth">The positive finite display width.</param>
    /// <param name="imageHeight">The positive finite display height.</param>
    /// <param name="canvasWidth">The positive canvas width.</param>
    /// <param name="canvasHeight">The positive canvas height.</param>
    /// <param name="fit">The existing project fit policy.</param>
    /// <returns>Double precision source display and destination canvas rectangles.</returns>
    public static BackgroundDisplayPlacement Compute(double imageWidth, double imageHeight,
        int canvasWidth, int canvasHeight, BackgroundFit fit)
    {
        if (!double.IsFinite(imageWidth) || imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (!double.IsFinite(imageHeight) || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageHeight));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasHeight);

        double scale = fit switch
        {
            BackgroundFit.Contain => Math.Min(canvasWidth / (double)imageWidth, canvasHeight / (double)imageHeight),
            BackgroundFit.Cover => Math.Max(canvasWidth / (double)imageWidth, canvasHeight / (double)imageHeight),
            BackgroundFit.Original => 1d,
            _ => throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Background fit is not supported by display geometry."),
        };

        double displayedWidth = imageWidth * scale;
        double displayedHeight = imageHeight * scale;
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(displayedWidth) || displayedWidth <= 0 ||
            !double.IsFinite(displayedHeight) || displayedHeight <= 0)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Display metrics cannot produce a reliable finite placement.");
        }

        double left = (canvasWidth - displayedWidth) / 2d;
        double top = (canvasHeight - displayedHeight) / 2d;

        // 画布之外的部分不画：目标矩形缩到可见范围，源矩形同步缩到对应的那一块。
        double visibleWidth = Math.Min(displayedWidth, canvasWidth);
        double visibleHeight = Math.Min(displayedHeight, canvasHeight);

        return new BackgroundDisplayPlacement(
            new BackgroundDisplayRect(
                ((displayedWidth - visibleWidth) / 2d / scale),
                ((displayedHeight - visibleHeight) / 2d / scale),
                (visibleWidth / scale),
                (visibleHeight / scale)),
            new BackgroundDisplayRect(
                (left + ((displayedWidth - visibleWidth) / 2d)),
                (top + ((displayedHeight - visibleHeight) / 2d)),
                visibleWidth,
                visibleHeight));
    }
}


/// <summary>A source display or destination canvas rectangle without binary32 rounding.</summary>
/// <param name="X">The left coordinate.</param>
/// <param name="Y">The top coordinate.</param>
/// <param name="Width">The positive extent along the horizontal axis.</param>
/// <param name="Height">The positive extent along the vertical axis.</param>
public readonly record struct BackgroundDisplayRect(double X, double Y, double Width, double Height);

/// <summary>Shared fit math result retaining positive finite display metrics.</summary>
/// <param name="Source">The visible rectangle in source display units.</param>
/// <param name="Destination">The corresponding canvas rectangle.</param>
public sealed record BackgroundDisplayPlacement(BackgroundDisplayRect Source, BackgroundDisplayRect Destination);
