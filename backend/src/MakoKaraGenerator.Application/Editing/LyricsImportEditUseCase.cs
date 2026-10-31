using MakoKaraGenerator.Core.Commands;
using System.Collections.Immutable;
using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享固定歌词输入的准备，以及现有来源替换、新来源导入与纯文本追加。</summary>
/// <remarks>准备不持有工作区；提交由工作区所有者同步调度，文件系统、媒体准备和客户端取消仍由适配层负责。</remarks>
public static class LyricsImportEditUseCase
{
    /// <summary>识别固定字节的格式、候选与理由，不读取文件或编辑工程。</summary>
    /// <param name="source">本次文件选择取得的固定字节。</param>
    /// <returns>原注册表的完整检测结果；无法识别时为空。</returns>
    public static LyricsImportDetection? Detect(LyricsImportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return LyricsImporterRegistry.CreateDefault().Detect(source.FileName, source.Header);
    }

    /// <summary>按原格式、编码及切分规则解析固定输入，保留完整诊断，不提交任何工程变化。</summary>
    /// <param name="source">同识别阶段使用的固定字节。</param>
    /// <param name="options">格式、编码与切分选择。</param>
    /// <param name="mediaDurationUs">调用方已经准备的媒体时长；非正数表示未知。</param>
    /// <returns>不可变歌词文档与集合已冻结的完整报告。</returns>
    /// <exception cref="NotSupportedException">原注册表无法识别格式。</exception>
    /// <exception cref="ArgumentException">原解析器拒绝输入。</exception>
    public static LyricsImportResult Prepare(LyricsImportSource source, LyricsImportOptions options, long mediaDurationUs = 0) =>
        Prepare(source, options, mediaDurationUs, null);

