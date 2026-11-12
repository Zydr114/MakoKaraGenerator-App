namespace MakoKaraGenerator.Core.Projects;

/// <summary>扫字线高亮的混合方式；P3 第 6 步出样张后由用户定稿取值（设计 §10.3）。</summary>
public enum WipeHighlightBlend
{
    /// <summary>普通叠加。</summary>
    Normal = 0,

    /// <summary>滤色。</summary>
    Screen = 1,

    /// <summary>相加。</summary>
    Add = 2,
}

/// <summary>扫字样式：过渡带羽化与扫字线高亮（P3 设计 §8）。</summary>
/// <remarks>两态共享，放在行样式上，可由预设、分支、行与元素覆盖设置。文字标记不使用。</remarks>
public sealed record WipeSpec
{
    /// <summary>羽化宽度比例的上限：过渡带至多两倍字号。</summary>
    public const double MaxFeatherRatio = 2d;

    /// <summary>无羽化、无高亮的硬切换。</summary>
    public static WipeSpec Default { get; } = new();

    /// <summary>过渡带宽度与字号之比；0 为硬切换。</summary>
    public double FeatherRatio { get; init; }

    /// <summary>扫字线高亮；为空表示关闭。</summary>
    public WipeHighlightSpec? Highlight { get; init; }

    /// <summary>参数是否有效。</summary>
    public bool IsValid => double.IsFinite(FeatherRatio) && FeatherRatio is >= 0d and <= MaxFeatherRatio
        && (Highlight is null || Highlight.IsValid);
}

/// <summary>扫字线高亮：以演唱位置为中心的峰。</summary>
public sealed record WipeHighlightSpec
{
    /// <summary>高亮宽度比例的上限。</summary>
    public const double MaxWidthRatio = 2d;

    /// <summary>高亮颜色。</summary>
    public RgbaColor Color { get; init; } = RgbaColor.White;

    /// <summary>高亮宽度与字号之比，须为正。</summary>
    public double WidthRatio { get; init; } = .25d;

    /// <summary>高亮强度，0～1。</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>混合方式。</summary>
    public WipeHighlightBlend Blend { get; init; } = WipeHighlightBlend.Normal;

    /// <summary>参数是否有效。</summary>
    public bool IsValid => double.IsFinite(WidthRatio) && WidthRatio is > 0d and <= MaxWidthRatio
        && float.IsFinite(Opacity) && Opacity is >= 0f and <= 1f && Enum.IsDefined(Blend);
}
