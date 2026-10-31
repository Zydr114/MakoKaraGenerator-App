using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 导入过程中的一条问题记录。
/// </summary>
/// <param name="Kind">问题种类。</param>
/// <param name="Severity">严重程度。</param>
/// <param name="SourceLineNumber">原始文件里的行号（从 1 开始）；不适用时为 0。</param>
/// <param name="RawText">触发问题的原始文本，始终保留（docs/development.md §7.2）。</param>
/// <param name="Message">面向用户的说明。</param>
public sealed record LyricsImportIssue(
    LyricsImportIssueKind Kind,
    IssueSeverity Severity,
    int SourceLineNumber,
    string RawText,
    string Message);
