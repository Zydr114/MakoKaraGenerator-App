using System.Collections.Immutable;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>全文快编行级输入条目：可选绑定既有稳定行 ID。</summary>
public readonly record struct LyricTextDiffLineInput(string Text, string? SourceLineId = null);

/// <summary>全文快编分支级 Diff 应用结果。</summary>
public sealed record LyricBranchTextDiffResult(
    LyricBranch Branch,
    int PreservedTokenCount,
    int ResetTokenCount);

/// <summary>
/// 面向「全文快编」的无损 LCS 文本对齐器：
/// 行级优先采用显式 <c>SourceLineId</c> 或自动 LCS 匹配；行内采用字素级 LCS，
/// 完整落在未改动连续区间内的既有词元原样保留（含稳定 ID、时间、状态、PhraseEndHint、关联注音与样式覆写），
/// 仅对变更或新增片段按 <see cref="TokenSplitRule.Mixed"/> 重新切分为未定时词元。
/// </summary>
public static class LyricTextDiffAligner
{
    private const int MaxGraphemeLcsCells = 250_000;
    private const int MaxLineLcsCells = 250_000;

    /// <summary>对单行执行字素级 LCS 无损重建。</summary>
    public static (LyricLine Line, int PreservedCount, int ResetCount) AlignLine(
        LyricLine oldLine,
        string newText,
        Func<string> nextTokenId)
    {
        ArgumentNullException.ThrowIfNull(oldLine);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(nextTokenId);

        if (string.Equals(oldLine.Text, newText, StringComparison.Ordinal))
            return (oldLine, oldLine.Tokens.Length, 0);

        if (newText.Length == 0)
        {
            LyricLine emptied = RubySegmentEditor.RemoveDetachedGroups(oldLine with
            {
                Text = string.Empty,
                Tokens = [],
            });
            return (emptied, 0, 0);
        }

        IReadOnlyList<TextElementRange> oldElements = GraphemeSplitter.Split(oldLine.Text);
        IReadOnlyList<TextElementRange> newElements = GraphemeSplitter.Split(newText);

        // oldToNew[oldIdx] = matched newIdx, or -1 if unmatched.
        int[] oldToNew = ComputeElementMatches(oldLine.Text, oldElements, newText, newElements);

        var placedTokens = new (int NewStart, int NewEnd, LyricToken Token)[oldLine.Tokens.Length];
        int placedCount = 0;
        int oldCursor = 0;

        foreach (LyricToken oldToken in oldLine.Tokens)
        {
            int graphemeCount = GraphemeSplitter.Split(oldToken.Text).Count;
            if (graphemeCount == 0) continue;

            int oldStart = oldCursor;
            int oldEnd = oldStart + graphemeCount;
            oldCursor = oldEnd;

            if (oldEnd > oldElements.Count) break;

            int firstMapped = oldToNew[oldStart];
            if (firstMapped < 0) continue;

            bool contiguous = true;
            for (int offset = 1; offset < graphemeCount; offset++)
            {
                if (oldToNew[oldStart + offset] != firstMapped + offset)
                {
                    contiguous = false;
                    break;
                }
            }

            if (contiguous)
            {
                placedTokens[placedCount++] = (firstMapped, firstMapped + graphemeCount, oldToken);
            }
        }

        var builder = ImmutableArray.CreateBuilder<LyricToken>();
        int newCursor = 0;
        int preserved = 0;
        int reset = 0;

        for (int p = 0; p < placedCount; p++)
        {
            (int matchStart, int matchEnd, LyricToken token) = placedTokens[p];
            if (matchStart < newCursor)
                continue; // safety guard against any overlapping placement

            if (matchStart > newCursor)
            {
                reset += AppendSplitSpan(builder, newText, newElements, newCursor, matchStart, nextTokenId);
            }

            builder.Add(token);
            preserved++;
            newCursor = matchEnd;
        }

        if (newCursor < newElements.Count)
        {
            reset += AppendSplitSpan(builder, newText, newElements, newCursor, newElements.Count, nextTokenId);
        }

        LyricLine updated = RubySegmentEditor.RemoveDetachedGroups(oldLine with
        {
            Text = newText,
            Tokens = builder.ToImmutable(),
        });
        return (updated, preserved, reset);
    }

    /// <summary>对整条歌词分支应用全文快编行序列，无损保留未改动行与词元。</summary>
    public static LyricBranchTextDiffResult ApplyToBranch(
        LyricBranch branch,
        IReadOnlyList<LyricTextDiffLineInput> targetLines,
        Func<string> nextLineId,
        Func<string> nextTokenId)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(targetLines);
        ArgumentNullException.ThrowIfNull(nextLineId);
        ArgumentNullException.ThrowIfNull(nextTokenId);

        int oldCount = branch.Lines.Length;
        int newCount = targetLines.Count;

        var oldById = new Dictionary<string, int>(oldCount, StringComparer.Ordinal);
        for (int i = 0; i < oldCount; i++)
            oldById[branch.Lines[i].Id] = i;

