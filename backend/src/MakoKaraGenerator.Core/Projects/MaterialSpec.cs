using MakoKaraGenerator.Core.Effects;
using System.Text;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>持久源材质的唯一判别真值；不含native句柄或另一份回退颜色。</summary>
public abstract record MaterialSpec
{
    private protected MaterialSpec() { }
    /// <summary>格式化判别身份，不调用只适用于静态材质的颜色访问外观。</summary>
    protected virtual bool PrintMembers(StringBuilder builder) { builder.Append("Kind = ").Append(GetType().Name); return true; }
    /// <summary>唯一纯色。</summary>
    public sealed record Solid(RgbaColor Color) : MaterialSpec;
    /// <summary>完整规范渐变。</summary>
    public sealed record Gradient(GradientSpec Value) : MaterialSpec;
    /// <summary>固定包内材质导出，参数整体保存。</summary>
    public sealed record PackageMaterial : MaterialSpec
    {
        /// <summary>建立固定的源材质引用，资源语义由唯一包解析器校验。</summary>
        public PackageMaterial(string packageId, string revision, string exportId, EffectParameterOverrides? parameters = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageId); ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
            _ = ProjectEffectsJson.PackagePath(revision); PackageId = packageId; Revision = revision; ExportId = exportId;
            Parameters = parameters ?? EffectParameterOverrides.Empty;
        }
        /// <summary>包身份。</summary>
        public string PackageId { get; }
        /// <summary>完整资源修订。</summary>
        public string Revision { get; }
        /// <summary>材质导出身份。</summary>
        public string ExportId { get; }
        /// <summary>保留精度的规范完整覆盖参数。</summary>
        public EffectParameterOverrides Parameters { get; }
    }
    /// <summary>本判别值结构是否有效；资源验证不在这里伪造成功。</summary>
    public bool IsValid => this is Solid || this is Gradient { Value.IsValid: true } || this is PackageMaterial;
    /// <summary>静态颜色访问外观；渐变取首色标，包材质没有静态颜色。</summary>
    public RgbaColor RepresentativeColor => this switch
    {
        Solid solid => solid.Color, Gradient { Value.Stops.IsEmpty: false } gradient => gradient.Value.Stops[0].Color,
        PackageMaterial => throw new EffectPackageException("SourceMaterialColorUnavailable", "style.material", "包材质没有纯色，请使用材质参数或明确整体替换。"),
        _ => throw new ArgumentException("材质结构无效。"),
    };
    /// <summary>静态颜色编辑：渐变更新首色标；包材质须明确整体替换。</summary>
    public MaterialSpec WithColor(RgbaColor color) => this switch
    {
        Solid => new Solid(color), Gradient gradient => new Gradient(gradient.Value with { Stops = gradient.Value.Stops.SetItem(0, gradient.Value.Stops[0] with { Color = color }) }),
        _ => throw new EffectPackageException("SourceMaterialReplacementRequired", "style.material", "不能用颜色写入悄悄移除包材质，请明确替换材质。"),
    };
    /// <summary>静态渐变编辑；关闭时明确以首色标建立纯色。</summary>
    public MaterialSpec WithGradient(GradientSpec? gradient) => this is PackageMaterial
        ? throw new EffectPackageException("SourceMaterialReplacementRequired", "style.material", "不能用渐变编辑悄悄移除包材质。")
        : gradient is null ? new Solid(RepresentativeColor) : new Gradient(gradient);
}
