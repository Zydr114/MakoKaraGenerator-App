namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>一次导出的结果统计。</summary>
/// <remarks>
/// 与 <see cref="LyricsExportPlan" /> 的区别：plan 是"导出前告诉你会丢什么"，
/// 报告是"实际写出去多少、丢了什么"。两者都返回，便于界面做前后对照。
/// </remarks>
public sealed class LyricsExportReport
{
    /// <summary>导出器标识。</summary>
    public required string ExporterId { get; init; }

    /// <summary>导出器显示名。</summary>
    public required string ExporterName { get; init; }

    /// <summary>文档里的歌词行总数。</summary>
    public required int SourceLineCount { get; init; }

    /// <summary>实际写出的歌词行数。</summary>
    public required int ExportedLineCount { get; init; }

    /// <summary>被跳过的行数。</summary>
    public required int SkippedLineCount { get; init; }

    /// <summary>被丢弃的逐字时间个数。</summary>
    public required int DroppedTokenTimeCount { get; init; }

    /// <summary>实际写出的 ruby 片段数；非 TTML 导出器默认为零。</summary>
    public int WrittenRubySegmentCount { get; init; }

    /// <summary>未写出的 ruby 片段数（含省略来源、行及不可绑定注音）。</summary>
    public int SkippedRubySegmentCount { get; init; }

    /// <summary>写出片段正文但省略无效/不一致时间的数量。</summary>
    public int DroppedRubySegmentTimeCount { get; init; }

    /// <summary>因精度不足被四舍五入的时间个数。</summary>
    public required int RoundedTimeCount { get; init; }

    /// <summary>写入的字节数。</summary>
    public required long WrittenByteCount { get; init; }

    /// <summary>写入的元数据标签个数。</summary>
    public required int MetadataCount { get; init; }

    /// <summary>导出前的损失清单。</summary>
    public required LyricsExportPlan Plan { get; init; }
}
