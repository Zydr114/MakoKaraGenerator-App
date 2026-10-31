using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Timeline;

/// <summary>
/// 时间轴上的一个逐字块：一个文本元素在时间轴上的表示。
/// </summary>
/// <param name="LineIndex">所属歌词行在文档中的序号。</param>
/// <param name="TokenIndex">该元素在所属行内的序号。</param>
/// <param name="LineId">所属行标识。</param>
/// <param name="TokenId">元素标识。</param>
/// <param name="Text">元素文本，用于画在块上。</param>
/// <param name="Time">元素的时间区间，保证合法。</param>
/// <param name="Utf16Start">元素在所属行文本中的起始下标。</param>
/// <param name="Utf16Length">元素占用的 UTF-16 代码单元数量。</param>
/// <remarks>
/// 带上 UTF-16 区间是为了让歌词画布也用<b>同一份</b>逐字信息推进填充：
/// 否则画布会自己按"元素均分进度"，与时间轴上的真实时间脱节
/// （那种做法在逐字时长不等时会明显对不上）。
/// </remarks>
public readonly record struct TimelineTokenBlock(
    int LineIndex,
    int TokenIndex,
    string LineId,
    string TokenId,
    string Text,
    TimeRange Time,
    int Utf16Start,
    int Utf16Length)
{
    /// <summary>起点（微秒）。</summary>
    public long StartUs => Time.StartUs;

    /// <summary>终点（微秒）。</summary>
    public long EndUs => Time.EndUs;

    /// <summary>时长（微秒）。</summary>
    public long DurationUs => Time.DurationUs;

    /// <summary>结束下标（不含）。</summary>
    public int Utf16End => Utf16Start + Utf16Length;
}

/// <summary>
/// 从歌词文档构造逐字块列表。
/// </summary>
/// <remarks>
/// 只包含<b>有合法时间区间</b>的元素：时间缺失的元素无法在时间轴上定位，
/// 因此不画在时间轴上（它们仍然出现在歌词栏里，等待打轴）。
/// 这与 docs/development.md §5.4 的要求一致——不得给缺失时间编造位置。
/// </remarks>
public static class TimelineTokenBlocks
{
    /// <summary>按行序、元素序构造块列表。</summary>
    /// <param name="lyrics">歌词文档。</param>
    /// <returns>逐字块列表；没有可定位元素时为空。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lyrics"/> 为 <see langword="null"/> 时抛出。</exception>
    public static IReadOnlyList<TimelineTokenBlock> FromLyrics(LyricsDocument lyrics)
    {
        ArgumentNullException.ThrowIfNull(lyrics);

        List<TimelineTokenBlock> blocks = [];

        for (int lineIndex = 0; lineIndex < lyrics.Lines.Length; lineIndex++)
        {
            LyricLine line = lyrics.Lines[lineIndex];

            int utf16Offset = 0;

            for (int tokenIndex = 0; tokenIndex < line.Tokens.Length; tokenIndex++)
            {
                LyricToken token = line.Tokens[tokenIndex];

                if (token.Time is { IsValid: true } time)
                {
                    blocks.Add(new TimelineTokenBlock(
                        lineIndex,
                        tokenIndex,
                        line.Id,
                        token.Id,
                        token.Text,
                        time,
                        utf16Offset,
                        token.Text.Length));
                }

                utf16Offset += token.Text.Length;
            }
        }

        return blocks;
    }
}

/// <summary>时间轴上被命中的区域。</summary>
public enum TimelineHitKind
{
    /// <summary>没有命中任何区域。</summary>
    None = 0,

    /// <summary>命中缩略条（使用独立坐标系）。</summary>
    Overview = 1,

    /// <summary>命中标尺。</summary>
    Ruler = 2,

    /// <summary>命中波形轨空白处。</summary>
    Waveform = 3,

    /// <summary>命中逐字块的主体。</summary>
    TokenBody = 4,

    /// <summary>命中逐字块的起始边界。</summary>
    TokenStartEdge = 5,

    /// <summary>命中逐字块的结束边界。</summary>
    TokenEndEdge = 6,

    /// <summary>命中歌词行轨。</summary>
    Line = 7,

    /// <summary>命中选中行的起点手柄。</summary>
    LineStartEdge = 8,

    /// <summary>命中选中行的终点手柄。</summary>
    LineEndEdge = 9,
}

/// <summary>
/// 一次命中测试的结果。
/// </summary>
/// <param name="Kind">命中区域类型。</param>
/// <param name="BlockIndex">命中的逐字块序号；不适用时为 <c>-1</c>。</param>
/// <param name="X">命中处的横向像素位置。</param>
public readonly record struct TimelineHit(
    TimelineHitKind Kind,
    int BlockIndex,
    double X)
{
    /// <summary>未命中。</summary>
    public static TimelineHit None { get; } = new(TimelineHitKind.None, -1, 0d);

    /// <summary>是否命中了某个逐字块。</summary>
    public bool HasBlock => BlockIndex >= 0;

    /// <summary>是否命中了逐字块的边界（用于改时间而不是改切分）。</summary>
    public bool IsEdge => Kind is TimelineHitKind.TokenStartEdge or TimelineHitKind.TokenEndEdge;

}

