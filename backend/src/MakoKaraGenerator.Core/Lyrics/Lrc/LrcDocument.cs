namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// 一次 LRC 解析的结果：歌词条目、元数据与诊断。
/// </summary>
/// <remarks>
/// 解析器只做语法层面的事，不生成 <see cref="LyricsDocument" />：
/// 把它变成项目的歌词模型、以及推导行结束时间，都是导入器的职责
/// （docs/development.md §7.1："导入器只负责语法解析和初步映射"）。
/// </remarks>
public sealed class LrcDocument
{
    /// <summary>创建一个解析结果。</summary>
    /// <param name="entries">歌词条目（已按时间排序）。</param>
    /// <param name="metadata">元数据标签。</param>
    /// <param name="diagnostics">解析诊断。</param>
    /// <param name="offsetUs">文件里 <c>[offset:]</c> 声明的偏移量（微秒，可能为负）。</param>
    /// <param name="sourceLineCount">原始文件的总行数。</param>
    public LrcDocument(
        IReadOnlyList<LrcEntry> entries,
        IReadOnlyList<LrcMetadataEntry> metadata,
        IReadOnlyList<LrcDiagnostic> diagnostics,
        long offsetUs,
        int sourceLineCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Entries = entries;
        Metadata = metadata;
        Diagnostics = diagnostics;
        OffsetUs = offsetUs;
        SourceLineCount = sourceLineCount;
    }

    /// <summary>歌词条目（已按时间排序）。</summary>
    public IReadOnlyList<LrcEntry> Entries { get; }

    /// <summary>元数据标签。</summary>
    public IReadOnlyList<LrcMetadataEntry> Metadata { get; }

    /// <summary>解析诊断（未知标签、跳过的行等）。</summary>
    public IReadOnlyList<LrcDiagnostic> Diagnostics { get; }

    /// <summary>文件里 <c>[offset:]</c> 声明的偏移量（微秒）；没有该标签时为 0。</summary>
    public long OffsetUs { get; }

    /// <summary>原始文件的总行数。</summary>
    public int SourceLineCount { get; }

    /// <summary>取一条元数据的值（不存在时返回 <see langword="null" />）。</summary>
    /// <param name="key">标签名，大小写不敏感。</param>
    /// <returns>标签值；不存在时为 <see langword="null" />。</returns>
    public string? GetMetadata(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        foreach (LrcMetadataEntry entry in Metadata)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
