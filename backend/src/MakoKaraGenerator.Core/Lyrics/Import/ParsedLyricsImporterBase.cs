using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 非 LRC 家族文本导入器的公共骨架（ADR-0046）：
/// 解码 → 解析成 <see cref="ParsedLyrics" /> → 推导行区间 → 建模型 → 出报告。
/// </summary>
/// <remarks>
/// <para>
/// 子类只做一件事：把文本变成中间的"行 / 音节 / 时间"结构。
/// 行区间推导、标识分配、文本元素与行文本的一致性校验、报告计数都在这里统一处理，
/// 免得每种格式各写一套、慢慢走偏。
/// </para>
/// <para>
/// 时间语义：格式明确给出的时间标 <see cref="TimingStatus.Imported" />；
/// 只有格式给起点、终点由"下一个元素的起点"或"行终点"补齐的，同样算导入值
/// （这是格式固有语义，例如增强 LRC 的 <c>&lt;mm:ss.xx&gt;</c> 只有起点）；
/// 只有软件按比例推导的时间才标 <see cref="TimingStatus.Estimated" />。
/// </para>
/// </remarks>
public abstract class ParsedLyricsImporterBase : ILyricsImporter
{
    /// <inheritdoc/>
    public abstract string Id { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract string SourceFormat { get; }

    /// <inheritdoc/>
    public abstract bool CanImport(string fileName, ReadOnlySpan<byte> header);

    /// <inheritdoc/>
    public abstract string MatchReason(string fileName, ReadOnlySpan<byte> header);

    /// <inheritdoc/>
    public LyricsImportResult Import(Stream input, LyricsImportContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        using MemoryStream buffer = new();
        input.CopyTo(buffer);

        LyricsTextInfo text = LyricsTextDecoder.Decode(buffer.ToArray(), context.EncodingOverride);
        BudgetedImportList<LyricsImportIssue> issues = new(context.PreparationGuard);

        if (text.IsFallbackDecoding)
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.EncodingAssumed,
                IssueSeverity.Warning,
                0,
                text.EncodingName,
                $"文件不是 UTF-8，且运行时无法确定原始编码，已按 {text.EncodingName} 近似解码；文本可能不正确。"));
        }

        ParsedLyrics parsed = ParseForPreparation(text.Text, context, issues);

        LyricsIdAllocator ids = new();
        context.PreparationGuard?.Reserve(64L + 16L * parsed.Lines.Length);
        ImmutableArray<LyricLine>.Builder lines = ImmutableArray.CreateBuilder<LyricLine>(parsed.Lines.Length);
        int derivedLineEnds = 0;
        int fallbackLineEnds = 0;

        for (int i = 0; i < parsed.Lines.Length; i++)
        {
            ParsedLyricsLine parsedLine = parsed.Lines[i];
            TimeRange range = ResolveLineRange(parsed, i, context, issues, ref derivedLineEnds, ref fallbackLineEnds);
            ImmutableArray<LyricToken> tokens = BuildTokens(parsedLine, range, ids, issues);
            string lineId = ids.NextLineId();
            LyricLine line = LyricLine.Create(lineId, parsedLine.Text, range, TimingStatus.Imported, tokens);
            ImmutableArray<RubyAnnotation> ruby = BuildRubyAnnotations(parsedLine, tokens, ids, issues);
            lines.Add(line with { RubyAnnotations = ruby });
        }

        ImmutableArray<string> discarded = parsed.DiscardedFeatures.IsDefaultOrEmpty
            ? []
            : parsed.DiscardedFeatures;

        foreach (string feature in discarded)
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.DiscardedFeature,
                IssueSeverity.Info,
                0,
                feature,
                $"来源格式里的「{feature}」在本项目里没有对应表达，已丢弃。"));
        }

        LyricsDocument document = LyricsDocument.Create(lines, context.Language);
        int tokenCount = 0;
        int timedCount = 0;
        int estimatedCount = 0;

        foreach (LyricLine line in document.Lines)
        {
            foreach (LyricToken token in line.Tokens)
            {
                tokenCount++;

                if (token.Time is null)
                {
                    continue;
                }

                timedCount++;

                if (token.TimingStatus == TimingStatus.Estimated)
                {
                    estimatedCount++;
                }
            }
        }

        LyricsImportReport report = new()
        {
            ImporterId = Id,
            ImporterName = DisplayName,
            SourceFormat = SourceFormat,
            SourceLineCount = parsed.SourceLineCount,
            LineCount = document.Lines.Length,
            TokenCount = tokenCount,
            TimedTokenCount = timedCount,
            MissingTokenCount = tokenCount - timedCount,
            EstimatedTokenCount = estimatedCount,
            SkippedLineCount = issues.Count(issue => issue.Kind is LyricsImportIssueKind.TextWithoutTimestamp or LyricsImportIssueKind.EmptyText),
            MergedContinuationCount = 0,
            DerivedLineEndCount = derivedLineEnds,
            FallbackLineEndCount = fallbackLineEnds,
            OffsetUs = 0L,
            EncodingName = text.EncodingName,
            HadByteOrderMark = text.HadByteOrderMark,
            NewLineKind = text.NewLineKind,
            Issues = issues,
            DiscardedFeatures = discarded,
        };

        return new LyricsImportResult(document, report);
    }

    private static ImmutableArray<RubyAnnotation> BuildRubyAnnotations(
        ParsedLyricsLine parsed,
        ImmutableArray<LyricToken> tokens,
        LyricsIdAllocator ids,
        BudgetedImportList<LyricsImportIssue> issues)
    {
        if (parsed.Ruby.IsDefaultOrEmpty)
            return [];

        ImmutableArray<RubyAnnotation>.Builder result = ImmutableArray.CreateBuilder<RubyAnnotation>(parsed.Ruby.Length);
        foreach (ParsedLyricsRuby source in parsed.Ruby)
        {
            if (source.BaseTokenIndex < 0 || source.BaseTokenIndex >= tokens.Length)
            {
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Warning,
                    parsed.SourceLineNumber, source.Text, "注音无法唯一绑定基础元素，已保留为导入诊断。"));
                continue;
            }

            ImmutableArray<RubyTextSegment>.Builder segments = ImmutableArray.CreateBuilder<RubyTextSegment>(source.Segments.Length);
            for (int i = 0; i < source.Segments.Length; i++)
            {
                ParsedLyricsRubySegment segment = source.Segments[i];
                TimeRange? time = null;
                TimingStatus status = TimingStatus.Missing;
                if (segment.StartUs is { } start && segment.EndUs is { } end && end > start)
                {
                    time = TimeRange.Create(Math.Max(0L, start), end);
                    status = segment.IsEstimated ? TimingStatus.Estimated : TimingStatus.Imported;
                }
                else if (segment.StartUs is not null || segment.EndUs is not null)
                {
                    issues.Add(new LyricsImportIssue(LyricsImportIssueKind.WordTimeOutsideLine, IssueSeverity.Warning,
                        parsed.SourceLineNumber, segment.Text, "注音片段时间无效，已按未计时片段保留文本。"));
                }

                segments.Add(RubyTextSegment.Create(ids.NextRubySegmentId(), segment.Text, time, status));
            }

            if (segments.Count == 0)
                continue;

            string text = string.Concat(segments.Select(segment => segment.Text));
            result.Add(new RubyAnnotation
            {
                Id = ids.NextRubyId(),
                BaseTokenId = tokens[source.BaseTokenIndex].Id,
                Text = text,
                Language = source.Language,
                Segments = segments.ToImmutable(),
            });
        }

        return result.ToImmutable();
    }

    /// <summary>把文本解析成格式无关的中间表示。</summary>
    /// <param name="text">已解码的文本。</param>
    /// <param name="context">导入条件。</param>
    /// <param name="issues">问题收集列表（可追加）。</param>
    /// <returns>解析结果。</returns>
    protected abstract ParsedLyrics Parse(string text, LyricsImportContext context, List<LyricsImportIssue> issues);

    /// <summary>有界导入入口；保留原扩展导入器的 List 接口。</summary>
    protected virtual ParsedLyrics ParseForPreparation(string text, LyricsImportContext context, IList<LyricsImportIssue> issues)
    {
        List<LyricsImportIssue> original = [.. issues];
        int count = original.Count;
        var result = Parse(text, context, original);
        foreach (var issue in original.Skip(count)) issues.Add(issue);
        return result;
    }

    /// <summary>推导一行的区间：优先用格式给的终点，其次下一行起点，再次音频时长，最后兜底时长。</summary>
    private static TimeRange ResolveLineRange(
        ParsedLyrics parsed,
        int index,
        LyricsImportContext context,
        BudgetedImportList<LyricsImportIssue> issues,
        ref int derivedLineEnds,
        ref int fallbackLineEnds)
    {
        ParsedLyricsLine line = parsed.Lines[index];
        long start = Math.Max(0L, line.StartUs);

        if (line.EndUs is { } explicitEnd && explicitEnd > start)
        {
            return TimeRange.Create(start, explicitEnd);
        }

        if (index + 1 < parsed.Lines.Length && parsed.Lines[index + 1].StartUs > start)
        {
            derivedLineEnds++;
            return TimeRange.Create(start, parsed.Lines[index + 1].StartUs);
        }

        if (context.MediaDurationUs is { } mediaDurationUs && mediaDurationUs > start)
        {
            derivedLineEnds++;
            return TimeRange.Create(start, mediaDurationUs);
        }

        fallbackLineEnds++;
        issues.Add(new LyricsImportIssue(
            LyricsImportIssueKind.LineEndFallback,
            IssueSeverity.Warning,
            line.SourceLineNumber,
            line.Text,
            $"行终点无法由下一行推导，已按兜底时长 {TimeUs.ToSeconds(context.FallbackLineDurationUs):0.###} 秒结束。"));

        return TimeRange.Create(start, start + context.FallbackLineDurationUs);
    }

    /// <summary>把一行解析结果变成文本元素（格式没给逐字时按 Unicode 文本元素切出未定时的载体）。</summary>
    private static ImmutableArray<LyricToken> BuildTokens(
        ParsedLyricsLine parsed,
        TimeRange range,
        LyricsIdAllocator ids,
        BudgetedImportList<LyricsImportIssue> issues)
    {
        ImmutableArray<ParsedLyricsToken> source = parsed.Tokens.IsDefaultOrEmpty ? [] : parsed.Tokens;

        if (source.Length == 0)
        {
            ImmutableArray<LyricToken>.Builder plain = ImmutableArray.CreateBuilder<LyricToken>();

            foreach (TextElementRange element in GraphemeSplitter.Enumerate(parsed.Text))
            {
                plain.Add(LyricsImportPreparationGuard.Token(LyricsImportPreparationGuard.Of(issues), ids.NextTokenId(), element.Text));
            }

            return plain.ToImmutable();
        }

        LyricsImportPreparationGuard.Of(issues)?.Reserve(64L + 16L * source.Length);
        ImmutableArray<LyricToken>.Builder tokens = ImmutableArray.CreateBuilder<LyricToken>(source.Length);

        for (int i = 0; i < source.Length; i++)
        {
            ParsedLyricsToken token = source[i];
            long rawStart = Math.Max(0L, token.StartUs);
            long rawEnd = token.EndUs is { } explicitEnd && explicitEnd > rawStart
                ? explicitEnd
                : NextStartOrLineEnd(source, i, rawStart, range);

            long start = Math.Clamp(rawStart, range.StartUs, Math.Max(range.StartUs, range.EndUs - 1));
            long end = Math.Clamp(rawEnd, start + 1, range.EndUs);

            if (start != rawStart || end != rawEnd)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.WordTimeOutsideLine,
                    IssueSeverity.Info,
                    token.SourceLineNumber,
                    token.Text,
                    $"元素「{token.Text}」的时间已钳制进行区间（原文 {TimeUs.ToSeconds(rawStart):0.###}~{TimeUs.ToSeconds(rawEnd):0.###} 秒）。"));
            }

            TimingStatus status = token.IsEstimated ? TimingStatus.Estimated : TimingStatus.Imported;
            tokens.Add(LyricsImportPreparationGuard.Token(LyricsImportPreparationGuard.Of(issues), ids.NextTokenId(), token.Text, TimeRange.Create(start, end), status));
        }

        if (!string.Concat(tokens.Select(token => token.Text)).Equals(parsed.Text, StringComparison.Ordinal))
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.TokenTextMismatch,
                IssueSeverity.Warning,
                parsed.SourceLineNumber,
                parsed.Text,
                "解析出的元素文本与行文本不一致，已按 Unicode 文本元素重建该行（时间置为缺失）。"));

            ImmutableArray<LyricToken>.Builder rebuilt = ImmutableArray.CreateBuilder<LyricToken>();

            foreach (TextElementRange element in GraphemeSplitter.Enumerate(parsed.Text))
            {
                rebuilt.Add(LyricsImportPreparationGuard.Token(LyricsImportPreparationGuard.Of(issues), ids.NextTokenId(), element.Text));
            }

            return rebuilt.ToImmutable();
        }

        return tokens.ToImmutable();
    }

    private static long NextStartOrLineEnd(ImmutableArray<ParsedLyricsToken> source, int index, long start, TimeRange range) =>
        index + 1 < source.Length && source[index + 1].StartUs > start
            ? source[index + 1].StartUs
            : range.EndUs;
}
