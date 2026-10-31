using MakoKaraGenerator.Core.Time;
using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics.Lrc;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 普通 LRC 导入器：只得到行级时间。
/// </summary>
/// <remarks>
/// <para>
/// 按 docs/development.md §7.2：只生成行级时间，逐字时间一律置为
/// <see cref="TimingStatus.Missing" />，绝不把行时间平均分配给每个字后冒充实测数据。
/// </para>
/// <para>
/// 元素仍然会按 Unicode 文本元素切出来（且拼接后与行文本完全一致），
/// 因为"补齐逐字时间"这件事需要一个可以逐个打点的载体；
/// 但它们的<b>时间</b>是空的，状态是"缺失"。
/// </para>
/// </remarks>
public sealed class PlainLrcImporter : LrcImporterBase
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "lrc.plain";

    /// <summary>创建一个普通 LRC 导入器。</summary>
    /// <param name="parserOptions">行级解析选项；为 <see langword="null" /> 时使用默认。</param>
    public PlainLrcImporter(LrcParserOptions? parserOptions = null)
        : base(parserOptions)
    {
    }

    /// <inheritdoc/>
    public override string Id => IdValue;

    /// <inheritdoc/>
    public override string DisplayName => "普通 LRC（仅行级时间）";

    /// <inheritdoc/>
    public override string SourceFormat => "普通 LRC 行时间标签 [mm:ss.xx]";
    /// <inheritdoc/>
    public override bool CanImport(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        // 扩展名优先；其次看开头是否像 LRC（有 [数字:数字] 形状）。
        // 增强 LRC 也符合这两个条件，因此注册表里必须让增强导入器先跑。
        if (fileName.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LooksLikeLrc(header);
    }

    /// <inheritdoc/>
    public override string MatchReason(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (fileName.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase))
        {
            return "扩展名 .lrc，且文件里没有逐字时间标签 <mm:ss.xx>，因此按行级时间导入";
        }

        return fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? "扩展名 .txt，且内容不符合 UltraStar（没有 #BPM 等头部），按 LRC 行时间标签导入"
            : "扩展名未登记，但开头是 LRC 时间标签 [mm:ss.xx]";
    }

    /// <summary>判断一段字节开头是否像 LRC 时间标签。</summary>
    /// <param name="header">文件开头字节。</param>
    /// <returns>像 LRC 时为 <see langword="true" />。</returns>
    internal static bool LooksLikeLrc(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        // 只看 ASCII 部分即可：']' 与 ':' 都是 ASCII，UTF-8/GB18030 都不会把它们编码到高字节。
        bool sawBracket = false;
        bool sawColon = false;

        foreach (byte b in header)
        {
            if (b == (byte)'[')
            {
                sawBracket = true;
            }
            else if (b == (byte)':')
            {
                sawColon = true;
            }
            else if (b == (byte)']' && sawBracket && sawColon)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    protected override ImportedLineTokens BuildLine(LrcEntry entry, TimeRange range, LyricsIdAllocator ids,
        List<LyricsImportIssue> issues, ref int timedTokenCount) =>
        BuildLineForPreparation(entry, range, ids, issues, ref timedTokenCount);

    /// <inheritdoc />
    protected override ImportedLineTokens BuildLineForPreparation(
        LrcEntry entry,
        TimeRange range,
        LyricsIdAllocator ids,
        IList<LyricsImportIssue> issues,
        ref int timedTokenCount)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(issues);

        IEnumerable<TextElementRange> elements = GraphemeSplitter.Enumerate(entry.Text);
        ImmutableArray<LyricToken>.Builder builder = ImmutableArray.CreateBuilder<LyricToken>();

        foreach (TextElementRange element in elements)
        {
            // 状态 Missing 要求时间为 null（LyricToken.Create 会做自洽校验）。
            builder.Add(LyricsImportPreparationGuard.Token(LyricsImportPreparationGuard.Of(issues), ids.NextTokenId(), element.Text));
        }

        // 普通 LRC 不含任何逐字时间，因此 timedTokenCount 保持不变。
        _ = range;
        _ = timedTokenCount;
        return new ImportedLineTokens(builder.ToImmutable(), entry.Text);
    }
}
