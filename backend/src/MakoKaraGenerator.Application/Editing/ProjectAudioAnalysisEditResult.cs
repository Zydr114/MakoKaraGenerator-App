using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>显式分析采用与清除的中立拒绝代码。</summary>
public enum ProjectAudioAnalysisEditErrorCode
{
    /// <summary>工作区实例或新建、打开、恢复代次已经改变。</summary>
    WorkspaceStale,
    /// <summary>音频路径或完整固定媒体身份已经改变。</summary>
    AudioSourceStale,
    /// <summary>已经接受的分析状态已经改变。</summary>
    AnalysisStale,
    /// <summary>当前工程不再是捕获时的同一不可变实例。</summary>
    ProjectStale,
    /// <summary>历史连续性已改变，撤销回原实例不能复活旧输入。</summary>
    EditContinuityStale,
    /// <summary>当前工程没有可接受分析的音频来源。</summary>
    AudioSourceMissing,
    /// <summary>完整成功分析不属于当前固定音频来源。</summary>
    AnalysisSourceMismatch,
    /// <summary>准备结果不是完整成功分析。</summary>
    PreparedAnalysisUnavailable,
    /// <summary>活动历史合并组会吞并独立的分析编辑步骤。</summary>
    HistoryGroupActive,
}

/// <summary>由适配层解释或本地化的分析编辑拒绝原因。</summary>
/// <param name="Code">中立拒绝代码。</param>
/// <param name="Detail">可选的具体原因。</param>
public sealed record ProjectAudioAnalysisEditError(ProjectAudioAnalysisEditErrorCode Code, string? Detail = null);

/// <summary>一次完整分析采用或清除的原子结果。</summary>
public sealed record ProjectAudioAnalysisEditResult
{
    internal ProjectAudioAnalysisEditResult(bool accepted, Project project, bool changed,
        ProjectAudioAnalysisEditError? error = null)
    {
        Accepted = accepted;
        Project = project;
        Changed = changed;
        Error = error;
    }

    /// <summary>是否通过基线与分析状态门禁，无变化也属于接受。</summary>
    public bool Accepted { get; }
    /// <summary>是否产生实际工程变化。</summary>
    public bool Changed { get; }
    /// <summary>结束时的当前工程；拒绝与无变化保持原实例。</summary>
    public Project Project { get; }
    /// <summary>拒绝原因，接受时为空。</summary>
    public ProjectAudioAnalysisEditError? Error { get; }
}
