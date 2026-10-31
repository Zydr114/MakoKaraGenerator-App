namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 背景图与画布比例不一致时的适配方式。
/// </summary>
/// <remarks>
/// docs/development.md §12.1 要求"背景视频不足或超出音频时长时必须有明确的策略"；
/// 图片没有时长问题，但同样会遇到"比例对不上"，这里就是它的明确策略。
/// 背景色（<see cref="Composition.BackgroundColor" />）始终先铺满画布，
/// 因此留白处就是背景色，而不是黑板或透明。
/// </remarks>
public enum BackgroundFit
{
    /// <summary>保持比例完整放进画布，多出来的地方留背景色（letterbox）。默认。</summary>
    Contain = 0,

    /// <summary>保持比例铺满画布，超出画布的部分裁掉。</summary>
    Cover = 1,

    /// <summary>按原始像素尺寸居中放置：画布更大时留背景色，更小时裁掉溢出部分。</summary>
    Original = 2,
}
