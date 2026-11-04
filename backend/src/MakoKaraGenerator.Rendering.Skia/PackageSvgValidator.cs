using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>先完成唯一的静态 SVG 编译与完整图片解码，再返回不可变 inspection。</summary>
/// <remarks>此门禁没有编译 Svg.Skia Picture，也没有创建或观察 GPU mip。</remarks>
public sealed class PackageSvgValidator : IPackageSvgValidator
{
    private readonly SvgResourceBudget _limits;

    /// <summary>冻结本验证器的预算；不使用跨包可变缓存。</summary>
    public PackageSvgValidator(SvgResourceBudget? limits = null)
    { _limits = limits ?? SvgResourceBudget.Default; _limits.Validate(); }

    /// <inheritdoc/>
    public PackageSvgInspection Validate(EffectPackageContents contents, EffectPackageSvgResources resources,
        CancellationToken cancellationToken = default)
        => SvgStaticCompiler.CompilePackage(contents, resources, _limits, cancellationToken).Inspection;
}
