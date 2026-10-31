namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 行进入动画：三个通道从"起始值"过渡到行自己的样式。
/// </summary>
/// <remarks>
/// <para>
/// 正常显示从行起点起算；前奏准备显示从本次揭示起点起算，起唱不重播。
/// 持续 <see cref="DurationUs"/> 之后行就完全回到自己的样式。
/// 因此它不需要关键帧表：一条动画就是"起点 + 时长 + 曲线"。
/// </para>
/// <para>
/// 三个通道各有含义：<see cref="StartOpacity"/> 是淡入的起点，
/// <see cref="StartScale"/> 是缩放起点（1 表示不缩放），
/// <see cref="StartOffsetPx"/> 是纵向偏移起点（正值代表"从下方进入"，0 表示不位移）。
/// 结束值恒为"行本身的样式"（不透明度 1、缩放 1、偏移 0），因此没有对应的结束字段。
/// </para>
/// <para>
/// 工程级默认放在 <see cref="Project.EnterAnimation"/>，行级覆盖放在
/// <see cref="LyricLinePresentation.EnterAnimation"/>：行级为 <see langword="null"/> 表示继承工程默认。
/// </para>
/// <para>
/// <b>与样式里的同名概念是"相乘"而不是"覆盖"</b>：最终不透明度 = 行样式的
/// <see cref="LyricStyle.Opacity"/> × 本动画的不透明度；最终缩放 = 行样式的
/// <see cref="LyricStyle.Scale"/> × 本动画的缩放。纵向偏移只有动画有（样式用
/// <see cref="LyricStyle.VerticalCenterRatio"/> 定位，不是"偏移量"）。
/// 两者是不同层的东西：样式描述"这一行长什么样"，动画描述"它怎么进入画面"。
/// </para>
/// </remarks>
public sealed record LineAnimation
{
    /// <summary>默认动画：0.4 秒淡入 + 轻微上移，缓出。</summary>
    public static LineAnimation Default { get; } = new();

    /// <summary>时长（微秒）；0 表示立即到位（没有过渡）。</summary>
    public long DurationUs { get; init; } = 400_000;

    /// <summary>缓动方式。</summary>
    public AnimationEasing Easing { get; init; } = AnimationEasing.EaseOut;

    /// <summary>起始不透明度（0 为完全透明，1 为不透明）。</summary>
    public float StartOpacity { get; init; }

    /// <summary>起始缩放（1 为不缩放）。</summary>
    public float StartScale { get; init; } = 1f;

    /// <summary>起始纵向偏移（像素，正值向下）。</summary>
    public float StartOffsetPx { get; init; } = 12f;

    /// <summary>动画参数是否可求值。</summary>
    public bool IsValid => DurationUs is >= 0 and <= 10_000_000 && Enum.IsDefined(Easing)
        && float.IsFinite(StartOpacity) && StartOpacity is >= 0 and <= 1
        && float.IsFinite(StartScale) && StartScale is > 0 and <= 4
        && float.IsFinite(StartOffsetPx) && Math.Abs(StartOffsetPx) <= 10_000;

    /// <summary>这条动画是否实际上什么也不做（不会改变画面）。</summary>
    /// <remarks>界面据此提示"当前设置看不出效果"，而不是让用户对着一个静止的画面猜。</remarks>
    public bool IsNeutral => StartOpacity >= 1f && StartScale == 1f && StartOffsetPx == 0f;
}