/// <summary>
/// 时间轴的命中测试。
/// </summary>
/// <remarks>
/// <para>
/// 命中测试<b>必须</b>走 <see cref="TimelineLayout.Axis"/>（除缩略条外），
/// 这与绘制用的是同一个映射，因此"画在哪就能点到哪"。
/// 旧项目的病根正是绘制与命中各写一套换算（见 docs/decisions.md ADR-0011）。
/// </para>
/// <para>
/// 把命中测试放在本程序集（而不是控件里）是为了它能被单元测试直接覆盖——
/// 否则"对齐"只能靠眼看。
/// </para>
/// </remarks>
public static class TimelineHitTester
{
    /// <summary>边界抓取半径（像素）。</summary>
    public const double DefaultEdgeTolerancePx = 7d;

    /// <summary>做一次命中测试。</summary>
    /// <param name="layout">轨道布局。</param>
    /// <param name="blocks">逐字块列表。</param>
    /// <param name="x">横向像素位置。</param>
    /// <param name="y">纵向像素位置。</param>
    /// <param name="edgeTolerancePx">边界抓取半径（像素）。</param>
    /// <param name="selectedBlockIndex">当前选中块；其边沿在重合点优先命中。</param>
    /// <param name="lines">行轨中的歌词行。</param>
    /// <param name="selectedLineIndex">当前选中行；只显示并命中它的两个手柄。</param>
    /// <returns>命中结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> 或 <paramref name="blocks"/> 为 <see langword="null"/> 时抛出。</exception>
    public static TimelineHit HitTest(
        TimelineLayout layout,
        IReadOnlyList<TimelineTokenBlock> blocks,
        double x,
        double y,
        double edgeTolerancePx = DefaultEdgeTolerancePx,
        int selectedBlockIndex = -1,
        IReadOnlyList<LyricLine>? lines = null,
        int selectedLineIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentOutOfRangeException.ThrowIfNegative(edgeTolerancePx);

        TimelineLane? lane = layout.LaneAtY(y);

        if (lane is null)
        {
            return TimelineHit.None;
        }

        switch (lane.Value)
        {
            case TimelineLane.Overview:
                return new TimelineHit(TimelineHitKind.Overview, -1, x);

            case TimelineLane.Ruler:
                return new TimelineHit(TimelineHitKind.Ruler, -1, x);

            case TimelineLane.Waveform:
                return new TimelineHit(TimelineHitKind.Waveform, -1, x);

            case TimelineLane.Line:
                return HitLineLane(layout, lines, x, edgeTolerancePx, selectedLineIndex);

            default:
                return HitTokenLane(layout, blocks, x, edgeTolerancePx, selectedBlockIndex);
        }
    }

    private static TimelineHit HitLineLane(
        TimelineLayout layout, IReadOnlyList<LyricLine>? lines,
        double x, double tolerance, int selectedLineIndex)
    {
        if (lines is null || selectedLineIndex < 0 || selectedLineIndex >= lines.Count
            || lines[selectedLineIndex].Time is not { IsValid: true } selected)
            return new TimelineHit(TimelineHitKind.Line, -1, x);

        double startDistance = Math.Abs(x - layout.Axis.TimeToX(selected.StartUs));
        double endDistance = Math.Abs(x - layout.Axis.TimeToX(selected.EndUs));
        if (startDistance <= tolerance && startDistance <= endDistance)
            return new TimelineHit(TimelineHitKind.LineStartEdge, selectedLineIndex, x);
        if (endDistance <= tolerance)
            return new TimelineHit(TimelineHitKind.LineEndEdge, selectedLineIndex, x);
        return new TimelineHit(TimelineHitKind.Line, -1, x);
    }

