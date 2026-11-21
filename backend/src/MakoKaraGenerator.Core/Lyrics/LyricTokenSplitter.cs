using System.Collections.Immutable;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 按规则把一行文本切成若干段（切分的唯一实现）。
/// </summary>
/// <remarks>
/// <para>
/// 不变式：<b>切出来的各段依次拼接必须完全等于输入文本</b>，包括空白与标点。
/// 实现方式是"只决定边界落在哪些 Unicode 文本元素之前"，从不按 UTF-16 下标硬切，
/// 因此 emoji、组合音标、代理对都不会被拆坏（docs/development.md §5.2）。
/// </para>
/// <para>
/// 标点与空白从不作为段的开头（它们依附前一个元素），所以段与段之间不会出现空段。
/// </para>
/// </remarks>
public static class LyricTokenSplitter
{
    /// <summary>按规则切分文本。</summary>
    /// <param name="text">行文本。</param>
    /// <param name="rule">切分规则。</param>
    /// <returns>各段文本；输入为空时返回空数组，<see cref="TokenSplitRule.None" /> 时返回整行一段。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> 为 <see langword="null" /> 时抛出。</exception>
    public static ImmutableArray<string> Split(string text, TokenSplitRule rule) => Split(text, rule, null);

    /// <summary>按同一规则逐项建立切分；观察器在每个真实段分配之前运行，可中止宿主的有界准备。</summary>
    /// <param name="text">待切分正文。</param>
    /// <param name="rule">既有切分规则。</param>
    /// <param name="onCreatingSegment">可选的段分配观察器；默认桌面调用不设预算。</param>
    /// <returns>精确拼接原正文的固定段序列。</returns>
    public static ImmutableArray<string> Split(string text, TokenSplitRule rule, Action? onCreatingSegment)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [];
        }

        if (rule == TokenSplitRule.None)
        {
            onCreatingSegment?.Invoke();
            return [text];
        }

        ImmutableArray<string>.Builder segments = ImmutableArray.CreateBuilder<string>();
        using IEnumerator<TextElementRange> elements = GraphemeSplitter.Enumerate(text).GetEnumerator();
        _ = elements.MoveNext();
        TextElementRange previous = elements.Current;
        int segmentStart = 0;
        while (elements.MoveNext())
        {
            TextElementRange current = elements.Current;
            if (ShouldCutBefore(previous.Text, current.Text, rule))
            {
                onCreatingSegment?.Invoke();
                segments.Add(text[segmentStart..current.Start]);
                segmentStart = current.Start;
            }
            previous = current;
        }

        onCreatingSegment?.Invoke();
        segments.Add(text[segmentStart..]);

        return segments.ToImmutable();
    }

    /// <summary>取各段的起始下标（首段恒为 0），便于调用方按位置对齐。</summary>
    /// <param name="text">行文本。</param>
    /// <param name="rule">切分规则。</param>
    /// <returns>各段起始下标。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> 为 <see langword="null" /> 时抛出。</exception>
    public static ImmutableArray<int> CutOffsets(string text, TokenSplitRule rule)
    {
        ImmutableArray<string> segments = Split(text, rule);
        ImmutableArray<int>.Builder offsets = ImmutableArray.CreateBuilder<int>(segments.Length);
        int cursor = 0;

        foreach (string segment in segments)
        {
            offsets.Add(cursor);
            cursor += segment.Length;
        }

        return offsets.ToImmutable();
    }

    private static bool ShouldCutBefore(string previousText, string currentText, TokenSplitRule rule)
    {
        ElementClass current = Classify(currentText);
        ElementClass previous = Classify(previousText);

        return rule switch
        {
            // 每个文本元素都切。
            TokenSplitRule.PerTextElement => true,

            // CJK 逐字、emoji 逐个；拉丁字母/数字按词；连续空白独立为无时间空白段；标点各自独立为无时间标点段。
            TokenSplitRule.Mixed => current == ElementClass.Space
                ? previous != ElementClass.Space
                : previous is ElementClass.Space or ElementClass.Punctuation
                    || current is ElementClass.Cjk or ElementClass.Other or ElementClass.Punctuation
                    || (current == ElementClass.Word && previous != ElementClass.Word),

            // 连续的字母数字算一段，连续的 CJK 也算一段；连续空白独立成段，标点各自独立成段。
            TokenSplitRule.LatinWords => current == ElementClass.Space
                ? previous != ElementClass.Space
                : previous is ElementClass.Space or ElementClass.Punctuation
                    || current == ElementClass.Punctuation
                    || (current == ElementClass.Word && previous != ElementClass.Word)
                    || (current == ElementClass.Cjk && previous != ElementClass.Cjk)
                    || current == ElementClass.Other,

            // 标点之后另起一段。
            TokenSplitRule.ByPunctuation => previous == ElementClass.Punctuation,

            // 空白之后另起一段。
            TokenSplitRule.ByWhitespace => previous == ElementClass.Space,

            _ => false,
        };
    }

    /// <summary>文本元素的大类（只看第一个码位；一个 grapheme 内的字符类别通常一致）。</summary>
    private enum ElementClass
    {
        /// <summary>中日韩文字（含假名、谚文）。</summary>
        Cjk,

        /// <summary>拉丁字母、数字等"词"字符。</summary>
        Word,

        /// <summary>标点与符号。</summary>
        Punctuation,

        /// <summary>空白。</summary>
        Space,

        /// <summary>其它（emoji、控制符等）。</summary>
        Other,
    }

    private static ElementClass Classify(string element)
    {
        if (element.Length == 0)
        {
            return ElementClass.Other;
        }

        if (LyricTokenSemantics.IsWhitespaceElement(element))
        {
            return ElementClass.Space;
        }

        if (LyricTokenSemantics.IsPunctuationElement(element))
        {
            return ElementClass.Punctuation;
        }

        char first = element[0];
        int codePoint = char.IsSurrogatePair(element, 0)
            ? char.ConvertToUtf32(element, 0)
            : first;

        if (LyricTokenSemantics.IsCjkCodePoint(codePoint))
        {
            return ElementClass.Cjk;
        }

        // 其余符号（emoji 等）归入 Other：每个 emoji 自成一个元素，但不与相邻文字粘连。
        return char.IsLetterOrDigit(first) ? ElementClass.Word : ElementClass.Other;
    }
}
