using MakoKaraGenerator.Core.Time;
using System.Collections.Immutable;
using System.Text;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics.Lrc;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 增强型 LRC 导入器：识别行内的逐字时间标签。
/// </summary>
/// <remarks>
/// <para>
/// 常见写法是 <c>[00:12.00]&lt;00:12.00&gt;夜&lt;00:12.50&gt;空中</c>：
/// 每个 <c>&lt;mm:ss.xx&gt;</c> 表示"紧随其后的文本从这一刻开始"。
/// 行文本会去掉这些标签，元素的文本<b>逐字拼接</b>回来正好等于行文本
/// （否则项目校验会报"元素文本与行文本不一致"）。
/// </para>
/// <para>
/// 该行没有任何逐字标签时，行为与普通 LRC 一致：元素时间为
/// <see cref="TimingStatus.Missing" />。绝不把行时间平均分配给每个字。
/// </para>
/// <para>
/// 逐字标签的时间可能越出所属行、或不是递增的（不同工具的输出质量差异很大）。
/// 这类情况一律<b>钳制进合法范围并逐条记录诊断</b>，而不是静默修正
/// （docs/development.md §19）。
/// </para>
/// </remarks>
public sealed class EnhancedLrcImporter : LrcImporterBase
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "lrc.enhanced";

    private readonly EnhancedLrcOptions _wordOptions;

    /// <summary>创建一个增强 LRC 导入器。</summary>
    /// <param name="parserOptions">行级解析选项；为 <see langword="null" /> 时使用默认。</param>
    /// <param name="wordOptions">逐字解析策略；为 <see langword="null" /> 时使用默认。</param>
    public EnhancedLrcImporter(LrcParserOptions? parserOptions = null, EnhancedLrcOptions? wordOptions = null)
        : base(parserOptions) =>
        _wordOptions = wordOptions ?? EnhancedLrcOptions.Default;

    /// <inheritdoc/>
    public override string Id => IdValue;

    /// <inheritdoc/>
    public override string DisplayName => "增强 LRC（含逐字时间）";

    /// <inheritdoc/>
    public override string SourceFormat =>
        $"增强 LRC 逐字标签 {_wordOptions.TagOpen}mm:ss.xx{_wordOptions.TagClose}";

    /// <inheritdoc/>
    public override bool CanImport(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        // 逐字标签本身才是有力证据：扩展名相同的情况下（.lrc）由它决定用哪个导入器。
        return LooksLikeEnhancedLrc(header)
            || fileName.EndsWith(".elrc", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override string MatchReason(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return LooksLikeEnhancedLrc(header)
            ? "文件里含逐字时间标签 <mm:ss.xx>，按增强 LRC 导入（优先于普通 LRC）"
            : "扩展名为 .elrc，按增强 LRC 导入（未发现逐字标签时逐字时间为空）";
    }

    /// <summary>判断一段字节开头是否含逐字时间标签的形状（<c>&lt;数字...:</c>）。</summary>
    /// <param name="header">文件开头字节。</param>
    /// <returns>像增强 LRC 时为 <see langword="true" />。</returns>
    internal static bool LooksLikeEnhancedLrc(ReadOnlySpan<byte> header)
    {
        for (int i = 0; i + 3 < header.Length; i++)
        {
            if (header[i] == (byte)'<'
                && header[i + 1] is >= (byte)'0' and <= (byte)'9'
                && header[i + 2] is >= (byte)'0' and <= (byte)'9'
                && header[i + 3] == (byte)':')
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

        BudgetedImportList<EnhancedSegment> segments = ParseSegments(entry, issues);
        string lineText = string.Concat(segments.Select(segment => segment.Text));

        if (!segments.Any(segment => segment.StartUs is not null))
        {
            // 没有逐字标签：与普通 LRC 一致，全部元素时间为缺失。
            return new ImportedLineTokens(CreateMissingTokens(entry.Text, ids, LyricsImportPreparationGuard.Of(issues)), entry.Text);
        }

        BudgetedImportList<LyricToken> tokens = new(LyricsImportPreparationGuard.Of(issues));
        BudgetedImportList<int> timedIndexes = new(LyricsImportPreparationGuard.Of(issues));
        BudgetedImportList<long> timedStarts = new(LyricsImportPreparationGuard.Of(issues));

        foreach (EnhancedSegment segment in segments)
        {
            foreach ((string text, bool isWhitespace) in SplitWhitespace(segment.Text, _wordOptions.WhitespaceIsUntimed))
            {
                bool timed = segment.StartUs is not null
                    && !(isWhitespace && _wordOptions.WhitespaceIsUntimed);

                if (timed)
                {
                    // 时间点先记下来，钳制与配对在下面统一做。
                    tokens.Add(LyricsImportPreparationGuard.Token(LyricsImportPreparationGuard.Of(issues), ids.NextTokenId(), text));
                    timedIndexes.Add(tokens.Count - 1);
                    timedStarts.Add(segment.StartUs!.Value);
                }
                else
                {
                    tokens.Add(LyricsImportPreparationGuard.Token(LyricsImportPreparationGuard.Of(issues), ids.NextTokenId(), text));
                }
            }
        }

        ClampStarts(entry, range, tokens, timedIndexes, timedStarts, issues);
        DropDuplicateStarts(entry, tokens, timedIndexes, timedStarts, issues);
        AssignEnds(range, tokens, timedIndexes, timedStarts);
        timedTokenCount += timedIndexes.Count;

        return new ImportedLineTokens([.. tokens], lineText);
    }

    /// <summary>按文本元素创建一批"没有时间"的元素（文本拼接等于输入）。</summary>
    private static ImmutableArray<LyricToken> CreateMissingTokens(string text, LyricsIdAllocator ids, LyricsImportPreparationGuard? guard)
    {
        ImmutableArray<LyricToken>.Builder builder = ImmutableArray.CreateBuilder<LyricToken>();

        foreach (Text.TextElementRange element in Text.GraphemeSplitter.Enumerate(text))
        {
            builder.Add(LyricsImportPreparationGuard.Token(guard, ids.NextTokenId(), element.Text));
        }

        return builder.ToImmutable();
    }

    /// <summary>把逐字标签时间钳制进行范围，并保证严格递增；无法容纳的元素退化为"缺失"。</summary>
    private static void ClampStarts(
        LrcEntry entry,
        TimeRange range,
        BudgetedImportList<LyricToken> tokens,
        BudgetedImportList<int> timedIndexes,
        BudgetedImportList<long> timedStarts,
        IList<LyricsImportIssue> issues)
    {
        int writable = 0;
        long previous = range.StartUs;

        for (int i = 0; i < timedIndexes.Count; i++)
        {
            long startUs = timedStarts[i];

            if (startUs < range.StartUs)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.WordTimeOutsideLine,
                    IssueSeverity.Warning,
                    entry.SourceLineNumber,
                    tokens[timedIndexes[i]].Text,
                    $"逐字时间 {Time.TimeUs.FormatClockShort(startUs)} 早于行起点，已钳制到行起点。"));
                startUs = range.StartUs;
            }

            if (startUs < previous)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.WordTimesNotIncreasing,
                    IssueSeverity.Warning,
                    entry.SourceLineNumber,
                    tokens[timedIndexes[i]].Text,
                    $"逐字时间 {Time.TimeUs.FormatClockShort(startUs)} 早于前一个元素的起点，已顺延到 {Time.TimeUs.FormatClockShort(previous)}。"));
                startUs = previous;
            }

            if (startUs >= range.EndUs)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.WordTimeOutsideLine,
                    IssueSeverity.Warning,
                    entry.SourceLineNumber,
                    tokens[timedIndexes[i]].Text,
                    $"逐字时间 {Time.TimeUs.FormatClockShort(startUs)} 已越过行终点，该元素退化为“时间缺失”。"));
                continue;
            }

            timedStarts[writable] = startUs;
            timedIndexes[writable] = timedIndexes[i];
            writable++;
            previous = startUs;
        }

        // 被判定为容纳不下的元素从"有时间"的列表里去掉（它们的元素本身仍然保留）。
        timedIndexes.RemoveRange(writable, timedIndexes.Count - writable);
        timedStarts.RemoveRange(writable, timedStarts.Count - writable);
    }

    /// <summary>
    /// 丢掉起点与前一元素相同的元素（它们会算出零长度区间），并记为诊断。
    /// </summary>
    /// <remarks>
    /// 不这么做的话，<see cref="AssignEnds" /> 会因为"终点等于起点"而跳过它，
    /// 元素<b>静默</b>变成"时间缺失"——用户永远不知道发生了什么。
    /// 保留先出现的那个：文件顺序是唯一的依据。
    /// </remarks>
    private static void DropDuplicateStarts(
        LrcEntry entry,
        BudgetedImportList<LyricToken> tokens,
        BudgetedImportList<int> timedIndexes,
        BudgetedImportList<long> timedStarts,
        IList<LyricsImportIssue> issues)
    {
        int writable = 0;

        for (int i = 0; i < timedIndexes.Count; i++)
        {
            if (i > 0 && timedStarts[i] == timedStarts[writable - 1])
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.WordTimesNotIncreasing,
                    IssueSeverity.Warning,
                    entry.SourceLineNumber,
                    tokens[timedIndexes[i]].Text,
                    $"逐字时间 {Time.TimeUs.FormatClockShort(timedStarts[i])} 与前一个元素相同，缺少可用的时间区间，该元素退化为“时间缺失”。"));
                continue;
            }

            timedIndexes[writable] = timedIndexes[i];
            timedStarts[writable] = timedStarts[i];
            writable++;
        }

        timedIndexes.RemoveRange(writable, timedIndexes.Count - writable);
        timedStarts.RemoveRange(writable, timedStarts.Count - writable);
    }

    /// <summary>把每个已定起点的元素的终点接到下一个元素的起点（最后一个接到行终点）。</summary>
    private static void AssignEnds(
        TimeRange range,
        BudgetedImportList<LyricToken> tokens,
        BudgetedImportList<int> timedIndexes,
        BudgetedImportList<long> timedStarts)
    {
        for (int i = 0; i < timedIndexes.Count; i++)
        {
            long startUs = timedStarts[i];
            long endUs = i + 1 < timedStarts.Count ? timedStarts[i + 1] : range.EndUs;

            if (endUs <= startUs)
            {
                continue;
            }

            int index = timedIndexes[i];
            tokens[index] = tokens[index] with
            {
                Time = TimeRange.Create(startUs, endUs),
                TimingStatus = TimingStatus.Imported,
            };
        }
    }

    /// <summary>把一段文本切成"空白"与"非空白"的连续片段。</summary>
    /// <param name="text">文本。</param>
    /// <param name="splitWhitespace">是否把空白单独切出来（关闭时整段作为一个元素）。</param>
    /// <returns>片段序列。</returns>
    private static IEnumerable<(string Text, bool IsWhitespace)> SplitWhitespace(string text, bool splitWhitespace)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        if (!splitWhitespace)
        {
            yield return (text, false);
            yield break;
        }

        int start = 0;
        bool current = char.IsWhiteSpace(text[0]);

        for (int i = 1; i < text.Length; i++)
        {
            bool isWhite = char.IsWhiteSpace(text[i]);

            if (isWhite == current)
            {
                continue;
            }

            yield return (text[start..i], current);
            start = i;
            current = isWhite;
        }

        yield return (text[start..], current);
    }

    /// <summary>扫描行文本，切出"逐字标签 + 其后文本"的片段。</summary>
    private BudgetedImportList<EnhancedSegment> ParseSegments(LrcEntry entry, IList<LyricsImportIssue> issues)
    {
        BudgetedImportList<EnhancedSegment> segments = new(LyricsImportPreparationGuard.Of(issues));
        BudgetedImportText buffer = new(LyricsImportPreparationGuard.Of(issues));
        long? startUs = null;
        int position = 0;
        string text = entry.Text;
        bool reported = false;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            segments.Add(new EnhancedSegment(startUs, buffer.ToString()));
            buffer.Clear();
        }

        while (position < text.Length)
        {
            if (text[position] == _wordOptions.TagOpen)
            {
                int close = text.IndexOf(_wordOptions.TagClose, position + 1);

                if (close < 0)
                {
                    // 标签没闭合：剩下的都当字面文本，并记录一次。
                    if (!reported)
                    {
                        issues.Add(new LyricsImportIssue(
                            LyricsImportIssueKind.MalformedTag,
                            IssueSeverity.Warning,
                            entry.SourceLineNumber,
                            text[position..],
                            "逐字时间标签没有闭合，已按普通文本保留。"));
                        reported = true;
                    }

                    buffer.Append(text, position, text.Length - position);
                    position = text.Length;
                    break;
                }

                ReadOnlySpan<char> inner = text.AsSpan(position + 1, close - position - 1);

                if (LrcTimeTagParser.TryParse(inner, out long tagTimeUs))
                {
                    Flush();
                    startUs = tagTimeUs;
                }
                else
                {
                    // 无法识别的标记：原文保留在文本里（供用户对照），并记诊断。
                    if (!reported)
                    {
                        issues.Add(new LyricsImportIssue(
                            LyricsImportIssueKind.MalformedTag,
                            IssueSeverity.Warning,
                            entry.SourceLineNumber,
                            text[position..(close + 1)],
                            $"无法识别的逐字标签 '{text[position..(close + 1)]}'，已按普通文本保留。"));
                        reported = true;
                    }

                    buffer.Append(text, position, close - position + 1);
                }

                position = close + 1;
                continue;
            }

            int nextTag = text.IndexOf(_wordOptions.TagOpen, position);
            int end = nextTag < 0 ? text.Length : nextTag;
            buffer.Append(text, position, end - position);
            position = end;
        }

        Flush();
        return segments;
    }

    /// <summary>行内的一段文本及其起点（<see langword="null" /> 表示没有逐字时间）。</summary>
    private readonly record struct EnhancedSegment(long? StartUs, string Text);
}
