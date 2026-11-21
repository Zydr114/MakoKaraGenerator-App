using System.Collections.Immutable;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>一行按规则重切的结果。</summary>
/// <param name="Line">重切后的行；与传入实例相同表示没有变化。</param>
/// <param name="PreservedCount">沿用原时间的元素个数（含完全没变、以及"第一段继承原区间"）。</param>
/// <param name="CreatedCount">新建的元素个数。</param>
/// <param name="EstimatedCount">其中时间由软件推导（按比例分配或合并多个来源）的个数。</param>
public sealed record LyricTokenSplitResult(
    LyricLine Line,
    int PreservedCount,
    int CreatedCount,
    int EstimatedCount);

/// <summary>
/// 按规则重切一行的文本元素，并尽可能沿用原来的时间。
/// </summary>
/// <remarks>
/// <para>
/// 语义（ADR-0046）：
/// </para>
/// <list type="number">
///   <item>与旧元素文本<b>完全相同</b>的段：原样沿用该元素（标识、时间、状态都不动）；</item>
///   <item>某个旧元素被切开时，默认（不估算）<b>第一段继承原区间与状态</b>，其余段没有时间（<see cref="TimingStatus.Missing" />）；
///         这条规则保证"切分不动已确认的时间"。</item>
///   <item>一个段横跨多个旧元素时（合并）：只有这些元素都有时间才取它们的<b>并集</b>，
///         状态取"都相同就用它、否则记 <see cref="TimingStatus.Estimated" />"；只要有一个没有时间，这一段就是未定时。</item>
///   <item>勾选估算时，按各段文本长度把来源区间按比例切开，全部标 <see cref="TimingStatus.Estimated" />。</item>
/// </list>
/// <para>
/// 行文本与行区间本身<b>从不</b>被本方法改动。
/// </para>
/// </remarks>
public static class LyricLineTokenSplitter
{
    /// <summary>按规则重切一行。</summary>
    /// <param name="line">要重切的行。</param>
    /// <param name="rule">切分规则；<see cref="TokenSplitRule.None" /> 时原样返回。</param>
    /// <param name="estimateTimes">是否按比例估算时间（默认 <see langword="false" />）。</param>
    /// <param name="nextTokenId">新建元素的标识工厂。</param>
    /// <returns>重切结果；没有实际变化时 <see cref="LyricTokenSplitResult.Line" /> 是原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line" /> 或 <paramref name="nextTokenId" /> 为 <see langword="null" /> 时抛出。</exception>
    public static LyricTokenSplitResult Apply(
        LyricLine line,
        TokenSplitRule rule,
        bool estimateTimes,
        Func<string> nextTokenId) => Apply(line, rule, estimateTimes, nextTokenId, null);

    /// <summary>按同一规则重切，段分配前可由宿主观察并中止有界准备。</summary>
    /// <param name="line">原始完整行。</param>
    /// <param name="rule">既有规则。</param>
    /// <param name="estimateTimes">是否显式估算时间。</param>
    /// <param name="nextTokenId">真实新元素标识分配器。</param>
    /// <param name="onCreatingSegment">可选的真实段分配观察器。</param>
    /// <returns>原领域切分及计数结果。</returns>
    public static LyricTokenSplitResult Apply(LyricLine line, TokenSplitRule rule, bool estimateTimes,
        Func<string> nextTokenId, Action? onCreatingSegment)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(nextTokenId);

        if (rule == TokenSplitRule.None)
        {
            return new LyricTokenSplitResult(line, line.Tokens.Length, 0, 0);
        }

