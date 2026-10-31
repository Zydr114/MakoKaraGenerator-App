using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 按文件特征挑选导入器的注册表。
/// </summary>
/// <remarks>
/// <para>
/// 识别分三层（ADR-0046）：
/// </para>
/// <list type="number">
///   <item>扩展名登记表（<see cref="LyricsFormatCatalog" />）给出候选；</item>
///   <item>候选各自用 <see cref="ILyricsImporter.CanImport" /> 看内容（同一个 <c>.lrc</c> 靠这一层区分普通/增强）；</item>
///   <item>候选都不认、或扩展名根本未登记时，退回"所有导入器都试一遍"，
///         与加入登记表之前的行为一致。</item>
/// </list>
/// <para>
/// 顺序即优先级：增强 LRC 必须先于普通 LRC，因为两者的扩展名相同，
/// 只有"行内是否含逐字标签"能把它们分开。多个候选都能处理时会按优先级选一个，
/// 但把"还有谁能处理"写进报告（<see cref="LyricsImportDetection.IsAmbiguous" />）。
/// </para>
/// </remarks>
public sealed class LyricsImporterRegistry
{
    private const int HeaderProbeLength = 4096;

    private readonly List<ILyricsImporter> _importers;
    private readonly LyricsFormatCatalog _catalog;

    /// <summary>创建一个注册表。</summary>
    /// <param name="importers">导入器列表，按优先级排列。</param>
    /// <param name="catalog">扩展名登记表；为 <see langword="null" /> 时用默认表。</param>
    /// <exception cref="ArgumentNullException"><paramref name="importers" /> 为 <see langword="null" /> 时抛出。</exception>
    public LyricsImporterRegistry(IEnumerable<ILyricsImporter> importers, LyricsFormatCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(importers);

        _importers = [.. importers];
        _catalog = catalog ?? LyricsFormatCatalog.Default;
    }

    /// <summary>已注册的导入器（按优先级）。</summary>
    public IReadOnlyList<ILyricsImporter> Importers => _importers;

    /// <summary>创建默认注册表（逐字优先、行级在后）。</summary>
    /// <returns>注册表。</returns>
    public static LyricsImporterRegistry CreateDefault() =>
        new(
        [
            new EnhancedLrcImporter(),
            new PlainLrcImporter(),
            new PlainTextImporter(),
            new SrtImporter(),
            new WebVttImporter(),
            new AssImporter(),
            new UltraStarImporter(),
            new TtmlImporter(),
        ]);

    /// <summary>按文件名与文件头挑一个导入器。</summary>
    /// <param name="fileName">文件名。</param>
    /// <param name="header">文件开头字节。</param>
    /// <returns>匹配的导入器；都不匹配时为 <see langword="null" />。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName" /> 为 <see langword="null" /> 时抛出。</exception>
    public ILyricsImporter? Find(string fileName, ReadOnlySpan<byte> header) =>
        Detect(fileName, header)?.Importer;

    /// <summary>识别文件的格式，并给出理由与全部候选。</summary>
    /// <param name="fileName">文件名。</param>
    /// <param name="header">文件开头字节。</param>
    /// <returns>识别结果；没有导入器能处理时为 <see langword="null" />。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName" /> 为 <see langword="null" /> 时抛出。</exception>
    public LyricsImportDetection? Detect(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        ImmutableArray<LyricsImportCandidate> matches = CollectMatches(CandidatesFor(fileName), fileName, header);

        if (matches.IsEmpty)
        {
            matches = CollectMatches(_importers, fileName, header);
        }

        return matches.IsEmpty
            ? null
            : new LyricsImportDetection(matches[0].Importer, matches[0].Reason, matches.Length > 1, matches);
    }

    /// <summary>按内容导入（自动挑选导入器，或在指定导入器时按指定格式解析）。</summary>
    /// <param name="input">输入流（会先缓冲到内存，因此不要求可定位）。</param>
    /// <param name="context">导入条件。</param>
    /// <param name="importerOverride">由用户指定的导入器（对应界面上的"改选格式"）；为 <see langword="null" /> 时自动识别。</param>
    /// <returns>歌词文档与导入报告。</returns>
    /// <exception cref="NotSupportedException">找不到能处理该文件的导入器时抛出。</exception>
    public LyricsImportResult Import(
        Stream input,
        LyricsImportContext context,
        ILyricsImporter? importerOverride = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // 先缓冲：CanImport 需要看文件头，导入器又需要从头读，非可定位流没法同时满足。
        using MemoryStream buffer = new();
        input.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        ReadOnlySpan<byte> header = bytes.AsSpan(0, Math.Min(bytes.Length, HeaderProbeLength));
        LyricsImportDetection? detection = importerOverride is null ? Detect(context.FileName, header) : null;

        ILyricsImporter importer = importerOverride
            ?? detection?.Importer
            ?? throw new NotSupportedException($"无法识别的歌词格式：'{context.FileName}'。");

        using MemoryStream seekable = new(bytes, writable: false);
        LyricsImportResult result = importer.Import(seekable, context);

        return PostProcess(result, detection, importerOverride, context);
    }

