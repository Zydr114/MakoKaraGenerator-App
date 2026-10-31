using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>
/// 字体覆盖率判断：某个 Skia 字体是否画得出某个文本元素。
/// </summary>
/// <remarks>
/// <para>
/// 这是字体回退的核心判断，放在 Skia 后端而不是 <c>MakoKaraGenerator.Text</c>，
/// 因为它必须问具体的字体对象。
/// </para>
/// <para>
/// <b>刻意不使用 <c>SKTypeface.ContainsGlyphs</c></b>：它的语义是"所有码点都必须有字形"，
/// 而变体选择符 <c>U+FE0F</c> 在<b>任何</b>字体里都没有字形，
/// 于是彩虹旗 <c>🏳️‍🌈</c> 会把真正能画它的 <c>Noto Color Emoji</c> 判成"不覆盖"，
/// 回退一路退到中文字体，最终画出一排豆腐块（docs/decisions.md ADR-0010）。
/// 正确判据是"每个非默认可忽略码点都有字形"。
/// </para>
/// </remarks>
public static class SkiaTypefaceCoverage
{
    /// <summary>判断一个字体是否覆盖某个文本元素。</summary>
    /// <param name="typeface">候选字体。</param>
    /// <param name="element">文本元素（可以包含多个码点，例如一个 ZWJ emoji 序列）。</param>
    /// <returns>每个非默认可忽略码点都有字形时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeface"/> 或 <paramref name="element"/> 为 <see langword="null"/> 时抛出。</exception>
    public static bool IsElementCoveredBy(SKTypeface typeface, string element)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        ArgumentNullException.ThrowIfNull(element);

        foreach (int codepoint in TextElementRules.EnumerateCodepoints(element))
        {
            if (TextElementRules.IsDefaultIgnorable(codepoint))
            {
                continue;
            }

            if (typeface.GetGlyph(codepoint) == 0)
            {
                return false;
            }
        }

        return true;
    }
}
