using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 解析出来的一个音节（逐字元素）。
/// </summary>
/// <remarks>
/// <see cref="Text"/> 与 <see cref="StartUs" /> 是格式里明确给出的；
/// <see cref="EndUs" /> 为 <see langword="null" /> 表示"格式只给了起点"，
/// 由 <see cref="ParsedLyricsImporterBase" /> 按"下一个元素的起点"补齐（这属于格式固有语义，仍算导入值）。
/// </remarks>
/// <param name="Text">该音节的文本。</param>
/// <param name="StartUs">起点（微秒）。</param>
/// <param name="EndUs">终点（微秒）；未知时为 <see langword="null" />。</param>
/// <param name="IsEstimated">时间是否由软件估算（而不是格式给出）。</param>
/// <param name="SourceLineNumber">原始文件里的行号（从 1 开始）；不适用时为 0。</param>
public sealed record ParsedLyricsToken(
    string Text,
    long StartUs,
    long? EndUs = null,
    bool IsEstimated = false,
    int SourceLineNumber = 0);

/// <summary>
/// 解析出来的一行歌词。
/// </summary>
/// <param name="Text">行文本。</param>
/// <param name="StartUs">起点（微秒）。</param>
/// <param name="EndUs">终点（微秒）；未知时为 <see langword="null" />（由下一行起点或兜底时长推导）。</param>
/// <param name="Tokens">逐字元素；空表示"这行只有行级时间"。</param>
/// <param name="SourceLineNumber">原始文件里的行号（从 1 开始）；不适用时为 0。</param>
/// <param name="Ruby">该行绑定的注音组。</param>
public sealed record ParsedLyricsLine(
    string Text,
    long StartUs,
    long? EndUs = null,
    ImmutableArray<ParsedLyricsToken> Tokens = default,
    int SourceLineNumber = 0,
    ImmutableArray<ParsedLyricsRuby> Ruby = default);

/// <summary>导入层的注音组；<see cref="BaseTokenIndex"/> 指向所属行的 token。</summary>
public sealed record ParsedLyricsRuby(
    int BaseTokenIndex,
    string Text,
    ImmutableArray<ParsedLyricsRubySegment> Segments,
    string? Language = null);

/// <summary>导入层的注音片段。</summary>
public sealed record ParsedLyricsRubySegment(
    string Text,
    long? StartUs,
    long? EndUs,
    bool IsEstimated = false);

/// <summary>
/// 一次解析的结果：格式无关的中间表示。
/// </summary>
/// <remarks>
/// 各导入器只负责"把文件变成这个结构"，剩下的行区间推导、标识分配、报告口径
/// 都由 <see cref="ParsedLyricsImporterBase" /> 统一处理（ADR-0046）。
/// </remarks>
/// <param name="Lines">歌词行。</param>
/// <param name="SourceLineCount">源文件的物理行数（用于报告规模）。</param>
/// <param name="DiscardedFeatures">来源格式里无法表达、已丢弃的信息（例如音高、翻译轨）。</param>
public sealed record ParsedLyrics(
    ImmutableArray<ParsedLyricsLine> Lines,
    int SourceLineCount = 0,
    ImmutableArray<string> DiscardedFeatures = default);
