using System.Globalization;

namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>
/// LRC 家族导出器共用的检查与写作逻辑。
/// </summary>
/// <remarks>
/// 普通 LRC 与增强 LRC 的差别只在"怎么把一行写出去"，
/// 元数据、时间精度、"行有没有时间"这些口径必须只有一处定义，
/// 否则两个导出器的损失清单迟早会不一致。
/// </remarks>
internal static class LrcExportShared
{
    /// <summary>一次导出前的通用统计。</summary>
    /// <param name="Issues">逐行问题。</param>
    /// <param name="ExportableLineCount">可导出的行数。</param>
    /// <param name="SkippedLineCount">因缺时间而跳过的行数。</param>
    /// <param name="RoundedTimeCount">会被四舍五入到百分秒的时间个数（含行与元素）。</param>
    /// <param name="StatefulItemCount">带"非导入/非缺失"状态的行与元素数量。</param>
    /// <param name="TimedTokenCount">带逐字时间的元素数量。</param>
    /// <param name="UntimedTokenCount">没有逐字时间的元素数量。</param>
    internal readonly record struct Inspection(
        List<LyricsExportIssue> Issues,
        int ExportableLineCount,
        int SkippedLineCount,
        int RoundedTimeCount,
        int StatefulItemCount,
        int TimedTokenCount,
        int UntimedTokenCount);

    /// <summary>按 docs/development.md §7.4 清点损失。</summary>
    /// <param name="document">歌词文档。</param>
    /// <param name="options">导出选项。</param>
    /// <returns>统计结果。</returns>
    internal static Inspection Inspect(LyricsDocument document, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        List<LyricsExportIssue> issues = [];
        int exportable = 0;
        int skipped = 0;
        int rounded = 0;
        int stateful = 0;
        int timedTokens = 0;
        int untimedTokens = 0;

        foreach (LyricLine line in document.Lines)
        {
            if (line.Time is not { IsValid: true })
            {
                skipped++;
                issues.Add(new LyricsExportIssue(
                    LyricsExportIssueKind.LineWithoutTime,
                    line.Id,
                    $"行 '{line.Id}' 没有时间，无法导出。"));
            }
            else
            {
                exportable++;

                if (NeedsRounding(line.Time.Value.StartUs, options))
                {
                    rounded++;
                }
            }

            if (line.Text.Contains('\n', StringComparison.Ordinal))
            {
                issues.Add(new LyricsExportIssue(
                    LyricsExportIssueKind.LineTextHasNewLine,
                    line.Id,
                    $"行 '{line.Id}' 的文本含换行，在普通 LRC 里会变成续行。"));
            }

            if (line.TimingStatus is not (TimingStatus.Imported or TimingStatus.Missing))
            {
                stateful++;
            }

            foreach (LyricToken token in line.Tokens)
            {
                if (token.Time is { IsValid: true } time)
                {
                    timedTokens++;

                    if (NeedsRounding(time.StartUs, options))
                    {
                        rounded++;
                    }
                }
                else
                {
                    untimedTokens++;
                }

                if (token.TimingStatus is not (TimingStatus.Missing or TimingStatus.Imported))
                {
                    stateful++;
                }
            }
        }

        return new Inspection(issues, exportable, skipped, rounded, stateful, timedTokens, untimedTokens);
    }

    /// <summary>写入 ti/ar/al 元数据，返回写入条数。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="options">导出选项。</param>
    /// <returns>写入的标签数量。</returns>
    internal static int WriteMetadata(StreamWriter writer, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);

        int count = 0;

        count += WriteTag(writer, "ti", options.Title);
        count += WriteTag(writer, "ar", options.Artist);
        count += WriteTag(writer, "al", options.Album);

        return count;

        static int WriteTag(StreamWriter writer, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            writer.Write('[');
            writer.Write(key);
            writer.Write(':');
            writer.Write(value);
            writer.WriteLine(']');
            return 1;
        }
    }

    /// <summary>"时间来源状态"损失（LRC 没有表达这些状态的位置）。</summary>
    /// <param name="statefulItemCount">带状态的行与元素数量。</param>
    /// <returns>损失项；不需要报告时为 <see langword="null" />。</returns>
    internal static LyricsExportLoss? DescribeStateLoss(int statefulItemCount) => statefulItemCount > 0
        ? new LyricsExportLoss(
            "时间来源状态",
            statefulItemCount,
            "LRC 没有表达“已确认 / 已修改 / 估算”的位置，这些状态不会被保留。")
        : null;

    /// <summary>"时间精度"损失（百分秒写入会四舍五入）。</summary>
    /// <param name="roundedTimeCount">需要取整的时间个数。</param>
    /// <returns>损失项；不需要报告时为 <see langword="null" />。</returns>
    internal static LyricsExportLoss? DescribePrecisionLoss(int roundedTimeCount) => roundedTimeCount > 0
        ? new LyricsExportLoss(
            "时间精度",
            roundedTimeCount,
            "这些时间不是 10 毫秒的整数倍，写成 LRC 百分秒时会被四舍五入"
            + "（可改用毫秒精度导出以避免）。")
        : null;

    /// <summary>判断某个时间在给定选项下是否需要取整。</summary>
    /// <param name="timeUs">时间（微秒）。</param>
    /// <param name="options">导出选项。</param>
    /// <returns>需要取整时为 <see langword="true" />。</returns>
    internal static bool NeedsRounding(long timeUs, LyricsExportOptions options) =>
        !options.MillisecondPrecision && timeUs % (Time.TimeUs.PerMillisecond * 10L) != 0L;

    /// <summary>把时间格式化成 LRC 标签正文。</summary>
    /// <param name="timeUs">时间（微秒）。</param>
    /// <param name="options">导出选项。</param>
    /// <returns>标签正文。</returns>
    internal static string FormatTimestamp(long timeUs, LyricsExportOptions options) =>
        LyricsExportPlanFormatter.FormatTimestamp(timeUs, options.MillisecondPrecision);

}
