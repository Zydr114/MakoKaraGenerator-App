using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>已完成实例组合的组透明度及真实失败归属。</summary>
/// <param name="InstanceId">最后写入本组的实例。</param><param name="ArtifactHash">该实例固定产物。</param>
/// <param name="Opacity">最终组因素，静态组基线当前为一。</param>
public sealed record PreparedGroupOpacity(EffectInstanceId InstanceId, string ArtifactHash, GroupOpacity Opacity)
{
    /// <summary>最后写入本组的真实控制器来源，独立源码接缝为 null。</summary>
    public EffectPackageIdentity? PackageIdentity { get; init; }
}

public sealed partial class PreparedRenderFrame
{
    /// <summary>已验证真实组的最终共同透明度；不含恒等输出。</summary>
    public ImmutableArray<PreparedGroupOpacity> GroupOpacities { get; private init; } = [];
    /// <summary>附加最终组透明度，不修改源场景、字形或播放头。</summary>
    public PreparedRenderFrame WithGroupOpacities(ImmutableArray<PreparedGroupOpacity> outputs)
    {
        if (outputs.IsDefault) throw new PreparedFrameException("InvalidPreparedGroupOpacity", "组透明度集合不能是 default。");
        HashSet<GroupTarget> seen = [];
        foreach (var item in outputs)
        {
            if (item is null || !item.InstanceId.IsValid || string.IsNullOrWhiteSpace(item.ArtifactHash) || item.Opacity is not { Target: { } target } opacity
                || !seen.Add(target) || !FinalGroupGeometry.Any(group => group.Target == target))
                throw new PreparedFrameException("InvalidPreparedGroupOpacity", "组透明度必须属于唯一真实组及固定实例。");
            if (!double.IsFinite(opacity.Factor) || opacity.Factor is < 0 or > 1)
                throw new PreparedFrameException("InvalidStyleOpacity", "最终组透明度必须在 [0,1]，不夹值。");
        }
        outputs = [.. outputs.Where(item => item.Opacity.Factor != 1)];
        if (outputs.SequenceEqual(GroupOpacities)) return this;
        return new(this, OverlayBatches, DiagnosticSnapshot, VisualBudget, Particles, EmissionSchedule) { GroupOpacities = outputs };
    }
}
