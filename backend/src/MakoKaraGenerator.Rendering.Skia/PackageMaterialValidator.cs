using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>使用生产绘制的同一能力矩阵检查全部回退，不偷偷按停止点纯色降级。</summary>
public sealed class PackageMaterialValidator : IPackageMaterialValidator
{
    /// <inheritdoc/>
    public void Validate(ImmutableArray<EffectMaterialDefinition> materials, CancellationToken cancellationToken = default)
    {
        if (materials.IsDefault) throw new ArgumentException("材质集合必须明确。", nameof(materials));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var material in materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (material.Fallback is EffectMaterialFallback.Gradient gradient)
            {
                if (!GradientSupport.IsSupported(gradient.Value))
                    throw new EffectPackageException("PackageMaterialFallbackUnsupported", "exports." + material.Id + ".fallback", GradientSupport.UnsupportedReason(gradient.Value)!);
                double angle = gradient.Value.AngleDegrees * (Math.PI / 180);
                if (!float.IsFinite((float)angle) || gradient.Value.Kind == GradientKind.Radial
                    && (!float.IsFinite((float)gradient.Value.RadiusX) || !float.IsFinite((float)gradient.Value.RadiusY)
                        || (float)gradient.Value.RadiusX <= 0 || (float)gradient.Value.RadiusY <= 0))
                    throw new EffectPackageException("PackageMaterialFallbackUnsupported", "exports." + material.Id + ".fallback", "渐变超出生产浮点范围，不能静默失效。");
            }
        }
    }
}
