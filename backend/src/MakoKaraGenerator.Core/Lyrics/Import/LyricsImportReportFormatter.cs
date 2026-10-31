using System.Globalization;
using System.Text;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 把导入报告排版成给人看的文本（docs/development.md §7.3）。
/// </summary>
/// <remarks>
/// 排版放在 Core 而不是界面里，是为了让报告口径只有一处定义：
/// 界面、日志、测试看到的都是同一段文字。
/// </remarks>
public static class LyricsImportReportFormatter
{
    /// <summary>默认最多列出的问题条数。</summary>
    public const int DefaultMaxIssues = 8;

    /// <summary>排版导入报告。</summary>
    /// <param name="report">导入报告。</param>
    /// <param name="maxIssues">最多列出多少条问题明细。</param>
    /// <returns>多行文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report" /> 为 <see langword="null" /> 时抛出。</exception>
    public static string Format(LyricsImportReport report, int maxIssues = DefaultMaxIssues)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder builder = new();

        builder.Append("格式：").Append(report.ImporterName).Append(" · ").AppendLine(report.SourceFormat);
        builder
            .Append("规模：源文件 ")
            .Append(report.SourceLineCount.ToString(CultureInfo.InvariantCulture))
            .Append(" 行 → 歌词 ")
            .Append(report.LineCount.ToString(CultureInfo.InvariantCulture))
            .Append(" 行 · 文本元素 ")
            .Append(report.TokenCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" 个");

        builder
            .Append("逐字时间：")
            .Append(report.TimedTokenCount.ToString(CultureInfo.InvariantCulture))
            .Append(" 个 · 缺失 ")
            .Append(report.MissingTokenCount.ToString(CultureInfo.InvariantCulture))
            .Append(" 个 · 估算 ")
            .Append(report.EstimatedTokenCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" 个");

        builder
            .Append("行终点：")
            .Append(report.DerivedLineEndCount.ToString(CultureInfo.InvariantCulture))
            .Append(" 行由下一行推导 · ")
            .Append(report.FallbackLineEndCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" 行用兜底时长");

        if (report.SkippedLineCount > 0 || report.MergedContinuationCount > 0)
        {
            builder
                .Append("其它：跳过 ")
                .Append(report.SkippedLineCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 行 · 续行合并 ")
                .Append(report.MergedContinuationCount.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" 行");
        }

        builder
            .Append("编码：")
            .Append(report.EncodingName)
            .Append(report.HadByteOrderMark ? "（带 BOM）" : "（无 BOM）")
            .Append(" · 换行：")
            .Append(report.NewLineKind.ToDisplayName());

        if (report.OffsetUs != 0L)
        {
            builder
                .Append(" · offset：")
                .Append(TimeUs.ToSeconds(report.OffsetUs).ToString("+0.###;-0.###", CultureInfo.InvariantCulture))
                .Append('秒');
        }

        if (report.DetectionReason is { Length: > 0 })
        {
            builder
                .Append("识别：")
                .Append(report.DetectionReason)
                .AppendLine();
        }

        if (report.CandidateNames.Count > 1)
        {
            builder
                .Append("候选：")
                .Append(string.Join(" / ", report.CandidateNames))
                .AppendLine(report.IsFormatAmbiguous ? "（多个候选都匹配，已按优先级选择）" : string.Empty);
        }

        if (report.DiscardedFeatures.Count > 0)
        {
            builder
                .Append("丢弃：")
                .Append(string.Join(" · ", report.DiscardedFeatures))
                .AppendLine();
        }

        if (report.SplitLineCount > 0)
        {
            builder
                .Append("切分：重切 ")
                .Append(report.SplitLineCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 行 · 新建 ")
                .Append(report.SplitCreatedTokenCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 个元素")
                .Append(report.SplitEstimatedTokenCount > 0
                    ? $" · 其中 {report.SplitEstimatedTokenCount} 个时间按比例估算"
                    : " · 新元素未定时")
                .AppendLine();
        }

        builder.AppendLine();

        if (report.Issues.Count == 0)
        {
            builder.Append("问题：无");
            return builder.ToString();
        }

        builder
            .Append("问题：")
            .Append(report.Issues.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" 条")
            .Append(DescribeSeverities(report))
            .AppendLine();

        int shown = 0;

        foreach (LyricsImportIssue issue in report.Issues)
        {
            if (shown >= maxIssues)
            {
                break;
            }

            builder.Append("  · ");

            if (issue.SourceLineNumber > 0)
            {
                builder
                    .Append('第')
                    .Append(issue.SourceLineNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("行：");
            }

            builder.AppendLine(issue.Message);
            shown++;
        }

        if (report.Issues.Count > shown)
        {
            builder
                .Append("  · 另有 ")
                .Append((report.Issues.Count - shown).ToString(CultureInfo.InvariantCulture))
                .Append(" 条未列出");
        }

        return builder.ToString();
    }

    /// <summary>按严重程度拼出简要统计。</summary>
    private static string DescribeSeverities(LyricsImportReport report)
    {
        int errors = 0;
        int warnings = 0;
        int infos = 0;

        foreach (LyricsImportIssue issue in report.Issues)
        {
            switch (issue.Severity)
            {
                case IssueSeverity.Error:
                    errors++;
                    break;

                case IssueSeverity.Warning:
                    warnings++;
                    break;

                default:
                    infos++;
                    break;
            }
        }

        StringBuilder builder = new();

        if (errors > 0)
        {
            builder.Append(" · 错误 ").Append(errors.ToString(CultureInfo.InvariantCulture));
        }

        if (warnings > 0)
        {
            builder.Append(" · 警告 ").Append(warnings.ToString(CultureInfo.InvariantCulture));
        }

        if (infos > 0)
        {
            builder.Append(" · 提示 ").Append(infos.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
