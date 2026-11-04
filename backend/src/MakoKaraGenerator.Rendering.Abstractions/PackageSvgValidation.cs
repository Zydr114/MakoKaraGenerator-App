using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering;

/// <summary>将完整 SVG 验证结果绑定至实际固定闭包；不授予 SVG 运行能力。</summary>
public static class PackageSvgValidation
{
    /// <summary>验证全部声明资源并核对返回事实；没有 SVG 时不要求或调用验证器。</summary>
    public static void Validate(EffectPackageContents contents, EffectPackageSvgResources resources,
        IPackageSvgValidator? validator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents); ArgumentNullException.ThrowIfNull(resources);
        cancellationToken.ThrowIfCancellationRequested();
        if (resources.PackageId != contents.PackageId || resources.Revision != contents.Revision)
            throw Failure("PackageSvgResourceOwnershipMismatch", "manifest.json", "SVG 描述不属于当前固定内容闭包。");
        if (resources.ById.IsEmpty) return;
        if (validator is null)
            throw Failure("PackageSvgValidatorMissing", "files", "包内全部 SVG 须完成静态语义及完整图片解码，不能只按描述建立句柄。");

        var inspection = validator.Validate(contents, resources, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection is null || inspection.PackageId != contents.PackageId || inspection.Revision != contents.Revision
            || inspection.Profile != EffectPackageSvgResources.StaticProfile || string.IsNullOrWhiteSpace(inspection.RulesVersion)
            || inspection.EstimatedPrepareBytes < 0 || inspection.Resources.IsDefault
            || inspection.Resources.Length != resources.ById.Count)
            throw Invalid("manifest.json", "SVG 验证结果未完整绑定当前包身份、修订与静态 profile。");

        var files = contents.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        HashSet<string> inspected = new(StringComparer.Ordinal);
        foreach (var resource in inspection.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resource is null || resource.Id is null || !inspected.Add(resource.Id)
                || !resources.ById.TryGetValue(resource.Id, out var definition))
                throw Invalid("files", "SVG 验证结果必须精确覆盖全部资源，不能重复、遗漏或增加身份。");
            string path = definition.DescriptorPath;
            if (resource.Mode != definition.Mode || resource.Viewport != definition.Viewport
                || resource.SourcePath != definition.SourcePath
                || resource.SourceSha256 != files[definition.SourcePath].Sha256)
                throw Invalid(path, "SVG 验证结果的模式、viewport、源路径或原字节摘要与描述不符。");
            ValidateDependencies(resource, definition, files, cancellationToken);
            ValidateAtlasPlans(resource, definition, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateDependencies(SvgResourceInspection inspection, EffectSvgResourceDefinition definition,
        Dictionary<string, EffectPackageFile> files, CancellationToken cancellationToken)
    {
        var expected = definition.Images.Append(definition.SourcePath).Append(definition.DescriptorPath)
            .ToHashSet(StringComparer.Ordinal);
        if (inspection.Dependencies.IsDefault || inspection.Dependencies.Length != expected.Count)
            throw Invalid(definition.DescriptorPath, "SVG 验证结果必须覆盖描述、源与全部声明图片的精确依赖。");
        HashSet<string> inspected = new(StringComparer.Ordinal);
        foreach (var dependency in inspection.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dependency is null || dependency.Path is null || !expected.Contains(dependency.Path)
                || !inspected.Add(dependency.Path) || !files.TryGetValue(dependency.Path, out var file)
                || dependency.Role != file.Role || dependency.Sha256 != file.Sha256)
                throw Invalid(definition.DescriptorPath, "SVG 验证结果含有外来、重复或摘要不符的依赖。");
        }
    }

    private static void ValidateAtlasPlans(SvgResourceInspection inspection, EffectSvgResourceDefinition definition,
        CancellationToken cancellationToken)
    {
        if (inspection.AtlasPlans.IsDefault || inspection.AtlasPlans.Length != definition.Variants.Length)
            throw Invalid(definition.DescriptorPath, "SVG 验证结果必须精确覆盖描述中的全部 atlas 档位。");
        var variants = definition.Variants.ToDictionary(variant => variant.Id, StringComparer.Ordinal);
        HashSet<string> inspected = new(StringComparer.Ordinal);
        foreach (var plan in inspection.AtlasPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan is null || plan.VariantId is null || !inspected.Add(plan.VariantId)
                || !variants.TryGetValue(plan.VariantId, out var variant) || plan.Levels.IsDefaultOrEmpty
                || plan.Levels[0] is not { } first
                || first.Width != variant.Width + 2 * definition.PaddingPx
                || first.Height != variant.Height + 2 * definition.PaddingPx || plan.MipBytes < 0)
                throw Invalid(definition.DescriptorPath, "SVG 验证结果的 atlas 档位身份或基础页尺寸与描述不符。");
        }
    }

    private static EffectPackageException Invalid(string path, string message)
        => Failure("PackageSvgInspectionInvalid", path, message);
    private static EffectPackageException Failure(string code, string path, string message) => new(code, path, message);
}
