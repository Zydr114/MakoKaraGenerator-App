using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>一次显式分析编辑捕获的完整工作区、音频与历史连续性基线。</summary>
public sealed class ProjectAudioAnalysisEditBaseline
{
    internal ProjectAudioAnalysisEditBaseline(ProjectWorkspace workspace)
    {
        ExpectedWorkspace = workspace;
        ExpectedWorkspaceGeneration = workspace.Generation;
        ExpectedProject = workspace.Project;
        ExpectedAudioPath = ExpectedProject.AudioPath;
        ExpectedAudioMedia = ExpectedProject.AudioMedia;
        ExpectedAnalysis = ExpectedProject.AudioAnalysis;
        EditContinuityToken = workspace.History.EditContinuityToken;
    }

    /// <summary>捕获输入的工作区实例。</summary>
    public ProjectWorkspace ExpectedWorkspace { get; }
    /// <summary>捕获时新建、打开或恢复的工作区代次。</summary>
    public long ExpectedWorkspaceGeneration { get; }
    /// <summary>捕获时的完整不可变工程实例。</summary>
    public Project ExpectedProject { get; }
    /// <summary>捕获时的音频路径声明，不授予文件访问能力。</summary>
    public string? ExpectedAudioPath { get; }
    /// <summary>捕获时的固定音频身份，未固定时为空。</summary>
    public MediaFileRevision? ExpectedAudioMedia { get; }
    /// <summary>捕获时已经接受的完整分析状态。</summary>
    public ProjectAudioAnalysis? ExpectedAnalysis { get; }

    internal object EditContinuityToken { get; }
}
