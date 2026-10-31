using System.Text;

namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>
/// 增强 LRC 导出器：行时间标签 + 逐字时间标签。
/// </summary>
/// <remarks>
/// <para>
/// 输出形状（每个逐字标签紧贴它后面那个元素的文本）：
/// </para>
/// <code>
/// [00:14.20]&lt;00:14.25&gt;When&lt;00:14.67&gt; the&lt;00:15.12&gt;night
/// </code>
/// <para>
/// <b>为什么标签前后不额外插空格</b>：标签后面的文本一直到下一个标签为止都属于同一个词，
/// 因此"标签 + 空格 + 词"会让空格变成词的一部分。本项目的时间轴把词间空白单独当作一个
/// 无时间的元素（空白不参与演唱），若导出时再额外补空格，文本就会与工程里的文本不一致。
/// 这里选择"一个字都不加"：导出的文本与工程文本<b>逐字相同</b>，
/// 需要空格的场合本来就已经作为元素存在于数据里。
/// </para>
/// <para>
/// 同样因为格式限制：没有逐字时间的元素在读取时会被并入相邻元素（LRC 没有表达"这里没有时间"的语法）。
/// 这属于有损，会如实写进损失清单。
/// </para>
/// </remarks>
public sealed class EnhancedLrcExporter : ILyricsExporter
{
    /// <summary>导出器标识。</summary>
    public const string IdValue = "lrc.enhanced";

    /// <inheritdoc/>
    public string Id => IdValue;

    /// <inheritdoc/>
    public string DisplayName => "增强 LRC（含逐字时间）";

    /// <inheritdoc/>
    public LyricsExportPlan Plan(LyricsDocument document, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        LrcExportShared.Inspection inspection = LrcExportShared.Inspect(document, options);
        List<LyricsExportLoss> losses = [];

        // 只有"行里有逐字时间、但另一些元素没有"才会真的丢东西：
        // 整行都没有逐字时间时，写出来就是普通的行级 LRC，什么都没丢。
        int mergeableUntimed = CountMergeableUntimedTokens(document);

        if (mergeableUntimed > 0)
        {
            losses.Add(new LyricsExportLoss(
                "未定时的元素",
                mergeableUntimed,
                "这些元素没有逐字时间，但同一行里另有带时间的元素；"
                + "LRC 没有表达“这里没有时间”的语法，写出去再读回来时它们会被并入相邻元素。"));
        }

        foreach (LyricLine line in document.Lines)
        {
            if (!TokensCoverLineText(line))
            {
                losses.Add(new LyricsExportLoss(
                    "逐字时间（文本不一致）",
                    line.Tokens.Length,
                    $"行 '{line.Id}' 的元素文本拼接与行文本不一致，该行按行文本整行导出，其逐字时间不会被写出。"));
            }
        }

        AddIfPresent(losses, LrcExportShared.DescribeStateLoss(inspection.StatefulItemCount));
        AddIfPresent(losses, LrcExportShared.DescribePrecisionLoss(inspection.RoundedTimeCount));

        return new LyricsExportPlan(
            losses,
            inspection.ExportableLineCount,
            inspection.SkippedLineCount,
            inspection.Issues);
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
        int writtenTokenTimes = 0;
        int rounded = 0;

        foreach (LyricLine line in document.Lines)
        {
            if (line.Time is not { IsValid: true } lineTime)
            {
                skipped++;
                continue;
            }

            if (LrcExportShared.NeedsRounding(lineTime.StartUs, options))
            {
                rounded++;
            }

            writer.Write('[');
            writer.Write(LrcExportShared.FormatTimestamp(lineTime.StartUs, options));
            writer.Write(']');

            // 没有元素、或元素文本拼不回整行文本时，按行文本整行导出：
            // 宁可少写逐字时间，也不能把行里的字丢掉。
            if (line.Tokens.IsEmpty || !TokensCoverLineText(line))
            {
                writer.Write(line.Text);
                writer.WriteLine();
                exported++;
                continue;
            }

            foreach (LyricToken token in line.Tokens)
            {
                if (token.Time is { IsValid: true } tokenTime)
                {
                    if (LrcExportShared.NeedsRounding(tokenTime.StartUs, options))
                    {
                        rounded++;
                    }

                    writer.Write('<');
                    writer.Write(LrcExportShared.FormatTimestamp(tokenTime.StartUs, options));
                    writer.Write('>');
                    writtenTokenTimes++;
                }

                // 文本原样写出：没有时间的元素直接续在上一个标签之后。
                writer.Write(token.Text);
            }

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
            DroppedTokenTimeCount = 0,          // 逐字时间在这个格式里是被保留的
            RoundedTimeCount = rounded,
            WrittenByteCount = output.CanSeek ? output.Position : 0L,
            MetadataCount = metadataCount,
            Plan = plan,
        };
    }

    /// <summary>统计"同一行里既有带时间元素、又有不带时间元素"时，会被并入相邻元素的数量。</summary>
    /// <summary>判断元素的文本拼接是否与整行文本一致。</summary>
    /// <param name="line">歌词行。</param>
    /// <returns>一致（或没有元素）时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 项目校验会保证这一点，但导出发生在"用户当前数据"之上：
    /// 一旦不一致（例如元素被手改过），宁可按行文本整行导出、少写逐字时间，
    /// 也绝不能把行里的一些字丢掉（docs/development.md §19）。
    /// </remarks>
    private static bool TokensCoverLineText(LyricLine line)
    {
        if (line.Tokens.IsEmpty)
        {
            return true;
        }

        StringBuilder builder = new(line.Text.Length);

        foreach (LyricToken token in line.Tokens)
        {
            builder.Append(token.Text);
        }

        return string.Equals(builder.ToString(), line.Text, StringComparison.Ordinal);
    }


    private static int CountMergeableUntimedTokens(LyricsDocument document)
    {
        int count = 0;

        foreach (LyricLine line in document.Lines)
        {
            bool hasTimed = false;

            foreach (LyricToken token in line.Tokens)
            {
                if (token.Time is { IsValid: true })
                {
                    hasTimed = true;
                    break;
                }
            }

            if (!hasTimed)
            {
                continue;
            }

            foreach (LyricToken token in line.Tokens)
            {
                if (token.Time is not { IsValid: true })
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void AddIfPresent(List<LyricsExportLoss> losses, LyricsExportLoss? loss)
    {
        if (loss is not null)
        {
            losses.Add(loss);
        }
    }
}
