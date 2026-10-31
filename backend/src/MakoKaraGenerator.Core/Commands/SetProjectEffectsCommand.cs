using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Commands;

/// <summary>一次原子效果改动；撤销恢复完整包引用、实例、参数及顺序。</summary>
public sealed class SetProjectEffectsCommand : IProjectCommand
{
    private readonly ProjectEffects _effects;
    /// <summary>固定本次源数据。</summary>
    public SetProjectEffectsCommand(ProjectEffects effects) { ArgumentNullException.ThrowIfNull(effects); _effects = effects; }
    /// <inheritdoc/>
    public string Name => "effects.set";
    /// <inheritdoc/>
    public bool Mergeable => false;
    /// <inheritdoc/>
    public Project Apply(Project project) => ProjectEffectsEditor.Replace(project, _effects);
}
