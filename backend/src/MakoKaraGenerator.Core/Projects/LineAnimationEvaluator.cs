namespace MakoKaraGenerator.Core.Projects;

/// <summary>某一时刻动画算出的三个通道值。</summary>
/// <param name="Opacity">不透明度，1 为不透明。</param>
/// <param name="Scale">缩放，1 为不缩放。</param>
/// <param name="VerticalOffsetPx">纵向偏移（像素，正值向下）。</param>
public readonly record struct LineAnimationValues(float Opacity, float Scale, float VerticalOffsetPx)
{
    /// <summary>什么也不做的取值（动画结束后的状态）。</summary>
    public static LineAnimationValues Neutral { get; } = new(1f, 1f, 0f);
}

/// <summary>
/// 把"行进入动画 + 时刻"求值成三个通道（纯函数）。
/// </summary>
/// <remarks>
/// <para>
/// 这是动画语义<b>唯一</b>的实现处：预览与导出都走它（渲染器不自己算），
/// 因此"预览里看到的和成片里的一样"由结构保证。
/// </para>
/// <para>
/// 边界：行开始之前取<b>起始值</b>（还没开始演，停在第一帧），
/// 时长走完或时长不大于 0 时取结束值（回到行自己的样式）。
/// </para>
/// </remarks>
public static class LineAnimationEvaluator
{
    /// <summary>求出某个行内时刻的通道值。</summary>
    /// <param name="animation">动画；为 <see langword="null"/> 时返回 <see langword="null"/>（不动画）。</param>
    /// <param name="lineStartUs">行起点（微秒）。</param>
    /// <param name="timeUs">当前时刻（微秒）。</param>
    /// <returns>通道值；没有动画时为 <see langword="null"/>。</returns>
    public static LineAnimationValues? Evaluate(LineAnimation? animation, long lineStartUs, long timeUs) =>
        animation is null ? null : Evaluate(animation, timeUs - lineStartUs);

    /// <summary>求出某个"行内已过时长"处的通道值。</summary>
    /// <param name="animation">动画。</param>
    /// <param name="elapsedUs">从行起点起算的已过时长（微秒）；小于 0 表示还没到起点。</param>
    /// <returns>通道值。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="animation"/> 为 <see langword="null"/> 时抛出。</exception>
    public static LineAnimationValues Evaluate(LineAnimation animation, long elapsedUs)
    {
        ArgumentNullException.ThrowIfNull(animation);

        if (animation.DurationUs <= 0L || elapsedUs >= animation.DurationUs)
        {
            return LineAnimationValues.Neutral;
        }

        if (elapsedUs <= 0L)
        {
            return Start(animation);
        }

        double progress = (double)elapsedUs / animation.DurationUs;
        double eased = AnimationEasingFunctions.Evaluate(animation.Easing, progress);

        return new LineAnimationValues(
            Lerp(animation.StartOpacity, 1f, eased),
            Lerp(animation.StartScale, 1f, eased),
            Lerp(animation.StartOffsetPx, 0f, eased));
    }

    private static LineAnimationValues Start(LineAnimation animation) =>
        new(animation.StartOpacity, animation.StartScale, animation.StartOffsetPx);

    private static float Lerp(float from, float to, double amount) =>
        (float)(from + ((to - from) * amount));
}
