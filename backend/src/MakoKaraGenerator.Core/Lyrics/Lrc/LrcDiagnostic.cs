namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// LRC 解析过程中记录的一条诊断。
/// </summary>
/// <param name="Kind">问题种类。</param>
/// <param name="SourceLineNumber">原始文件里的行号（从 1 开始）。</param>
/// <param name="RawText">触发问题的原始文本，保留下来供用户对照。</param>
/// <param name="Message">面向用户的说明。</param>
/// <remarks>
/// docs/development.md §7.2 要求"无法识别的标记必须保留在诊断信息中"，
/// 因此这里始终保存 <see cref="RawText" />，不做截断或归并。
/// </remarks>
public sealed record LrcDiagnostic(
    LrcDiagnosticKind Kind,
    int SourceLineNumber,
    string RawText,
    string Message);