    private List<ILyricsImporter> CandidatesFor(string fileName)
    {
        IReadOnlyList<string> ids = _catalog.CandidatesFor(fileName);

        if (ids.Count == 0)
        {
            return [];
        }

        List<ILyricsImporter> candidates = [];

        foreach (string id in ids)
        {
            foreach (ILyricsImporter importer in _importers)
            {
                if (string.Equals(importer.Id, id, StringComparison.Ordinal))
                {
                    candidates.Add(importer);
                    break;
                }
            }
        }

        return candidates;
    }

    private static ImmutableArray<LyricsImportCandidate> CollectMatches(
        IReadOnlyList<ILyricsImporter> importers,
        string fileName,
        ReadOnlySpan<byte> header)
    {
        ImmutableArray<LyricsImportCandidate>.Builder matches = ImmutableArray.CreateBuilder<LyricsImportCandidate>();

        foreach (ILyricsImporter importer in importers)
        {
            if (importer.CanImport(fileName, header))
            {
                matches.Add(new LyricsImportCandidate(importer, importer.MatchReason(fileName, header)));
            }
        }

        return matches.ToImmutable();
    }

    /// <summary>补上识别信息，并在需要时按规则重新切分（都属于"导入这件事"的一部分，写进同一份报告）。</summary>
    private static LyricsImportResult PostProcess(
        LyricsImportResult result,
        LyricsImportDetection? detection,
        ILyricsImporter? forced,
        LyricsImportContext context)
    {
        LyricsImportReport report = result.Report;
        BudgetedImportList<LyricsImportIssue> issues = new(context.PreparationGuard);
        issues.AddRange(report.Issues);
        LyricsDocument document = result.Document;

        if (detection is { } found)
        {
            report = report with
            {
                DetectionReason = found.Reason,
                CandidateNames = found.CandidateNames,
                IsFormatAmbiguous = found.IsAmbiguous,
            };

            if (found.IsAmbiguous)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.FormatAmbiguous,
                    IssueSeverity.Info,
                    0,
                    context.FileName,
                    $"{found.Matches.Length} 个导入器都能处理这个文件，已按优先级选择「{found.Importer.DisplayName}」：{found.Reason}"));
            }
        }
        else if (forced is not null)
        {
            report = report with { DetectionReason = $"由用户指定为「{forced.DisplayName}」" };
        }

        if (context.SplitRule != TokenSplitRule.None)
        {
            // The importer may return a complete multi-source document. Split each source
            // while keeping one allocator for the complete identity namespace.
            Func<string> nextTokenId = LyricsTokenIds.CreateFactory(document);
            int changedLines = 0, createdTokens = 0, estimatedTokens = 0;
            foreach (LyricBranch branch in document.Branches)
            {
                LyricsTokenSplitSummary branchSplit = LyricsDocumentTokenSplitter.Apply(
                    document with { PrimaryBranchId = branch.Id }, context.SplitRule,
                    context.EstimateSplitTimes, null, nextTokenId, context.PreparationGuard);
                if (branchSplit.ChangedLineCount == 0) continue;
                document = document.ReplaceBranch(branchSplit.Document.PrimaryBranch!);
                changedLines += branchSplit.ChangedLineCount;
                createdTokens += branchSplit.CreatedCount;
                estimatedTokens += branchSplit.EstimatedCount;
            }
            LyricsTokenSplitSummary split = new(document, changedLines, createdTokens, estimatedTokens);

            if (split.ChangedLineCount > 0)
            {
                document = split.Document;

                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.TokenSplitApplied,
                    IssueSeverity.Info,
                    0,
                    string.Empty,
                    $"导入后按「{context.SplitRule.ToDisplayName()}」重新切分了 {split.ChangedLineCount} 行，新建 {split.CreatedCount} 个元素"
                        + (split.EstimatedCount > 0
                            ? $"（其中 {split.EstimatedCount} 个时间按比例估算，需要校正）"
                            : "（新元素未定时，需要逐个打点）")));

                int tokenCount = 0;
                int timedCount = 0;
                int estimatedCount = 0;

                foreach (LyricLine line in document.AllLines)
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

                report = report with
                {
                    LineCount = document.AllLines.Count(),
                    TokenCount = tokenCount,
                    TimedTokenCount = timedCount,
                    MissingTokenCount = tokenCount - timedCount,
                    EstimatedTokenCount = estimatedCount,
                    SplitLineCount = split.ChangedLineCount,
                    SplitCreatedTokenCount = split.CreatedCount,
                    SplitEstimatedTokenCount = split.EstimatedCount,
                };
            }
        }

        return new LyricsImportResult(document, report with { Issues = issues });
    }
}
