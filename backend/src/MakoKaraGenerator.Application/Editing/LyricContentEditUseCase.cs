using System.Collections.Immutable;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>供桌面及独立宿主共用的正文、注音与行操作，不持有第二份工程或历史。</summary>
/// <remarks>由工作区所有者同步调度；客户端会话、焦点与输入法生命周期仍由适配层负责。</remarks>
public static partial class LyricContentEditUseCase
{
    /// <summary>提交完整行正文；桌面连续输入仍按同一稳定行合并。</summary>
    public static LyricContentEditResult SetLineText(ProjectWorkspace workspace, LyricLineTextEditRequest request) =>
        CommitPrepared(workspace, PrepareSetLineText(workspace, request));

    /// <summary>纯准备完整行正文及真实重切统计，不改变工作区或历史。</summary>
    /// <remarks>可选观察器在每个实际新增元素分配标识之前运行；宿主可抛出自身预算异常来终止纯准备，桌面默认不设限。</remarks>
    public static PreparedLyricContentEdit PrepareSetLineText(ProjectWorkspace workspace, LyricLineTextEditRequest request,
        Action? onCreatingToken = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Prepare(workspace, () => BuildSetLineText(workspace, request, onCreatingToken), "lyrics.set-line-text", true,
            new ProjectEditMergeIdentity("line.text", [request.Target]));
    }

    /// <summary>提交基础元素注音文字；空字符串删除对应注音。</summary>
    public static LyricContentEditResult SetRubyText(ProjectWorkspace workspace, LyricRubyTextEditRequest request, bool mergeable = false) =>
        CommitPrepared(workspace, PrepareSetRubyText(workspace, request, mergeable));

    /// <summary>纯准备注音文字，保留固定候选身份及原有属性。</summary>
    public static PreparedLyricContentEdit PrepareSetRubyText(ProjectWorkspace workspace, LyricRubyTextEditRequest request, bool mergeable = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Prepare(workspace, () => BuildSetRubyText(workspace, request), "line.ruby", mergeable,
            new ProjectEditMergeIdentity("line.ruby", [request.Target]));
    }

    /// <summary>在指定来源的稳定行之后新增未定时空行；没有前行时追加。</summary>
    public static LyricContentEditResult AddLineAfter(ProjectWorkspace workspace, LyricLineInsertRequest request) =>
        CommitPrepared(workspace, PrepareAddLineAfter(workspace, request));

    /// <summary>纯准备空行及一次固定的新标识，不改变工作区或历史。</summary>
    public static PreparedLyricContentEdit PrepareAddLineAfter(ProjectWorkspace workspace, LyricLineInsertRequest request) =>
        Prepare(workspace, () => BuildAddLineAfter(workspace, request), "lyrics.add-line", false, null);

    /// <summary>复制完整稳定行及展示覆盖；副本按原行时长后移。</summary>
    public static LyricContentEditResult DuplicateLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        CommitPrepared(workspace, PrepareDuplicateLine(workspace, baseline));

    /// <summary>纯准备完整行副本及一次固定的新身份和反馈。</summary>
    public static PreparedLyricContentEdit PrepareDuplicateLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        Prepare(workspace, () => BuildDuplicateLine(workspace, baseline), "lyrics.duplicate-line", false, null);

    /// <summary>删除稳定行并给出真实的后继或末行建议选择。</summary>
    public static LyricContentEditResult RemoveLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        CommitPrepared(workspace, PrepareRemoveLine(workspace, baseline));

    /// <summary>纯准备行删除；不复制被删除的大行或生成完整身份差集。</summary>
    public static PreparedLyricContentEdit PrepareRemoveLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        Prepare(workspace, () => BuildRemoveLine(workspace, baseline), "lyrics.remove-line", false, null);

