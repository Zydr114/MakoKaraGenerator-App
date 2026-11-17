using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>注音布局样式；空值保留歌词注音本身的数值。</summary>
public sealed record RubyLayoutSpec
{
    /// <summary>注音相对于正文字号的比例；空值使用注音本身的比例。</summary>
    public double? RelativeScale { get; init; }
    /// <summary>注音与正文的画布像素间距；空值使用注音本身的间距。</summary>
    public double? GapPx { get; init; }
    /// <summary>数值为有限的正比例和非负间距。</summary>
    public bool IsValid => (RelativeScale is null || double.IsFinite(RelativeScale.Value) && RelativeScale.Value > 0d && RelativeScale.Value <= float.MaxValue)
        && (GapPx is null || double.IsFinite(GapPx.Value) && GapPx.Value >= 0d && GapPx.Value <= float.MaxValue);
    /// <summary>检查比例转换为实际字体字号时不会上溢或下溢。</summary>
    /// <param name="fontSizePx">正文字号。</param>
    /// <returns>可安全转换为布局字号时为真。</returns>
    public bool IsValidForFont(float fontSizePx)
    {
        if (!IsValid || !float.IsFinite(fontSizePx) || fontSizePx <= 0f) return false;
        if (RelativeScale is not { } scale) return true;
        float factor = (float)scale;
        float size = fontSizePx * factor;
        return float.IsFinite(factor) && factor > 0f && float.IsFinite(size) && size > 0f;
    }
    /// <summary>取得注音使用的最终比例。</summary>
    public double ResolveScale(RubyAnnotation annotation) => RelativeScale ?? annotation.RelativeScale;
    /// <summary>取得注音使用的最终间距。</summary>
    public double ResolveGap(RubyAnnotation annotation) => GapPx ?? annotation.GapPx;
}

/// <summary>注音布局的部分覆盖；显式空值表示回退注音本身，未设置表示继承下层样式。</summary>
public sealed record RubyLayoutSpecOverride
{
    /// <summary>比例覆盖。</summary>
    public OptionalValue<double?> RelativeScale { get; init; }
    /// <summary>间距覆盖。</summary>
    public OptionalValue<double?> GapPx { get; init; }
    /// <summary>是否没有任何覆盖。</summary>
    public bool IsEmpty => !RelativeScale.IsSet && !GapPx.IsSet;
}
