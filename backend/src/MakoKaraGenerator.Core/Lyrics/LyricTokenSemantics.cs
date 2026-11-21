using System.Collections.Immutable;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>文本元素的语义大类，用于区分实义打轴单元与无时间空白/标点。</summary>
public enum LyricTokenKind
{
    /// <summary>实义歌词内容（汉字、假名、拉丁词、数字、emoji 等可打轴单元）。</summary>
    Content,

    /// <summary>纯空白字符（半角空格、全角空格、制表符等）。</summary>
    Whitespace,

    /// <summary>纯标点或非实义符号。</summary>
    Punctuation,
}

/// <summary>
/// 共享的文本元素语义与英/日空格语境停顿纯规则。
/// </summary>
/// <remarks>
/// 本类为纯规则实现，供分词器、时间轴编辑器、渲染桥接与 TTML 导入导出共用，
/// 确保全链路对「实义单元」「空白/标点」以及「切分空格 vs 停顿空格」的判断完全一致。
/// </remarks>
public static class LyricTokenSemantics
{
    /// <summary>判断单个文本元素的语义大类。</summary>
    /// <param name="text">元素文本。</param>
    /// <returns>元素语义大类；空字符串视为 <see cref="LyricTokenKind.Content"/>。</returns>
    public static LyricTokenKind Classify(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LyricTokenKind.Content;
        }

        bool sawWhitespace = false;
        bool sawPunctuation = false;

        foreach (TextElementRange element in GraphemeSplitter.Enumerate(text))
        {
            if (IsWhitespaceElement(element.Text))
            {
                sawWhitespace = true;
                continue;
            }

            if (IsPunctuationElement(element.Text))
            {
                sawPunctuation = true;
                continue;
            }

            return LyricTokenKind.Content;
        }

        if (sawPunctuation)
        {
            return LyricTokenKind.Punctuation;
        }

