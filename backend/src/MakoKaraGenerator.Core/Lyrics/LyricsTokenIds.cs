using System.Globalization;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 文本元素标识的生成工具。
/// </summary>
/// <remarks>
/// 切分会往文档里插入新元素，标识必须接着现有编号往下排，
/// 否则新建的 <c>token-0001</c> 会与导入时分配的第一个元素撞号（ADR-0006 要求标识唯一）。
/// 歌词文档中全部分支的行、元素、注音与注音片段共用同一标识命名空间，因此必须扫描整个文档。
/// </remarks>
public static class LyricsTokenIds
{
    private const string TokenPrefix = "token-";

    /// <summary>生成一个"接着已有编号往下排"且覆盖全文档命名空间的标识工厂。</summary>
    /// <param name="document">当前歌词文档。</param>
    /// <returns>每次调用返回一个在全文档范围内唯一的标识。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> 为 <see langword="null" /> 时抛出。</exception>
    public static Func<string> CreateFactory(LyricsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        HashSet<string> used = new(StringComparer.Ordinal);
        long next = CollectNamespace(document, used);

        return () =>
        {
            while (true)
            {
                if (next > int.MaxValue)
                {
                    throw new OverflowException("文本元素标识编号已耗尽。");
                }

                string candidate = string.Create(CultureInfo.InvariantCulture, $"token-{next++:D4}");
                if (used.Add(candidate))
                {
                    return candidate;
                }
            }
        };
    }

    /// <summary>取文档里现有全命名空间中 <c>token-</c> 标识之后的下一个编号。</summary>
    /// <param name="document">当前歌词文档。</param>
    /// <returns>下一个可用编号（至少 1）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> 为 <see langword="null" /> 时抛出。</exception>
    /// <exception cref="OverflowException">现有编号已达到 <see cref="int.MaxValue" /> 时抛出。</exception>
    public static int NextNumber(LyricsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return checked((int)CollectNamespace(document, used: null));
    }

    private static long CollectNamespace(LyricsDocument document, HashSet<string>? used)
    {
        long next = 1;

        void Observe(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            used?.Add(id);

            if (id.StartsWith(TokenPrefix, StringComparison.Ordinal)
                && int.TryParse(
                    id.AsSpan(TokenPrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int number)
                && number >= next)
            {
                next = (long)number + 1;
            }
        }

        foreach (LyricBranch branch in document.Branches)
        {
            Observe(branch.Id);

            foreach (LyricLine line in branch.Lines)
            {
                Observe(line.Id);

                foreach (LyricToken token in line.Tokens)
                {
                    Observe(token.Id);
                }

                foreach (RubyAnnotation ruby in line.RubyAnnotations)
                {
                    Observe(ruby.Id);

                    foreach (RubyTextSegment segment in ruby.Segments)
                    {
                        Observe(segment.Id);
                    }
                }
            }
        }

        return next;
    }
}
