namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// LRC 解析选项。
/// </summary>
/// <remarks>
/// 增强 LRC 的逐字标记在工具之间并不统一（docs/development.md §7.2），
/// 因此凡是"要猜"的地方都做成显式选项，并由导入报告说明实际采用的行为。
/// </remarks>
public sealed record LrcParserOptions
{
    /// <summary>默认选项。</summary>
    public static LrcParserOptions Default { get; } = new();

    /// <summary>是否应用 <c>[offset:±毫秒]</c> 偏移。默认应用。</summary>
    public bool ApplyOffset { get; init; } = true;

    /// <summary>原文件时间乱序时是否按时间重排。默认重排，并记一条诊断。</summary>
    public bool SortByTime { get; init; } = true;

    /// <summary>遇到"没有时间标签的文本行"时，是否追加到上一条歌词。默认追加。</summary>
    public bool MergeContinuationLines { get; init; } = true;
}
