using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Commands;

/// <summary>组合预设应用的一次原子历史；撤销恢复快照、身份、种子及全部资源。</summary>
public sealed class SetPresetApplicationsCommand : IProjectCommand
{
    private readonly PresetApplications _applications;
    /// <summary>使用完整预演结果的应用记录。</summary>
    public SetPresetApplicationsCommand(PresetApplications applications)
    { ArgumentNullException.ThrowIfNull(applications); _applications = applications; }
    /// <inheritdoc/>
    public string Name => "presets.set";
    /// <inheritdoc/>
    public bool Mergeable => false;
    /// <inheritdoc/>
    public Project Apply(Project project) => PresetApplicationsEditor.Replace(project, _applications);
}