        int[] matchedOldIndex = new int[newCount];
        Array.Fill(matchedOldIndex, -1);
        bool[] usedOld = new bool[oldCount];

        // 1. Honor explicit SourceLineId mappings first.
        bool anyUnmapped = false;
        for (int j = 0; j < newCount; j++)
        {
            if (targetLines[j].SourceLineId is { } sourceId && oldById.TryGetValue(sourceId, out int idx) && !usedOld[idx])
            {
                matchedOldIndex[j] = idx;
                usedOld[idx] = true;
            }
            else if (targetLines[j].SourceLineId is null)
            {
                anyUnmapped = true;
            }
        }

        // 2. For entries without explicit SourceLineId, run line-level LCS on remaining unmatched lines.
        if (anyUnmapped)
        {
            var freeOld = new List<int>(oldCount);
            for (int i = 0; i < oldCount; i++) if (!usedOld[i]) freeOld.Add(i);

            var freeNew = new List<int>(newCount);
            for (int j = 0; j < newCount; j++) if (matchedOldIndex[j] < 0 && targetLines[j].SourceLineId is null) freeNew.Add(j);

            if (freeOld.Count > 0 && freeNew.Count > 0)
            {
                var anchors = ComputeExactLineLcs(branch.Lines, freeOld, targetLines, freeNew);
                // Pair exact LCS anchors, plus in-order 1-to-1 pairing inside gaps between anchors
                // so editing a single character on a line still preserves that line's identity and tokens.
                int prevOldPos = 0;
                int prevNewPos = 0;
                foreach ((int oldPos, int newPos) in anchors)
                {
                    PairGap(prevOldPos, oldPos, prevNewPos, newPos, freeOld, freeNew, matchedOldIndex, usedOld);
                    int oldIdx = freeOld[oldPos];
                    int newIdx = freeNew[newPos];
                    matchedOldIndex[newIdx] = oldIdx;
                    usedOld[oldIdx] = true;
                    prevOldPos = oldPos + 1;
                    prevNewPos = newPos + 1;
                }
                PairGap(prevOldPos, freeOld.Count, prevNewPos, freeNew.Count, freeOld, freeNew, matchedOldIndex, usedOld);
            }
        }

        // 3. Build resulting branch lines.
        var resultLines = ImmutableArray.CreateBuilder<LyricLine>(newCount);
        int totalPreserved = 0;
        int totalReset = 0;

        for (int j = 0; j < newCount; j++)
        {
            string text = targetLines[j].Text ?? string.Empty;
            int oldIdx = matchedOldIndex[j];
            if (oldIdx >= 0)
            {
                (LyricLine aligned, int preserved, int reset) = AlignLine(branch.Lines[oldIdx], text, nextTokenId);
                resultLines.Add(aligned);
                totalPreserved += preserved;
                totalReset += reset;
            }
            else
            {
                LyricLine created = CreateNewLine(nextLineId(), text, nextTokenId, out int createdTokens);
                resultLines.Add(created);
                totalReset += createdTokens;
            }
        }

