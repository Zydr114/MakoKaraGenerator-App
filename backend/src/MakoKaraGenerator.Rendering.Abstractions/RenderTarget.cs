using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>
/// 一帧像素的存放格式。
/// </summary>
/// <remarks>
/// <para>
/// 首期只实现一种格式：BGRA 8888、<b>已预乘</b> alpha。选它的原因是两端都不用转换：
/// </para>
/// <list type="bullet">
///   <item><description>预览：Avalonia 的 <c>WriteableBitmap</c> 直接支持
///         <c>PixelFormat.Bgra8888</c> + <c>AlphaFormat.Premul</c>；</description></item>
///   <item><description>导出：FFmpeg 的 <c>bgra</c> / <c>argb</c> 原始帧输入可直接使用。</description></item>
/// </list>
/// <para>
/// 预乘意味着 <c>B/G/R</c> 已经乘过 <c>A/255</c>，因此 <see cref="RenderFrame.TryGetPixel" />
/// 取到的是<b>直通</b>颜色，二者不可直接比较。
/// </para>
/// </remarks>
public enum PixelFormat
{
    /// <summary>BGRA 8888，预乘 alpha。</summary>
    Bgra8888Premultiplied,
}

/// <summary>
/// 渲染目标的像素尺寸。
/// </summary>
/// <remarks>
/// <para>
/// 预览与导出用同一个管线、同一套布局规则，差别只在目标尺寸与质量档位
/// （docs/development.md §10.3）。管线按
/// <c>目标像素 / 合成画布像素</c> 得到缩放系数，并把几何与字号<b>整体</b>缩放；
/// 因此草稿预览只是"同一张画面的缩略图"，不是另一套排版。
/// </para>
/// <para>
/// 本类型是纯像素尺寸，不含 DPI 或窗口概念——那是 UI 的事，管线不应当知道。
/// </para>
/// </remarks>
public sealed record RenderTarget
{
    /// <summary>目标宽度（像素），必须为正。</summary>
    public required int PixelWidth { get; init; }

    /// <summary>目标高度（像素），必须为正。</summary>
    public required int PixelHeight { get; init; }

    /// <summary>按合成设置的画布尺寸创建目标。</summary>
    /// <param name="composition">合成设置。</param>
    /// <returns>与画布等大的渲染目标。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="composition"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">画布尺寸非正时抛出。</exception>
    public static RenderTarget FromComposition(Composition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        if (composition.CanvasWidth <= 0 || composition.CanvasHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(composition),
                composition,
                "画布尺寸必须为正，渲染目标无法建立。");
        }

        return new RenderTarget { PixelWidth = composition.CanvasWidth, PixelHeight = composition.CanvasHeight };
    }

    /// <summary>
    /// 等比缩放到不超过给定尺寸，用于预览。
    /// </summary>
    /// <param name="maxWidth">最大宽度（像素），必须为正。</param>
    /// <param name="maxHeight">最大高度（像素），必须为正。</param>
    /// <returns>等比缩小后的目标；本来就没超过时返回自身。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数非正时抛出。</exception>
    /// <remarks>
    /// 只缩不放：预览窗口比画布大时不应把画面放大，否则等于凭空增加开销。
    /// </remarks>
    public RenderTarget FitWithin(int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);

        double scale = Math.Min((double)maxWidth / PixelWidth, (double)maxHeight / PixelHeight);

        if (scale >= 1d)
        {
            return this;
        }

        return new RenderTarget
        {
            PixelWidth = Math.Max(1, (int)Math.Round(PixelWidth * scale)),
            PixelHeight = Math.Max(1, (int)Math.Round(PixelHeight * scale)),
        };
    }

    /// <summary>相对一个合成画布的横向缩放系数。</summary>
    /// <param name="composition">合成设置。</param>
    /// <returns>目标宽度除以画布宽度。</returns>
    public double ScaleXOf(Composition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return composition.CanvasWidth <= 0 ? 1d : (double)PixelWidth / composition.CanvasWidth;
    }

    /// <summary>相对一个合成画布的纵向缩放系数。</summary>
    /// <param name="composition">合成设置。</param>
    /// <returns>目标高度除以画布高度。</returns>
    public double ScaleYOf(Composition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return composition.CanvasHeight <= 0 ? 1d : (double)PixelHeight / composition.CanvasHeight;
    }
}
