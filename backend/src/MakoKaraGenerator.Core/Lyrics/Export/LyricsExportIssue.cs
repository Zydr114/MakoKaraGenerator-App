namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>导出时逐行遇到的问题种类。</summary>
public enum LyricsExportIssueKind
{
    /// <summary>该行没有时间，无法写成 LRC 时间标签。</summary>
    LineWithoutTime = 0,

    /// <summary>该行文本里含换行，在普通 LRC 里会变成续行。</summary>
    LineTextHasNewLine = 1,

    /// <summary>项目 raw 时间已保留，但标准 TTML 有效显示区间不同。</summary>
    SourceProjectionLimited = 2,
}

/// <summary>导出时逐行遇到的问题。</summary>
/// <param name="Kind">问题种类。</param>
/// <param name="LineId">歌词行标识。</param>
/// <param name="Message">面向用户的说明。</param>
public sealed record LyricsExportIssue(LyricsExportIssueKind Kind, string LineId, string Message);