    /// <summary>
    /// 取命中处对应的时间。
    /// </summary>
    /// <param name="layout">轨道布局。</param>
    /// <param name="hit">命中结果。</param>
    /// <returns>时刻（微秒）。</returns>
    /// <remarks>
    /// 缩略条使用独立的坐标系（ADR-0011 不变式 5），因此必须在这里分流，
    /// 而不是让调用方自己决定用哪个轴——那是"第二套映射"的入口。
    /// </remarks>
    public static long TimeAt(TimelineLayout layout, TimelineHit hit)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return hit.Kind == TimelineHitKind.Overview
            ? layout.OverviewAxis.XToTime(hit.X)
            : layout.Axis.XToTime(hit.X);
    }

    private static TimelineHit HitTokenLane(
        TimelineLayout layout,
        IReadOnlyList<TimelineTokenBlock> blocks,
        double x,
        double edgeTolerancePx,
        int selectedBlockIndex)
    {
        TimeAxis axis = layout.Axis;
        if (selectedBlockIndex >= 0 && selectedBlockIndex < blocks.Count)
        {
            TimelineTokenBlock selected = blocks[selectedBlockIndex];
            double startDistance = Math.Abs(x - axis.TimeToX(selected.StartUs));
            double endDistance = Math.Abs(x - axis.TimeToX(selected.EndUs));
            if (startDistance <= edgeTolerancePx && startDistance <= endDistance)
            {
                return new TimelineHit(TimelineHitKind.TokenStartEdge, selectedBlockIndex, x);
            }

            if (endDistance <= edgeTolerancePx)
            {
                return new TimelineHit(TimelineHitKind.TokenEndEdge, selectedBlockIndex, x);
            }
        }

        int bestBlock = -1;
        TimelineHitKind bestKind = TimelineHitKind.None;
        double bestDistance = double.PositiveInfinity;

        for (int i = 0; i < blocks.Count; i++)
        {
            double startX = axis.TimeToX(blocks[i].StartUs);
            double endX = axis.TimeToX(blocks[i].EndUs);
            double startDistance = Math.Abs(x - startX);
            double endDistance = Math.Abs(x - endX);

            // 未选中块时，共享边界默认优先后一个块的起点；拖动仍只改命中的块。
            if (startDistance <= edgeTolerancePx && startDistance <= bestDistance)
            {
                bestBlock = i;
                bestKind = TimelineHitKind.TokenStartEdge;
                bestDistance = startDistance;
            }

            if (endDistance <= edgeTolerancePx && endDistance < bestDistance)
            {
                bestBlock = i;
                bestKind = TimelineHitKind.TokenEndEdge;
                bestDistance = endDistance;
            }
        }

        // 边界优先于主体：相邻两块紧贴时，边界才是用户想抓的东西。
        if (bestBlock >= 0)
        {
            return new TimelineHit(bestKind, bestBlock, x);
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            double startX = axis.TimeToX(blocks[i].StartUs);
            double endX = axis.TimeToX(blocks[i].EndUs);

            if (x >= startX && x < endX)
            {
                return new TimelineHit(TimelineHitKind.TokenBody, i, x);
            }
        }

        return new TimelineHit(TimelineHitKind.None, -1, x);
    }


}

/// <summary>
/// 标尺刻度间隔的选择。
/// </summary>
/// <remarks>
/// 刻度必须落在"整"的时间值上（1/2/5/10 × 10ⁿ 毫秒），
/// 否则缩放时标签会变成 00:01.37 这类没有信息量的数字。
/// 选择逻辑放在本程序集以便测试。
/// </remarks>
public static class TimeRulerTicks
{
    private static readonly long[] NiceIntervalsUs =
    [
        1_000, 2_000, 5_000,
        10_000, 20_000, 50_000,
        100_000, 200_000, 500_000,
        1_000_000, 2_000_000, 5_000_000,
        10_000_000, 15_000_000, 30_000_000,
        60_000_000, 120_000_000, 300_000_000,
        600_000_000, 1_800_000_000, 3_600_000_000,
    ];

    /// <summary>
    /// 选择不小于指定像素间距的最小"整"间隔。
    /// </summary>
    /// <param name="axis">当前映射。</param>
    /// <param name="minSpacingPx">相邻刻度之间期望的最小像素间距。</param>
    /// <returns>刻度间隔（微秒）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="axis"/> 为 null 时抛出（值类型不会发生）。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minSpacingPx"/> 非正时抛出。</exception>
    public static long ChooseIntervalUs(TimeAxis axis, double minSpacingPx = 80d)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSpacingPx);

        foreach (long candidate in NiceIntervalsUs)
        {
            if (axis.PixelsFromUs(candidate) >= minSpacingPx)
            {
                return candidate;
            }
        }

        return NiceIntervalsUs[^1];
    }

    /// <summary>
    /// 枚举视口内的刻度时刻。
    /// </summary>
    /// <param name="axis">当前映射。</param>
    /// <param name="intervalUs">刻度间隔（微秒）。</param>
    /// <returns>按时间递增的刻度时刻，全部落在视口内。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intervalUs"/> 非正时抛出。</exception>
    public static IEnumerable<long> Enumerate(TimeAxis axis, long intervalUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalUs);

        long startUs = axis.ViewStartUs;
        long endUs = axis.ViewEndUs;

        // 从 >= 视口左端的第一个刻度开始。
        long first = ((startUs + intervalUs - 1) / intervalUs) * intervalUs;

        for (long timeUs = first; timeUs <= endUs; timeUs += intervalUs)
        {
            yield return timeUs;
        }
    }
}
