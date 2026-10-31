namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// LRC 里的一条歌词（一行级时间）。
/// </summary>
/// <param name="StartUs">行起点（微秒，已应用偏移）。</param>
/// <param name="Text">行文本。</param>
/// <param name="SourceLineNumber">原始文件里的首个行号（从 1 开始）。</param>
/// <param name="TagCount">该行在原始文件里出现的时间标签个数（&gt; 1 表示一行被扩展成多条）。</param>
/// <param name="IsContinuation">该条是否由"无时间标签的续行"合并而来。</param>
/// <remarks>
/// LRC 只给出行的<b>起点</b>，没有终点，因此这里也不编造终点：
/// 终点由导入器按"下一行起点"推导，并在导入报告里说明（docs/development.md §7.2）。
/// </remarks>
public sealed record LrcEntry(
    long StartUs,
    string Text,
    int SourceLineNumber,
    int TagCount = 1,
    bool IsContinuation = false);
