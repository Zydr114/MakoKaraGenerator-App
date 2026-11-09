using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MakoKaraGenerator.Scripting.Tests")]

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>A trusted owner-local projection; only its detached Catalog enters the author SDK.</summary>
internal sealed partial class ProjectMediaFeatureProjection
{
    private readonly ProjectMediaFeatureBinding _binding;
    private readonly ImmutableArray<ProjectMediaDependency> _dependencies;

    private ProjectMediaFeatureProjection(ProjectMediaFeatureBinding binding, ProjectMediaDependencyOwner? owner,
        ImmutableArray<ProjectMediaDependency> dependencies)
    {
        RequireFixed(binding);
        if (dependencies.IsDefault || dependencies.Any(dependency => !ReferenceEquals(dependency.Owner, owner)))
            throw Mismatch();
        foreach (ProjectMediaDependency dependency in dependencies) ProjectMediaFeatureBinding.RequireDependency(binding.Plan, dependency);
        if (owner is not null && (!binding.Plan.Owners.Any(candidate => ReferenceEquals(candidate, owner))
            || !binding.Plan.Dependencies.Where(dependency => ReferenceEquals(dependency.Owner, owner)).SequenceEqual(dependencies)))
            throw Mismatch();
        _binding = binding; _dependencies = dependencies; Owner = owner;
        Catalog = new(ProjectMediaFeatureValues(binding.Input), [.. dependencies.Select(dependency =>
            (dependency.Query.Id, Space(dependency.Query.Space), dependency.WindowUs))]);
    }

    /// <summary>The exact plan owner; null for an export without media references. Snapshot membership is admitted outside this projection.</summary>
    internal ProjectMediaDependencyOwner? Owner { get; }
    /// <summary>Resource-free metadata with a fresh scope for this particular owner/session.</summary>
    internal MediaQueryCatalog Catalog { get; }

    /// <summary>Resolves a real compiled instance, never a standalone source-only pseudo package.</summary>
    internal static ProjectMediaFeatureProjection ResolveInstance(ProjectMediaFeatureBinding binding,
        CompiledFrameEffect definition, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(definition); token.ThrowIfCancellationRequested();
        if (definition.PackageIdentity is not { } identity || definition.PackageDefinition is not { } package
            || definition.PackageDeclaration is not { } export
            || !ReferenceEquals(package.GetExport(identity.ExportId), export)) throw Mismatch();
        if (!definition.Id.IsValid || export is not (EffectControllerDefinition or EffectGraphDefinition)) throw Mismatch();
        PackageSdkParameters.Validate(package, export, definition.Parameters, definition.Resources);
        return Resolve(binding, ProjectMediaDependencyOwnerKind.Instance, definition.Id.Value, "",
            EffectPackageMediaQueries.Read(package.Contents, token), identity.ExportId, definition.Parameters, null, token);
    }

    /// <summary>A metadata-only trusted seam; expected values must come from the formally read actual snapshot overrides.</summary>
    internal static ProjectMediaFeatureProjection ResolveInstance(ProjectMediaFeatureBinding binding, EffectInstanceId instance,
        EffectPackageMediaQueryCatalog package, string exportId, ParameterValues parameters,
        ImmutableDictionary<string, EffectParameterValue> expectedParameters, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(expectedParameters);
        token.ThrowIfCancellationRequested();
        if (!instance.IsValid || package.GetExport(exportId) is not (EffectControllerDefinition or EffectGraphDefinition)) throw Mismatch();
        return Resolve(binding, ProjectMediaDependencyOwnerKind.Instance, instance.Value, "", package, exportId, parameters, expectedParameters, token);
    }

    /// <summary>Retains the real source reference even when its resolved base MaterialValue is shared.</summary>
    internal static ProjectMediaFeatureProjection ResolveSource(ProjectMediaFeatureBinding binding,
        PreparedMaterialOwner.Source owner, EffectPackageDefinition package, MaterialValue.PackageMaterial value,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return ResolveSource(binding, owner, EffectPackageMediaQueries.Read(package.Contents, token), value, token);
    }

    /// <summary>Uses the same Core typed parameter reader as the fixed dependency resolver.</summary>
    internal static ProjectMediaFeatureProjection ResolveSource(ProjectMediaFeatureBinding binding,
        PreparedMaterialOwner.Source owner, EffectPackageMediaQueryCatalog package, MaterialValue.PackageMaterial value,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(value);
        token.ThrowIfCancellationRequested();
        if (!owner.IsValid) throw Mismatch();
        SourceMaterialReference source = owner.Reference; var material = source.Material;
        if (package.Contents.PackageId != material.PackageId || package.Contents.Revision != material.Revision
            || package.GetExport(material.ExportId) is not EffectMaterialDefinition export
            || !export.Regions.Contains(source.Region, StringComparer.Ordinal)
            || value.Handle.Identity != new ResourceIdentity(material.PackageId, material.Revision, material.ExportId)) throw Mismatch();
        var resolved = package.ResolveParameters(material.ExportId, material.Parameters.Encode(), token);
        return Resolve(binding, ProjectMediaDependencyOwnerKind.SourceMaterial, source.Path, source.Region, package,
            material.ExportId, value.Parameters, resolved, token);
    }

