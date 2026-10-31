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

/// <summary>闭合的规范静态样式成员；不使用颜色兼容访问外观。</summary>
public enum StyleEditProperty
{
    /// <summary>字体族。</summary>
    TypographyFontFamilyName,
    /// <summary>有序字体回退链。</summary>
    TypographyFallbackFamilyNames,
    /// <summary>字号。</summary>
    TypographyFontSizePx,
    /// <summary>字重。</summary>
    TypographyFontWeight,
    /// <summary>粗体。</summary>
    TypographyIsBold,
    /// <summary>斜体。</summary>
    TypographyIsItalic,
    /// <summary>字距。</summary>
    TypographyLetterSpacingPx,
    /// <summary>未唱材质。</summary>
    UnsungMaterial,
    /// <summary>已唱材质。</summary>
    SungMaterial,
    /// <summary>完整描边或关闭。</summary>
    Stroke,
    /// <summary>描边材质。</summary>
    StrokeMaterial,
    /// <summary>描边宽度。</summary>
    StrokeWidthPx,
    /// <summary>描边连接。</summary>
    StrokeJoin,
    /// <summary>描边位置。</summary>
    StrokePosition,
    /// <summary>完整阴影或关闭。</summary>
    Shadow,
    /// <summary>阴影颜色。</summary>
    ShadowColor,
    /// <summary>阴影透明度。</summary>
    ShadowOpacity,
    /// <summary>阴影角度。</summary>
    ShadowAngleDegrees,
    /// <summary>阴影距离。</summary>
    ShadowDistancePx,
    /// <summary>阴影模糊半径。</summary>
    ShadowBlurRadiusPx,
    /// <summary>纵向中心比例。</summary>
    VerticalCenterRatio,
    /// <summary>水平对齐。</summary>
    HorizontalAlignment,
    /// <summary>整体透明度。</summary>
    Opacity,
    /// <summary>整体缩放。</summary>
    Scale,
}

/// <summary>只携带对应成员真实类型的样式意图。</summary>
/// <param name="Property">规范成员。</param>
public abstract record StyleEditChange(StyleEditProperty Property)
{
    /// <summary>设置 TypographyFontFamilyName；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record FontFamilyName(string? Value) : StyleEditChange(StyleEditProperty.TypographyFontFamilyName);
    /// <summary>设置 TypographyFallbackFamilyNames；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record FallbackFamilyNames(ImmutableArray<string> Value) : StyleEditChange(StyleEditProperty.TypographyFallbackFamilyNames);
    /// <summary>设置 TypographyFontSizePx；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record FontSize(float Value) : StyleEditChange(StyleEditProperty.TypographyFontSizePx);
    /// <summary>设置 TypographyFontWeight；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record FontWeight(int Value) : StyleEditChange(StyleEditProperty.TypographyFontWeight);
    /// <summary>设置 TypographyIsBold；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record IsBold(bool Value) : StyleEditChange(StyleEditProperty.TypographyIsBold);
    /// <summary>设置 TypographyIsItalic；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record IsItalic(bool Value) : StyleEditChange(StyleEditProperty.TypographyIsItalic);
    /// <summary>设置 TypographyLetterSpacingPx；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record LetterSpacing(float Value) : StyleEditChange(StyleEditProperty.TypographyLetterSpacingPx);
    /// <summary>设置 UnsungMaterial；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record UnsungMaterial(MaterialSpec Value) : StyleEditChange(StyleEditProperty.UnsungMaterial);
    /// <summary>设置 SungMaterial；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record SungMaterial(MaterialSpec Value) : StyleEditChange(StyleEditProperty.SungMaterial);
    /// <summary>设置 Stroke；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record Stroke(StrokeSpec? Value) : StyleEditChange(StyleEditProperty.Stroke);
    /// <summary>设置 StrokeMaterial；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record StrokeMaterial(MaterialSpec Value) : StyleEditChange(StyleEditProperty.StrokeMaterial);
    /// <summary>设置 StrokeWidthPx；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record StrokeWidth(float Value) : StyleEditChange(StyleEditProperty.StrokeWidthPx);
    /// <summary>设置 StrokeJoin；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record StrokeJoin(global::MakoKaraGenerator.Core.Projects.StrokeJoin Value) : StyleEditChange(StyleEditProperty.StrokeJoin);
    /// <summary>设置 StrokePosition；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record StrokePosition(global::MakoKaraGenerator.Core.Projects.StrokePosition Value) : StyleEditChange(StyleEditProperty.StrokePosition);
    /// <summary>设置 Shadow；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record Shadow(ShadowSpec? Value) : StyleEditChange(StyleEditProperty.Shadow);
    /// <summary>设置 ShadowColor；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record ShadowColor(RgbaColor Value) : StyleEditChange(StyleEditProperty.ShadowColor);
    /// <summary>设置 ShadowOpacity；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record ShadowOpacity(float Value) : StyleEditChange(StyleEditProperty.ShadowOpacity);
    /// <summary>设置 ShadowAngleDegrees；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record ShadowAngle(double Value) : StyleEditChange(StyleEditProperty.ShadowAngleDegrees);
    /// <summary>设置 ShadowDistancePx；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record ShadowDistance(float Value) : StyleEditChange(StyleEditProperty.ShadowDistancePx);
    /// <summary>设置 ShadowBlurRadiusPx；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record ShadowBlur(float Value) : StyleEditChange(StyleEditProperty.ShadowBlurRadiusPx);
    /// <summary>设置 VerticalCenterRatio；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record VerticalCenter(double Value) : StyleEditChange(StyleEditProperty.VerticalCenterRatio);
    /// <summary>设置 HorizontalAlignment；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record HorizontalAlignment(LyricHorizontalAlignment Value) : StyleEditChange(StyleEditProperty.HorizontalAlignment);
    /// <summary>设置 Opacity；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record Opacity(float Value) : StyleEditChange(StyleEditProperty.Opacity);
    /// <summary>设置 Scale；空值仅具有该成员的明确语义。</summary>
    /// <param name="Value">成员新值。</param>
    public sealed record Scale(float Value) : StyleEditChange(StyleEditProperty.Scale);
}