        LyricBranch updatedBranch = branch with { Lines = resultLines.ToImmutable() };
        return new LyricBranchTextDiffResult(updatedBranch, totalPreserved, totalReset);
    }

    private static LyricLine CreateNewLine(string lineId, string text, Func<string> nextTokenId, out int tokenCount)
    {
        if (text.Length == 0)
        {
            tokenCount = 0;
            return LyricLine.Create(lineId, string.Empty);
        }

        IReadOnlyList<string> segments = LyricTokenSplitter.Split(text, TokenSplitRule.Mixed);
        var tokens = ImmutableArray.CreateBuilder<LyricToken>(segments.Count);
        foreach (string seg in segments)
            tokens.Add(LyricToken.Create(nextTokenId(), seg));
        tokenCount = tokens.Count;
        return new LyricLine
        {
            Id = lineId,
            Text = text,
            Time = null,
            TimingStatus = TimingStatus.Missing,
            Tokens = tokens.ToImmutable(),
        };
    }

    private static int AppendSplitSpan(
        ImmutableArray<LyricToken>.Builder builder,
        string newText,
        IReadOnlyList<TextElementRange> newElements,
        int startElementIndex,
        int endElementIndex,
        Func<string> nextTokenId)
    {
        int startChar = newElements[startElementIndex].Start;
        int endChar = newElements[endElementIndex - 1].End;
        string spanText = newText[startChar..endChar];
        IReadOnlyList<string> pieces = LyricTokenSplitter.Split(spanText, TokenSplitRule.Mixed);
        foreach (string piece in pieces)
            builder.Add(LyricToken.Create(nextTokenId(), piece));
        return pieces.Count;
    }

    private static int[] ComputeElementMatches(
        string oldText,
        IReadOnlyList<TextElementRange> oldElements,
        string newText,
        IReadOnlyList<TextElementRange> newElements)
    {
        int m = oldElements.Count;
        int n = newElements.Count;
        int[] oldToNew = new int[m];
        Array.Fill(oldToNew, -1);
        if (m == 0 || n == 0) return oldToNew;

        // Trim common prefix and suffix first so DP only runs on the changed core.
        int prefix = 0;
        int limit = Math.Min(m, n);
        while (prefix < limit && ElementEquals(oldText, oldElements[prefix], newText, newElements[prefix]))
        {
            oldToNew[prefix] = prefix;
            prefix++;
        }

        int suffix = 0;
        while (suffix < limit - prefix
            && ElementEquals(oldText, oldElements[m - 1 - suffix], newText, newElements[n - 1 - suffix]))
        {
            oldToNew[m - 1 - suffix] = n - 1 - suffix;
            suffix++;
        }

        int coreM = m - prefix - suffix;
        int coreN = n - prefix - suffix;
        if (coreM <= 0 || coreN <= 0 || (long)coreM * coreN > MaxGraphemeLcsCells)
            return oldToNew;

        int[,] dp = new int[coreM + 1, coreN + 1];
        for (int i = coreM - 1; i >= 0; i--)
        {
            for (int j = coreN - 1; j >= 0; j--)
            {
                if (ElementEquals(oldText, oldElements[prefix + i], newText, newElements[prefix + j]))
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        int ci = 0, cj = 0;
        while (ci < coreM && cj < coreN)
        {
            if (ElementEquals(oldText, oldElements[prefix + ci], newText, newElements[prefix + cj]))
            {
                oldToNew[prefix + ci] = prefix + cj;
                ci++;
                cj++;
            }
            else if (dp[ci + 1, cj] >= dp[ci, cj + 1])
            {
                ci++;
            }
            else
            {
                cj++;
            }
        }

        return oldToNew;
    }

    private static bool ElementEquals(string a, TextElementRange ra, string b, TextElementRange rb) =>
        ra.Length == rb.Length && a.AsSpan(ra.Start, ra.Length).SequenceEqual(b.AsSpan(rb.Start, rb.Length));

    private static List<(int OldPos, int NewPos)> ComputeExactLineLcs(
        ImmutableArray<LyricLine> oldLines,
        List<int> freeOld,
        IReadOnlyList<LyricTextDiffLineInput> newLines,
        List<int> freeNew)
    {
        int m = freeOld.Count;
        int n = freeNew.Count;
        var anchors = new List<(int, int)>();
        if (m == 0 || n == 0) return anchors;

        int prefix = 0;
        int limit = Math.Min(m, n);
        while (prefix < limit && string.Equals(oldLines[freeOld[prefix]].Text, newLines[freeNew[prefix]].Text, StringComparison.Ordinal))
        {
            anchors.Add((prefix, prefix));
            prefix++;
        }

        int suffix = 0;
        var suffixAnchors = new List<(int, int)>();
        while (suffix < limit - prefix
            && string.Equals(oldLines[freeOld[m - 1 - suffix]].Text, newLines[freeNew[n - 1 - suffix]].Text, StringComparison.Ordinal))
        {
            suffixAnchors.Add((m - 1 - suffix, n - 1 - suffix));
            suffix++;
        }

        int coreM = m - prefix - suffix;
        int coreN = n - prefix - suffix;
        if (coreM > 0 && coreN > 0 && (long)coreM * coreN <= MaxLineLcsCells)
        {
            int[,] dp = new int[coreM + 1, coreN + 1];
            for (int i = coreM - 1; i >= 0; i--)
            {
                for (int j = coreN - 1; j >= 0; j--)
                {
                    if (string.Equals(oldLines[freeOld[prefix + i]].Text, newLines[freeNew[prefix + j]].Text, StringComparison.Ordinal))
                        dp[i, j] = dp[i + 1, j + 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            int ci = 0, cj = 0;
            while (ci < coreM && cj < coreN)
            {
                if (string.Equals(oldLines[freeOld[prefix + ci]].Text, newLines[freeNew[prefix + cj]].Text, StringComparison.Ordinal))
                {
                    anchors.Add((prefix + ci, prefix + cj));
                    ci++;
                    cj++;
                }
                else if (dp[ci + 1, cj] >= dp[ci, cj + 1])
                {
                    ci++;
                }
                else
                {
                    cj++;
                }
            }
        }

        for (int k = suffixAnchors.Count - 1; k >= 0; k--)
            anchors.Add(suffixAnchors[k]);
        return anchors;
    }

    private static void PairGap(
        int oldStart, int oldEnd, int newStart, int newEnd,
        List<int> freeOld, List<int> freeNew,
        int[] matchedOldIndex, bool[] usedOld)
    {
        int count = Math.Min(oldEnd - oldStart, newEnd - newStart);
        for (int k = 0; k < count; k++)
        {
            int oldIdx = freeOld[oldStart + k];
            int newIdx = freeNew[newStart + k];
            if (!usedOld[oldIdx] && matchedOldIndex[newIdx] < 0)
            {
                matchedOldIndex[newIdx] = oldIdx;
                usedOld[oldIdx] = true;
            }
        }
    }
}
