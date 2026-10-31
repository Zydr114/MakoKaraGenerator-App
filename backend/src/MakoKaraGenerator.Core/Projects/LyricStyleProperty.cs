namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 样式里可以逐属性追踪来源的那几项。
/// </summary>
/// <remarks>
/// 用枚举而不是字符串作为来源表的键：属性名拼错时界面会<b>安静地</b>显示错误的来源，
/// 而枚举在编译期就会被拦住。枚举成员名与 <see cref="LyricStyle" /> 的属性名一致，
/// 便于诊断面板直接对号入座。
/// </remarks>
public enum LyricStyleProperty
{
    /// <summary>对应 <see cref="LyricStyle.FontFamilyName" />。</summary>
    FontFamilyName,

    /// <summary>对应 <see cref="LyricStyle.FallbackFamilyNames" />。</summary>
    FallbackFamilyNames,

    /// <summary>对应 <see cref="LyricStyle.FontSizePx" />。</summary>
    FontSizePx,

    /// <summary>对应 <see cref="LyricStyle.FontWeight" />。</summary>
    FontWeight,
    /// <summary>对应 <see cref="LyricStyle.IsBold" />。</summary>
    IsBold,
    /// <summary>对应 <see cref="LyricStyle.IsItalic" />。</summary>
    IsItalic,
    /// <summary>对应 <see cref="LyricStyle.LetterSpacingPx" />。</summary>
    LetterSpacingPx,

    /// <summary>对应 <see cref="LyricStyle.VerticalCenterRatio" />。</summary>
    VerticalCenterRatio,

    /// <summary>对应 <see cref="LyricStyle.HorizontalAlignment" />。</summary>
    HorizontalAlignment,

    /// <summary>对应 <see cref="LyricStyle.UnsungColor" />。</summary>
    UnsungColor,

    /// <summary>对应 <see cref="LyricStyle.SungColor" />。</summary>
    SungColor,

    /// <summary>对应 <see cref="LyricStyle.UnsungGradientSpec" />。</summary>
    UnsungGradientSpec,
    /// <summary>对应 <see cref="LyricStyle.SungGradientSpec" />。</summary>
    SungGradientSpec,
    /// <summary>对应 <see cref="LyricStyle.StrokeColor" />。</summary>
    StrokeColor,
    /// <summary>对应 <see cref="LyricStyle.StrokeWidthPx" />。</summary>
    StrokeWidthPx,
    /// <summary>对应 <see cref="LyricStyle.StrokeSpec" />。</summary>
    StrokeSpec,
    /// <summary>对应 <see cref="LyricStyle.ShadowColor" />。</summary>
    ShadowColor,
    /// <summary>对应 <see cref="LyricStyle.ShadowBlurPx" />。</summary>
    ShadowBlurPx,
    /// <summary>对应 <see cref="LyricStyle.ShadowSpec" />。</summary>
    ShadowSpec,
    /// <summary>对应 <see cref="LyricStyle.Opacity" />。</summary>
    Opacity,
    /// <summary>对应 <see cref="LyricStyle.Scale" />。</summary>
    Scale,
}

/// <summary><see cref="LyricStyleProperty" /> 的辅助方法。</summary>
public static class LyricStylePropertyExtensions
{
    /// <summary>把 JSON 里的属性名解析回枚举；不认识的名字返回 <see langword="false"/>。</summary>
    /// <param name="name">存进文件的属性名。</param>
    /// <param name="property">解析结果；失败时为 <see langword="default"/>。</param>
    /// <returns>认识这个名字时为 <see langword="true"/>。</returns>
    /// <remarks>名字清单由 <see cref="ToJsonName"/> 与 <see cref="All"/> 派生，不另维护一份。</remarks>
    public static bool TryParseJsonName(string? name, out LyricStyleProperty property)
    {
        property = default;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (LyricStyleProperty candidate in All)
        {
            if (string.Equals(ToJsonName(candidate), name, StringComparison.Ordinal))
            {
                property = candidate;

                return true;
            }
        }

        return false;
    }

    /// <summary>属性在 JSON 里对应的字段名。</summary>
    /// <param name="property">样式属性。</param>
    /// <returns>字段名（camelCase）。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="property"/> 不是已知属性时抛出。</exception>
    /// <remarks>
    /// 诊断信息要告诉用户"是哪一项有问题"，因此需要这个名字；把它放在属性旁边，
    /// 免得校验器、映射器各写一份字符串（那是"改了这边忘了那边"的经典来源）。
    /// </remarks>
    public static string ToJsonName(LyricStyleProperty property) => property switch
    {
        LyricStyleProperty.FontFamilyName => "fontFamilyName",
        LyricStyleProperty.FallbackFamilyNames => "fallbackFamilyNames",
        LyricStyleProperty.FontSizePx => "fontSizePx",
        LyricStyleProperty.FontWeight => "fontWeight",
        LyricStyleProperty.IsBold => "isBold",
        LyricStyleProperty.IsItalic => "isItalic",
        LyricStyleProperty.LetterSpacingPx => "letterSpacingPx",
        LyricStyleProperty.VerticalCenterRatio => "verticalCenterRatio",
        LyricStyleProperty.HorizontalAlignment => "horizontalAlignment",
        LyricStyleProperty.UnsungColor => "unsungColor",
        LyricStyleProperty.SungColor => "sungColor",
        LyricStyleProperty.UnsungGradientSpec => "unsungGradientSpec",
        LyricStyleProperty.SungGradientSpec => "sungGradientSpec",
        LyricStyleProperty.StrokeColor => "strokeColor",
        LyricStyleProperty.StrokeWidthPx => "strokeWidthPx",
        LyricStyleProperty.StrokeSpec => "strokeSpec",
        LyricStyleProperty.ShadowColor => "shadowColor",
        LyricStyleProperty.ShadowBlurPx => "shadowBlurPx",
        LyricStyleProperty.ShadowSpec => "shadowSpec",
        LyricStyleProperty.Opacity => "opacity",
        LyricStyleProperty.Scale => "scale",
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "未知的样式属性。"),
    };

    /// <summary>全部样式属性，顺序与解析器的应用顺序一致。</summary>
    public static IReadOnlyList<LyricStyleProperty> All { get; } =
    [
        LyricStyleProperty.FontFamilyName,
        LyricStyleProperty.FallbackFamilyNames,
        LyricStyleProperty.FontSizePx,
        LyricStyleProperty.FontWeight,
        LyricStyleProperty.IsBold,
        LyricStyleProperty.IsItalic,
        LyricStyleProperty.LetterSpacingPx,
        LyricStyleProperty.VerticalCenterRatio,
        LyricStyleProperty.HorizontalAlignment,
        LyricStyleProperty.UnsungColor,
        LyricStyleProperty.SungColor,
        LyricStyleProperty.UnsungGradientSpec,
        LyricStyleProperty.SungGradientSpec,
        LyricStyleProperty.StrokeColor,
        LyricStyleProperty.StrokeWidthPx,
        LyricStyleProperty.StrokeSpec,
        LyricStyleProperty.ShadowColor,
        LyricStyleProperty.ShadowBlurPx,
        LyricStyleProperty.ShadowSpec,
        LyricStyleProperty.Opacity,
        LyricStyleProperty.Scale,
    ];
}
