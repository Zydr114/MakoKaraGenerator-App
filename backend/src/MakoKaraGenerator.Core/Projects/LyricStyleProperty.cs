namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 样式里可以逐属性覆盖、恢复与追踪来源的各项（P3 设计 §10.5，共 43 个）。
/// </summary>
/// <remarks>
/// <para>
/// 用枚举而不是字符串作为来源表的键：属性名拼错时界面会<b>安静地</b>显示错误的来源，
/// 而枚举在编译期就会被拦住。与 HTTP 样式路径、模板属性清单一一对应；JSON 名就是路径（<see cref="LyricStylePropertyExtensions.ToJsonName"/>）。
/// </para>
/// <para>
/// 粒度（D-P3-13）：填充是整个材质；描边是整个列表；阴影、辉光、扫字高亮既有整体项，也有分量项，按态展开。
/// 「整体项」覆盖关闭、跟随与完整替换，分量项只覆盖写了的分量。
/// </para>
/// </remarks>
public enum LyricStyleProperty
{
    /// <summary><c>typography.fontFamilyName</c>。</summary>
    FontFamilyName,
    /// <summary><c>typography.fallbackFamilyNames</c>。</summary>
    FallbackFamilyNames,
    /// <summary><c>typography.fontSizePx</c>。</summary>
    FontSizePx,
    /// <summary><c>typography.fontWeight</c>。</summary>
    FontWeight,
    /// <summary><c>typography.isBold</c>。</summary>
    IsBold,
    /// <summary><c>typography.isItalic</c>。</summary>
    IsItalic,
    /// <summary><c>typography.letterSpacingPx</c>。</summary>
    LetterSpacingPx,
    /// <summary><c>unsung.fill</c>：未唱填充材质。</summary>
    UnsungFill,
    /// <summary><c>sung.fill</c>：已唱填充材质。</summary>
    SungFill,
    /// <summary><c>unsung.strokes</c>：未唱描边整表。</summary>
    UnsungStrokes,
    /// <summary><c>sung.strokes</c>：已唱描边整表或跟随。</summary>
    SungStrokes,
    /// <summary><c>unsung.shadow</c>：未唱阴影整体（含关闭）。</summary>
    UnsungShadow,
    /// <summary><c>unsung.shadow.color</c>。</summary>
    UnsungShadowColor,
    /// <summary><c>unsung.shadow.opacity</c>。</summary>
    UnsungShadowOpacity,
    /// <summary><c>unsung.shadow.angleDegrees</c>。</summary>
    UnsungShadowAngleDegrees,
    /// <summary><c>unsung.shadow.distancePx</c>。</summary>
    UnsungShadowDistancePx,
    /// <summary><c>unsung.shadow.blurRadiusPx</c>。</summary>
    UnsungShadowBlurRadiusPx,
    /// <summary><c>sung.shadow</c>：已唱阴影整体（含关闭与跟随）。</summary>
    SungShadow,
    /// <summary><c>sung.shadow.color</c>。</summary>
    SungShadowColor,
    /// <summary><c>sung.shadow.opacity</c>。</summary>
    SungShadowOpacity,
    /// <summary><c>sung.shadow.angleDegrees</c>。</summary>
    SungShadowAngleDegrees,
    /// <summary><c>sung.shadow.distancePx</c>。</summary>
    SungShadowDistancePx,
    /// <summary><c>sung.shadow.blurRadiusPx</c>。</summary>
    SungShadowBlurRadiusPx,
    /// <summary><c>unsung.glow</c>：未唱辉光整体（含关闭）。</summary>
    UnsungGlow,
    /// <summary><c>unsung.glow.color</c>。</summary>
    UnsungGlowColor,
    /// <summary><c>unsung.glow.opacity</c>。</summary>
    UnsungGlowOpacity,
    /// <summary><c>unsung.glow.blurRadiusPx</c>。</summary>
    UnsungGlowBlurRadiusPx,
    /// <summary><c>unsung.glow.spreadPx</c>。</summary>
    UnsungGlowSpreadPx,
    /// <summary><c>sung.glow</c>：已唱辉光整体（含关闭与跟随）。</summary>
    SungGlow,
    /// <summary><c>sung.glow.color</c>。</summary>
    SungGlowColor,
    /// <summary><c>sung.glow.opacity</c>。</summary>
    SungGlowOpacity,
    /// <summary><c>sung.glow.blurRadiusPx</c>。</summary>
    SungGlowBlurRadiusPx,
    /// <summary><c>sung.glow.spreadPx</c>。</summary>
    SungGlowSpreadPx,
    /// <summary><c>wipe.featherRatio</c>。</summary>
    WipeFeatherRatio,
    /// <summary><c>wipe.highlight</c>：高亮整体（含关闭）。</summary>
    WipeHighlight,
    /// <summary><c>wipe.highlight.color</c>。</summary>
    WipeHighlightColor,
    /// <summary><c>wipe.highlight.widthRatio</c>。</summary>
    WipeHighlightWidthRatio,
    /// <summary><c>wipe.highlight.opacity</c>。</summary>
    WipeHighlightOpacity,
    /// <summary><c>wipe.highlight.blend</c>。</summary>
    WipeHighlightBlend,
    /// <summary><c>verticalCenterRatio</c>。</summary>
    VerticalCenterRatio,
    /// <summary><c>horizontalAlignment</c>。</summary>
    HorizontalAlignment,
    /// <summary><c>opacity</c>。</summary>
    Opacity,
    /// <summary><c>scale</c>。</summary>
    Scale,
}

