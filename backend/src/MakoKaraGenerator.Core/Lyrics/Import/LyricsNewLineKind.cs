namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 换行符种类。
/// </summary>
public enum LyricsNewLineKind
{
    /// <summary>文本里没有换行符。</summary>
    None = 0,

    /// <summary><c>\n</c>（Unix）。</summary>
    LineFeed = 1,

    /// <summary><c>\r\n</c>（Windows）。</summary>
    CarriageReturnLineFeed = 2,

    /// <summary><c>\r</c>（老式 Mac）。</summary>
    CarriageReturn = 3,

    /// <summary>多种换行符混用。</summary>
    Mixed = 4,
}
