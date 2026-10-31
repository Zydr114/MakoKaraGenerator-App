namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 一次导入的结果：歌词文档与导入报告。
/// </summary>
/// <remarks>
/// docs/development.md §7.1 给的原型是 <c>LyricsDocument Import(...)</c>。
/// 这里改成同时返回报告，是因为报告不是"附加信息"而是导入的<b>必要产出</b>
/// （§7.3 要求显示识别到的行数、元素数、缺失与估算数量、跳过的行、编码处理结果）；
/// 用一个返回值把两者绑在一起，调用方就不可能忘记取报告。
/// </remarks>
public sealed class LyricsImportResult
{
    /// <summary>创建一个导入结果。</summary>
    /// <param name="document">导入后的歌词文档（行级时间已填，逐字时间按格式能力填写）。</param>
    /// <param name="report">导入报告。</param>
    public LyricsImportResult(LyricsDocument document, LyricsImportReport report)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(report);

        Document = document;
        Report = report;
    }

    /// <summary>导入后的歌词文档。</summary>
    public LyricsDocument Document { get; }

    /// <summary>导入报告。</summary>
    public LyricsImportReport Report { get; }
}
