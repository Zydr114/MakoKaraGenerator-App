using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>一次字体检查中有回退或缺字的文本元素。</summary>
/// <param name="ElementIndex">元素在整行中的零基序号。</param>
/// <param name="Utf16Start">元素在原文中的 UTF-16 起点。</param>
/// <param name="Text">原文元素。</param>
/// <param name="ActualFamily">实际绘制字体族。</param>
/// <param name="IsFallback">是否由回退字体绘制。</param>
/// <param name="HasMissingGlyph">是否仍含缺字字形。</param>
public readonly record struct FontElementDiagnostic(
    int ElementIndex, int Utf16Start, string Text, string ActualFamily, bool IsFallback, bool HasMissingGlyph);

/// <summary>文字布局与系统字体发现共同给出的字体诊断。</summary>
/// <param name="RequestedFamilyAvailable">请求的字体族是否在系统字体表中。</param>
/// <param name="ResolvedFamily">实际命中的主字体族。</param>
/// <param name="AvailableWeights">该字体族提供的字重。</param>
/// <param name="SupportsItalic">该字体族是否提供斜体字形。</param>
/// <param name="Elements">发生回退或缺字的元素及其实际字体。</param>
public sealed record FontInspection(
    bool RequestedFamilyAvailable,
    string ResolvedFamily,
    IReadOnlyList<int> AvailableWeights,
    bool SupportsItalic,
    IReadOnlyList<FontElementDiagnostic> Elements);

/// <summary>供样式检查器使用的系统字体发现和实际整形诊断。</summary>
public static class SkiaFontInspector
{
    /// <summary>取得当前字体环境中可选择的字体族。</summary>
    public static IReadOnlyList<string> AvailableFamilies() =>
        [.. SKFontManager.Default.FontFamilies.Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>以预览和导出同一套整形器检查字体、回退和缺字位置。</summary>
    /// <param name="text">要检查的真实歌词文本。</param>
    /// <param name="options">文字布局参数。</param>
    /// <returns>请求字体和实际字形的诊断。</returns>
    public static FontInspection Inspect(string text, TextLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        SKFontManager manager = SKFontManager.Default;
        bool requestedAvailable = string.IsNullOrWhiteSpace(options.FamilyName)
            || manager.FontFamilies.Contains(options.FamilyName, StringComparer.OrdinalIgnoreCase);
        using SkiaTextLayoutEngine engine = new();
        string resolved = engine.ResolveFamilyName(options.FamilyName);
        using SKFontStyleSet styles = manager.GetFontStyles(resolved);
        List<int> weights = [];
        bool italic = false;
        for (int index = 0; index < styles.Count; index++)
        {
            SKFontStyle style = styles[index];
            if (!weights.Contains(style.Weight)) weights.Add(style.Weight);
            italic |= style.Slant != SKFontStyleSlant.Upright;
        }
        weights.Sort();
        ShapedLine line = engine.Shape(text, options);
        FontElementDiagnostic[] elements =
        [
            .. line.Elements.Select((element, index) => (element, index))
                .Where(item => item.element.IsFallback || item.element.HasMissingGlyph)
                .Select(item => new FontElementDiagnostic(item.index, item.element.Utf16Start,
                    item.element.Text, item.element.FamilyName,
                    item.element.IsFallback, item.element.HasMissingGlyph)),
        ];
        return new FontInspection(requestedAvailable, resolved, weights, italic, elements);
    }
}
