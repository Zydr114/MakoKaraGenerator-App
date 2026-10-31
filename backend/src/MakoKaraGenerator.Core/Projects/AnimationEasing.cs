namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 进入动画的缓动函数（roadmap `STYLE-04` 的最小集）。
/// </summary>
/// <remarks>
/// 只提供四种最常见的：线性、缓入、缓出、缓入出。曲线的具体公式在
/// <see cref="AnimationEasingFunctions" />，并用测试固定下来——
/// 缓动一改，所有已定稿工程的观感都会变，因此它属于"改之前要想清楚"的东西。
/// </remarks>
public enum AnimationEasing
{
    /// <summary>匀速。</summary>
    Linear = 0,

    /// <summary>慢起快收（二次曲线）。</summary>
    EaseIn = 1,

    /// <summary>快起慢收（二次曲线）。默认。</summary>
    EaseOut = 2,

    /// <summary>两头慢、中间快（二次曲线）。</summary>
    EaseInOut = 3,
}

/// <summary>缓动函数的求值（纯函数）。</summary>
public static class AnimationEasingFunctions
{
    /// <summary>求缓动后的进度。</summary>
    /// <param name="easing">缓动方式。</param>
    /// <param name="progress">线性进度；小于 0 或大于 1 会被夹到区间内。</param>
    /// <returns>缓动后的进度，落在 <c>[0, 1]</c> 内。</returns>
    public static double Evaluate(AnimationEasing easing, double progress)
    {
        double t = Math.Clamp(progress, 0d, 1d);

        return easing switch
        {
            AnimationEasing.Linear => t,
            AnimationEasing.EaseIn => t * t,
            AnimationEasing.EaseOut => 1d - ((1d - t) * (1d - t)),
            AnimationEasing.EaseInOut => t < 0.5d
                ? 2d * t * t
                : 1d - (2d * (1d - t) * (1d - t)),
            _ => t,
        };
    }
}
