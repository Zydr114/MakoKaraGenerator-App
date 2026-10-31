namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>
/// 歌词导出器（docs/development.md §7.1、§7.4）。
/// </summary>
/// <remarks>
/// 导出分成 <see cref="Plan" /> 与 <see cref="Export" /> 两步：
/// 前者只评估损失、不写文件，后者才真正输出。
/// §7.4 要求"导出前必须先给出损失提示"，拆成两个方法后这条要求就无法被绕过。
/// </remarks>
public interface ILyricsExporter
{
    /// <summary>导出器标识，例如 <c>lrc.plain</c>。</summary>
    string Id { get; }

    /// <summary>导出器显示名。</summary>
    string DisplayName { get; }

    /// <summary>评估这次导出会丢失什么（不写文件）。</summary>
    /// <param name="document">歌词文档。</param>
    /// <param name="options">导出选项。</param>
    /// <returns>损失评估结果。</returns>
    LyricsExportPlan Plan(LyricsDocument document, LyricsExportOptions options);

    /// <summary>导出歌词。</summary>
    /// <param name="document">歌词文档。</param>
    /// <param name="output">输出流。</param>
    /// <param name="options">导出选项。</param>
    /// <returns>导出结果统计。</returns>
    LyricsExportReport Export(LyricsDocument document, Stream output, LyricsExportOptions options);
}
