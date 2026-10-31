using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>只映射已知片段引用；固定相对成员保留运行身份，范围配方无法安全拆分时取消。</summary>
internal static class ClipSplitReferences
{
    public static bool TryRemap(Project source, IReadOnlyDictionary<string, LyricDisplayClip[]> splits, out Project result, out BasicLayoutError? error)
    {
        result = source; error = null;
        if (splits.Count == 0) return true;
        try
        {
            var instances = source.Effects.Instances.Select(instance => instance.Binding.Kind != ProjectEffectBindingKind.Clips
                || !instance.Binding.Ids.Any(splits.ContainsKey) ? instance : instance with { Binding = new(ProjectEffectBindingKind.Clips,
                    ids: instance.Binding.Ids.SelectMany(id => splits.TryGetValue(id, out var parts) ? parts.Select(part => part.Id) : [id]).Distinct(StringComparer.Ordinal)) }).ToArray();
            List<PresetApplication> applications = [];
            foreach (var application in source.PresetApplications.Items)
            {
                if (application.Target.Kind != ProjectEffectBindingKind.Clips || !splits.TryGetValue(application.Target.Ids[0], out var parts))
                { applications.Add(application); continue; }
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
            result = PresetApplicationsEditor.Replace(source with { Effects = new(source.Effects.Packages, instances, source.Effects.Budgets) }, new(applications));
            return true;
        }
        catch (Exception conflict) when (conflict is ArgumentException or InvalidOperationException or EffectPackageException)
        { result = source; error = new(BasicLayoutErrorCode.ReferenceConflict) { Detail = conflict.Message }; return false; }
    }
}
