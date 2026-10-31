using System.Diagnostics.CodeAnalysis;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Application.Editing;

public static partial class LyricTimingEditUseCase
{
    /// <summary>即时设置行／元素端点；可合并时按稳定目标与操作隔离，并沿用历史时间窗。</summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="request">固定输入基线、端点与媒体边界；空端点保留原整数微秒。</param>
    /// <param name="mergeable">是否允许与同目标的即时设置合并；假时明确断开前后历史。</param>
    /// <returns>原工程或一次提交后的工程，以及中立错误与最小变更范围。</returns>
    /// <remarks>现有显式短合并组仍生效，以保持打点同时设置时间与估算状态的合同。</remarks>
    public static LyricTimingEditResult SetTime(
        ProjectWorkspace workspace, LyricTimingEditRequest request, bool mergeable = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SetTimeCore(workspace, request, mergeable,
            new ProjectEditMergeIdentity("set", [request.Target]));
    }

    /// <summary>按工程帧率微调已有元素时间，整数换算与加法均检查溢出，不自动夹紧。</summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="baseline">固定工作区及元素原时间。</param>
    /// <param name="frameDelta">有符号帧数；零为接受但无变化。</param>
    /// <param name="contentDurationUs">已准备的媒体时长；非正值表示未知。</param>
    /// <returns>同目标重复微调按原时间窗合并，不会与其他目标或端点设置互相吞并。</returns>
    public static LyricTimingEditResult Nudge(
        ProjectWorkspace workspace, LyricTimingEditBaseline baseline, int frameDelta, long contentDurationUs = 0)
    {
        LyricTimingEditResult? failure = ValidateBaseline(workspace, baseline, out TimeRange? current);
        if (failure is not null) return failure;
        if (baseline.Target.TokenId is null) return Reject(workspace.Project, LyricTimingEditErrorCode.TokenRequired);
        if (current is not { IsValid: true } time) return Reject(workspace.Project, LyricTimingEditErrorCode.Untimed);
        if (frameDelta == 0) return AcceptUnchanged(workspace.Project);

        long startUs, endUs;
        try
        {
            long stepUs = checked(TimeUs.FromFrames(Math.Abs((long)frameDelta), workspace.Project.Composition.FrameRate)
                * Math.Sign(frameDelta));
            startUs = checked(time.StartUs + stepUs);
            endUs = checked(time.EndUs + stepUs);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            return Reject(workspace.Project, LyricTimingEditErrorCode.NudgeOutOfBounds);
        }
        if (startUs < 0 || (contentDurationUs > 0 && endUs > contentDurationUs))
            return Reject(workspace.Project, LyricTimingEditErrorCode.NudgeOutOfBounds);
        return SetTimeCore(workspace,
            new(workspace, baseline.ExpectedWorkspaceGeneration, baseline.Target, current, startUs, endUs, contentDurationUs),
            mergeable: true, new ProjectEditMergeIdentity("nudge", [baseline.Target]));
    }

