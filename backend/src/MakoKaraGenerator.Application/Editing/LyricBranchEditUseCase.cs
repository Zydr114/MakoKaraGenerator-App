using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享来源新增、删除、受控配置与主行关联，不持有第二份工程或客户端选择。</summary>
/// <remarks>由工作区所有者同步调用；输入、确认、会话与窗口关闭仍由客户端管理。</remarks>
public static class LyricBranchEditUseCase
{
    /// <summary>新增空翻译来源，沿原 FollowPrimary 默认，不写外观或展示树。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">完整原歌词、工作区门禁及名称／语言。</param>
    /// <returns>一次历史与新来源建议选择；失败保留旧工程。</returns>
    public static LyricBranchEditResult AddTranslation(ProjectWorkspace workspace, LyricTranslationBranchAddRequest request) =>
        CommitPrepared(workspace, PrepareAddTranslation(workspace, request));

    /// <summary>只准备AddTranslation候选，不改变工程、历史或分配第二次标识。</summary>
    /// <param name="workspace">拥有工程的实际工作区。</param>
    /// <param name="request">原有来源用例的完整门禁及输入。</param>
    /// <returns>绑定工作区、代次和原工程引用的固定候选。</returns>
    public static PreparedLyricBranchEdit PrepareAddTranslation(ProjectWorkspace workspace, LyricTranslationBranchAddRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Project source = workspace.Project;
        long generation = workspace.Generation;
        return new(workspace, generation, source, PrepareAddTranslationCore(workspace, request), "branch.add");
    }

