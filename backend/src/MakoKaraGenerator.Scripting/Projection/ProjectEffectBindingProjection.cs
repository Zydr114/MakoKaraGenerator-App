using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>持久工程绑定到宿主与工作区共用的只读 SDK 身份投影。</summary>
public static class ProjectEffectBindingProjection
{
    /// <summary>保持六类绑定、集合顺序和节点后代策略；不扩大输出权限。</summary>
    public static EffectBinding ToBinding(ProjectEffectBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            ProjectEffectBindingKind.Project => new EffectBinding.Project(),
            ProjectEffectBindingKind.Branch => new EffectBinding.Branch(new(value.Id!)),
            ProjectEffectBindingKind.Lines => new EffectBinding.Lines([.. value.Ids.Select(id => new LineId(id))]),
            ProjectEffectBindingKind.Tokens => new EffectBinding.Tokens([.. value.Ids.Select(id => new TokenId(id))]),
            ProjectEffectBindingKind.Clips => new EffectBinding.Clips([.. value.Ids.Select(id => new ClipId(id))]),
            ProjectEffectBindingKind.Node => new EffectBinding.Node(new(value.Id!), value.IncludeDescendants),
            _ => throw new InvalidOperationException("绑定类型无效。"),
        };
    }
}
