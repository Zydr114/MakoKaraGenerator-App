using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享显式分析采用与清除，不执行分析、文件读取或宿主授权。</summary>
/// <remarks>
/// 由工作区单一所有者同步调用。宿主的会话修订、只读策略、结果所有权与文件完整性
/// 必须在调用前完成；本用例不在任务完成时自动采用，也不改变渲染能力合同。
/// </remarks>
public static class ProjectAudioAnalysisEditUseCase
{
    /// <summary>在工作区所有者线程捕获本次显式编辑的完整基线。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <returns>不可伪造的原工程、音频、分析与历史连续性基线。</returns>
    public static ProjectAudioAnalysisEditBaseline Capture(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new(workspace);
    }

    /// <summary>按稳定基线采用已经准备的完整成功分析，一次变化只有一次独立历史。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">本次显式操作捕获的工作区基线。</param>
    /// <param name="analysis">完整成功结果，不接受失败或无流运行状态。</param>
    /// <returns>原子提交或中立拒绝；完整相等的成功结果保持无变化。</returns>
    public static ProjectAudioAnalysisEditResult AdoptPrepared(ProjectWorkspace workspace,
        ProjectAudioAnalysisEditBaseline baseline, ProjectAudioAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(analysis);
        if (CheckBaseline(workspace, baseline) is { } error) return Reject(workspace.Project, error);
        Project source = workspace.Project;
        if (analysis.Data is not { } data) return Reject(source, ProjectAudioAnalysisEditErrorCode.PreparedAnalysisUnavailable);
        if (string.IsNullOrWhiteSpace(source.AudioPath)) return Reject(source, ProjectAudioAnalysisEditErrorCode.AudioSourceMissing);
        if (source.AudioMedia is { } media && media.Hash != data.SourceHash)
            return Reject(source, ProjectAudioAnalysisEditErrorCode.AnalysisSourceMismatch);
        return Submit(workspace, source, analysis);
    }

    /// <summary>按稳定基线清除已经接受的成功、失败或无流分析；空分析保持无变化。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">本次显式操作捕获的工作区基线。</param>
    /// <returns>原子提交或中立拒绝。</returns>
    public static ProjectAudioAnalysisEditResult Clear(ProjectWorkspace workspace, ProjectAudioAnalysisEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        return CheckBaseline(workspace, baseline) is { } error
            ? Reject(workspace.Project, error) : Submit(workspace, workspace.Project, null);
    }

    private static ProjectAudioAnalysisEditErrorCode? CheckBaseline(ProjectWorkspace workspace,
        ProjectAudioAnalysisEditBaseline baseline)
    {
        if (!ReferenceEquals(workspace, baseline.ExpectedWorkspace) || workspace.Generation != baseline.ExpectedWorkspaceGeneration)
            return ProjectAudioAnalysisEditErrorCode.WorkspaceStale;
        Project source = workspace.Project;
        if (!string.Equals(source.AudioPath, baseline.ExpectedAudioPath, StringComparison.Ordinal)
            || source.AudioMedia != baseline.ExpectedAudioMedia)
            return ProjectAudioAnalysisEditErrorCode.AudioSourceStale;
        if (source.AudioAnalysis != baseline.ExpectedAnalysis) return ProjectAudioAnalysisEditErrorCode.AnalysisStale;
        if (!ReferenceEquals(source, baseline.ExpectedProject)) return ProjectAudioAnalysisEditErrorCode.ProjectStale;
        if (!ReferenceEquals(workspace.History.EditContinuityToken, baseline.EditContinuityToken))
            return ProjectAudioAnalysisEditErrorCode.EditContinuityStale;
        return null;
    }

    private static ProjectAudioAnalysisEditResult Submit(ProjectWorkspace workspace, Project source, ProjectAudioAnalysis? analysis)
    {
        if (source.AudioAnalysis == analysis) return new(true, source, false);
        if (workspace.History.IsInGroup) return Reject(source, ProjectAudioAnalysisEditErrorCode.HistoryGroupActive);
        workspace.History.BreakMergeGroup();
        Project result = workspace.Execute(new SetAudioAnalysisCommand(analysis));
        workspace.History.BreakMergeGroup();
        return new(true, result, !ReferenceEquals(source, result));
    }

    private static ProjectAudioAnalysisEditResult Reject(Project source, ProjectAudioAnalysisEditErrorCode code) =>
        new(false, source, false, new(code));
}