    private static LyricBranchEditResult PrepareAddTranslationCore(ProjectWorkspace workspace, LyricTranslationBranchAddRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedLyrics);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (source.Lyrics != request.ExpectedLyrics) return Reject(source, LyricBranchEditErrorCode.TargetStale);
        if (string.IsNullOrWhiteSpace(request.Name)) return Reject(source, LyricBranchEditErrorCode.InvalidConfiguration);
        try
        {
            string id = new LyricsEditIdAllocator(source.Lyrics).NextBranchId("translation");
            var branch = LyricBranch.Create(id, request.Name.Trim(), LyricBranchRole.Translation, language: request.Language,
                timingMode: LyricBranchTimingMode.FollowPrimary);
            return PrepareSubmission(source, source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) } },
                id);
        }
        catch (LyricsIdentifierExhaustedException exception)
        { return Reject(source, LyricBranchEditErrorCode.IdentifierExhausted, exception.Message); }
    }

    /// <summary>删除明确的非主来源，只清理原展示引用，不删除容器、效果或预设。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">来源完整原记录及工作区门禁。</param>
    /// <returns>一次历史与主来源建议选择。</returns>
    public static LyricBranchEditResult Remove(ProjectWorkspace workspace, LyricBranchEditBaseline baseline) =>
        CommitPrepared(workspace, PrepareRemove(workspace, baseline));

    /// <summary>只准备Remove候选，不改变工程、历史或分配第二次标识。</summary>
    /// <param name="workspace">拥有工程的实际工作区。</param>
    /// <param name="baseline">原有来源用例的完整门禁及输入。</param>
    /// <returns>绑定工作区、代次和原工程引用的固定候选。</returns>
    public static PreparedLyricBranchEdit PrepareRemove(ProjectWorkspace workspace, LyricBranchEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Project source = workspace.Project;
        long generation = workspace.Generation;
        return new(workspace, generation, source, PrepareRemoveCore(workspace, baseline), "branch.remove");
    }

    private static LyricBranchEditResult PrepareRemoveCore(ProjectWorkspace workspace, LyricBranchEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(baseline);
        if (CheckBranch(workspace, baseline, out LyricBranch? branch) is { } error) return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (branch!.Id == source.Lyrics.PrimaryBranchId) return Reject(source, LyricBranchEditErrorCode.PrimaryBranchProtected);
        var lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Remove(branch) };
        return PrepareSubmission(source, source with { Lyrics = lyrics }, source.Lyrics.PrimaryBranchId);
    }

    /// <summary>将受控元数据、可选单行关联和可选高级展示作为同一候选提交。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">完整原来源、目标展示原投影与受控新值。</param>
    /// <returns>真实拒绝或接受，无变化保留引用与重做。</returns>
    public static LyricBranchEditResult Configure(ProjectWorkspace workspace, LyricBranchConfigureEditRequest request) =>
        CommitPrepared(workspace, PrepareConfigure(workspace, request));

    /// <summary>只准备Configure候选，不改变工程、历史或分配第二次标识。</summary>
    /// <param name="workspace">拥有工程的实际工作区。</param>
    /// <param name="request">原有来源用例的完整门禁及输入。</param>
    /// <returns>绑定工作区、代次和原工程引用的固定候选。</returns>
    public static PreparedLyricBranchEdit PrepareConfigure(ProjectWorkspace workspace, LyricBranchConfigureEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Project source = workspace.Project;
        long generation = workspace.Generation;
        return new(workspace, generation, source, PrepareConfigureCore(workspace, request), "branch.configure");
    }

    private static LyricBranchEditResult PrepareConfigureCore(ProjectWorkspace workspace, LyricBranchConfigureEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline); ArgumentNullException.ThrowIfNull(request.Configuration);
        if (CheckBranch(workspace, request.Baseline, out LyricBranch? branch) is { } error) return Reject(workspace.Project, error);
        Project source = workspace.Project;
        var settings = request.Configuration;
        LyricBranch updated = branch! with { Name = settings.Name, Language = settings.Language, Role = settings.Role,
            TimingMode = settings.TimingMode, StyleOverride = settings.StyleOverride };
        if (request.Association is { } association)
        {
            if (TryAssociate(source, updated, association, out updated) is { } associationError)
                return Reject(source, associationError);
        }
        if (CheckConfiguration(source, updated) is { } configurationError) return Reject(source, configurationError);
        LyricBranchPresentation? presentation = request.Presentation;
        if ((presentation is null) != (request.ExpectedPresentation is null)
            || presentation is not null && (presentation.BranchId != branch!.Id || request.ExpectedPresentation!.BranchId != branch.Id))
            return Reject(source, LyricBranchEditErrorCode.InvalidTarget);
        LyricBranchPresentation currentPresentation = source.LyricPresentation.Resolve(branch!.Id);
        if (request.ExpectedPresentation is not null && currentPresentation != request.ExpectedPresentation)
            return Reject(source, LyricBranchEditErrorCode.TargetStale);
        if (presentation is not null && (!presentation.IsValid
            || presentation.SlotSequenceProfile is not null && presentation.PolicyFor(updated) != LyricBranchAnimationPolicy.Karaoke
            || presentation.Lines.Any(line => !updated.Lines.Any(sourceLine => sourceLine.Id == line.LineId))))
            return Reject(source, LyricBranchEditErrorCode.InvalidPresentation);
        Project candidate = source;
        if (updated != branch)
        {
            candidate = LyricBranchEditor.Update(source, updated);
            if (ReferenceEquals(source, candidate)) return Reject(source, LyricBranchEditErrorCode.InvalidConfiguration);
        }
        if (presentation is not null && presentation != currentPresentation)
        {
            // 原投影的纯树变换保留现有片段、前奏和节点；最终统一出口只校验一次候选。
            candidate = candidate with { LyricPresentation = candidate.LyricPresentation.SetBranch(presentation) };
        }
        return PrepareSubmission(source, candidate);
    }

    /// <summary>只改变稳定行的主行关联，保留当前正文、元素、注音、样式及精确时间。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">稳定来源／行与原关联。</param>
    /// <returns>一次关联命令或无变化，不猜测失效的主行。</returns>
    public static LyricBranchEditResult LinkPrimaryLine(ProjectWorkspace workspace, LyricPrimaryLineLinkEditRequest request) =>
        CommitPrepared(workspace, PrepareLinkPrimaryLine(workspace, request));

    /// <summary>只准备LinkPrimaryLine候选，不改变工程、历史或分配第二次标识。</summary>
    /// <param name="workspace">拥有工程的实际工作区。</param>
    /// <param name="request">原有来源用例的完整门禁及输入。</param>
    /// <returns>绑定工作区、代次和原工程引用的固定候选。</returns>
    public static PreparedLyricBranchEdit PrepareLinkPrimaryLine(ProjectWorkspace workspace, LyricPrimaryLineLinkEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Project source = workspace.Project;
        long generation = workspace.Generation;
        return new(workspace, generation, source, PrepareLinkPrimaryLineCore(workspace, request), "branch.configure");
    }

    private static LyricBranchEditResult PrepareLinkPrimaryLineCore(ProjectWorkspace workspace, LyricPrimaryLineLinkEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(request);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (string.IsNullOrWhiteSpace(request.BranchId)) return Reject(source, LyricBranchEditErrorCode.InvalidTarget);
        LyricBranch? branch = source.Lyrics.Branches.FirstOrDefault(current => current.Id == request.BranchId);
        if (branch is null) return Reject(source, LyricBranchEditErrorCode.TargetStale);
        if (TryAssociate(source, branch, new(request.LineId, request.ExpectedPrimaryLineId, request.PrimaryLineId), out var updated) is { } associationError)
            return Reject(source, associationError);
        if (ReferenceEquals(updated, branch)) return new(true, source, ProjectLyricsChangeSet.None);
        if (CheckConfiguration(source, updated) is { } configurationError) return Reject(source, configurationError);
        Project candidate = LyricBranchEditor.Update(source, updated);
        return ReferenceEquals(candidate, source) ? Reject(source, LyricBranchEditErrorCode.InvalidConfiguration)
            : PrepareSubmission(source, candidate);
    }

    private static LyricBranchEditErrorCode? TryAssociate(Project source, LyricBranch branch, LyricBranchLineAssociationEdit association,
        out LyricBranch updated)
    {
        updated = branch;
        if (branch.Id == source.Lyrics.PrimaryBranchId) return LyricBranchEditErrorCode.PrimaryBranchProtected;
        if (string.IsNullOrWhiteSpace(association.LineId)) return LyricBranchEditErrorCode.InvalidTarget;
        int index = -1;
        for (int current = 0; current < branch.Lines.Length; current++)
            if (branch.Lines[current].Id == association.LineId) { index = current; break; }
        if (index < 0 || branch.Lines[index].PrimaryLineId != association.ExpectedPrimaryLineId) return LyricBranchEditErrorCode.TargetStale;
        if (association.PrimaryLineId == association.ExpectedPrimaryLineId) return null;
        if (association.PrimaryLineId is { } id && !source.Lyrics.Lines.Any(line => line.Id == id)) return LyricBranchEditErrorCode.InvalidPrimaryLine;
        updated = branch with { Lines = branch.Lines.SetItem(index, branch.Lines[index] with { PrimaryLineId = association.PrimaryLineId }) };
        return null;
    }

    private static LyricBranchEditErrorCode? CheckConfiguration(Project source, LyricBranch branch)
    {
        if ((branch.Id == source.Lyrics.PrimaryBranchId) != (branch.Role == LyricBranchRole.Primary))
            return LyricBranchEditErrorCode.PrimaryBranchProtected;
        if (!branch.IsValid) return LyricBranchEditErrorCode.InvalidConfiguration;
        try { _ = LyricStyleResolver.Resolve(source.LyricStyle, branchOverride: branch.StyleOverride); }
        catch (ArgumentException) { return LyricBranchEditErrorCode.InvalidConfiguration; }
        return null;
    }

    private static LyricBranchEditErrorCode? CheckBranch(ProjectWorkspace workspace, LyricBranchEditBaseline baseline, out LyricBranch? branch)
    {
        ArgumentNullException.ThrowIfNull(baseline.ExpectedBranch);
        branch = null;
        if (CheckWorkspace(workspace, baseline.ExpectedWorkspace, baseline.ExpectedWorkspaceGeneration) is { } error) return error;
        if (string.IsNullOrWhiteSpace(baseline.BranchId) || baseline.ExpectedBranch.Id != baseline.BranchId) return LyricBranchEditErrorCode.InvalidTarget;
        branch = workspace.Project.Lyrics.Branches.FirstOrDefault(current => current.Id == baseline.BranchId);
        return branch is not null && branch == baseline.ExpectedBranch ? null : LyricBranchEditErrorCode.TargetStale;
    }

    private static LyricBranchEditErrorCode? CheckWorkspace(ProjectWorkspace workspace, ProjectWorkspace expected, long generation)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(workspace, expected) && workspace.Generation == generation ? null : LyricBranchEditErrorCode.WorkspaceStale;
    }

    /// <summary>提交已准备且通过宿主预算检查的固定来源候选。</summary>
    /// <param name="workspace">拥有工程和唯一历史的实际工作区。</param>
    /// <param name="prepared">纯准备阶段捕获的固定候选。</param>
    /// <returns>实际提交结果；旧工作区或工程引用拒绝且不改变历史。</returns>
    public static LyricBranchEditResult CommitPrepared(ProjectWorkspace workspace, PreparedLyricBranchEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(prepared);
        if (CheckWorkspace(workspace, prepared.Workspace, prepared.Generation) is { } error)
            return Reject(workspace.Project, error);
        if (!ReferenceEquals(workspace.Project, prepared.Source))
            return Reject(workspace.Project, LyricBranchEditErrorCode.TargetStale);
        LyricBranchEditResult result = prepared.Result;
        if (!result.Accepted || ReferenceEquals(result.Project, prepared.Source)) return result;
        workspace.History.BreakMergeGroup();
        workspace.Execute(ProjectCommand.Create(prepared.CommandName, _ => result.Project));
        workspace.History.BreakMergeGroup();
        return result;
    }

    private static LyricBranchEditResult PrepareSubmission(Project source, Project candidate, string? selectedBranchId = null) =>
        ProjectEditSubmission.TryPrepare(source, candidate, out Project result, out string reason)
            ? new(true, result, LyricContentEditUseCase.DescribeChanges(source, result), selectedBranchId: selectedBranchId)
            : Reject(source, LyricBranchEditErrorCode.DomainValidationFailed, reason);

    private static LyricBranchEditResult Reject(Project source, LyricBranchEditErrorCode code, string? detail = null) =>
        new(false, source, ProjectLyricsChangeSet.None, new(code, detail));
}
