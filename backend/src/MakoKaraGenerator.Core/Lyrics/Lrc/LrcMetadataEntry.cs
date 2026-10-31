namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// LRC 文件里的一条元数据标签（例如 <c>[ar:艺术家]</c>）。
/// </summary>
/// <param name="Key">标签名（小写化，例如 <c>ar</c>）。</param>
/// <param name="Value">标签值，保留原始大小写与空格。</param>
/// <param name="SourceLineNumber">原始文件里的行号（从 1 开始）。</param>
public sealed record LrcMetadataEntry(string Key, string Value, int SourceLineNumber);
