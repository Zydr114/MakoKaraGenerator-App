using MakoKaraGenerator.Core.Lyrics.Import;
using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>文档级按规则切分的汇总。</summary>
/// <param name="Document">切分后的文档；没有变化时是原实例。</param>
/// <param name="ChangedLineCount">实际发生变化的行数。</param>
/// <param name="CreatedCount">新建的元素总数。</param>
/// <param name="EstimatedCount">其中时间由软件推导的元素个数。</param>
public sealed record LyricsTokenSplitSummary(
    LyricsDocument Document,
    int ChangedLineCount,
    int CreatedCount,
    int EstimatedCount);

/// <summary>
/// 对整篇歌词（或其中若干行）按规则重新切分。
/// </summary>
/// <remarks>
/// 导入后按规则切分、界面上的「应用到全部行」都用这一个入口，
/// 保证两条路径的语义与计数口径一致（ADR-0046）。
/// </remarks>
public static class LyricsDocumentTokenSplitter
{
    /// <summary>按规则切分若干行。</summary>
    /// <param name="document">歌词文档。</param>
    /// <param name="rule">切分规则。</param>
    /// <param name="estimateTimes">是否按比例估算时间。</param>
    /// <param name="lineIndices">要处理的行下标；为 <see langword="null" /> 时处理全部行。</param>
    /// <param name="nextTokenId">新建元素的标识工厂；为 <see langword="null" /> 时按文档现有编号续排。</param>
    /// <returns>切分结果与计数。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> 为 <see langword="null" /> 时抛出。</exception>
    public static LyricsTokenSplitSummary Apply(LyricsDocument document, TokenSplitRule rule, bool estimateTimes,
        IReadOnlyCollection<int>? lineIndices = null, Func<string>? nextTokenId = null) =>
        Apply(document, rule, estimateTimes, lineIndices, nextTokenId, null);

    /// <summary>按原规则重切，在共享切分算法分配前预留累计逻辑工作。</summary>
    /// <param name="document">原文档。</param>
    /// <param name="rule">原切分规则。</param>
    /// <param name="estimateTimes">是否估算。</param>
    /// <param name="lineIndices">选定行或全部。</param>
    /// <param name="nextTokenId">原标识分配器或默认。</param>
    /// <param name="preparationGuard">可选累计准备配额。</param>
    public static LyricsTokenSplitSummary Apply(
        LyricsDocument document,
        TokenSplitRule rule,
        bool estimateTimes,
        IReadOnlyCollection<int>? lineIndices,
        Func<string>? nextTokenId,
        LyricsImportPreparationGuard? preparationGuard)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (rule == TokenSplitRule.None || document.Lines.Length == 0)
        {
            return new LyricsTokenSplitSummary(document, 0, 0, 0);
        }

        preparationGuard?.Reserve(128L + 32L * document.Lines.Length);
        Func<string> factory = nextTokenId ?? LyricsTokenIds.CreateFactory(document);
        ImmutableArray<LyricLine>.Builder lines = document.Lines.ToBuilder();
        int changed = 0;
        int created = 0;
        int estimated = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lineIndices is not null && !lineIndices.Contains(i))
            {
                continue;
            }

            preparationGuard?.Reserve(128L + 32L * lines[i].Tokens.Length + 2L * lines[i].Text.Length);
            LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(lines[i], rule, estimateTimes, factory,
                preparationGuard is null ? null : () => preparationGuard.Reserve(1024));

            if (ReferenceEquals(result.Line, lines[i]))
            {
                continue;
            }

            lines[i] = result.Line;
            changed++;
            created += result.CreatedCount;
            estimated += result.EstimatedCount;
        }

        return changed == 0
            ? new LyricsTokenSplitSummary(document, 0, 0, 0)
            : new LyricsTokenSplitSummary(document with { Lines = lines.ToImmutable() }, changed, created, estimated);
    }
}