    private static ProjectMediaFeatureProjection Resolve(ProjectMediaFeatureBinding binding, ProjectMediaDependencyOwnerKind kind,
        string path, string region, EffectPackageMediaQueryCatalog package, string exportId, ParameterValues parameters,
        ImmutableDictionary<string, EffectParameterValue>? expectedParameters, CancellationToken token)
    {
        RequireFixed(binding); token.ThrowIfCancellationRequested();
        if (expectedParameters is not null && !SdkParametersEqual(expectedParameters, parameters, package.Contents)) throw Mismatch();
        var export = package.GetExport(exportId);
        var owners = binding.Plan.Owners.Where(owner => owner.Kind == kind && owner.Path == path && owner.Region == region
            && owner.PackageId == package.Contents.PackageId && owner.Revision == package.Contents.Revision && owner.ExportId == exportId).ToArray();
        if (export.MediaQueries.IsEmpty)
        {
            if (owners.Length != 0) throw Mismatch();
            return new(binding, null, []);
        }
        if (owners.Length != 1) throw Mismatch();
        ProjectMediaDependencyOwner owner = owners[0];
        if (!SdkParametersEqual(owner.Parameters, parameters, package.Contents)
            || expectedParameters is not null && !CoreParametersEqual(owner.Parameters, expectedParameters)) throw Mismatch();
        ImmutableArray<ProjectMediaDependency> dependencies = [.. binding.Plan.Dependencies.Where(dependency => ReferenceEquals(dependency.Owner, owner))];
        if (dependencies.Length != export.MediaQueries.Length) throw Mismatch();
        for (int index = 0; index < dependencies.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            ProjectMediaDependency dependency = dependencies[index]; EffectMediaQueryReference reference = export.MediaQueries[index];
            EffectMediaQueryDefinition query = package.GetQuery(reference.QueryId);
            long window = query.Window switch
            {
                EffectMediaWindow.Literal literal => literal.DurationUs,
                EffectMediaWindow.Parameter parameter when owner.Parameters[parameter.ParameterId] is EffectParameterValue.Duration duration => duration.Value,
                _ => throw Mismatch(),
            };
            if (dependency.Query.Id != query.Id || dependency.Query.Space != query.Space || dependency.WindowUs != window
                || dependency.Reference.QueryId != reference.QueryId || !FallbackEqual(dependency.Reference.Pending, reference.Pending)
                || !FallbackEqual(dependency.Reference.Unavailable, reference.Unavailable) || !FallbackEqual(dependency.Reference.NoMedia, reference.NoMedia)) throw Mismatch();
        }
        return new(binding, owner, dependencies);
    }

    private static bool FallbackEqual(EffectMediaFallbackReference left, EffectMediaFallbackReference right)
        => left.GetType() == right.GetType() && left.OutputIds.SequenceEqual(right.OutputIds, StringComparer.Ordinal);

    // 绑定构造时已完整校验输入与计划（会话借用时 ValidateSnapshot 再校验一次），两者都是深度不可变对象；
    // 逐阶段、逐采样只核对采样器仍暴露同一对象，不再重算整份身份摘要（每实例每帧重复，随行数线性放大）。
    private static void RequireFixed(ProjectMediaFeatureBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(binding.Sampler.Input, binding.Input) || !ReferenceEquals(binding.Sampler.Plan, binding.Plan)) throw Mismatch();
    }

    /// <summary>A new handle for exactly one Text or Visual call. The caller must invalidate it in finally.</summary>
    internal MediaFeatures CreateStage(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); RequireFixed(_binding);
        var mapping = _binding.Input.Context.Mapping;
        return new(Catalog, new(mapping.CanvasWidth, mapping.CanvasHeight), (profile, rect, time, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested(); RequireFixed(_binding);
            int index = -1;
            for (int candidate = 0; candidate < Catalog.Profiles.Length; candidate++)
                if (ReferenceEquals(Catalog.Profiles[candidate], profile)) { index = candidate; break; }
            if (index < 0) throw new EffectOutputException("UndeclaredMediaQuery", "媒体 profile 不属于当前固定 owner 目录。");
            ProjectMediaDependency dependency = _dependencies[index];
            ProjectMediaFeatureBinding.RequireDependency(_binding.Plan, dependency);
            ProjectMediaFeatureSample result = _binding.Sampler.Sample(dependency, new(rect.Left, rect.Top, rect.Right, rect.Bottom), time, cancellation);
            cancellation.ThrowIfCancellationRequested(); RequireFixed(_binding);
            if (result is null || !ReferenceEquals(result.Input, _binding.Input) || !ReferenceEquals(result.Dependency, dependency)
                || !Same(result.Rect.Left, rect.Left) || !Same(result.Rect.Top, rect.Top)
                || !Same(result.Rect.Right, rect.Right) || !Same(result.Rect.Bottom, rect.Bottom) || result.ProjectTimeUs != time)
                throw ResultMismatch();
            return DetachedSample(result, Catalog.Input!, profile, rect, time);
        }, token);
    }

    private static EffectOutputException Mismatch() => new("MediaFeatureBindingMismatch", "媒体绑定必须匹配真实 owner、完整参数和精确依赖闭包。");
    private static EffectOutputException ResultMismatch() => new("MediaFeatureResultMismatch", "媒体结果必须保持固定输入和精确请求身份及数值分支。");
}
