using System.Collections.Immutable;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 改一行文本之后重建逐字元素的结果。
/// </summary>
public sealed record LyricTextResplitResult
{
    /// <summary>新的元素列表，拼接后应当正好等于新的行文本。</summary>
    public required ImmutableArray<LyricToken> Tokens { get; init; }

    /// <summary>保留了原有时间与状态（即"原样搬过来"）的元素个数。</summary>
    public required int PreservedCount { get; init; }

    /// <summary>因为落在改动区间而变成"未定时"的元素个数。</summary>
    public required int ResetCount { get; init; }
}

/// <summary>
/// 行文本变化时重建该行的逐字元素。
/// </summary>
/// <remarks>
/// <para>
/// 这是 <c>EDITOR-03</c>（输入和编辑歌词行）里最关键的一步：用户改一个错字，
/// 不该把已经辛苦打好的逐字时间全丢掉，也不该让时间轴错位到别的字上。
/// 规则只有两条：
/// </para>
/// <list type="number">
///   <item>行文本与元素文本必须保持一致：所有元素文本拼接后**正好等于**新的行文本；</item>
///   <item>时间只在<b>没被改动的位置</b>上继承——正文的公共前缀与公共后缀里、
///         且整体落在未改动区域内的旧元素原样保留（含标识、时间与状态）；
///         改动区间一律变成"未定时"的单个文本元素。</item>
/// </list>
/// <para>
/// 之所以按文本对齐而不是按下标对齐：在下标对齐下，往中间插一个字会把后面每个元素的
/// 时间整体错位一格，那比"重新打点"更糟——错位的时间看着是对的，实际全错。
/// </para>
/// <para>
/// 本类不生成标识（标识由调用方提供的工厂给出），
/// 这样 Core 不必知道项目用什么编号策略，也便于测试断言。
/// </para>
/// </remarks>
public static class LyricTextResplitter
{
    /// <summary>按新文本重建一行的逐字元素。</summary>
    /// <param name="oldText">原来的行文本。</param>
    /// <param name="oldTokens">原来的元素列表。</param>
    /// <param name="newText">新的行文本。</param>
    /// <param name="newTokenId">为新增元素生成标识的工厂。</param>
    /// <returns>重建结果。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    public static LyricTextResplitResult Resplit(
        string oldText,
        ImmutableArray<LyricToken> oldTokens,
        string newText,
        Func<string> newTokenId)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(newTokenId);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return new LyricTextResplitResult
            {
                Tokens = oldTokens,
                PreservedCount = oldTokens.Length,
                ResetCount = 0,
            };
        }

        IReadOnlyList<TextElementRange> newElements = GraphemeSplitter.Split(newText);
        IReadOnlyList<TextElementRange> oldElements = GraphemeSplitter.Split(oldText);

        (int prefix, int suffix) = SharedEdges(oldElements, newElements);

        // 未改动区间在新文本里覆盖到哪个下标（不含）。
        int unchangedEnd = newElements.Count - suffix;
        int oldSuffixStart = oldElements.Count - suffix;

        // 按"新文本的每个元素位置"装配：先把能整体搬过来的旧元素放到位，空缺一律补成未定时。
        // 用槽位而不是分三段拼接，是因为跨过未改动区间边界的旧元素既不能整体保留、
        // 也绝不能把它落在未改动区间里的那些字一起丢掉——分三段拼接时正是这样漏掉字的。
        LyricToken?[] slots = new LyricToken?[newElements.Count];

        // 一个旧元素可能横跨多个文本元素（合并过的"夜空"占两个位置），
        // 因此"哪些位置已经被搬过来的元素覆盖"必须单独记，不能只看槽位是否为空。
        bool[] covered = new bool[newElements.Count];

        int preserved = 0;
        int oldIndex = 0;

        foreach (LyricToken oldToken in oldTokens)
        {
            int tokenGraphemes = CountGraphemes(oldToken.Text);

            if (tokenGraphemes == 0)
            {
                continue;
            }

            int start = oldIndex;
            int end = start + tokenGraphemes;
            oldIndex = end;

            if (end <= prefix)
            {
                // 公共前缀里，新文本与旧文本逐元素一一对应，位置不变。
                slots[start] = oldToken;

                for (int i = start; i < end; i++)
                {
                    covered[i] = true;
                }

                preserved++;
            }
            else if (start >= oldSuffixStart)
            {
                // 公共后缀里，位置要按"后缀在两侧各自的起点"平移。
                int target = unchangedEnd + (start - oldSuffixStart);
                slots[target] = oldToken;

                for (int i = 0; i < tokenGraphemes; i++)
                {
                    covered[target + i] = true;
                }

                preserved++;
            }
        }

        int reset = 0;

        for (int i = 0; i < slots.Length; i++)
        {
            if (covered[i])
            {
                continue;
            }

            slots[i] = LyricToken.Create(
                newTokenId(),
                newElements[i].Text,
                time: null,
                timingStatus: TimingStatus.Missing);

            reset++;
        }

        return new LyricTextResplitResult
        {
            // 槽位只是"放在哪个位置"的装配图：多文本元素占住起始位置、后续位置靠 covered 标记，
            // 因此投影时要跳过空洞（那些位置属于前面那个元素）。
            Tokens = [.. slots.Where(static token => token is not null).Select(static token => token!)],
            PreservedCount = preserved,
            ResetCount = reset,
        };
    }

    /// <summary>求两侧文本"按文本元素计的"公共前缀与公共后缀长度。</summary>
    private static (int Prefix, int Suffix) SharedEdges(
        IReadOnlyList<TextElementRange> oldElements,
        IReadOnlyList<TextElementRange> newElements)
    {
        int limit = Math.Min(oldElements.Count, newElements.Count);
        int prefix = 0;

        while (prefix < limit
            && string.Equals(oldElements[prefix].Text, newElements[prefix].Text, StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;

        while (suffix < limit - prefix
            && string.Equals(
                oldElements[oldElements.Count - 1 - suffix].Text,
                newElements[newElements.Count - 1 - suffix].Text,
                StringComparison.Ordinal))
        {
            suffix++;
        }

        return (prefix, suffix);
    }

    /// <summary>数一个元素文本里包含多少个文本元素。</summary>
    private static int CountGraphemes(string text) =>
        text.Length == 0 ? 0 : GraphemeSplitter.Split(text).Count;
}