/// <summary>阴影、辉光、扫字高亮的分量。</summary>
public enum LyricStyleEffectComponent
{
    /// <summary>不是分量项。</summary>
    None,
    /// <summary>颜色。</summary>
    Color,
    /// <summary>不透明度。</summary>
    Opacity,
    /// <summary>阴影角度。</summary>
    AngleDegrees,
    /// <summary>阴影距离。</summary>
    DistancePx,
    /// <summary>模糊半径。</summary>
    BlurRadiusPx,
    /// <summary>辉光扩张。</summary>
    SpreadPx,
    /// <summary>高亮宽度比例。</summary>
    WidthRatio,
    /// <summary>高亮混合方式。</summary>
    Blend,
}

/// <summary><see cref="LyricStyleProperty" /> 的辅助方法。</summary>
public static class LyricStylePropertyExtensions
{
    private static readonly Dictionary<string, LyricStyleProperty> ByJsonName =
        Enum.GetValues<LyricStyleProperty>().ToDictionary(ToJsonName, static property => property, StringComparer.Ordinal);

    /// <summary>把 JSON 里的属性名（即属性路径）解析回枚举；不认识的名字返回 <see langword="false"/>。</summary>
    /// <param name="name">存进文件的属性名。</param>
    /// <param name="property">解析结果；失败时为 <see langword="default"/>。</param>
    /// <returns>认识这个名字时为 <see langword="true"/>。</returns>
    public static bool TryParseJsonName(string? name, out LyricStyleProperty property)
    {
        property = default;
        return name is not null && ByJsonName.TryGetValue(name, out property);
    }

