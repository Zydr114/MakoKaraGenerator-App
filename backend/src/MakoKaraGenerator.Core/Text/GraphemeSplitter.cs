using System.Globalization;

namespace MakoKaraGenerator.Core.Text;

/// <summary>
/// 文本元素在原始字符串中的位置与内容。
/// </summary>
/// <param name="Start">在 UTF-16 字符串中的起始下标。</param>
/// <param name="Length">以 UTF-16 代码单元计的长度（emoji 可能为 2 或更多）。</param>
/// <param name="Text">该文本元素的原始文本。</param>
public readonly record struct TextElementRange(int Start, int Length, string Text)
{
    /// <summary>结束下标（不含）。</summary>
    public int End => Start + Length;
}

/// <summary>
/// 按 Unicode 文本元素（grapheme cluster）切分文本。
/// </summary>
/// <remarks>
/// <para>
/// docs/development.md §5.2 要求"逐字"按文本元素而不是 UTF-16 <see cref="char"/> 切分，
/// 否则会把 emoji 的多个码点、组合音标或变体选择符拆坏。本类是这条策略的唯一实现（Core 不依赖任何 UI 或渲染库）。
/// </para>
/// <para>
/// 实现使用 <see cref="StringInfo.ParseCombiningCharacters(string)"/>。
/// <b>已知限制</b>：.NET 的实现遵循较旧的 Unicode 版本，
/// 对部分 ZWJ 序列（例如 <c>👩‍🚀</c>）可能仍会切成多个元素；
/// 正式实现需要评估是否引入更新的分段规则或第三方实现。
/// </para>
/// </remarks>
public static class GraphemeSplitter
{
    /// <summary>以相同 .NET 文本元素规则逐项枚举，不预先建立整行范围数组。</summary>
    /// <param name="text">待枚举的原始正文。</param>
    /// <returns>按原顺序给出的完整 Unicode 文本元素。</returns>
    public static IEnumerable<TextElementRange> Enumerate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EnumerateCore(text);
    }

    private static IEnumerable<TextElementRange> EnumerateCore(string text)
    {
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            yield return new(elements.ElementIndex, element.Length, element);
        }
    }

    /// <summary>按文本元素切分，并给出每个元素在原始字符串中的位置。</summary>
    /// <param name="text">待切分的文本。</param>
    /// <returns>文本元素序列；空字符串返回空序列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/> 时抛出。</exception>
    public static IReadOnlyList<TextElementRange> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [];
        }

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        TextElementRange[] ranges = new TextElementRange[starts.Length];

        for (int i = 0; i < starts.Length; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            ranges[i] = new TextElementRange(start, end - start, text[start..end]);
        }

        return ranges;
    }

    /// <summary>按文本元素切分为字符串序列。</summary>
    /// <param name="text">待切分的文本。</param>
    /// <returns>文本元素序列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/> 时抛出。</exception>
    public static IReadOnlyList<string> SplitToStrings(string text)
    {
        IReadOnlyList<TextElementRange> ranges = Split(text);

        if (ranges.Count == 0)
        {
            return [];
        }

        string[] result = new string[ranges.Count];

        for (int i = 0; i < ranges.Count; i++)
        {
            result[i] = ranges[i].Text;
        }

        return result;
    }
}
