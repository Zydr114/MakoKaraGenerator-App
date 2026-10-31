using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

public static partial class LyricContentEditUseCase
{
    /// <summary>只准备稳定行及已定时元素的整行平移；不修改工程、历史或重做。</summary>
    /// <param name="workspace">单一工程与历史所有者。</param>
    /// <param name="baseline">明确来源、稳定行与完整原行；不依赖当前界面选择。</param>
    /// <param name="deltaUs">精确有符号微秒；领域规则检查加法溢出和非负边界。</param>
    /// <param name="contentDurationUs">已准备媒体的微秒上界；非正表示未知。</param>
    /// <returns>可供展示的完整候选工程与时间摘要；拒绝保留当前工程引用。</returns>
    public static LyricTimingEditResult PreviewShiftLine(ProjectWorkspace workspace,
        LyricContentEditBaseline baseline, long deltaUs, long contentDurationUs = 0) =>
        PrepareLineShift(workspace, baseline, deltaUs, contentDurationUs);

    /// <summary>原子平移整行及其已定时元素，沿用领域状态规则与一次不可合并历史。</summary>
    /// <param name="workspace">单一工程与历史所有者。</param>
    /// <param name="baseline">完整原行基线；其他来源或工程设置变化可保留。</param>
    /// <param name="deltaUs">精确有符号微秒。</param>
    /// <param name="contentDurationUs">已准备媒体上界；非正表示未知。</param>
    /// <returns>真实提交结果；零平移保留工程引用、dirty与redo。</returns>
    public static LyricTimingEditResult ShiftLine(ProjectWorkspace workspace,
        LyricContentEditBaseline baseline, long deltaUs, long contentDurationUs = 0)
    {
        LyricTimingEditResult prepared = PrepareLineShift(workspace, baseline, deltaUs, contentDurationUs);
        if (!prepared.Accepted || !prepared.Changed) return prepared;
        workspace.History.BreakMergeGroup();
        Project committed = workspace.Execute(ProjectCommand.Create("line.shift", _ => prepared.Project));
        workspace.History.BreakMergeGroup();
        return new(true, committed, prepared.ChangeSet);
    }

    /// <summary>只准备固定原行内的元素重排，时间冲突要求显式选择时间槽重分配。</summary>
    /// <param name="workspace">单一工程所有者。</param>
    /// <param name="request">完整原行与稳定元素身份，目标下标属于该固定原行。</param>
    /// <returns>候选工程和结构摘要；预览不修改历史、dirty或redo。</returns>
    public static LyricContentEditResult PreviewReorderToken(ProjectWorkspace workspace, LyricTokenReorderRequest request) =>
        PrepareTokenReorder(workspace, request);

    /// <summary>在明确来源中提交元素重排，保留元素身份、样式、注音和未定时状态。</summary>
    /// <param name="workspace">单一工程与历史所有者。</param>
    /// <param name="request">完整原行基线和被移动元素的稳定标识。</param>
    /// <returns>真实的一次历史提交；无变化保留原引用及重做。</returns>
    public static LyricContentEditResult ReorderToken(ProjectWorkspace workspace, LyricTokenReorderRequest request)
    {
        return CommitPrepared(workspace, PrepareReorderToken(workspace, request));
    }

    /// <summary>固定元素重排候选；沿既有纯预演算法，不创建第二份历史。</summary>
    public static PreparedLyricContentEdit PrepareReorderToken(ProjectWorkspace workspace, LyricTokenReorderRequest request) =>
        Prepare(workspace, () =>
        {
            Project source = workspace.Project;
            LyricContentEditResult result = PrepareTokenReorder(workspace, request);
            return result.Accepted && result.Changed ? PrepareCandidate(source, result.Project) : result;
        }, "line.reorder", false, null);

    private static LyricTimingEditResult PrepareLineShift(ProjectWorkspace workspace,
        LyricContentEditBaseline baseline, long deltaUs, long contentDurationUs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        Project source = workspace.Project;
        if (CheckBaseline(workspace, baseline, out LyricBranch branch, out int index) is { } error)
            return RejectLineShift(source, error == LyricContentEditErrorCode.WorkspaceStale
                ? LyricTimingEditErrorCode.WorkspaceStale : LyricTimingEditErrorCode.TargetStale);
        bool accepted = false;
        string reason = string.Empty;
        Project candidate = LyricBranchEditor.Edit(source, branch.Id, view =>
        {
            accepted = LyricTimingEditor.TryShiftLineAndTokens(view, baseline.Target.LineId, deltaUs,
                out Project changed, out reason);
            return accepted ? changed : view;
        });
        if (!accepted) return RejectLineShift(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        LyricLine shifted = candidate.Lyrics.Branches.First(value => value.Id == branch.Id).Lines[index];
        if (contentDurationUs > 0 && shifted.Time is { } time && time.EndUs > contentDurationUs)
            return RejectLineShift(source, LyricTimingEditErrorCode.BeyondContentDuration);
        if (!ReferenceEquals(candidate, source) && !TryValidatePresentation(candidate, out reason))
            return RejectLineShift(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        return new(true, candidate, LyricTimingEditUseCase.DescribeChanges(source, candidate));
    }

    private static LyricContentEditResult PrepareTokenReorder(ProjectWorkspace workspace, LyricTokenReorderRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        ArgumentNullException.ThrowIfNull(request.TokenId);
        Project source = workspace.Project;
        if (CheckBaseline(workspace, request.Baseline, out LyricBranch branch, out int index) is { } error)
            return Reject(source, error);
        if (!branch.Lines[index].Tokens.Any(token => token.Id == request.TokenId))
            return Reject(source, LyricContentEditErrorCode.TargetStale);
        bool accepted = false;
        string reason = string.Empty;
        Project candidate = LyricBranchEditor.Edit(source, branch.Id, view =>
        {
            accepted = LyricTokenReorderEditor.TryReorder(view, request.Baseline.Target.LineId,
                request.TokenId, request.DestinationIndex, request.ReassignTimeSlots, out Project changed, out reason);
            return accepted ? changed : view;
        });
        if (!accepted || (!ReferenceEquals(candidate, source) && !TryValidatePresentation(candidate, out reason)))
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
        return new(true, candidate, DescribeChanges(source, candidate));
    }

    private static LyricTimingEditResult RejectLineShift(Project source, LyricTimingEditErrorCode code, string? detail = null) =>
        new(false, source, ProjectTimingChangeSet.None, new(code, detail));
}
