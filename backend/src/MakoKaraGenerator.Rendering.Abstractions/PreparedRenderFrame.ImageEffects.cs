using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>真实实例拥有的图请求；原生后端失败可保留作者输入代次。</summary>
/// <param name="InstanceId">固定效果实例。</param><param name="ArtifactHash">编译输入代次。</param>
/// <param name="Binding">已固定的图绑定。</param>
public sealed record PreparedImageEffectBinding(EffectInstanceId InstanceId, string ArtifactHash, ImageEffectBinding Binding)
{
    /// <summary>发出绑定的真实控制器来源，独立源码接缝为 null。</summary>
    public EffectPackageIdentity? PackageIdentity { get; init; }
}

public sealed partial class PreparedRenderFrame
{
    /// <summary>按工程实例顺序固定的图请求；同实例同目标唯一。</summary>
    public ImmutableArray<PreparedImageEffectBinding> ImageEffects { get; private init; } = [];
    /// <summary>给固定文字帧附加真实目标图描述；不加载资源或求值作者代码。</summary>
    public PreparedRenderFrame WithImageEffects(ImmutableArray<PreparedImageEffectBinding> bindings)
    {
        if (bindings.IsDefault) throw new PreparedFrameException("InvalidPreparedImageEffect", "图绑定不能是 default。");
        HashSet<(EffectInstanceId, ImageEffectTarget)> seen = [];
        foreach (var output in bindings)
        {
            if (output is null || !output.InstanceId.IsValid || string.IsNullOrWhiteSpace(output.ArtifactHash) || output.Binding is not { } binding
                || binding.Graph is null || binding.Parameters is null || binding.Target is null || !seen.Add((output.InstanceId, binding.Target)))
                throw new PreparedFrameException("InvalidPreparedImageEffect", "图绑定身份、参数或重复目标无效。");
            bool present = binding.Target switch
            {
                ImageEffectTarget.Token token => FinalGeometry.Any(item => item.WithRuby == token.Target && item.BaseGeometry.CanTransformIndependently),
                ImageEffectTarget.Line line => FinalLineGeometry.Any(item => item.Target == line.Target),
                ImageEffectTarget.Mark mark => FinalMarkGeometry.Any(item => item.Target == mark.Target),
                ImageEffectTarget.Group group => FinalGroupGeometry.Any(item => item.Target == group.Target),
                _ => false,
            };
            if (!present) throw new PreparedFrameException("InvalidPreparedImageEffectTarget", "图绑定必须是本准备帧的真实装载，元素族须独立。");
        }
        if (bindings.SequenceEqual(ImageEffects)) return this;
        return new(this, OverlayBatches, DiagnosticSnapshot, VisualBudget, Particles, EmissionSchedule) { ImageEffects = bindings };
    }
}