    /// <summary>清除指定元素的时间与状态；已有缺失状态为接受的无变化，不产生空历史。</summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="baseline">固定工作区、稳定元素及原时间。</param>
    /// <returns>变化时为一次独立的 token.time.clear 历史；客户端可为无变化显示原提示。</returns>
    public static LyricTimingEditResult ClearToken(ProjectWorkspace workspace, LyricTimingEditBaseline baseline)
    {
        LyricTimingEditResult? failure = ValidateBaseline(workspace, baseline, out _);
        if (failure is not null) return failure;
        if (baseline.Target.TokenId is not { } tokenId)
            return Reject(workspace.Project, LyricTimingEditErrorCode.TokenRequired);
        Project source = workspace.Project;
        bool accepted = false;
        string reason = string.Empty;
        Project candidate = LyricBranchEditor.Edit(source, baseline.Target.BranchId, view =>
        {
            accepted = LyricTimingEditor.TryClearTokenTime(view, baseline.Target.LineId, tokenId,
                out Project updated, out reason);
            return accepted ? updated : view;
        });
        if (!accepted || !TryValidate(candidate, out reason))
            return Reject(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        return ExecuteCandidate(workspace, source, candidate, "token.time.clear", mergeable: false, identity: null);
    }

    /// <summary>即时提交稳定元素批次；任一失效或非法目标拒绝整批，不提交中间工程。</summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="request">所有目标的批次前原时间及按顺序应用的精确端点。</param>
    /// <returns>一次可按同一批目标合并的历史，以及真正发生变化的目标。</returns>
    /// <remarks>此入口不增加媒体时长限制；连续指针拖动应使用独立手势身份。</remarks>
    public static LyricTimingEditResult CommitTokenBatch(ProjectWorkspace workspace, LyricTimingTokenBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedWorkspace);
        Project source = workspace.Project;
        if (!ReferenceEquals(workspace, request.ExpectedWorkspace) || workspace.Generation != request.ExpectedWorkspaceGeneration)
            return Reject(source, LyricTimingEditErrorCode.WorkspaceStale);
        if (request.Changes.IsDefaultOrEmpty) return AcceptUnchanged(source);
        foreach (LyricTimingTokenTimeChange change in request.Changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            ArgumentNullException.ThrowIfNull(change.Target);
            if (change.Target.TokenId is null) return Reject(source, LyricTimingEditErrorCode.TokenRequired);
            if (!TryReadTime(source, change.Target, out TimeRange? current) || current != change.ExpectedTime)
                return Reject(source, LyricTimingEditErrorCode.TargetStale);
        }

        Project candidate = source;
        foreach (LyricTimingTokenTimeChange change in request.Changes)
            if (!TrySetTime(candidate, change.Target, change.StartUs, change.EndUs, out candidate, out string reason))
                return Reject(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        if (!TryValidate(candidate, out string validationReason))
            return Reject(source, LyricTimingEditErrorCode.DomainValidationFailed, validationReason);
        return ExecuteCandidate(workspace, source, candidate, "token.time", mergeable: true,
            new ProjectEditMergeIdentity("batch", [.. request.Changes.Select(change => (ProjectEditTarget)change.Target)]));
    }

    /// <summary>开始绑定固定工作区与目标的即时拖动；开始本身不写历史，也不清空重做。</summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="baseline">开始时捕获的稳定目标与有效原时间。</param>
    /// <param name="gesture">成功时可即时更新并显式结束的手势。</param>
    /// <param name="error">失败时的中立原因。</param>
    /// <returns>原时间仍有效且不处于显式合并组中时为真。</returns>
    public static bool TryBeginGesture(ProjectWorkspace workspace, LyricTimingEditBaseline baseline,
        [NotNullWhen(true)] out LyricTimingEditGesture? gesture, out LyricTimingEditError? error)
    {
        gesture = null;
        LyricTimingEditResult? failure = ValidateBaseline(workspace, baseline, out TimeRange? current);
        error = failure?.Error;
        if (failure is not null) return false;
        if (current is not { IsValid: true })
        {
            error = new(LyricTimingEditErrorCode.Untimed);
            return false;
        }
        if (workspace.History.IsInGroup)
        {
            error = new(LyricTimingEditErrorCode.GestureStale);
            return false;
        }
        workspace.History.BreakMergeGroup();
        gesture = new LyricTimingEditGesture(workspace, baseline);
        return true;
    }

    internal static LyricTimingEditResult SetTimeCore(ProjectWorkspace workspace, LyricTimingEditRequest request,
        bool mergeable, ProjectEditMergeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        LyricTimingEditResult? failure = ValidateBaseline(workspace,
            new(request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration, request.Target, request.ExpectedTime),
            out TimeRange? current);
        if (failure is not null) return failure;
        Project source = workspace.Project;
        if ((request.StartUs ?? current?.StartUs) is not { } startUs
            || (request.EndUs ?? current?.EndUs) is not { } endUs)
            return Reject(source, LyricTimingEditErrorCode.MissingEndpoint);
        if (request.Target.TokenId is not null)
        {
            if (startUs < 0) return Reject(source, LyricTimingEditErrorCode.InvalidStart);
            if (endUs <= startUs) return Reject(source, LyricTimingEditErrorCode.InvalidRange);
        }
        if (request.ContentDurationUs > 0 && endUs > request.ContentDurationUs)
            return Reject(source, LyricTimingEditErrorCode.BeyondContentDuration);
        if (!TrySetTime(source, request.Target, startUs, endUs, out Project candidate, out string reason)
            || !TryValidate(candidate, out reason))
            return Reject(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        return ExecuteCandidate(workspace, source, candidate,
            request.Target.TokenId is null ? "line.time" : "token.time", mergeable, identity);
    }

    private static LyricTimingEditResult? ValidateBaseline(ProjectWorkspace workspace,
        LyricTimingEditBaseline baseline, out TimeRange? current)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baseline.ExpectedWorkspace);
        ArgumentNullException.ThrowIfNull(baseline.Target);
        current = null;
        if (!ReferenceEquals(workspace, baseline.ExpectedWorkspace) || workspace.Generation != baseline.ExpectedWorkspaceGeneration)
            return Reject(workspace.Project, LyricTimingEditErrorCode.WorkspaceStale);
        if (!TryReadTime(workspace.Project, baseline.Target, out current) || current != baseline.ExpectedTime)
            return Reject(workspace.Project, LyricTimingEditErrorCode.TargetStale);
        return null;
    }

    private static bool TrySetTime(Project source, LyricTimingEditTarget target, long startUs, long endUs,
        out Project candidate, out string reason)
    {
        bool accepted = false;
        string detail = string.Empty;
        candidate = LyricBranchEditor.Edit(source, target.BranchId, view =>
        {
            Project updated;
            accepted = target.TokenId is { } tokenId
                ? LyricTimingEditor.TrySetTokenTime(view, target.LineId, tokenId, startUs, endUs, out updated, out detail)
                : LyricTimingEditor.TrySetLineTime(view, target.LineId, startUs, endUs, out updated, out detail);
            return accepted ? updated : view;
        });
        reason = detail;
        return accepted;
    }

    private static bool TryValidate(Project candidate, out string reason)
    {
        ValidationReport report = PresentationValidator.Validate(candidate);
        reason = report.HasErrors ? report.Issues.First(issue => issue.Severity == IssueSeverity.Error).Message : string.Empty;
        return !report.HasErrors;
    }

    private static LyricTimingEditResult ExecuteCandidate(ProjectWorkspace workspace, Project source, Project candidate,
        string name, bool mergeable, ProjectEditMergeIdentity? identity)
    {
        if (ReferenceEquals(source, candidate))
        {
            if (!mergeable) workspace.History.BreakMergeGroup();
            return AcceptUnchanged(source);
        }
        if (!mergeable) workspace.History.BreakMergeGroup();
        IProjectCommand command = ProjectCommand.Create(name, _ => candidate, mergeable);
        Project result = identity is null ? workspace.Execute(command) : workspace.ExecuteEdit(command, identity);
        if (!mergeable) workspace.History.BreakMergeGroup();
        return new(accepted: true, result, DescribeChanges(source, result));
    }

    private static LyricTimingEditResult AcceptUnchanged(Project project) => new(true, project, ProjectTimingChangeSet.None);
}
