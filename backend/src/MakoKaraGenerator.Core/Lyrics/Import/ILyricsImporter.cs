namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 歌词格式导入器（docs/development.md §7.1）。
/// </summary>
/// <remarks>
/// 导入器只做语法解析与初步映射，不修改 UI、不碰音频时间轴。
/// 所有"要猜"的地方都必须落到 <see cref="LyricsImportReport" /> 里，
/// 绝不允许把估算当成实测（§7.2、§19）。
/// </remarks>
public interface ILyricsImporter
{
    /// <summary>导入器标识，例如 <c>lrc.plain</c>。</summary>
    string Id { get; }

    /// <summary>导入器显示名。</summary>
    string DisplayName { get; }

    /// <summary>来源格式的一句话说明，写进导入报告。</summary>
    string SourceFormat { get; }

    /// <summary>判断这个导入器是否能处理某个文件。</summary>
    /// <param name="fileName">文件名（含扩展名）。</param>
    /// <param name="header">文件开头的一小段字节，用于嗅探；可能为空。</param>
    /// <returns>能处理时为 <see langword="true" />。</returns>
    bool CanImport(string fileName, ReadOnlySpan<byte> header);

    /// <summary>
    /// 说明"为什么认为这个导入器能处理"。
    /// </summary>
    /// <remarks>
    /// 识别结果要写在导入报告里（ADR-0046），因此理由必须是可读、可检验的一句话，
    /// 而不是"看起来像"。只有在 <see cref="CanImport" /> 返回 <see langword="true" /> 时才有意义。
    /// </remarks>
    /// <param name="fileName">文件名（含扩展名）。</param>
    /// <param name="header">文件开头的一小段字节。</param>
    /// <returns>面向用户的一句话理由。</returns>
    string MatchReason(string fileName, ReadOnlySpan<byte> header);

    /// <summary>导入歌词。</summary>
    /// <param name="input">输入流。</param>
    /// <param name="context">导入条件。</param>
    /// <returns>歌词文档与导入报告。</returns>
    LyricsImportResult Import(Stream input, LyricsImportContext context);
}