        return sawWhitespace ? LyricTokenKind.Whitespace : LyricTokenKind.Content;
    }

    /// <summary>判断该元素是否为无时间的纯空白或纯标点（渲染裁剪桥接的唯一合法作用域）。</summary>
    public static bool IsUntimedNonContent(LyricToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Time is null
            && token.TimingStatus == TimingStatus.Missing
            && Classify(token.Text) is LyricTokenKind.Whitespace or LyricTokenKind.Punctuation;
    }

    /// <summary>判断该元素是否为无时间的纯空白（不含换行），适合 AMLL TTML 输出为裸文本节点。</summary>
    public static bool IsUntimedInlineWhitespace(LyricToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Time is null
            && token.TimingStatus == TimingStatus.Missing
            && Classify(token.Text) == LyricTokenKind.Whitespace
            && !token.Text.Contains('\n')
            && !token.Text.Contains('\r');
    }

    /// <summary>
    /// 计算指定元素在行内的有效句中收尾提示（○）：
    /// 显式覆写优先；否则根据至下一实义元素之间的无时间空白语境自动推导。
    /// </summary>
    /// <param name="tokens">行内全部元素。</param>
    /// <param name="tokenIndex">目标元素下标。</param>
    /// <returns>应显示句中停顿收尾提示时返回 <see langword="true"/>。</returns>
    public static bool HasPhraseEndHint(ImmutableArray<LyricToken> tokens, int tokenIndex)
    {
        if (tokens.IsDefaultOrEmpty || tokenIndex < 0 || tokenIndex >= tokens.Length)
        {
            return false;
        }

        LyricToken token = tokens[tokenIndex];
        if (Classify(token.Text) != LyricTokenKind.Content)
        {
            return false;
        }

        if (token.PhraseEndHint is { } explicitHint)
        {
            return explicitHint;
        }

        return DerivePhraseEndHint(tokens, tokenIndex);
    }

    /// <summary>忽略显式覆写，仅按邻接语境推导指定实义元素的句中收尾提示。</summary>
    public static bool DerivePhraseEndHint(ImmutableArray<LyricToken> tokens, int tokenIndex)
    {
        if (tokens.IsDefaultOrEmpty || tokenIndex < 0 || tokenIndex >= tokens.Length)
        {
            return false;
        }

        LyricToken token = tokens[tokenIndex];
        if (Classify(token.Text) != LyricTokenKind.Content)
        {
            return false;
        }

        int nextContentIndex = -1;
        int spaceCount = 0;
        bool hasFullwidthOrSpecialSpace = false;

        for (int i = tokenIndex + 1; i < tokens.Length; i++)
        {
            LyricToken candidate = tokens[i];
            LyricTokenKind kind = Classify(candidate.Text);
            if (kind == LyricTokenKind.Content)
            {
                nextContentIndex = i;
                break;
            }

            // 间隔里出现显式已定时的空白/标点时，不视为无时间停顿间隔。
            if (candidate.Time is not null)
            {
                return false;
            }

            if (kind == LyricTokenKind.Whitespace)
            {
                foreach (char ch in candidate.Text)
                {
                    if (!char.IsWhiteSpace(ch))
                    {
                        continue;
                    }

                    spaceCount++;
                    if (ch != ' ')
                    {
                        hasFullwidthOrSpecialSpace = true;
                    }
                }
            }
        }

        // 行尾（后面没有更多实义元素）或中间没有空白 → false。
        if (nextContentIndex < 0 || spaceCount == 0)
        {
            return false;
        }

        // 全角空格 U+3000 / 制表符 / 连续多个空格 → 停顿型空格。
        if (hasFullwidthOrSpecialSpace || spaceCount >= 2)
        {
            return true;
        }

        // 单个半角空格 U+0020：与中日韩字符相邻 → 停顿；夹在拉丁字母/数字词之间 → 切分。
        return EndsWithCjk(token.Text) || StartsWithCjk(tokens[nextContentIndex].Text);
    }

    /// <summary>判断指定下标是否为行内最后一个实义元素。</summary>
    public static bool IsLastContentToken(ImmutableArray<LyricToken> tokens, int tokenIndex)
    {
        if (tokens.IsDefaultOrEmpty || tokenIndex < 0 || tokenIndex >= tokens.Length)
        {
            return false;
        }

        if (Classify(tokens[tokenIndex].Text) != LyricTokenKind.Content)
        {
            return false;
        }

        for (int i = tokenIndex + 1; i < tokens.Length; i++)
        {
            if (Classify(tokens[i].Text) == LyricTokenKind.Content)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>判断单个 grapheme 元素是否为空白。</summary>
    public static bool IsWhitespaceElement(string element) =>
        !string.IsNullOrEmpty(element) && char.IsWhiteSpace(element, 0);

    /// <summary>判断单个 grapheme 元素是否为标点或 ASCII/全角非字母符号（长音符 ー 与 emoji 除外）。</summary>
    public static bool IsPunctuationElement(string element)
    {
        if (string.IsNullOrEmpty(element))
        {
            return false;
        }

        char first = element[0];
        int codePoint = char.IsSurrogatePair(element, 0)
            ? char.ConvertToUtf32(element, 0)
            : first;

        if (char.IsPunctuation(first))
        {
            return true;
        }

        // ASCII 符号（< 0x2000）以及全角 ASCII 符号（如 ～ U+FF5E、＝ U+FF1D 等）归入标点符号；
        // emoji 与音乐符号（♪ 🎵 等）保留为实义元素。
        return char.IsSymbol(first) && (codePoint < 0x2000 || codePoint is >= 0xFF00 and <= 0xFFEF);
    }

    /// <summary>判断码点是否属于中日韩文字（含汉字、平/片假名、长音符、叠字符、谚文）。</summary>
    public static bool IsCjkCodePoint(int codePoint) => codePoint switch
    {
        0x3005 or 0x3006 or 0x3007 => true, // 々 〆 〇
        >= 0x3040 and <= 0x30FF => true,    // 平假名 / 片假名（含长音符 ー U+30FC）
        >= 0x3400 and <= 0x4DBF => true,    // CJK 扩展 A
        >= 0x4E00 and <= 0x9FFF => true,    // CJK 基本区
        >= 0xF900 and <= 0xFAFF => true,    // CJK 兼容表意文字
        >= 0xAC00 and <= 0xD7AF => true,    // 谚文音节
        >= 0x20000 and <= 0x2FA1F => true,  // CJK 扩展 B~F
        >= 0xFF66 and <= 0xFF9F => true,    // 半角片假名
        _ => false,
    };

    private static bool StartsWithCjk(string text)
    {
        foreach (TextElementRange element in GraphemeSplitter.Enumerate(text))
        {
            if (IsWhitespaceElement(element.Text) || IsPunctuationElement(element.Text))
            {
                continue;
            }

            return IsCjkCodePoint(FirstCodePoint(element.Text));
        }

        return false;
    }

    private static bool EndsWithCjk(string text)
    {
        int lastContentCodePoint = -1;
        foreach (TextElementRange element in GraphemeSplitter.Enumerate(text))
        {
            if (IsWhitespaceElement(element.Text) || IsPunctuationElement(element.Text))
            {
                continue;
            }

            lastContentCodePoint = FirstCodePoint(element.Text);
        }

        return lastContentCodePoint >= 0 && IsCjkCodePoint(lastContentCodePoint);
    }

    private static int FirstCodePoint(string element) =>
        char.IsSurrogatePair(element, 0) ? char.ConvertToUtf32(element, 0) : element[0];
}
