namespace MakoKaraGenerator.Core.Projects;

/// <summary>自动分支布局可使用的归一化安全区；不限制显式行位置。</summary>
/// <param name="Left">左侧边界。</param>
/// <param name="Top">上侧边界。</param>
/// <param name="Right">右侧边界。</param>
/// <param name="Bottom">下侧边界。</param>
public readonly record struct NormalizedSafeArea(double Left, double Top, double Right, double Bottom)
{
    /// <summary>完整画布。</summary>
    public static NormalizedSafeArea Full { get; } = new(0, 0, 1, 1);
    /// <summary>翻译默认使用画布底部。</summary>
    public static NormalizedSafeArea Translation { get; } = new(.05, .76, .95, .96);
    /// <summary>边界是否有限、有序且位于画布中。</summary>
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right)
        && double.IsFinite(Bottom) && Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1
        && Right > Left && Bottom > Top;
}