    /// <summary>按原解析规则纯准备，使用调用方显式选择的累计逻辑工作配额。</summary>
    /// <param name="source">完整固定字节。</param>
    /// <param name="options">原格式、编码、切分选项。</param>
    /// <param name="mediaDurationUs">捕获的媒体时长。</param>
    /// <param name="preparationGuard">可选准备配额。</param>
    public static LyricsImportResult Prepare(LyricsImportSource source, LyricsImportOptions options, long mediaDurationUs, LyricsImportPreparationGuard? preparationGuard)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        LyricsImporterRegistry registry = LyricsImporterRegistry.CreateDefault();
        ILyricsImporter? selected = options.ImporterId is { Length: > 0 } id
            ? registry.Importers.FirstOrDefault(importer => string.Equals(importer.Id, id, StringComparison.Ordinal)) : null;
        using Stream input = source.OpenRead();
        LyricsImportResult prepared = registry.Import(input, new()
        {
            FileName = source.FileName,
            PreparationGuard = preparationGuard,
            MediaDurationUs = mediaDurationUs > 0 ? mediaDurationUs : null,
            EncodingOverride = ResolveEncoding(options.EncodingName),
            SplitRule = options.SplitRule,
            EstimateSplitTimes = options.EstimateSplitTimes,
        }, selected);
        // Core 文档使用 init 记录及 ImmutableArray；报告的只读接口也必须有不可变的实际底层集合。
        return new(prepared.Document, prepared.Report with
        {
            Issues = prepared.Report.Issues.ToImmutableArray(),
            CandidateNames = prepared.Report.CandidateNames.ToImmutableArray(),
            DiscardedFeatures = prepared.Report.DiscardedFeatures.ToImmutableArray(),
        });
    }

    /// <summary>将完整已解析行替换到明确来源，或连同新的独立时间来源一次提交。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">工作区身份及完整原来源，或新来源的完整原歌词。</param>
    /// <param name="prepared">纯准备阶段的文档和报告；报告中的诊断不被笼统视为拒绝。</param>
    /// <param name="newBranchName">新来源名称；已有来源必须为空。</param>
    /// <param name="newBranchRole">新来源角色，不能替换工程主来源角色。</param>
    /// <param name="sourceBranchId">多来源候选中显式选择的源分支；省略时只允许单来源候选。</param>
    /// <returns>实际工程变化、导入行数及最终建议选择；空文件保留原工程。</returns>
    public static LyricsImportEditResult CommitPrepared(ProjectWorkspace workspace, LyricsImportEditBaseline baseline,
        LyricsImportResult prepared, string? newBranchName = null, LyricBranchRole newBranchRole = LyricBranchRole.Translation, string? sourceBranchId = null)
        => CommitCandidate(workspace, PrepareImportCandidate(Capture(workspace), baseline, prepared, newBranchName, newBranchRole, sourceBranchId: sourceBranchId));

    /// <summary>同步捕获真实工作区引用、代次与不可变工程；调用方负责写者同步。</summary>
    public static LyricsImportEditSnapshot Capture(ProjectWorkspace workspace)
    { ArgumentNullException.ThrowIfNull(workspace); return new(workspace); }

    /// <summary>在固定快照上纯准备文件替换或新来源，不读取实时工作区或执行历史。</summary>
    public static PreparedLyricsImportEdit PrepareImportCandidate(LyricsImportEditSnapshot snapshot, LyricsImportEditBaseline baseline,
        LyricsImportResult prepared, string? newBranchName = null, LyricBranchRole newBranchRole = LyricBranchRole.Translation,
        LyricsImportPreparationGuard? preparationGuard = null, Action<LyricBranch, LyricLine, int, int>? onPreparedLine = null, string? sourceBranchId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(prepared);
        LyricsImportEditResult result = PrepareImportCore(snapshot, baseline, prepared, newBranchName, newBranchRole, preparationGuard, onPreparedLine, sourceBranchId);
        LyricsImportReport report = result.Accepted ? DescribeSelectedImport(snapshot.Project, prepared, sourceBranchId, preparationGuard) : prepared.Report;
        return new(snapshot, result, baseline.BranchId is null ? "branch.import" : "lyrics.import", report);
    }

    private static LyricsImportEditResult PrepareImportCore(LyricsImportEditSnapshot snapshot, LyricsImportEditBaseline baseline,
        LyricsImportResult prepared, string? newBranchName, LyricBranchRole newBranchRole, LyricsImportPreparationGuard? preparationGuard,
        Action<LyricBranch, LyricLine, int, int>? onPreparedLine, string? sourceBranchId)
    {
        if (CheckTarget(snapshot, baseline, newBranchName, newBranchRole, out LyricBranch? branch) is { } error)
            return Reject(snapshot.Project, error);
        Project source = snapshot.Project;
        if (sourceBranchId is null && prepared.Document.Branches.Length > 1)
            return Reject(source, LyricsImportEditErrorCode.SourceBranchRequired);
        LyricBranch? selected = sourceBranchId is null ? prepared.Document.PrimaryBranch
            : prepared.Document.Branches.FirstOrDefault(value => value.Id == sourceBranchId);
        if (selected is null) return Reject(source, LyricsImportEditErrorCode.SourceBranchNotFound);
        if (selected.Lines.IsEmpty) return Reject(source, LyricsImportEditErrorCode.EmptyImport);
        if (branch?.Id == source.Lyrics.PrimaryBranchId && selected.Lines.Any(line => line.PrimaryLineId is not null))
            return Reject(source, LyricsImportEditErrorCode.DomainValidationFailed, "主歌词来源不能导入带 PrimaryLineId 的关联行。");
        try
        {
            LyricsEditIdAllocator allocator = new(source.Lyrics, preparationGuard is null ? null : preparationGuard.Item);
            bool create = branch is null;
            if (create) preparationGuard?.Item(newBranchName);
            branch ??= LyricBranch.Create(allocator.NextBranchId("import"), newBranchName!.Trim(), newBranchRole,
                timingMode: LyricBranchTimingMode.Independent);
            preparationGuard?.Reserve(128L + 64L * selected.Lines.Length);
            Dictionary<string, string> mapping = new(StringComparer.Ordinal);
            void Map(string id, string replacement)
            {
                preparationGuard?.Reserve(128L + 2L * id.Length + 2L * replacement.Length);
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("源歌词身份为空。");
                if (!mapping.TryAdd(id, replacement)) throw new LyricsIdentifierExhaustedException();
            }
            foreach (LyricLine line in selected.Lines)
            {
                if (line.RubyAnnotations.Any(ruby => !line.Tokens.Any(token => token.Id == ruby.BaseTokenId)))
                    return Reject(source, LyricsImportEditErrorCode.DomainValidationFailed, "源注音未绑定当前行的元素。");
                Map(line.Id, allocator.NextLineId());
                foreach (LyricToken token in line.Tokens) Map(token.Id, allocator.NextTokenId());
                foreach (RubyAnnotation ruby in line.RubyAnnotations)
                {
                    Map(ruby.Id, allocator.NextRubyId());
                    foreach (RubyTextSegment segment in ruby.Segments) Map(segment.Id, allocator.NextRubySegmentId());
                }
            }
            var mapped = ImmutableArray.CreateBuilder<LyricLine>(selected.Lines.Length);
            foreach (LyricLine line in selected.Lines)
            {
                preparationGuard?.Reserve(64L + 32L * (line.Tokens.Length + (long)line.RubyAnnotations.Length
                    + line.RubyAnnotations.Sum(ruby => (long)ruby.Segments.Length)));
                LyricLine finalLine = line with
                {
                    Id = mapping[line.Id],
                    PrimaryLineId = line.PrimaryLineId is { } linked ? mapping.GetValueOrDefault(linked, linked) : null,
                    Tokens = [.. line.Tokens.Select(token => token with { Id = mapping[token.Id] })],
                    RubyAnnotations = [.. line.RubyAnnotations.Select(ruby => ruby with
                    {
                        Id = mapping[ruby.Id], BaseTokenId = mapping[ruby.BaseTokenId],
                        Segments = [.. ruby.Segments.Select(segment => segment with { Id = mapping[segment.Id] })],
                    })],
                };
                onPreparedLine?.Invoke(branch, finalLine, mapped.Count, selected.Lines.Length);
                mapped.Add(finalLine);
            }
            ImmutableArray<LyricLine> lines = mapped.MoveToImmutable();
            branch = branch with { Lines = lines, Language = selected.Language ?? branch.Language };
            LyricsDocument lyrics = create
                ? source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) }
                : source.Lyrics.ReplaceBranch(branch);
            return PrepareSubmission(source, source with { Lyrics = lyrics },
                lines.Length, branch.Id, lines[0].Id);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricsImportEditErrorCode.IdentifierExhausted, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Reject(source, LyricsImportEditErrorCode.DomainValidationFailed, exception.Message);
        }
    }

    private static LyricsImportReport DescribeSelectedImport(Project target, LyricsImportResult prepared, string? sourceBranchId,
        LyricsImportPreparationGuard? guard)
    {
        LyricBranch selected = sourceBranchId is null ? prepared.Document.PrimaryBranch!
            : prepared.Document.Branches.Single(branch => branch.Id == sourceBranchId);
        List<LyricsImportIssue> issues = [.. prepared.Report.Issues];
        List<string> discarded = [.. prepared.Report.DiscardedFeatures];
        void Report(MakoKaraGenerator.Core.Diagnostics.IssueSeverity severity, string raw, string message)
        {
            guard?.Item(raw); guard?.Reserve(512L + 2L * message.Length);
            issues.Add(new(LyricsImportIssueKind.DiscardedFeature, severity, 0, raw, message));
        }
        if (prepared.Document.Branches.Length > 1)
        {
            string feature = "单来源导入省略其它来源（" + (prepared.Document.Branches.Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "）";
            guard?.Item(feature); discarded.Add(feature);
            Report(MakoKaraGenerator.Core.Diagnostics.IssueSeverity.Warning, selected.Id, feature + "；已显式选择源分支，工程其它来源不变。");
        }
        HashSet<string> ownLines = [.. selected.Lines.Select(line => line.Id)];
        HashSet<string> targetLines = [.. target.Lyrics.Lines.Select(line => line.Id)];
        foreach (LyricLine line in selected.Lines)
            if (line.PrimaryLineId is { } link && !ownLines.Contains(link) && !targetLines.Contains(link))
                Report(MakoKaraGenerator.Core.Diagnostics.IssueSeverity.Warning, line.Id,
                    "SourceBranchLinkUnavailable：PrimaryLineId=" + link + " 在目标主来源不存在；原值保留，需显式重关联。");
        int identifiers = selected.Lines.Sum(line => 1 + line.Tokens.Length + line.RubyAnnotations.Sum(ruby => 1 + ruby.Segments.Length));
        Report(MakoKaraGenerator.Core.Diagnostics.IssueSeverity.Info, selected.Id,
            "导入身份重分配：" + identifiers.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个 line/token/ruby/segment 标识已分配新值，内部关联同步重映射。");
        int tokens = selected.Lines.Sum(line => line.Tokens.Length);
        int timed = selected.Lines.SelectMany(line => line.Tokens).Count(token => token.Time is not null);
        return prepared.Report with { Issues = issues.ToImmutableArray(), DiscardedFeatures = discarded.ToImmutableArray(),
            LineCount = selected.Lines.Length, TokenCount = tokens, TimedTokenCount = timed, MissingTokenCount = tokens - timed,
            EstimatedTokenCount = selected.Lines.SelectMany(line => line.Tokens).Count(token => token.TimingStatus == TimingStatus.Estimated) };
    }

    /// <summary>追加非空原文行及完整 Unicode 元素；新翻译来源按原规则跟随主来源。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">明确已有来源或新来源的原值门禁。</param>
    /// <param name="text">粘贴文字；保留非空行的原文字与空白。</param>
    /// <param name="newBranchName">新翻译来源名称；已有来源必须为空。</param>
    /// <returns>本次追加行数与首个新行的建议选择；所有新行和元素均未定时。</returns>
    public static LyricsImportEditResult AppendText(ProjectWorkspace workspace, LyricsImportEditBaseline baseline,
        string text, string? newBranchName = null)
        => CommitCandidate(workspace, PrepareAppendCandidate(Capture(workspace), baseline, text, newBranchName));

    /// <summary>在固定快照上纯准备完整 Unicode 文本追加；新翻译来源沿用 FollowPrimary。</summary>
    public static PreparedLyricsImportEdit PrepareAppendCandidate(LyricsImportEditSnapshot snapshot, LyricsImportEditBaseline baseline,
        string text, string? newBranchName = null, LyricsImportPreparationGuard? preparationGuard = null,
        Action<LyricBranch, LyricLine, int, int>? onPreparedLine = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(text);
        return new(snapshot, PrepareAppendCore(snapshot, baseline, text, newBranchName, preparationGuard, onPreparedLine), "lyrics.paste");
    }

    private static LyricsImportEditResult PrepareAppendCore(LyricsImportEditSnapshot snapshot, LyricsImportEditBaseline baseline,
        string text, string? newBranchName, LyricsImportPreparationGuard? preparationGuard,
        Action<LyricBranch, LyricLine, int, int>? onPreparedLine)
    {
        if (CheckTarget(snapshot, baseline, newBranchName, LyricBranchRole.Translation, out LyricBranch? branch) is { } error)
            return Reject(snapshot.Project, error);
        Project source = snapshot.Project;
        preparationGuard?.Lines(text);
        string[] rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Where(row => !string.IsNullOrWhiteSpace(row)).ToArray();
        if (rows.Length == 0) return Reject(source, LyricsImportEditErrorCode.EmptyText);
        try
        {
            bool create = branch is null;
            if (create) preparationGuard?.Item(newBranchName);
            branch ??= LyricBranch.Create(Reserve(CaptureIds(source.Lyrics, preparationGuard), "translation-" + Guid.NewGuid().ToString("N"), preparationGuard),
                newBranchName!.Trim(), LyricBranchRole.Translation, timingMode: LyricBranchTimingMode.FollowPrimary);
            LyricsDocument candidateLyrics = create
                ? source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) } : source.Lyrics;
            var allocator = new LyricsEditIdAllocator(candidateLyrics, preparationGuard is null ? null : preparationGuard.Item);
            preparationGuard?.Reserve(64L + 32L * rows.Length);
            var appended = ImmutableArray.CreateBuilder<LyricLine>(rows.Length);
            int total = checked(branch.Lines.Length + rows.Length);
            foreach (string row in rows)
            {
                preparationGuard?.Item(row);
                LyricLine finalLine = LyricLine.Create(allocator.NextLineId(), row,
                    tokens: GraphemeSplitter.Enumerate(row).Select(element => CreateAppendToken(allocator, element.Text, preparationGuard)));
                onPreparedLine?.Invoke(branch, finalLine, branch.Lines.Length + appended.Count, total);
                appended.Add(finalLine);
            }
            ImmutableArray<LyricLine> created = appended.MoveToImmutable();
            candidateLyrics = candidateLyrics.ReplaceBranch(branch with { Lines = branch.Lines.AddRange(created) });
            return PrepareSubmission(source, source with { Lyrics = candidateLyrics }, created.Length,
                branch.Id, created[0].Id);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricsImportEditErrorCode.IdentifierExhausted, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Reject(source, LyricsImportEditErrorCode.DomainValidationFailed, exception.Message);
        }
    }

    private static LyricsImportEditErrorCode? CheckTarget(LyricsImportEditSnapshot snapshot, LyricsImportEditBaseline baseline,
        string? newBranchName, LyricBranchRole role, out LyricBranch? branch)
    {
        ArgumentNullException.ThrowIfNull(baseline.ExpectedWorkspace);
        branch = null;
        if (!ReferenceEquals(snapshot.Workspace, baseline.ExpectedWorkspace) || snapshot.Generation != baseline.ExpectedWorkspaceGeneration)
            return LyricsImportEditErrorCode.WorkspaceStale;
        if (baseline.BranchId is { } id)
        {
            if (baseline.ExpectedBranch is null || baseline.ExpectedBranch.Id != id || baseline.ExpectedLyrics is not null
                || newBranchName is not null) return LyricsImportEditErrorCode.InvalidTarget;
            branch = snapshot.Project.Lyrics.Branches.FirstOrDefault(current => current.Id == id);
            return branch is not null && branch == baseline.ExpectedBranch ? null : LyricsImportEditErrorCode.TargetStale;
        }
        if (baseline.ExpectedBranch is not null || baseline.ExpectedLyrics is null || string.IsNullOrWhiteSpace(newBranchName)
            || role == LyricBranchRole.Primary || !Enum.IsDefined(role)) return LyricsImportEditErrorCode.InvalidTarget;
        return snapshot.Project.Lyrics == baseline.ExpectedLyrics ? null : LyricsImportEditErrorCode.TargetStale;
    }

    /// <summary>在真实工作区唯一历史提交固定候选；任何代次或原工程引用改变均拒绝。</summary>
    public static LyricsImportEditResult CommitCandidate(ProjectWorkspace workspace, PreparedLyricsImportEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(prepared);
        var snapshot = prepared.Snapshot;
        if (!ReferenceEquals(workspace, snapshot.Workspace) || workspace.Generation != snapshot.Generation)
            return Reject(workspace.Project, LyricsImportEditErrorCode.WorkspaceStale);
        if (!ReferenceEquals(workspace.Project, snapshot.Project))
            return Reject(workspace.Project, LyricsImportEditErrorCode.TargetStale);
        if (!prepared.Result.Accepted || !prepared.Result.Changed) return prepared.Result;
        workspace.History.BreakMergeGroup();
        workspace.Execute(ProjectCommand.Create(prepared.CommandName, _ => prepared.Result.Project));
        workspace.History.BreakMergeGroup();
        return prepared.Result;
    }

    private static LyricToken CreateAppendToken(LyricsEditIdAllocator allocator, string text, LyricsImportPreparationGuard? guard)
    { guard?.Item(text); return LyricToken.Create(allocator.NextTokenId(), text); }

    private static LyricsImportEditResult PrepareSubmission(Project source, Project candidate,
        int lineCount, string selectedBranchId, string selectedLineId) =>
        ProjectEditSubmission.TryPrepare(source, candidate, out Project result, out string reason)
            ? new(true, result, LyricContentEditUseCase.DescribeChanges(source, result), lineCount: lineCount,
                selectedBranchId: selectedBranchId, selectedLineId: selectedLineId)
            : Reject(source, LyricsImportEditErrorCode.DomainValidationFailed, reason);

    private static LyricsImportEditResult Reject(Project source, LyricsImportEditErrorCode code, string? detail = null) =>
        new(false, source, ProjectLyricsChangeSet.None, new(code, detail));

    private static HashSet<string> CaptureIds(LyricsDocument document, LyricsImportPreparationGuard? guard)
    {
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (LyricBranch branch in document.Branches)
        {
            guard?.Item(branch.Id); identifiers.Add(branch.Id);
            foreach (LyricLine line in branch.Lines)
            {
                guard?.Item(line.Id); identifiers.Add(line.Id);
                foreach (LyricToken token in line.Tokens) { guard?.Item(token.Id); identifiers.Add(token.Id); }
                foreach (RubyAnnotation ruby in line.RubyAnnotations)
                {
                    guard?.Item(ruby.Id); identifiers.Add(ruby.Id);
                    foreach (RubyTextSegment segment in ruby.Segments) { guard?.Item(segment.Id); identifiers.Add(segment.Id); }
                }
            }
        }
        return identifiers;
    }

    private static string Reserve(HashSet<string> identifiers, string id, LyricsImportPreparationGuard? guard = null)
    { guard?.Item(id); return identifiers.Add(id) ? id : throw new LyricsIdentifierExhaustedException(); }

    private static Encoding? ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            if (string.Equals(name, "utf-8", StringComparison.OrdinalIgnoreCase)) return new UTF8Encoding(false);
            if (string.Equals(name, "gb18030", StringComparison.OrdinalIgnoreCase))
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GB18030");
            }
        }
        catch (ArgumentException) { return null; }
        return null;
    }
}
