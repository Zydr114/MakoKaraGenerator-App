using System.Globalization;
using System.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>
/// 把导出损失清单排版成给人看的文本（docs/development.md §7.4）。
/// </summary>
/// <remarks>
/// 与导入报告一样放在 Core：界面、日志与测试看到同一份口径。
/// </remarks>
public static class LyricsExportPlanFormatter
{
    /// <summary>默认最多列出的问题条数。</summary>
    public const int DefaultMaxIssues = 5;

    /// <summary>排版损失清单。</summary>
    /// <param name="plan">损失评估结果。</param>
    /// <param name="formatName">目标格式名。</param>
    /// <param name="maxIssues">最多列出多少条逐行问题。</param>
    /// <returns>多行文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> 为 <see langword="null" /> 时抛出。</exception>
    public static string Format(LyricsExportPlan plan, string formatName, int maxIssues = DefaultMaxIssues)
    {
        ArgumentNullException.ThrowIfNull(plan);

        StringBuilder builder = new();

        builder
            .Append("导出为 ")
            .Append(formatName)
            .Append('：')
            .Append(plan.ExportableLineCount.ToString(CultureInfo.InvariantCulture))
            .Append(" 行可导出");

        if (plan.SkippedLineCount > 0)
        {
            builder
                .Append(" · 跳过 ")
                .Append(plan.SkippedLineCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 行");
        }

        builder.AppendLine();

        if (!plan.HasLosses)
        {
            builder.Append("损失提示：无，该格式可以完整表达当前内容。");
            return builder.ToString();
        }

        builder.AppendLine("损失提示（导出后这些信息不可恢复）：");

        foreach (LyricsExportLoss loss in plan.Losses)
        {
            builder
                .Append("  · ")
                .Append(loss.Feature)
                .Append('（')
                .Append(loss.AffectedCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 项）：")
                .AppendLine(loss.Message);
        }

        int shown = 0;

        foreach (LyricsExportIssue issue in plan.Issues)
        {
            if (shown >= maxIssues)
            {
                break;
            }

            builder.Append("  · ").AppendLine(issue.Message);
            shown++;
        }

        if (plan.Issues.Count > shown)
        {
            builder
                .Append("  · 另有 ")
                .Append((plan.Issues.Count - shown).ToString(CultureInfo.InvariantCulture))
                .Append(" 条未列出");
        }

        return builder.ToString();
    }

    /// <summary>把一行时间格式化成 LRC 标签正文。</summary>
    /// <param name="timeUs">时间（微秒）。</param>
    /// <param name="millisecondPrecision">是否用毫秒精度。</param>
    /// <returns>标签正文，例如 <c>00:12.34</c> 或 <c>00:12.345</c>。</returns>
    internal static string FormatTimestamp(long timeUs, bool millisecondPrecision)
    {
        if (millisecondPrecision)
        {
            // FormatClockShort 就是 mm:ss.mmm，正好是毫秒精度。
            return TimeUs.FormatClockShort(Math.Max(0L, timeUs));
        }

        return TimeUs.FormatLrcTimestamp(Math.Max(0L, timeUs));
    }
}
