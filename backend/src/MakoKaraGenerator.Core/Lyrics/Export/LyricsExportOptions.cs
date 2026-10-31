namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>歌词导出选项。</summary>
public sealed record LyricsExportOptions
{
    /// <summary>默认选项。</summary>
    public static LyricsExportOptions Default { get; } = new();

    /// <summary>写入 <c>[ti:]</c> 的曲名。</summary>
    public string? Title { get; init; }

    /// <summary>写入 <c>[ar:]</c> 的艺术家。</summary>
    public string? Artist { get; init; }

    /// <summary>写入 <c>[al:]</c> 的专辑。</summary>
    public string? Album { get; init; }

    /// <summary>换行符，默认 <c>\n</c>。</summary>
    public string NewLine { get; init; } = "\n";

    /// <summary>是否在文件开头写 UTF-8 BOM，默认不写。</summary>
    public bool WriteByteOrderMark { get; init; }

    /// <summary>
    /// 时间精度是否用毫秒（<c>[mm:ss.fff]</c>）。
    /// </summary>
    /// <remarks>
    /// 默认 <see langword="false" />：写 LRC 标准的百分秒（<c>[mm:ss.xx]</c>），
    /// 兼容性最好，但不足 10 毫秒的部分会被四舍五入 —— 这属于有损，
    /// 会被记进损失清单。需要无损交换时打开本选项。
    /// </remarks>
    public bool MillisecondPrecision { get; init; }
}
