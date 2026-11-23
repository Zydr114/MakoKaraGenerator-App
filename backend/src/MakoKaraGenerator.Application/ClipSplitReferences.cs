using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>映射已知片段拆分与被完全替换容器节点的引用；固定相对成员保留运行身份，冲突或不可拆时取消。</summary>
internal static class ClipSplitReferences
{
    public static bool TryRemap(Project source, IReadOnlyDictionary<string, LyricDisplayClip[]> splits, out Project result, out BasicLayoutError? error)
        => TryRemap(source, splits, new Dictionary<string, string>(StringComparer.Ordinal), out result, out error);

    public static bool TryRemap(
        Project source,
        IReadOnlyDictionary<string, LyricDisplayClip[]> splits,
        IReadOnlyDictionary<string, string> nodeRemaps,
        out Project result,
        out BasicLayoutError? error)
    {
        result = source; error = null;
        if (splits.Count == 0 && nodeRemaps.Count == 0) return true;
        try
        {
            bool effectsChanged = false;
            var instances = source.Effects.Instances.Select(instance =>
            {
                if (instance.Binding.Kind == ProjectEffectBindingKind.Clips && instance.Binding.Ids.Any(splits.ContainsKey))
                {
                    effectsChanged = true;
                    return instance with
                    {
                        Binding = new(ProjectEffectBindingKind.Clips,
                            ids: instance.Binding.Ids.SelectMany(id => splits.TryGetValue(id, out var parts) ? parts.Select(part => part.Id) : [id]).Distinct(StringComparer.Ordinal))
                    };
                }
                if (instance.Binding.Kind == ProjectEffectBindingKind.Node && nodeRemaps.TryGetValue(instance.Binding.Id!, out var newNodeId))
                {
                    effectsChanged = true;
                    return instance with
                    {
                        Binding = new(ProjectEffectBindingKind.Node, id: newNodeId, includeDescendants: instance.Binding.IncludeDescendants)
                    };
                }
                return instance;
            }).ToArray();

            bool presetsChanged = false;
            List<PresetApplication> applications = [];
            foreach (var application in source.PresetApplications.Items)
            {
                if (application.Target.Kind == ProjectEffectBindingKind.Node && nodeRemaps.TryGetValue(application.Target.Id!, out var newNodeId))
                {
                    presetsChanged = true;
                    var remappedTarget = new ProjectEffectBinding(ProjectEffectBindingKind.Node, id: newNodeId, includeDescendants: application.Target.IncludeDescendants);
                    var remappedIdentities = application.Identities.Select(member =>
                        member.Binding.Kind == ProjectEffectBindingKind.Node && nodeRemaps.TryGetValue(member.Binding.Id!, out var mappedId)
                            ? new PresetEffectIdentity(member.RecipeId, new(ProjectEffectBindingKind.Node, id: mappedId, includeDescendants: member.Binding.IncludeDescendants), member.InstanceId, member.Seed, member.Enabled)
                            : member).ToArray();
                    applications.Add(new(application.Id, remappedTarget, application.Snapshot, remappedIdentities, application.BranchInputs, application.Enabled));
                    continue;
                }
                if (application.Target.Kind != ProjectEffectBindingKind.Clips || !splits.TryGetValue(application.Target.Ids[0], out var parts))
                { applications.Add(application); continue; }
                presetsChanged = true;
                if (application.Snapshot.EffectRecipes.Any(recipe => recipe.Target == TemplateEffectTarget.Application))
                { error = new(BasicLayoutErrorCode.RecipeConflict) { TemplateName = application.Snapshot.Name }; return false; }
                List<PresetEffectIdentity> assigned = [];
                foreach (var part in parts)
                {
                    var target = new ProjectEffectBinding(ProjectEffectBindingKind.Clips, ids: [part.Id]);
                    var identities = application.Snapshot.EffectRecipes.SelectMany(recipe => PresetApplicationTargets.Resolve(source, target, recipe.Target)
                        .Select(binding => application.Identities.Single(member => member.RecipeId == recipe.RecipeId && member.Binding.Equals(binding)))).ToArray();
                    assigned.AddRange(identities);
                    applications.Add(new(part.Id == application.Target.Ids[0] ? application.Id : "preset:" + Guid.NewGuid().ToString("N"),
                        target, application.Snapshot, identities, application.BranchInputs, application.Enabled));
                }
                if (!assigned.Select(member => member.InstanceId).Order(StringComparer.Ordinal).SequenceEqual(
                    application.Identities.Select(member => member.InstanceId).Order(StringComparer.Ordinal)))
                { error = new(BasicLayoutErrorCode.RecipeConflict) { TemplateName = application.Snapshot.Name }; return false; }
            }
            if (!effectsChanged && !presetsChanged) return true;
            var updated = effectsChanged ? source with { Effects = new(source.Effects.Packages, instances, source.Effects.Budgets) } : source;
            result = presetsChanged ? PresetApplicationsEditor.Replace(updated, new(applications)) : updated;
            return true;
        }
        catch (Exception conflict) when (conflict is ArgumentException or InvalidOperationException or EffectPackageException)
        { result = source; error = new(BasicLayoutErrorCode.ReferenceConflict) { Detail = conflict.Message }; return false; }
    }
}