    /// <summary>属性在 JSON 里对应的名字，即属性路径（如 <c>sung.shadow.color</c>）。</summary>
    /// <param name="property">样式属性。</param>
    /// <returns>属性路径。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="property"/> 不是已知属性时抛出。</exception>
    public static string ToJsonName(LyricStyleProperty property) => property switch
    {
        LyricStyleProperty.FontFamilyName => "typography.fontFamilyName",
        LyricStyleProperty.FallbackFamilyNames => "typography.fallbackFamilyNames",
        LyricStyleProperty.FontSizePx => "typography.fontSizePx",
        LyricStyleProperty.FontWeight => "typography.fontWeight",
        LyricStyleProperty.IsBold => "typography.isBold",
        LyricStyleProperty.IsItalic => "typography.isItalic",
        LyricStyleProperty.LetterSpacingPx => "typography.letterSpacingPx",
        LyricStyleProperty.UnsungFill => "unsung.fill",
        LyricStyleProperty.SungFill => "sung.fill",
        LyricStyleProperty.UnsungStrokes => "unsung.strokes",
        LyricStyleProperty.SungStrokes => "sung.strokes",
        LyricStyleProperty.UnsungShadow => "unsung.shadow",
        LyricStyleProperty.UnsungShadowColor => "unsung.shadow.color",
        LyricStyleProperty.UnsungShadowOpacity => "unsung.shadow.opacity",
        LyricStyleProperty.UnsungShadowAngleDegrees => "unsung.shadow.angleDegrees",
        LyricStyleProperty.UnsungShadowDistancePx => "unsung.shadow.distancePx",
        LyricStyleProperty.UnsungShadowBlurRadiusPx => "unsung.shadow.blurRadiusPx",
        LyricStyleProperty.SungShadow => "sung.shadow",
        LyricStyleProperty.SungShadowColor => "sung.shadow.color",
        LyricStyleProperty.SungShadowOpacity => "sung.shadow.opacity",
        LyricStyleProperty.SungShadowAngleDegrees => "sung.shadow.angleDegrees",
        LyricStyleProperty.SungShadowDistancePx => "sung.shadow.distancePx",
        LyricStyleProperty.SungShadowBlurRadiusPx => "sung.shadow.blurRadiusPx",
        LyricStyleProperty.UnsungGlow => "unsung.glow",
        LyricStyleProperty.UnsungGlowColor => "unsung.glow.color",
        LyricStyleProperty.UnsungGlowOpacity => "unsung.glow.opacity",
        LyricStyleProperty.UnsungGlowBlurRadiusPx => "unsung.glow.blurRadiusPx",
        LyricStyleProperty.UnsungGlowSpreadPx => "unsung.glow.spreadPx",
        LyricStyleProperty.SungGlow => "sung.glow",
        LyricStyleProperty.SungGlowColor => "sung.glow.color",
        LyricStyleProperty.SungGlowOpacity => "sung.glow.opacity",
        LyricStyleProperty.SungGlowBlurRadiusPx => "sung.glow.blurRadiusPx",
        LyricStyleProperty.SungGlowSpreadPx => "sung.glow.spreadPx",
        LyricStyleProperty.WipeFeatherRatio => "wipe.featherRatio",
        LyricStyleProperty.WipeHighlight => "wipe.highlight",
        LyricStyleProperty.WipeHighlightColor => "wipe.highlight.color",
        LyricStyleProperty.WipeHighlightWidthRatio => "wipe.highlight.widthRatio",
        LyricStyleProperty.WipeHighlightOpacity => "wipe.highlight.opacity",
        LyricStyleProperty.WipeHighlightBlend => "wipe.highlight.blend",
        LyricStyleProperty.VerticalCenterRatio => "verticalCenterRatio",
        LyricStyleProperty.HorizontalAlignment => "horizontalAlignment",
        LyricStyleProperty.Opacity => "opacity",
        LyricStyleProperty.Scale => "scale",
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "未知的样式属性。"),
    };

    /// <summary>全部样式属性，顺序与 P3 设计 §10.5 的属性清单一致。</summary>
    public static IReadOnlyList<LyricStyleProperty> All { get; } = Enum.GetValues<LyricStyleProperty>();

    /// <summary>未唱阴影的整体项与五个分量。</summary>
    public static IReadOnlyList<LyricStyleProperty> UnsungShadowProperties { get; } =
    [
        LyricStyleProperty.UnsungShadow, LyricStyleProperty.UnsungShadowColor, LyricStyleProperty.UnsungShadowOpacity,
        LyricStyleProperty.UnsungShadowAngleDegrees, LyricStyleProperty.UnsungShadowDistancePx, LyricStyleProperty.UnsungShadowBlurRadiusPx,
    ];

    /// <summary>已唱阴影的整体项与五个分量。</summary>
    public static IReadOnlyList<LyricStyleProperty> SungShadowProperties { get; } =
    [
        LyricStyleProperty.SungShadow, LyricStyleProperty.SungShadowColor, LyricStyleProperty.SungShadowOpacity,
        LyricStyleProperty.SungShadowAngleDegrees, LyricStyleProperty.SungShadowDistancePx, LyricStyleProperty.SungShadowBlurRadiusPx,
    ];

    /// <summary>未唱辉光的整体项与四个分量。</summary>
    public static IReadOnlyList<LyricStyleProperty> UnsungGlowProperties { get; } =
    [
        LyricStyleProperty.UnsungGlow, LyricStyleProperty.UnsungGlowColor, LyricStyleProperty.UnsungGlowOpacity,
        LyricStyleProperty.UnsungGlowBlurRadiusPx, LyricStyleProperty.UnsungGlowSpreadPx,
    ];

    /// <summary>已唱辉光的整体项与四个分量。</summary>
    public static IReadOnlyList<LyricStyleProperty> SungGlowProperties { get; } =
    [
        LyricStyleProperty.SungGlow, LyricStyleProperty.SungGlowColor, LyricStyleProperty.SungGlowOpacity,
        LyricStyleProperty.SungGlowBlurRadiusPx, LyricStyleProperty.SungGlowSpreadPx,
    ];

    /// <summary>扫字高亮的整体项与四个分量。</summary>
    public static IReadOnlyList<LyricStyleProperty> WipeHighlightProperties { get; } =
    [
        LyricStyleProperty.WipeHighlight, LyricStyleProperty.WipeHighlightColor, LyricStyleProperty.WipeHighlightWidthRatio,
        LyricStyleProperty.WipeHighlightOpacity, LyricStyleProperty.WipeHighlightBlend,
    ];

    /// <summary>阴影、辉光、高亮分量项对应的分量；其余属性为 <see cref="LyricStyleEffectComponent.None"/>。</summary>
    /// <param name="property">样式属性。</param>
    /// <returns>分量。</returns>
    public static LyricStyleEffectComponent Component(LyricStyleProperty property) => property switch
    {
        LyricStyleProperty.UnsungShadowColor or LyricStyleProperty.SungShadowColor or LyricStyleProperty.UnsungGlowColor
            or LyricStyleProperty.SungGlowColor or LyricStyleProperty.WipeHighlightColor => LyricStyleEffectComponent.Color,
        LyricStyleProperty.UnsungShadowOpacity or LyricStyleProperty.SungShadowOpacity or LyricStyleProperty.UnsungGlowOpacity
            or LyricStyleProperty.SungGlowOpacity or LyricStyleProperty.WipeHighlightOpacity => LyricStyleEffectComponent.Opacity,
        LyricStyleProperty.UnsungShadowAngleDegrees or LyricStyleProperty.SungShadowAngleDegrees => LyricStyleEffectComponent.AngleDegrees,
        LyricStyleProperty.UnsungShadowDistancePx or LyricStyleProperty.SungShadowDistancePx => LyricStyleEffectComponent.DistancePx,
        LyricStyleProperty.UnsungShadowBlurRadiusPx or LyricStyleProperty.SungShadowBlurRadiusPx
            or LyricStyleProperty.UnsungGlowBlurRadiusPx or LyricStyleProperty.SungGlowBlurRadiusPx => LyricStyleEffectComponent.BlurRadiusPx,
        LyricStyleProperty.UnsungGlowSpreadPx or LyricStyleProperty.SungGlowSpreadPx => LyricStyleEffectComponent.SpreadPx,
        LyricStyleProperty.WipeHighlightWidthRatio => LyricStyleEffectComponent.WidthRatio,
        LyricStyleProperty.WipeHighlightBlend => LyricStyleEffectComponent.Blend,
        _ => LyricStyleEffectComponent.None,
    };
}
