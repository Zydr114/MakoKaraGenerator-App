using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享画布、帧率、底色与输出时长的用户设置用例，不进行媒体读取或播放。</summary>
/// <remarks>由工作区所有者同步调用；文本解析、非法草稿、输入法与客户端会话由适配层管理。</remarks>
public static class ProjectSettingsEditUseCase
{
    /// <summary>以原宽高组验证后一次设置正整数画布，奇数尺寸保持合法编辑语义。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">工作区身份、完整宽高原值组及新值。</param>
    /// <returns>原子提交或中立拒绝，其他设置字段保持当前值。</returns>
    public static ProjectSettingsEditResult SetCanvasSize(ProjectWorkspace workspace, ProjectCanvasSizeEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (source.Composition.CanvasWidth != request.ExpectedWidth || source.Composition.CanvasHeight != request.ExpectedHeight)
            return Reject(source, ProjectSettingsEditErrorCode.TargetStale);
        if (request.Width <= 0 || request.Height <= 0) return Reject(source, ProjectSettingsEditErrorCode.InvalidCanvasSize);
        return Submit(workspace, source, CompositionEditor.SetCanvasSize(source, request.Width, request.Height), "composition.canvas");
    }

    /// <summary>验证原帧率后设置精确有理帧率，拒绝未初始化的非正数值。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">工作区身份、原帧率与新帧率。</param>
    /// <returns>原子提交或中立拒绝。</returns>
    public static ProjectSettingsEditResult SetFrameRate(ProjectWorkspace workspace, ProjectFrameRateEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (source.Composition.FrameRate != request.ExpectedFrameRate) return Reject(source, ProjectSettingsEditErrorCode.TargetStale);
        if (request.FrameRate.Numerator <= 0 || request.FrameRate.Denominator <= 0)
            return Reject(source, ProjectSettingsEditErrorCode.InvalidFrameRate);
        return Submit(workspace, source, CompositionEditor.SetFrameRate(source, request.FrameRate), "composition.frame-rate");
    }

    /// <summary>以原底色验证后更新底色，不改变背景媒体及其他设置字段。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">工作区身份、原底色与新底色。</param>
    /// <returns>原子提交或中立拒绝。</returns>
    public static ProjectSettingsEditResult SetBackgroundColor(ProjectWorkspace workspace, ProjectBackgroundColorEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (source.Composition.BackgroundColor != request.ExpectedColor) return Reject(source, ProjectSettingsEditErrorCode.TargetStale);
        return Submit(workspace, source, CompositionEditor.SetBackgroundColor(source, request.Color), "composition.background");
    }

    /// <summary>以原时长验证后设置精确微秒，允许未确定，拒绝非正数。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">工作区身份、原时长与新时长。</param>
    /// <returns>原子提交或中立拒绝。</returns>
    public static ProjectSettingsEditResult SetOutputDuration(ProjectWorkspace workspace, ProjectOutputDurationEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        if (CheckWorkspace(workspace, request.ExpectedWorkspace, request.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (source.Composition.OutputDurationUs != request.ExpectedDurationUs) return Reject(source, ProjectSettingsEditErrorCode.TargetStale);
        if (request.DurationUs is <= 0) return Reject(source, ProjectSettingsEditErrorCode.InvalidOutputDuration);
        return Submit(workspace, source, CompositionEditor.SetOutputDuration(source, request.DurationUs), "composition.duration");
    }

    /// <summary>在同一工作区所有者调用中按当前歌词最后有效时间派生并提交输出时长。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">输出时长字段组的原值与工作区身份。</param>
    /// <returns>没有已知有效时间时接受为无变化，不猜测时长。</returns>
    public static ProjectSettingsEditResult UseLyricsEnd(ProjectWorkspace workspace, ProjectOutputDurationEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        if (CheckDurationBaseline(workspace, baseline) is { } error) return Reject(workspace.Project, error);
        Project source = workspace.Project;
        return CompositionEditor.LyricsEndUs(source) is { } endUs
            ? Submit(workspace, source, CompositionEditor.SetOutputDuration(source, endUs), "composition.duration")
            : new(true, source, false);
    }

    /// <summary>只消费调用方已经准备的媒体时长标量，不读取媒体或访问播放设备。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">输出时长字段组的原值与工作区身份。</param>
    /// <param name="mediaDurationUs">已准备的精确正整数微秒。</param>
    /// <returns>不可用长度被拒绝；可用长度沿原时长命令提交。</returns>
    public static ProjectSettingsEditResult UsePreparedMediaDuration(ProjectWorkspace workspace,
        ProjectOutputDurationEditBaseline baseline, long mediaDurationUs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        if (CheckDurationBaseline(workspace, baseline) is { } error) return Reject(workspace.Project, error);
        Project source = workspace.Project;
        return mediaDurationUs <= 0 ? Reject(source, ProjectSettingsEditErrorCode.MediaDurationUnavailable)
            : Submit(workspace, source, CompositionEditor.SetOutputDuration(source, mediaDurationUs), "composition.duration");
    }

    private static ProjectSettingsEditErrorCode? CheckWorkspace(ProjectWorkspace workspace, ProjectWorkspace expected, long generation)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(workspace, expected) && workspace.Generation == generation ? null : ProjectSettingsEditErrorCode.WorkspaceStale;
    }

    private static ProjectSettingsEditErrorCode? CheckDurationBaseline(ProjectWorkspace workspace, ProjectOutputDurationEditBaseline baseline) =>
        CheckWorkspace(workspace, baseline.ExpectedWorkspace, baseline.ExpectedWorkspaceGeneration)
            ?? (workspace.Project.Composition.OutputDurationUs != baseline.ExpectedDurationUs ? ProjectSettingsEditErrorCode.TargetStale : null);

    private static ProjectSettingsEditResult Submit(ProjectWorkspace workspace, Project source, Project candidate, string name) =>
        ProjectEditSubmission.TryExecute(workspace, source, candidate, name, out Project result, out string reason)
            ? new(true, result, !ReferenceEquals(source, result))
            : Reject(source, ProjectSettingsEditErrorCode.DomainValidationFailed, reason);

    private static ProjectSettingsEditResult Reject(Project source, ProjectSettingsEditErrorCode code, string? detail = null) =>
        new(false, source, false, new(code, detail));
}
