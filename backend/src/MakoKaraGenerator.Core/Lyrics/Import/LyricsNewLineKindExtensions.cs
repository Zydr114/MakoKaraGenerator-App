namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// <see cref="LyricsNewLineKind" /> 的显示名。
/// </summary>
/// <remarks>与 <see cref="Lyrics.TimingStatus" /> 的显示名一样放在 Core：
/// 这些词是领域词汇，界面只负责摆放，不负责翻译。</remarks>
public static class LyricsNewLineKindExtensions
{
    /// <summary>取换行符种类的显示名。</summary>
    /// <param name="kind">换行符种类。</param>
    /// <returns>显示名。</returns>
    public static string ToDisplayName(this LyricsNewLineKind kind) => kind switch
    {
        LyricsNewLineKind.None => "无换行",
        LyricsNewLineKind.LineFeed => "LF",
        LyricsNewLineKind.CarriageReturnLineFeed => "CRLF",
        LyricsNewLineKind.CarriageReturn => "CR",
        LyricsNewLineKind.Mixed => "混合",
        _ => "未知",
    };
}
