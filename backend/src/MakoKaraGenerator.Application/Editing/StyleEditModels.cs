using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>静态样式的持久目标层级。</summary>
public enum StyleEditTargetKind
{
    /// <summary>工程基线。</summary>
    Project,
    /// <summary>来源覆盖。</summary>
    Branch,
    /// <summary>行覆盖。</summary>
    Line,
    /// <summary>元素覆盖。</summary>
    Token,
}

/// <summary>精确稳定身份；各层仅允许其所需的标识字段。</summary>
/// <param name="Kind">目标层级。</param>
/// <param name="BranchId">来源标识。</param>
/// <param name="LineId">来源内的行标识。</param>
/// <param name="TokenId">行内的元素标识。</param>
public sealed record StyleEditTarget(StyleEditTargetKind Kind, string? BranchId = null, string? LineId = null, string? TokenId = null);

/// <summary>有类型的规范样式成员值；<see cref="Property"/> 是 Core 的 43 项属性之一，与 HTTP 属性路径一一对应。</summary>
/// <param name="Property">写入的属性。</param>
public abstract record StyleEditChange(LyricStyleProperty Property)
{
    /// <summary>字体族；null 为未指定。</summary>
    public sealed record FontFamilyName(string? Value) : StyleEditChange(LyricStyleProperty.FontFamilyName);
    /// <summary>有序字体回退链。</summary>
    public sealed record FallbackFamilyNames(ImmutableArray<string> Value) : StyleEditChange(LyricStyleProperty.FallbackFamilyNames);
    /// <summary>字号。</summary>
    public sealed record FontSize(float Value) : StyleEditChange(LyricStyleProperty.FontSizePx);
    /// <summary>字重。</summary>
    public sealed record FontWeight(int Value) : StyleEditChange(LyricStyleProperty.FontWeight);
    /// <summary>粗体。</summary>
    public sealed record IsBold(bool Value) : StyleEditChange(LyricStyleProperty.IsBold);
    /// <summary>斜体。</summary>
    public sealed record IsItalic(bool Value) : StyleEditChange(LyricStyleProperty.IsItalic);
    /// <summary>字距。</summary>
    public sealed record LetterSpacing(float Value) : StyleEditChange(LyricStyleProperty.LetterSpacingPx);
    /// <summary>一态的填充材质；只接受静态纯色或渐变。</summary>
    public sealed record Fill(bool Sung, MaterialSpec Value) : StyleEditChange(Sung ? LyricStyleProperty.SungFill : LyricStyleProperty.UnsungFill);
    /// <summary>未唱描边整表；空表为无描边。</summary>
    public sealed record UnsungStrokes(StrokeLayers Value) : StyleEditChange(LyricStyleProperty.UnsungStrokes);
    /// <summary>已唱描边：跟随未唱，或整表。</summary>
    public sealed record SungStrokes(Followable<StrokeLayers> Value) : StyleEditChange(LyricStyleProperty.SungStrokes);
    /// <summary>完整未唱阴影；null 为关闭。</summary>
    public sealed record UnsungShadow(ShadowSpec? Value) : StyleEditChange(LyricStyleProperty.UnsungShadow);
    /// <summary>完整已唱阴影：跟随、关闭或显式值。</summary>
    public sealed record SungShadow(Followable<ShadowSpec?> Value) : StyleEditChange(LyricStyleProperty.SungShadow);
    /// <summary>完整未唱辉光；null 为关闭。</summary>
    public sealed record UnsungGlow(GlowSpec? Value) : StyleEditChange(LyricStyleProperty.UnsungGlow);
    /// <summary>完整已唱辉光：跟随、关闭或显式值。</summary>
    public sealed record SungGlow(Followable<GlowSpec?> Value) : StyleEditChange(LyricStyleProperty.SungGlow);
    /// <summary>阴影、辉光或扫字线高亮的颜色分量。</summary>
    /// <param name="Leaf">颜色分量属性。</param>
    /// <param name="Value">颜色。</param>
    public sealed record EffectColor(LyricStyleProperty Leaf, RgbaColor Value) : StyleEditChange(Leaf);
    /// <summary>阴影、辉光或扫字线高亮的数值分量（不透明度、角度、距离、模糊、扩散、宽度比例）。</summary>
    /// <param name="Leaf">数值分量属性。</param>
    /// <param name="Value">数值；单精度分量按单精度保存。</param>
    public sealed record EffectNumber(LyricStyleProperty Leaf, double Value) : StyleEditChange(Leaf);
    /// <summary>扫字羽化比例。</summary>
    public sealed record WipeFeather(double Value) : StyleEditChange(LyricStyleProperty.WipeFeatherRatio);
    /// <summary>完整扫字线高亮；null 为关闭。</summary>
    public sealed record WipeHighlight(WipeHighlightSpec? Value) : StyleEditChange(LyricStyleProperty.WipeHighlight);
    /// <summary>扫字线高亮的混合方式。</summary>
    public sealed record HighlightBlend(WipeHighlightBlend Value) : StyleEditChange(LyricStyleProperty.WipeHighlightBlend);
    /// <summary>纵向中心比例。</summary>
    public sealed record VerticalCenter(double Value) : StyleEditChange(LyricStyleProperty.VerticalCenterRatio);
    /// <summary>水平对齐。</summary>
    public sealed record HorizontalAlignment(LyricHorizontalAlignment Value) : StyleEditChange(LyricStyleProperty.HorizontalAlignment);
    /// <summary>整体不透明度。</summary>
    public sealed record Opacity(float Value) : StyleEditChange(LyricStyleProperty.Opacity);
    /// <summary>样式基础缩放。</summary>
    public sealed record Scale(float Value) : StyleEditChange(LyricStyleProperty.Scale);
}
