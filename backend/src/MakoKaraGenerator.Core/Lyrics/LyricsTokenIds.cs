using System.Globalization;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 文本元素标识的生成工具。
/// </summary>
/// <remarks>
/// 切分会往文档里插入新元素，标识必须接着现有编号往下排，
/// 否则新建的 <c>token-0001</c> 会与导入时分配的第一个元素撞号（ADR-0006 要求标识唯一）。
/// </remarks>
public static class LyricsTokenIds
{
    private const string TokenPrefix = "token-";

    /// <summary>生成一个"接着已有编号往下排"的标识工厂。</summary>
    /// <param name="document">当前歌词文档。</param>
    /// <returns>每次调用返回一个新的标识。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> 为 <see langword="null" /> 时抛出。</exception>
    public static Func<string> CreateFactory(LyricsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        int next = NextNumber(document);

        return () => string.Create(CultureInfo.InvariantCulture, $"token-{next++:D4}");
    }

    /// <summary>取文档里现有元素标识之后的下一个编号。</summary>
    /// <param name="document">当前歌词文档。</param>
    /// <returns>下一个可用编号（至少 1）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> 为 <see langword="null" /> 时抛出。</exception>
    public static int NextNumber(LyricsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        int next = 1;

        foreach (LyricLine line in document.Lines)
        {
            foreach (LyricToken token in line.Tokens)
            {
                if (token.Id.StartsWith(TokenPrefix, StringComparison.Ordinal)
                    && int.TryParse(
                        token.Id[TokenPrefix.Length..],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int number)
                    && number >= next)
                {
                    next = number + 1;
                }
            }
        }

        return next;
    }
}