        return Apply(line, LyricTokenSplitter.Split(line.Text, rule, onCreatingSegment), estimateTimes, nextTokenId);
    }

    /// <summary>
    /// 按给定的段重切一行（段可以来自规则，也可以来自界面上手工调整过的边界）。
    /// </summary>
    /// <param name="line">要重切的行。</param>
    /// <param name="segments">各段文本；依次拼接必须等于行文本，否则原样返回。</param>
    /// <param name="estimateTimes">是否按比例估算时间。</param>
    /// <param name="nextTokenId">新建元素的标识工厂。</param>
    /// <returns>重切结果；没有实际变化时 <see cref="LyricTokenSplitResult.Line" /> 是原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line" /> 或 <paramref name="nextTokenId" /> 为 <see langword="null" /> 时抛出。</exception>
    public static LyricTokenSplitResult Apply(
        LyricLine line,
        ImmutableArray<string> segments,
        bool estimateTimes,
        Func<string> nextTokenId)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(nextTokenId);

        ImmutableArray<LyricToken> old = line.Tokens;

        if (segments.IsDefaultOrEmpty || !string.Equals(string.Concat(segments), line.Text, StringComparison.Ordinal))
        {
            return new LyricTokenSplitResult(line, old.Length, 0, 0);
        }

        if (old.Length == 0 && segments.Length <= 1)
        {
            return new LyricTokenSplitResult(line, 0, 0, 0);
        }

        int[] oldStarts = new int[old.Length];
        int[] oldEnds = new int[old.Length];
        int cursor = 0;
        bool aligned = true;

        for (int i = 0; i < old.Length; i++)
        {
            oldStarts[i] = cursor;
            oldEnds[i] = cursor + old[i].Text.Length;
            cursor = oldEnds[i];
        }

        if (cursor != line.Text.Length)
        {
            // 元素文本拼起来不等于行文本：这行数据本身不自洽，只能按"没有旧元素"处理。
            aligned = false;
        }

        int[] firstOwner = new int[segments.Length];
        int[] lastOwner = new int[segments.Length];
        int[] offsets = new int[segments.Length];
        bool[] exact = new bool[segments.Length];
        bool unchanged = aligned && segments.Length == old.Length;
        cursor = 0;
        int scan = 0;

        for (int i = 0; i < segments.Length; i++)
        {
            int start = cursor;
            int end = cursor + segments[i].Length;
            firstOwner[i] = -1;
            lastOwner[i] = -1;
            offsets[i] = 0;

            if (aligned)
            {
                while (scan < old.Length && oldEnds[scan] <= start)
                {
                    scan++;
                }

                if (scan >= old.Length || oldStarts[scan] > start)
                {
                    aligned = false;
                }
                else
                {
                    firstOwner[i] = scan;
                    offsets[i] = start - oldStarts[scan];

                    int last = scan;
                    while (last + 1 < old.Length && oldEnds[last] < end)
                    {
                        last++;
                    }

                    lastOwner[i] = oldEnds[last] >= end ? last : -1;
                    exact[i] = offsets[i] == 0
                        && segments[i].Length == old[scan].Text.Length
                        && lastOwner[i] == firstOwner[i];
                }
            }

            unchanged &= exact[i];
            cursor = end;
        }

        if (unchanged && segments.Length == old.Length)
        {
            return new LyricTokenSplitResult(line, old.Length, 0, 0);
        }

        // 估算用的权重（纯空白与纯标点不参与分配，初始始终保持无时间）：
        // ① 同组（同一个旧元素 / “没有归属”的一组）里的累计实义文本长度；
        // ② 整行统一的累计实义文本长度（当这行里有实义元素本来就没有时间时，只能整行一起估）。
        bool[] isNonContent = new bool[segments.Length];
        Dictionary<int, long> totals = [];
        Dictionary<int, long> running = [];
        long[] weightBefore = new long[segments.Length];
        long[] weightAfter = new long[segments.Length];
        long[] globalBefore = new long[segments.Length];
        long[] globalAfter = new long[segments.Length];
        long globalTotal = 0L;

        for (int i = 0; i < segments.Length; i++)
        {
            isNonContent[i] = LyricTokenSemantics.Classify(segments[i]) != LyricTokenKind.Content;
            long segmentWeight = isNonContent[i] ? 0L : segments[i].Length;

            int key = firstOwner[i];
            long before = running.GetValueOrDefault(key);
            weightBefore[i] = before;
            weightAfter[i] = before + segmentWeight;
            running[key] = weightAfter[i];
            totals[key] = totals.GetValueOrDefault(key) + segmentWeight;

            globalBefore[i] = globalTotal;
            globalTotal = checked(globalTotal + segmentWeight);
            globalAfter[i] = globalTotal;
        }

        // 行里有实义元素没时间（例如从只有行级时间的文件导入）⇒ 用行区间 + 全文实义长度统一分配。
        bool estimateOverWholeLine = line.Time is not null
            && (old.Length == 0
                || !old.All(token => token.Time is not null
                    || LyricTokenSemantics.Classify(token.Text) != LyricTokenKind.Content));

        ImmutableArray<LyricToken>.Builder tokens = ImmutableArray.CreateBuilder<LyricToken>(segments.Length);
        HashSet<int> inheritedSources = [];
        int preserved = 0;
        int created = 0;
        int estimated = 0;
        cursor = 0;

        for (int i = 0; i < segments.Length; i++)
        {
            string text = segments[i];
            int first = firstOwner[i];
            int last = lastOwner[i];
            int start = cursor;
            cursor += text.Length;

            if (exact[i] && first >= 0)
            {
                tokens.Add(old[first]);
                inheritedSources.Add(first);
                preserved++;
                continue;
            }

            // 新切出的纯空白与纯标点元素初始一律保持无时间（Time = null, TimingStatus.Missing）。
            if (isNonContent[i])
            {
                tokens.Add(LyricToken.Create(nextTokenId(), text));
                created++;
                continue;
            }

            // 合并：段横跨多个旧元素。
            if (first >= 0 && last > first)
            {
                // 普通行级格式导入后，旧元素通常只有文本而没有逐字时间。
                // 用户显式勾选估算时仍应使用整行区间；不能因为进入“合并多个旧元素”
                // 分支就退回 Missing。
                if (estimateTimes && estimateOverWholeLine && line.Time is { } wholeLine && globalTotal > 0)
                {
                    (long s, long e) = ProRate(wholeLine, globalBefore[i], globalAfter[i], globalTotal);
                    tokens.Add(LyricToken.Create(nextTokenId(), text, TimeRange.Create(s, e), TimingStatus.Estimated));
                    created++;
                    estimated++;
                    continue;
                }

                (TimeRange? unionStart, TimingStatus status) = MergeRange(old, first, last);

                if (estimateTimes && unionStart is { } merged && totals.GetValueOrDefault(first) > 0)
                {
                    (long s, long e) = ProRate(merged, weightBefore[i], weightAfter[i], totals[first]);
                    tokens.Add(LyricToken.Create(nextTokenId(), text, TimeRange.Create(s, e), TimingStatus.Estimated));
                    created++;
                    estimated++;
                }
                else if (unionStart is { } value)
                {
                    tokens.Add(LyricToken.Create(nextTokenId(), text, value, status));
                    created++;
                }
                else
                {
                    tokens.Add(LyricToken.Create(nextTokenId(), text));
                    created++;
                }

                continue;
            }

            LyricToken? source = first >= 0 ? old[first] : null;

            if (!estimateTimes)
            {
                // 默认策略：来源的首个实义段继承原区间与状态（不猜、也不改已确认的时间），其余段未定时。
                if (source is not null && source.Time is not null && inheritedSources.Add(first))
                {
                    tokens.Add(source with { Text = text });
                    preserved++;
                }
                else
                {
                    tokens.Add(LyricToken.Create(nextTokenId(), text));
                    created++;
                }

                continue;
            }

            // 估算策略：按实义文本长度把来源区间切开；来源没有时间就退回行区间。
            TimeRange? interval = estimateOverWholeLine ? line.Time : source?.Time ?? line.Time;

            if (interval is { } range)
            {
                int key = estimateOverWholeLine ? -1 : first >= 0 ? first : -1;
                long before = estimateOverWholeLine ? globalBefore[i] : weightBefore[i];
                long after = estimateOverWholeLine ? globalAfter[i] : weightAfter[i];
                long total = estimateOverWholeLine ? globalTotal : totals[key];
                if (total > 0)
                {
                    (long s, long e) = ProRate(range, before, after, total);
                    tokens.Add(LyricToken.Create(nextTokenId(), text, TimeRange.Create(s, e), TimingStatus.Estimated));
                    created++;
                    estimated++;
                }
                else
                {
                    tokens.Add(LyricToken.Create(nextTokenId(), text));
                    created++;
                }
            }
            else
            {
                tokens.Add(LyricToken.Create(nextTokenId(), text));
                created++;
            }
        }

        LyricLine rebuilt = RubySegmentEditor.RemoveDetachedGroups(line with { Tokens = tokens.ToImmutable() });

        return new LyricTokenSplitResult(rebuilt, preserved, created, estimated);
    }

    /// <summary>取若干相邻旧元素区间的并集；只要有一个没有时间就没有结果。</summary>
    private static (TimeRange? Range, TimingStatus Status) MergeRange(ImmutableArray<LyricToken> old, int first, int last)
    {
        TimeRange? range = null;
        TimingStatus status = old[first].TimingStatus;

        for (int i = first; i <= last; i++)
        {
            if (old[i].Time is { IsValid: false } invalid)
            {
                throw new ArgumentOutOfRangeException(nameof(old), invalid, "重切来源元素的时间区间必须有效。");
            }
            if (old[i].Time is not { } time)
            {
                return (null, TimingStatus.Missing);
            }

            range = range is { } current
                ? TimeRange.Create(Math.Min(current.StartUs, time.StartUs), Math.Max(current.EndUs, time.EndUs))
                : time;

            if (old[i].TimingStatus != status)
            {
                status = TimingStatus.Estimated;
            }
        }

        return (range, status);
    }

    /// <summary>按权重把区间切开（最后一段的终点严格等于区间终点，不留取整误差）。</summary>
    private static (long StartUs, long EndUs) ProRate(TimeRange range, long before, long after, long total)
    {
        if (!range.TryGetDurationUs(out long durationUs))
            throw new ArgumentOutOfRangeException(nameof(range), range, "估算来源时间区间必须有效。");
        if (total <= 0)
        {
            return (range.StartUs, range.EndUs);
        }

        long start = checked(range.StartUs + RoundRatio(durationUs, before, total));
        long end = after >= total
            ? range.EndUs
            : checked(range.StartUs + RoundRatio(durationUs, after, total));

        start = Math.Clamp(start, range.StartUs, range.EndUs - 1);
        end = Math.Clamp(end, start + 1, range.EndUs);

        return (start, end);
    }

    private static long RoundRatio(long value, long numerator, long denominator)
    {
        if (numerator <= 0 || value <= 0) return 0;
        Int128 product = (Int128)value * numerator;
        Int128 quotient = product / denominator;
        Int128 remainder = product % denominator;
        Int128 twiceRemainder = remainder * 2;
        if (twiceRemainder > denominator || twiceRemainder == denominator && (quotient & 1) != 0)
            quotient++;
        return checked((long)quotient);
    }
}
