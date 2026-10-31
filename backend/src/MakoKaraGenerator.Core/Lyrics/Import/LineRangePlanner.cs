using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>行结束时间的来源。</summary>
public enum LineEndSource
{
    /// <summary>由下一行起点推导（时间标签文件的常规语义）。</summary>
    NextLineStart = 0,

    /// <summary>用音频总时长收尾（仅最后一行可能用到）。</summary>
    MediaDuration = 1,

    /// <summary>用兜底时长（无法推导时）。</summary>
    FallbackDuration = 2,
}

/// <summary>一行的时间区间与它的终点来源。</summary>
/// <param name="Range">时间区间。</param>
/// <param name="Source">终点来源。</param>
public readonly record struct PlannedLineRange(TimeRange Range, LineEndSource Source);

/// <summary>
/// 把"只有起点"的歌词行时间补成完整区间。
/// </summary>
/// <remarks>
/// <para>
/// LRC 只写起点，不写终点。要得到本项目需要的 <c>[start, end)</c>，只能推导：
/// 常规语义是"一行唱到下一行开始"。这不是实测数据，因此来源会被记录下来并写进导入报告，
/// 而不是悄悄当成文件里写的时间（docs/development.md §7.2、§19）。
/// </para>
/// <para>
/// 最后一行没有"下一行"，优先用音频总时长收尾；连音频时长也不知道时，
/// 才退回兜底时长，并标记为 <see cref="LineEndSource.FallbackDuration" />。
/// </para>
/// </remarks>
public static class LineRangePlanner
{
    /// <summary>规划每一行的时间区间。</summary>
    /// <param name="startsUs">各行的起点（微秒，必须非负且按时间递增）。</param>
    /// <param name="mediaDurationUs">音频总时长（微秒）；未知时为 <see langword="null" />。</param>
    /// <param name="fallbackLineDurationUs">兜底时长（微秒，必须为正）。</param>
    /// <returns>与 <paramref name="startsUs" /> 一一对应的区间规划。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="startsUs" /> 为 <see langword="null" /> 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">兜底时长不为正时抛出。</exception>
    public static PlannedLineRange[] Plan(
        IReadOnlyList<long> startsUs,
        long? mediaDurationUs,
        long fallbackLineDurationUs)
    {
        ArgumentNullException.ThrowIfNull(startsUs);
        ArgumentOutOfRangeException.ThrowIfLessThan(fallbackLineDurationUs, 1L);

        PlannedLineRange[] result = new PlannedLineRange[startsUs.Count];

        for (int i = 0; i < startsUs.Count; i++)
        {
            long startUs = startsUs[i];
            long endUs;
            LineEndSource source;

            long? nextStartUs = i + 1 < startsUs.Count ? startsUs[i + 1] : null;

            if (nextStartUs is { } next && next > startUs)
            {
                endUs = next;
                source = LineEndSource.NextLineStart;
            }
            else if (nextStartUs is null && mediaDurationUs is { } media && media > startUs)
            {
                endUs = media;
                source = LineEndSource.MediaDuration;
            }
            else
            {
                // 下一行起点与当前相同（重复时间戳），或最后一行且不知道音频长度：
                // 只能兜底，并如实标记来源。
                endUs = startUs + fallbackLineDurationUs;
                source = LineEndSource.FallbackDuration;
            }

            result[i] = new PlannedLineRange(TimeRange.Create(startUs, endUs), source);
        }

        return result;
    }
}
