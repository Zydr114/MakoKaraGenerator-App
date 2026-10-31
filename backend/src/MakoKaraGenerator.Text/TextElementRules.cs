using System.Globalization;

namespace MakoKaraGenerator.Text;

/// <summary>
/// 文本元素与码点的辅助判断。
/// </summary>
/// <remarks>
/// 本类只含<b>不依赖任何图形库</b>的纯逻辑：这些规则要么与服务端的文本语义有关，
/// 要么是字体回退策略的一部分，而"某字体是否覆盖某元素"必须问具体的字体后端，
/// 因此那部分在 <c>MakoKaraGenerator.Rendering.Skia.SkiaTypefaceCoverage</c>。
/// </remarks>
public static class TextElementRules
{
    /// <summary>
    /// 判断一个码点是否属于"默认可忽略"字符——即不要求字体提供字形的控制/格式字符。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个判断是字体回退正确性的关键。实测教训（docs/decisions.md ADR-0010）：
    /// <c>SKTypeface.ContainsGlyphs</c> 的语义是"所有码点都必须有字形"，
    /// 而彩虹旗 <c>🏳️‍🌈</c> 里的变体选择符 <c>U+FE0F</c> 在<b>任何</b>字体里都没有字形
    /// （glyph id 为 0），于是真正能画它的 <c>Noto Color Emoji</c> 会被误判为"不覆盖"，
    /// 回退逻辑一路退到中文字体，最终画出一排豆腐块。
    /// </para>
    /// <para>
    /// 因此覆盖率必须按"每个非默认可忽略码点都要有字形"来判断，而不是用库现成的整体判断。
    /// </para>
    /// </remarks>
    /// <param name="codepoint">Unicode 码点。</param>
    /// <returns>该码点不需要字形时为 <see langword="true"/>。</returns>
    public static bool IsDefaultIgnorable(int codepoint)
    {
        if (codepoint is < 0 or > 0x10FFFF)
        {
            return true;
        }

        return codepoint switch
        {
            // 格式字符、连接符与显式方向标记
            0x00AD or 0x034F or 0x061C => true,
            0x115F or 0x1160 or 0x17B4 or 0x17B5 => true,
            0x180B or 0x180C or 0x180D or 0x180E or 0x180F => true,
            >= 0x200B and <= 0x200F => true,
            >= 0x202A and <= 0x202E => true,
            >= 0x2060 and <= 0x206F => true,
            0x3164 or 0xFEFF or 0xFFA0 => true,

            // 变体选择符
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xE0100 and <= 0xE01EF => true,

            // 标签字符
            >= 0xE0000 and <= 0xE0FFF => true,

            _ => CharUnicodeInfo.GetUnicodeCategory(codepoint) == UnicodeCategory.Format,
        };
    }

    /// <summary>枚举字符串中的 Unicode 码点。</summary>
    /// <param name="text">待枚举的文本。</param>
    /// <returns>码点序列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 参数检查刻意放在非迭代器方法里：如果直接写在一个带 <c>yield return</c> 的方法体内，
    /// 它会因为惰性求值而延迟到第一次枚举时才执行，调用方拿不到及时的参数错误。
    /// </remarks>
    public static IEnumerable<int> EnumerateCodepoints(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EnumerateCodepointsCore(text);
    }

    private static IEnumerable<int> EnumerateCodepointsCore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (int i = 0; i < text.Length; i++)
        {
            int codepoint = char.ConvertToUtf32(text, i);

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
            {
                i++;
            }

            yield return codepoint;
        }
    }
}
