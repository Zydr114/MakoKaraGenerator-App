using System.Globalization;
using System.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>
/// 普通 LRC 导出器：<c>[mm:ss.xx]行文本</c>。
/// </summary>
/// <remarks>
/// <para>
/// 按 docs/development.md §7.4，导出前必须能说清损失。普通 LRC 能表达的只有
/// "行起点 + 行文本"，因此以下内容会被明确列入损失清单：
/// 逐字时间、时间来源状态、以及时间精度（若非 10 毫秒整数倍）。
/// </para>
/// <para>
/// 行文本里如果含换行（例如从多行歌词合并而来），这里<b>原样写出</b>：
/// 它会变成 LRC 的续行，本项目的导入器能原样合并回来。
/// 不做"替换成空格"这类静默改写，只把它记成一条提示。
/// </para>
/// </remarks>
public sealed class PlainLrcExporter : ILyricsExporter
{
    /// <summary>导出器标识。</summary>
    public const string IdValue = "lrc.plain";

    /// <inheritdoc/>
    public string Id => IdValue;

    /// <inheritdoc/>
    public string DisplayName => "普通 LRC";

    /// <inheritdoc/>
    public LyricsExportPlan Plan(LyricsDocument document, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        LrcExportShared.Inspection inspection = LrcExportShared.Inspect(document, options);
        List<LyricsExportLoss> losses = [];

        if (inspection.TimedTokenCount > 0)
        {
            losses.Add(new LyricsExportLoss(
                "逐字时间",
                inspection.TimedTokenCount,
                "普通 LRC 只能表示行级时间，这些元素的逐字时间不会被保留。"));
        }

        AddIfPresent(losses, LrcExportShared.DescribeStateLoss(inspection.StatefulItemCount));
        AddIfPresent(losses, LrcExportShared.DescribePrecisionLoss(inspection.RoundedTimeCount));

        return new LyricsExportPlan(
            losses,
            inspection.ExportableLineCount,
            inspection.SkippedLineCount,
            inspection.Issues);
    }

    /// <summary>把可空的损失项加进清单。</summary>
    private static void AddIfPresent(List<LyricsExportLoss> losses, LyricsExportLoss? loss)
    {
        if (loss is not null)
        {
            losses.Add(loss);
        }
    }

    /// <inheritdoc/>
    public LyricsExportReport Export(LyricsDocument document, Stream output, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        LyricsExportPlan plan = Plan(document, options);

        using StreamWriter writer = new(output, new UTF8Encoding(options.WriteByteOrderMark), leaveOpen: true);
        writer.NewLine = options.NewLine;

        int metadataCount = LrcExportShared.WriteMetadata(writer, options);

        int exported = 0;
        int skipped = 0;
        int droppedTokenTimes = 0;
        int rounded = 0;

        foreach (LyricLine line in document.Lines)
        {
            foreach (LyricToken token in line.Tokens)
            {
                if (token.Time is { IsValid: true })
                {
                    droppedTokenTimes++;
                }
            }

            if (line.Time is not { IsValid: true })
            {
                skipped++;
                continue;
            }

            long startUs = line.Time.Value.StartUs;

            if (LrcExportShared.NeedsRounding(startUs, options))
            {
                rounded++;
            }

            writer.Write('[');
            writer.Write(LrcExportShared.FormatTimestamp(startUs, options));
            writer.Write(']');
            writer.Write(line.Text);
            writer.WriteLine();

            exported++;
        }

        writer.Flush();

        return new LyricsExportReport
        {
            ExporterId = Id,
            ExporterName = DisplayName,
            SourceLineCount = document.Lines.Length,
            ExportedLineCount = exported,
            SkippedLineCount = skipped,
            DroppedTokenTimeCount = droppedTokenTimes,
            RoundedTimeCount = rounded,
            WrittenByteCount = output.CanSeek ? output.Position : 0L,
            MetadataCount = metadataCount,
            Plan = plan,
        };
    }

}
