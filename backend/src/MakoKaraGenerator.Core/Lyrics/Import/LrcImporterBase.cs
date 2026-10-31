using MakoKaraGenerator.Core.Time;
using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics.Lrc;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// LRC 家族导入器的公共骨架：解码 → 解析 → 推导行区间 → 建模型 → 出报告。
/// </summary>
/// <remarks>
/// 各格式的差别只在"一行文本怎么变成文本元素"，
/// 因此把它留成 <see cref="BuildLine" /> 一个口子，其余流程共用，
/// 避免不同格式在行区间、编号、报告口径上慢慢走偏。
/// </remarks>
public abstract class LrcImporterBase : ILyricsImporter
{
    /// <summary>创建一个导入器。</summary>
    /// <param name="parserOptions">行级解析选项；为 <see langword="null" /> 时使用默认。</param>
    protected LrcImporterBase(LrcParserOptions? parserOptions = null) =>
        ParserOptions = parserOptions ?? LrcParserOptions.Default;

    /// <inheritdoc/>
    public abstract string Id { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <summary>识别到的语法说明，写进导入报告。</summary>
    public abstract string SourceFormat { get; }

    /// <summary>行级解析选项。</summary>
    protected LrcParserOptions ParserOptions { get; }

    /// <inheritdoc/>
    public abstract bool CanImport(string fileName, ReadOnlySpan<byte> header);

    /// <inheritdoc/>
    public abstract string MatchReason(string fileName, ReadOnlySpan<byte> header);

    /// <inheritdoc/>
    public LyricsImportResult Import(Stream input, LyricsImportContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        LyricsTextInfo text = LyricsTextDecoder.Decode(input, context.EncodingOverride);
        LrcDocument lrc = LrcParser.Parse(text.Text, ParserOptions, context.PreparationGuard);

        BudgetedImportList<LyricsImportIssue> issues = new(context.PreparationGuard);
        BudgetedImportList<LyricToken> tokens = new(context.PreparationGuard);
        BudgetedImportList<LyricLine> lines = new(context.PreparationGuard);

        issues.AddRange(MapDiagnostics(lrc.Diagnostics));

        if (text.IsFallbackDecoding)
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.EncodingAssumed,
                IssueSeverity.Warning,
                0,
                text.EncodingName,
                $"文件不是 UTF-8，且运行时无法确定原始编码，已按 {text.EncodingName} 近似解码；文本可能不正确。"));
        }

        context.PreparationGuard?.Reserve(128L + 80L * lrc.Entries.Count);
        long[] starts = [.. lrc.Entries.Select(entry => entry.StartUs)];
        PlannedLineRange[] ranges = LineRangePlanner.Plan(starts, context.MediaDurationUs, context.FallbackLineDurationUs);

        LyricsIdAllocator ids = new();
        int timedTokens = 0;
        int derivedLineEnds = 0;
        int fallbackLineEnds = 0;

        for (int i = 0; i < lrc.Entries.Count; i++)
        {
            LrcEntry entry = lrc.Entries[i];
            PlannedLineRange planned = ranges[i];

            switch (planned.Source)
            {
                case LineEndSource.NextLineStart:
                    derivedLineEnds++;
                    break;

                case LineEndSource.FallbackDuration:
                    fallbackLineEnds++;
                    issues.Add(new LyricsImportIssue(
                        LyricsImportIssueKind.LineEndFallback,
                        IssueSeverity.Warning,
                        entry.SourceLineNumber,
                        entry.Text,
                        $"行终点无法由下一行推导，已按兜底时长 {Time.TimeUs.ToSeconds(context.FallbackLineDurationUs):0.###} 秒结束。"));
                    break;

                default:
                    break;
            }

            ImportedLineTokens built = BuildLineForPreparation(
                entry,
                planned.Range,
                ids,
                issues,
                ref timedTokens);

            tokens.AddRange(built.Tokens);

            lines.Add(LyricLine.Create(
                ids.NextLineId(),
                built.LineText,
                planned.Range,
                TimingStatus.Imported,
                built.Tokens));
        }

        LyricsDocument document = LyricsDocument.Create(lines, context.Language);

        LyricsImportReport report = new()
        {
            ImporterId = Id,
            ImporterName = DisplayName,
            SourceFormat = SourceFormat,
            SourceLineCount = lrc.SourceLineCount,
            LineCount = lines.Count,
            TokenCount = tokens.Count,
            TimedTokenCount = timedTokens,
            MissingTokenCount = tokens.Count - timedTokens,
            EstimatedTokenCount = 0,
            SkippedLineCount = lrc.Diagnostics.Count(d => d.Kind == LrcDiagnosticKind.EmptyText)
                + lrc.Diagnostics.Count(d => d.Kind == LrcDiagnosticKind.TextWithoutTimestamp),
            MergedContinuationCount = lrc.Entries.Count(entry => entry.IsContinuation),
            DerivedLineEndCount = derivedLineEnds,
            FallbackLineEndCount = fallbackLineEnds,
            OffsetUs = lrc.OffsetUs,
            EncodingName = text.EncodingName,
            HadByteOrderMark = text.HadByteOrderMark,
            NewLineKind = text.NewLineKind,
            Issues = issues,
        };

        return new LyricsImportResult(document, report);
    }

    /// <summary>
    /// 把一行的文本变成文本元素与行文本。
    /// </summary>
    /// <param name="entry">行级解析结果。</param>
    /// <param name="range">该行的时间区间。</param>
    /// <param name="ids">标识分配器。</param>
    /// <param name="issues">问题收集列表（可追加）。</param>
    /// <param name="timedTokenCount">带时间的元素计数（可累加）。</param>
    /// <returns>元素序列与行文本；元素文本<b>逐字拼接</b>必须等于行文本。</returns>
    protected abstract ImportedLineTokens BuildLine(
        LrcEntry entry,
        TimeRange range,
        LyricsIdAllocator ids,
        List<LyricsImportIssue> issues,
        ref int timedTokenCount);

    /// <summary>有界导入入口；原扩展导入器默认委托原 List 接口，内置导入器共享有界集合。</summary>
    protected virtual ImportedLineTokens BuildLineForPreparation(LrcEntry entry, TimeRange range, LyricsIdAllocator ids,
        IList<LyricsImportIssue> issues, ref int timedTokenCount)
    {
        List<LyricsImportIssue> original = [.. issues];
        int count = original.Count;
        var result = BuildLine(entry, range, ids, original, ref timedTokenCount);
        foreach (var issue in original.Skip(count)) issues.Add(issue);
        return result;
    }

    /// <summary>把 LRC 解析诊断映射成格式无关的导入问题。</summary>
    /// <param name="diagnostics">LRC 诊断。</param>
    /// <returns>导入问题序列。</returns>
    private static IEnumerable<LyricsImportIssue> MapDiagnostics(IReadOnlyList<LrcDiagnostic> diagnostics)
    {
        foreach (LrcDiagnostic diagnostic in diagnostics)
        {
            yield return new LyricsImportIssue(
                MapKind(diagnostic.Kind),
                MapSeverity(diagnostic.Kind),
                diagnostic.SourceLineNumber,
                diagnostic.RawText,
                diagnostic.Message);
        }
    }

    private static LyricsImportIssueKind MapKind(LrcDiagnosticKind kind) => kind switch
    {
        LrcDiagnosticKind.UnknownTag => LyricsImportIssueKind.UnknownTag,
        LrcDiagnosticKind.MalformedMetadata => LyricsImportIssueKind.MalformedTag,
        LrcDiagnosticKind.TextWithoutTimestamp => LyricsImportIssueKind.TextWithoutTimestamp,
        LrcDiagnosticKind.EmptyText => LyricsImportIssueKind.EmptyText,
        LrcDiagnosticKind.DuplicateTimestamp => LyricsImportIssueKind.DuplicateTimestamp,
        LrcDiagnosticKind.OutOfOrder => LyricsImportIssueKind.OutOfOrder,
        LrcDiagnosticKind.NegativeTimeClamped => LyricsImportIssueKind.NegativeTimeClamped,
        _ => LyricsImportIssueKind.UnknownTag,
    };

    private static IssueSeverity MapSeverity(LrcDiagnosticKind kind) => kind switch
    {
        LrcDiagnosticKind.EmptyText => IssueSeverity.Info,
        LrcDiagnosticKind.TextWithoutTimestamp => IssueSeverity.Info,
        LrcDiagnosticKind.DuplicateTimestamp => IssueSeverity.Warning,
        LrcDiagnosticKind.OutOfOrder => IssueSeverity.Warning,
        LrcDiagnosticKind.NegativeTimeClamped => IssueSeverity.Warning,
        _ => IssueSeverity.Warning,
    };
}
