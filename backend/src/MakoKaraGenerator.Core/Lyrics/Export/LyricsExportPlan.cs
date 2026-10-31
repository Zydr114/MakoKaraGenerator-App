namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>
/// 导出前的损失评估结果。
/// </summary>
/// <remarks>
/// docs/development.md §7.4 要求"导出器必须先给出损失提示"。因此导出分成两步：
/// 先 <see cref="ILyricsExporter.Plan" /> 拿到损失清单给用户看，确认后再
/// <see cref="ILyricsExporter.Export" />。这样"提示"不是写在文档里的口号，
/// 而是类型系统上的一个必经步骤。
/// </remarks>
public sealed class LyricsExportPlan
{
    /// <summary>创建一个损失评估结果。</summary>
    /// <param name="losses">目标格式无法表达的内容。</param>
    /// <param name="exportableLineCount">能导出的歌词行数。</param>
    /// <param name="skippedLineCount">因缺时间等原因会被跳过的行数。</param>
    /// <param name="issues">逐行的问题说明。</param>
    public LyricsExportPlan(
        IReadOnlyList<LyricsExportLoss> losses,
        int exportableLineCount,
        int skippedLineCount,
        IReadOnlyList<LyricsExportIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(losses);
        ArgumentNullException.ThrowIfNull(issues);

        Losses = losses;
        ExportableLineCount = exportableLineCount;
        SkippedLineCount = skippedLineCount;
        Issues = issues;
    }

    /// <summary>目标格式无法表达的内容。</summary>
    public IReadOnlyList<LyricsExportLoss> Losses { get; }

    /// <summary>能导出的歌词行数。</summary>
    public int ExportableLineCount { get; }

    /// <summary>会被跳过的行数。</summary>
    public int SkippedLineCount { get; }

    /// <summary>逐行的问题说明。</summary>
    public IReadOnlyList<LyricsExportIssue> Issues { get; }

    /// <summary>是否存在会丢失的信息。</summary>
    public bool HasLosses => Losses.Count > 0;
}
