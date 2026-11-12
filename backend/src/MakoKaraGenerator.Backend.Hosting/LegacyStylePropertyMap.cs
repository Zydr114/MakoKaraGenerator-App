using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>过渡期（P3 第 2～6 步）的样式属性换算：HTTP 合同仍是旧的 26 项，Core 已改为 43 项（P3 设计 §10.5）。</summary>
/// <remarks>
/// 旧的整项（描边、阴影、辉光）对应两态的整项，已唱一侧照模板原样复制「跟随」；旧的分量只对应未唱一侧。
/// 扫字样式在旧合同中没有对应项，导出时略去。第 7 步合同改版后删除本类。
/// </remarks>
internal static class LegacyStylePropertyMap
{
    private static readonly ImmutableArray<LyricStyleProperty> Strokes = [LyricStyleProperty.UnsungStrokes, LyricStyleProperty.SungStrokes];

    /// <summary>旧属性对应的新属性；第一项是读取来源时使用的主属性。</summary>
    internal static ImmutableArray<LyricStyleProperty> Import(TemplateProperty property) => property switch
    {
        TemplateProperty.FontFamilyName => [LyricStyleProperty.FontFamilyName],
        TemplateProperty.FallbackFamilyNames => [LyricStyleProperty.FallbackFamilyNames],
        TemplateProperty.FontSizePx => [LyricStyleProperty.FontSizePx],
        TemplateProperty.FontWeight => [LyricStyleProperty.FontWeight],
        TemplateProperty.IsBold => [LyricStyleProperty.IsBold],
        TemplateProperty.IsItalic => [LyricStyleProperty.IsItalic],
        TemplateProperty.LetterSpacingPx => [LyricStyleProperty.LetterSpacingPx],
        TemplateProperty.VerticalCenterRatio => [LyricStyleProperty.VerticalCenterRatio],
        TemplateProperty.HorizontalAlignment => [LyricStyleProperty.HorizontalAlignment],
        TemplateProperty.UnsungColor or TemplateProperty.UnsungGradientSpec => [LyricStyleProperty.UnsungFill],
        TemplateProperty.SungColor or TemplateProperty.SungGradientSpec => [LyricStyleProperty.SungFill],
        TemplateProperty.StrokeColor or TemplateProperty.StrokeWidthPx or TemplateProperty.StrokeSpec => Strokes,
        TemplateProperty.ShadowColor => [LyricStyleProperty.UnsungShadowColor],
        TemplateProperty.ShadowBlurPx => [LyricStyleProperty.UnsungShadowBlurRadiusPx],
        TemplateProperty.ShadowSpec => [LyricStyleProperty.UnsungShadow, .. LyricStylePropertyExtensions.UnsungShadowProperties,
            LyricStyleProperty.SungShadow, .. LyricStylePropertyExtensions.SungShadowProperties],
        TemplateProperty.Opacity => [LyricStyleProperty.Opacity],
        TemplateProperty.Scale => [LyricStyleProperty.Scale],
        TemplateProperty.GlowSpec => [LyricStyleProperty.UnsungGlow, .. LyricStylePropertyExtensions.UnsungGlowProperties,
            LyricStyleProperty.SungGlow, .. LyricStylePropertyExtensions.SungGlowProperties],
        TemplateProperty.GlowColor => [LyricStyleProperty.UnsungGlowColor],
        TemplateProperty.GlowOpacity => [LyricStyleProperty.UnsungGlowOpacity],
        TemplateProperty.GlowBlurRadiusPx => [LyricStyleProperty.UnsungGlowBlurRadiusPx],
        TemplateProperty.GlowSpreadPx => [LyricStyleProperty.UnsungGlowSpreadPx],
        _ => throw new JsonException("未知模板属性。"),
    };

    /// <summary>把旧属性清单换算成新属性清单（去重，保持首次出现的顺序）。</summary>
    internal static ImmutableArray<LyricStyleProperty> Import(IEnumerable<TemplateProperty> properties)
        => [.. properties.SelectMany(property => Import(property)).Distinct()];

    /// <summary>把新属性清单换算成旧属性清单，按旧枚举顺序；扫字样式没有旧对应项。</summary>
    internal static TemplateProperty[] Export(IEnumerable<LyricStyleProperty> properties)
    {
        HashSet<LyricStyleProperty> selected = [.. properties];
        return [.. Enum.GetValues<TemplateProperty>().Where(legacy => Exported(legacy, selected))];
    }

    /// <summary>读取来源用的主属性。</summary>
    internal static LyricStyleProperty Primary(TemplateProperty property) => Import(property)[0];

    /// <summary>旧合同里的属性名（camelCase）。</summary>
    internal static string JsonName(TemplateProperty property) => JsonNamingPolicy.CamelCase.ConvertName(property.ToString());

    // 旧整项在新清单含任一对应项时导出；旧分量只在新清单含对应的未唱分量时导出。
    private static bool Exported(TemplateProperty legacy, HashSet<LyricStyleProperty> selected) => legacy switch
    {
        TemplateProperty.UnsungColor or TemplateProperty.UnsungGradientSpec => selected.Contains(LyricStyleProperty.UnsungFill),
        TemplateProperty.SungColor or TemplateProperty.SungGradientSpec => selected.Contains(LyricStyleProperty.SungFill),
        TemplateProperty.StrokeColor or TemplateProperty.StrokeWidthPx => false,
        TemplateProperty.StrokeSpec => selected.Overlaps(Strokes),
        TemplateProperty.ShadowSpec or TemplateProperty.GlowSpec => selected.Overlaps(Import(legacy)),
        _ => selected.Contains(Primary(legacy)),
    };
}
