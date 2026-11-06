using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal static class ProjectMediaFeatureAdmission
{
    internal static void Validate(ProjectSnapshot snapshot, ImmutableArray<CompiledFrameEffect> effects,
        PreparedSourceMaterials? sourceMaterials, ProjectMediaFeatureBinding? binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var actual = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications, token);
        if (binding is null)
        {
            if (snapshot.MediaFeatureInput is not null || !actual.Dependencies.IsEmpty)
                throw new EffectOutputException("MediaFeatureBindingRequired", "完整媒体闭包需要同一固定输入和宿主绑定。");
            return;
        }
        if (snapshot.MediaFeatureInput is not { } input || !ProjectRuntimeEffectInputs.SameMediaInput(input, binding.Input))
            throw new EffectOutputException("MediaFeatureBindingMismatch", "固定采样绑定不属于快照的完整媒体输入。");
        binding.ValidateSnapshot(snapshot, input.Context.Mapping.OutputMode, token);
        sourceMaterials?.ValidateSourceClosure(snapshot);
        // A zero-query export has no plan owner: prove actual source ownership
        // independently instead of treating Owner == null as source authority.
        var enabled = snapshot.Effects.Instances.Where(instance => instance.Enabled).ToArray();
        if (effects.Any(definition => definition.PackageIdentity is null)
            || !enabled.Select(instance => instance.Id).SequenceEqual(effects.Select(definition => definition.Id.Value), StringComparer.Ordinal))
            throw Mismatch();
        foreach (var definition in effects)
        {
            token.ThrowIfCancellationRequested();
            var instance = enabled.Single(item => item.Id == definition.Id.Value);
            ValidateInstance(snapshot, instance, definition, token);
            ProjectMediaFeatureProjection.ResolveInstance(binding, definition, token);
        }
        var references = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if (sourceMaterials is null) throw new EffectOutputException("SourceMaterialResourcesRequired", "源材质媒体闭包缺少固定材质投影。");
            var slot = reference.Region == "stroke" ? PreparedMaterialSlot.Stroke
                : reference.Path.EndsWith(".sungFill", StringComparison.Ordinal) || reference.Path.EndsWith(".elapsedFill", StringComparison.Ordinal)
                    ? PreparedMaterialSlot.SungFill : PreparedMaterialSlot.UnsungFill;
            var contents = snapshot.Effects.GetPackage(reference.Material.PackageId, reference.Material.Revision);
            // PreparedSourceMaterials is public and proves no executable capability.
            // Keep the formal Definition gate on this controller-free path too.
            var package = EffectPackageDefinition.Read(contents, token);
            SourceMaterialCatalog.Validate(reference, package);
            ProjectMediaFeatureProjection.ResolveSource(binding, new(reference, slot), package,
                sourceMaterials.Resolve(reference.Material), token);
        }
    }

    private static void ValidateInstance(ProjectSnapshot snapshot, ProjectEffectInstance source,
        CompiledFrameEffect definition, CancellationToken token)
    {
        if (definition.PackageIdentity is not { } identity || identity.PackageId != source.PackageId
            || identity.Revision != source.Revision || identity.ExportId != source.ExportId
            || definition.Seed != source.Seed || !BindingEqual(source.Binding, definition.Binding)
            || !SubscriptionEqual(source.Subscription, definition.Subscription)) throw Mismatch();
        OverlayLayer? layer = source.OverlayLayer switch { EffectOverlayLayer.Package => null,
            EffectOverlayLayer.BehindText => OverlayLayer.BehindText, EffectOverlayLayer.AboveText => OverlayLayer.AboveText,
            _ => throw Mismatch() };
        if (definition.OverlayLayerOverride != layer) throw Mismatch();
        var contents = snapshot.Effects.GetPackage(source.PackageId, source.Revision);
        var catalog = EffectPackageMediaQueries.Read(contents, token);
        var parameters = catalog.ResolveParameters(source.ExportId, source.Parameters.Encode(), token);
        // Reuses the projection's single closed Core/SDK semantic comparer.
        if (!ProjectMediaFeatureProjection.SdkParametersEqual(parameters, definition.Parameters, contents)) throw Mismatch();
    }

    private static bool BindingEqual(ProjectEffectBinding source, EffectBinding actual) => (source.Kind, actual) switch
    {
        (ProjectEffectBindingKind.Project, EffectBinding.Project) => true,
        (ProjectEffectBindingKind.Branch, EffectBinding.Branch value) => source.Id == value.Id.Value,
        (ProjectEffectBindingKind.Lines, EffectBinding.Lines value) => source.Ids.SequenceEqual(value.Ids.Select(id => id.Value), StringComparer.Ordinal),
        (ProjectEffectBindingKind.Tokens, EffectBinding.Tokens value) => source.Ids.SequenceEqual(value.Ids.Select(id => id.Value), StringComparer.Ordinal),
        (ProjectEffectBindingKind.Clips, EffectBinding.Clips value) => source.Ids.SequenceEqual(value.Ids.Select(id => id.Value), StringComparer.Ordinal),
        (ProjectEffectBindingKind.Node, EffectBinding.Node value) => source.Id == value.Id.Value && source.IncludeDescendants == value.IncludeDescendants,
        _ => false,
    };
    private static bool SubscriptionEqual(ProjectEffectSubscription source, SourceSubscription actual) => (source.Kind, actual) switch
    {
        ("project", SourceSubscription.Project) => true,
        ("boundSources", SourceSubscription.BoundSources) => true,
        ("branch", SourceSubscription.Branch value) => source.BranchId == value.Id.Value,
        _ => false,
    };
    private static EffectOutputException Mismatch() => new("MediaFeatureBindingMismatch", "固定媒体实例不属于完整工程源及实际参数。");

    internal static void ValidateOutput(ProjectMediaFeatureBinding? binding, OutputMode outputMode)
    {
        if (binding is null) return;
        ProjectMediaFeatureBinding.ValidateInputPlan(binding.Input, binding.Plan);
        if (!ReferenceEquals(binding.Sampler.Input, binding.Input) || !ReferenceEquals(binding.Sampler.Plan, binding.Plan))
            throw new EffectOutputException("MediaFeatureBindingMismatch", "宿主采样器已更换固定媒体输入或计划。");
        MediaOutputMode mode = outputMode switch { OutputMode.Opaque => MediaOutputMode.Normal,
            OutputMode.Transparent => MediaOutputMode.Transparent, _ => throw new ArgumentOutOfRangeException(nameof(outputMode)) };
        if (binding.Input.Context.Mapping.OutputMode != mode)
            throw new EffectOutputException("MediaFeatureOutputMismatch", "输出模式不属于固定媒体输入。");
    }
}