    /// <summary>提交同一工作区、代次及原工程引用仍然有效的固定候选；拒绝不改变任何历史。</summary>
    public static LyricContentEditResult CommitPrepared(ProjectWorkspace workspace, PreparedLyricContentEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(workspace, prepared.Workspace) || workspace.Generation != prepared.Generation)
            return Reject(workspace.Project, LyricContentEditErrorCode.WorkspaceStale);
        if (!ReferenceEquals(workspace.Project, prepared.Source))
            return Reject(workspace.Project, LyricContentEditErrorCode.TargetStale);
        LyricContentEditResult result = prepared.Result;
        if (!result.Accepted || !result.Changed) return result;
        if (!prepared.Mergeable) workspace.History.BreakMergeGroup();
        var command = ProjectCommand.Create(prepared.CommandName, _ => result.Project, prepared.Mergeable);
        Project committed = prepared.Identity is null ? workspace.Execute(command) : workspace.ExecuteEdit(command, prepared.Identity);
        if (!prepared.Mergeable) workspace.History.BreakMergeGroup();
        return new(true, committed, result.ChangeSet, preservedCount: result.PreservedCount, resetCount: result.ResetCount,
            createdLineId: result.CreatedLineId, selectedLineId: result.SelectedLineId, insertedIndex: result.InsertedIndex,
            overlapsNext: result.OverlapsNext);
    }

    private static PreparedLyricContentEdit Prepare(ProjectWorkspace workspace, Func<LyricContentEditResult> build,
        string commandName, bool mergeable, ProjectEditMergeIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Project source = workspace.Project;
        long generation = workspace.Generation;
        return new(workspace, generation, source, build(), commandName, mergeable, identity);
    }

    private static LyricContentEditResult PrepareCandidate(Project source, Project candidate, int preservedCount = 0,
        int resetCount = 0, string? createdLineId = null, string? selectedLineId = null, int? insertedIndex = null,
        bool overlapsNext = false)
    {
        if (!ProjectEditSubmission.TryPrepare(source, candidate, out candidate, out string reason))
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
        if (candidate == source) return Unchanged(source);
        return new(true, candidate, DescribeChanges(source, candidate), preservedCount: preservedCount, resetCount: resetCount,
            createdLineId: createdLineId, selectedLineId: selectedLineId, insertedIndex: insertedIndex, overlapsNext: overlapsNext);
    }

    /// <summary>按原正文验证即时输入，复用领域 Unicode 重切规则；连续输入仅按稳定行合并。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">工作区身份、行目标、原正文与新正文。</param>
    /// <param name="onCreatingToken">可选的实际新增元素分配观察器。</param>
    /// <returns>重切计数、实际变化范围与中立拒绝原因。</returns>
    private static LyricContentEditResult BuildSetLineText(ProjectWorkspace workspace, LyricLineTextEditRequest request, Action? onCreatingToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.ExpectedText);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (request.Target.TokenId is not null) return Reject(source, LyricContentEditErrorCode.InvalidTarget);
        if (!TryFindLine(source, request.Target, out LyricBranch branch, out int index)
            || !string.Equals(branch.Lines[index].Text, request.ExpectedText, StringComparison.Ordinal))
            return Reject(source, LyricContentEditErrorCode.TargetStale);
        LyricLine line = branch.Lines[index];
        string text = request.Text ?? string.Empty;
        if (text == line.Text) return Unchanged(source);
        try
        {
            var allocator = new LyricsEditIdAllocator(source.Lyrics);
            string NextTokenId() { onCreatingToken?.Invoke(); return allocator.NextTokenId(); }
            LyricTextResplitResult resplit = LyricTextResplitter.Resplit(line.Text, line.Tokens, text, NextTokenId);
            LyricLine changed = RubySegmentEditor.RemoveDetachedGroups(line with { Text = text, Tokens = resplit.Tokens });
            if (!LyricTimingEditor.TryValidateLine(changed, out string reason))
                return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
            Project candidate = ReplaceBranch(source, branch with { Lines = branch.Lines.SetItem(index, changed) });
            return PrepareCandidate(source, candidate, resplit.PreservedCount, resplit.ResetCount);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message);
        }
    }

    /// <summary>更新基础元素注音，保留原注音身份及属性；空文字删除对应注音。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">工作区身份、基础元素与完整原注音。</param>
    /// <returns>实际变化范围与中立拒绝原因；相同文字不修改历史。</returns>
    private static LyricContentEditResult BuildSetRubyText(ProjectWorkspace workspace, LyricRubyTextEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Text);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (request.Target.TokenId is not { } tokenId) return Reject(source, LyricContentEditErrorCode.InvalidTarget);
        if (!TryFindLine(source, request.Target, out LyricBranch branch, out int index)
            || !branch.Lines[index].Tokens.Any(token => token.Id == tokenId))
            return Reject(source, LyricContentEditErrorCode.TargetStale);
        LyricLine line = branch.Lines[index];
        RubyAnnotation? previous = line.RubyAnnotations.FirstOrDefault(ruby => ruby.BaseTokenId == tokenId);
        if (previous != request.ExpectedRuby) return Reject(source, LyricContentEditErrorCode.TargetStale);
        if ((previous?.Text ?? string.Empty) == request.Text) return Unchanged(source);
        if (previous is not null && (previous.Segments.Length != 1 || previous.Segments.Any(segment => segment.Time is not null)))
            return Reject(source, LyricContentEditErrorCode.RubySegmentsRequireExplicitEdit);
        try
        {
            RubyAnnotation? next = request.Text.Length == 0 ? null : previous is null
                ? RubyAnnotation.Create(new LyricsEditIdAllocator(source.Lyrics).NextRubyId(), tokenId, request.Text)
                : previous with { Text = request.Text };
            ImmutableArray<RubyAnnotation> rubies = [.. line.RubyAnnotations.Where(ruby => ruby.BaseTokenId != tokenId)];
            if (next is not null) rubies = rubies.Add(next);
            Project candidate = ReplaceBranch(source, branch with
                { Lines = branch.Lines.SetItem(index, line with { RubyAnnotations = rubies }) });
            return PrepareCandidate(source, candidate);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message);
        }
    }

    /// <summary>在稳定行之后插入未定时空行；无前一行时允许向空来源或来源末尾追加。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">来源与前一行的身份、原值门禁。</param>
    /// <returns>新行稳定标识、位置和建议选择；成功变化只有一次历史。</returns>
    private static LyricContentEditResult BuildAddLineAfter(ProjectWorkspace workspace, LyricLineInsertRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        LyricBranch? branch = source.Lyrics.Branches.FirstOrDefault(branch => branch.Id == request.BranchId);
        if (branch is null) return Reject(source, LyricContentEditErrorCode.TargetStale);
        if (request.AfterLineId is null && request.ExpectedAfterLine is not null)
            return Reject(source, LyricContentEditErrorCode.InvalidTarget);
        int index = request.AfterLineId is null ? branch.Lines.Length
            : FindLineIndex(branch, request.AfterLineId) + 1;
        if (request.AfterLineId is not null && (index == 0 || request.ExpectedAfterLine is null
            || branch.Lines[index - 1] != request.ExpectedAfterLine))
            return Reject(source, LyricContentEditErrorCode.TargetStale);
        try
        {
            LyricLine created = LyricLine.Create(new LyricsEditIdAllocator(source.Lyrics).NextLineId(), string.Empty);
            Project candidate = ReplaceBranch(source, branch with { Lines = branch.Lines.Insert(index, created) });
            return PrepareCandidate(source, candidate, createdLineId: created.Id, selectedLineId: created.Id, insertedIndex: index);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message);
        }
    }

    /// <summary>复制完整行，沿领域规则整体后移原行时长，并复制其展示覆盖。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">稳定来源、行与完整原行记录。</param>
    /// <returns>副本标识、建议选择及真实邻行重叠信息；领域拒绝不提交部分变化。</returns>
    private static LyricContentEditResult BuildDuplicateLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        if (CheckBaseline(workspace, baseline, out LyricBranch branch, out int index) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        try
        {
            var allocator = new LyricsEditIdAllocator(source.Lyrics);
            LyricLine copy = LyricLineDuplicator.Duplicate(branch.Lines[index], allocator.NextLineId(), allocator.NextTokenId, allocator.NextRubySegmentId);
            Project candidate = ReplaceBranch(source, branch with { Lines = branch.Lines.Insert(index + 1, copy) });
            if (!TryValidatePresentation(candidate, out string reason))
                return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
            candidate = PresentationEditor.CopyLine(candidate, branch.Id, branch.Lines[index].Id, copy.Id);
            bool overlapsNext = copy.Time is { IsValid: true } copyTime && index + 1 < branch.Lines.Length
                && branch.Lines[index + 1].Time is { IsValid: true } nextTime && nextTime.StartUs < copyTime.EndUs;
            return PrepareCandidate(source, candidate, createdLineId: copy.Id, selectedLineId: copy.Id, insertedIndex: index + 1,
                overlapsNext: overlapsNext);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, exception.Message);
        }
    }

    /// <summary>删除稳定行，清理展示中的已删除源；保留真实悬空注音和主来源关联警告。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">稳定来源、行与完整原行记录。</param>
    /// <returns>原位置的下一行或末尾剩余行作为建议选择；空来源返回空选择。</returns>
    private static LyricContentEditResult BuildRemoveLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        if (CheckBaseline(workspace, baseline, out LyricBranch branch, out int index) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        ImmutableArray<LyricLine> lines = branch.Lines.RemoveAt(index);
        string? selectedId = lines.IsEmpty ? null : lines[Math.Min(index, lines.Length - 1)].Id;
        return PrepareCandidate(source, ReplaceBranch(source, branch with { Lines = lines }), selectedLineId: selectedId);
    }

    private static LyricContentEditErrorCode? CheckWorkspace(ProjectWorkspace workspace, ProjectWorkspace expected, long generation)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(workspace, expected) && workspace.Generation == generation
            ? null : LyricContentEditErrorCode.WorkspaceStale;
    }

    private static LyricContentEditErrorCode? CheckBaseline(ProjectWorkspace workspace, LyricContentEditBaseline baseline,
        out LyricBranch branch, out int index)
    {
        ArgumentNullException.ThrowIfNull(baseline.ExpectedLine);
        ArgumentNullException.ThrowIfNull(baseline.Target);
        branch = null!; index = -1;
        if (CheckWorkspace(workspace, baseline.ExpectedWorkspace, baseline.ExpectedWorkspaceGeneration) is { } error) return error;
        if (baseline.Target.TokenId is not null) return LyricContentEditErrorCode.InvalidTarget;
        return TryFindLine(workspace.Project, baseline.Target, out branch, out index)
            && branch.Lines[index] == baseline.ExpectedLine ? null : LyricContentEditErrorCode.TargetStale;
    }

    private static bool TryFindLine(Project project, LyricContentEditTarget target, out LyricBranch branch, out int index)
    {
        ArgumentNullException.ThrowIfNull(target);
        branch = project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == target.BranchId)!;
        index = branch is null ? -1 : FindLineIndex(branch, target.LineId);
        return index >= 0;
    }

    private static int FindLineIndex(LyricBranch branch, string lineId)
    {
        for (int index = 0; index < branch.Lines.Length; index++)
            if (branch.Lines[index].Id == lineId) return index;
        return -1;
    }

    private static Project ReplaceBranch(Project source, LyricBranch branch) =>
        source with { Lyrics = source.Lyrics.ReplaceBranch(branch) };

    private static bool TryValidatePresentation(Project candidate, out string reason)
    {
        var report = PresentationValidator.Validate(candidate);
        reason = report.HasErrors ? report.Issues.First(issue => issue.Severity == IssueSeverity.Error).Message : string.Empty;
        return !report.HasErrors;
    }

    private static LyricContentEditResult Unchanged(Project source) => new(true, source, ProjectLyricsChangeSet.None);
    private static LyricContentEditResult Reject(Project source, LyricContentEditErrorCode code, string? detail = null) =>
        new(false, source, ProjectLyricsChangeSet.None, new(code, detail));
}
