using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Commands;

/// <summary>接受或清除分析修订的一次历史；运行状态不写入工程。</summary>
public sealed class SetAudioAnalysisCommand : IProjectCommand
{
    private readonly ProjectAudioAnalysis? _analysis;
    /// <summary>固定本次接受的源数据。</summary>
    public SetAudioAnalysisCommand(ProjectAudioAnalysis? analysis) { _analysis = analysis; }
    /// <inheritdoc/>
    public string Name => "audio.analysis";
    /// <inheritdoc/>
    public bool Mergeable => false;
    /// <inheritdoc/>
    public Project Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.AudioPath is null && _analysis is not null) throw new ArgumentException("无音频源不能接受分析。");
        if (project.AudioMedia is { } media && _analysis?.Data is { } data && media.Hash != data.SourceHash) throw new ArgumentException("分析属于其他音频源。");
        return project.AudioAnalysis == _analysis ? project : project with { AudioAnalysis = _analysis };
    }
}
