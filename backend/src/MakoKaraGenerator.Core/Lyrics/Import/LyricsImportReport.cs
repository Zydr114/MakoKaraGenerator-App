using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 导入报告：识别到什么、丢掉了什么、推导了什么。
/// </summary>
/// <remarks>
/// 字段口径按 docs/development.md §7.3：
/// 行数与文本元素数、逐字时间数、估算或缺失的元素数、跳过的行与无法识别的标签、
/// 编码与换行符的处理结果。另外把"行终点是推导来的"这件事也计入报告，
/// 因为 LRC 并不携带行终点，用户有权知道哪些时间是算出来的而不是文件里写的。
/// </remarks>
public sealed record LyricsImportReport
{
    /// <summary>导入器标识（例如 <c>lrc.plain</c>）。</summary>
    public required string ImporterId { get; init; }

    /// <summary>导入器显示名。</summary>
    public required string ImporterName { get; init; }

    /// <summary>识别到的语法说明（例如"普通 LRC 行时间标签"）。</summary>
    public required string SourceFormat { get; init; }

    /// <summary>源文件总行数。</summary>
    public required int SourceLineCount { get; init; }

    /// <summary>导入后的歌词行数。</summary>
    public required int LineCount { get; init; }

    /// <summary>导入后的文本元素总数。</summary>
    public required int TokenCount { get; init; }

    /// <summary>带逐字时间的元素数。</summary>
    public required int TimedTokenCount { get; init; }

    /// <summary>缺失逐字时间的元素数。</summary>
    public required int MissingTokenCount { get; init; }

    /// <summary>估算得到的元素数（导入器本身或导入后按规则切分产生）。</summary>
    public required int EstimatedTokenCount { get; init; }

    /// <summary>跳过的源行数（只有时间标签没有文本等）。</summary>
    public required int SkippedLineCount { get; init; }

    /// <summary>被合并成续行的源行数。</summary>
    public required int MergedContinuationCount { get; init; }

    /// <summary>行终点由下一行起点推导出的行数。</summary>
    public required int DerivedLineEndCount { get; init; }

    /// <summary>行终点用了兜底时长的行数。</summary>
    public required int FallbackLineEndCount { get; init; }

    /// <summary>文件声明的 <c>[offset:]</c>（微秒）。</summary>
    public required long OffsetUs { get; init; }

    /// <summary>实际采用的编码名。</summary>
    public required string EncodingName { get; init; }

    /// <summary>文件是否带 BOM。</summary>
    public required bool HadByteOrderMark { get; init; }

    /// <summary>检测到的换行符种类。</summary>
    public required LyricsNewLineKind NewLineKind { get; init; }

    /// <summary>导入过程中发现的问题。</summary>
    public required IReadOnlyList<LyricsImportIssue> Issues { get; init; }

    /// <summary>选中的理由（识别依据）；由注册表填写，直接构造报告时可为 <see langword="null" />。</summary>
    public string? DetectionReason { get; init; }

    /// <summary>还有哪些导入器能处理这个文件（按优先级）；单一时为空。</summary>
    public IReadOnlyList<string> CandidateNames { get; init; } = [];

    /// <summary>是否存在多个能处理该文件的导入器（即按优先级裁决过）。</summary>
    public bool IsFormatAmbiguous { get; init; }

    /// <summary>来源格式里有信息无法表达、已丢弃的清单（音高、翻译、和声、样式标签等）。</summary>
    public IReadOnlyList<string> DiscardedFeatures { get; init; } = [];

    /// <summary>导入后按规则重新切分过的行数。</summary>
    public int SplitLineCount { get; init; }

    /// <summary>重新切分时新建的元素个数。</summary>
    public int SplitCreatedTokenCount { get; init; }

    /// <summary>重新切分时产生的估算时间个数（勾选估算时才会大于 0）。</summary>
    public int SplitEstimatedTokenCount { get; init; }

    /// <summary>问题总数。</summary>
    public int IssueCount => Issues.Count;

    /// <summary>最高严重程度；没有问题时为 <see langword="null" />。</summary>
    public IssueSeverity? MaxSeverity
    {
        get
        {
            IssueSeverity? max = null;

            foreach (LyricsImportIssue issue in Issues)
            {
                if (max is null || issue.Severity > max)
                {
                    max = issue.Severity;
                }
            }

            return max;
        }
    }

    /// <summary>按种类统计问题数量。</summary>
    /// <param name="kind">问题种类。</param>
    /// <returns>该种类的数量。</returns>
    public int CountIssues(LyricsImportIssueKind kind)
    {
        int count = 0;

        foreach (LyricsImportIssue issue in Issues)
        {
            if (issue.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }
}
