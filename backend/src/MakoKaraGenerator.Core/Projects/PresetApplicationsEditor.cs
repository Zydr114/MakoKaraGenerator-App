using System.Security.Cryptography;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>一次固定快照事务；库修改不调用这里，只有明确应用／更新才改变工程。</summary>
public static class PresetApplicationsEditor
{
    /// <summary>预演多选应用或替换，一个失败对象取消整次操作。</summary>
    public static bool TryApply(Project project, StyleTemplate preset, IEnumerable<ProjectEffectBinding> targets,
        IReadOnlyDictionary<string, string>? branchInputs, out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(preset); ArgumentNullException.ThrowIfNull(targets);
        result = project; reason = string.Empty;
        try
        {
            _ = StyleTemplate.Create(preset.Id, preset.Name, preset.Kind, preset.Style, preset.Properties, preset.EffectRecipes, preset.Packages);
            var selected = targets.Distinct().ToArray(); if (selected.Length == 0) throw new ArgumentException("没有应用对象。");
            if (selected.Select(PresetApplications.ObjectKey).Distinct().Count() != selected.Length) throw new ArgumentException("同一节点的后代策略冲突。");
            if (branchInputs is not null && branchInputs.Values.Any(id => !project.Lyrics.Branches.Any(branch => branch.Id == id))) throw new ArgumentException("订阅分支输入已失效。");
            var items = project.PresetApplications.Items.ToList();
            foreach (var target in selected)
            {
                if (!PresetApplicationTargets.CanApply(project, preset, target, out reason)) return false;
                var old = project.PresetApplications.Find(target); bool update = old?.Snapshot.Id == preset.Id;
                var identities = new List<PresetEffectIdentity>();
                foreach (var recipe in preset.EffectRecipes)
                {
                    var bindings = PresetApplicationTargets.Resolve(project, target, recipe.Target);
                    var previous = update ? old!.Identities.Where(member => member.RecipeId == recipe.RecipeId).ToArray() : [];
                    foreach (var binding in bindings)
                    {
                        var identity = previous.FirstOrDefault(member => member.Binding.Equals(binding));
                        if (identity is null && previous.Length == 1 && bindings.Length == 1) identity = previous[0];
                        identities.Add(identity is null ? new(recipe.RecipeId, binding, "fx:" + Guid.NewGuid().ToString("N"), recipe.FixedSeed ?? NewSeed(), recipe.Enabled)
                            : new(recipe.RecipeId, binding, identity.InstanceId, identity.Seed, identity.Enabled));
                    }
                }
                var application = new PresetApplication(old?.Id ?? "preset:" + Guid.NewGuid().ToString("N"), target, preset, identities, branchInputs,
                    enabled: !update || old!.Enabled);
                if (old is null) items.Add(application); else items[items.IndexOf(old)] = application;
            }
            result = Replace(project, new(items)); return true;
        }
        catch (Exception error) when (error is ArgumentException or EffectPackageException) { result = project; reason = error.Message; return false; }
    }

    /// <summary>明确更新同一个预设身份，保持既有实例／种子／启停状态。</summary>
    public static bool TryUpdate(Project project, string applicationId, StyleTemplate snapshot,
        IReadOnlyDictionary<string, string>? branchInputs, out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(snapshot);
        var item = project.PresetApplications.Items.FirstOrDefault(application => application.Id == applicationId);
        if (item is null || item.Snapshot.Id != snapshot.Id) { result = project; reason = "更新对象不存在或预设身份不同；请选择替换预设。"; return false; }
        return TryApply(project, snapshot, [item.Target], branchInputs ?? item.BranchInputs, out result, out reason);
    }

    /// <summary>移除直接应用；缺失的源对象也能明确移除，不删除祖先应用。</summary>
    public static Project Remove(Project project, IEnumerable<ProjectEffectBinding> targets)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(targets);
        var keys = targets.Select(PresetApplications.ObjectKey).ToHashSet();
        return Replace(project, new(project.PresetApplications.Items.Where(item => !keys.Contains(PresetApplications.ObjectKey(item.Target)))));
    }

    /// <summary>切换整个应用；相同状态不产生历史。</summary>
    public static Project SetEnabled(Project project, string applicationId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.PresetApplications.Items.Any(item => item.Id == applicationId)) throw new ArgumentException("应用记录不存在。", nameof(applicationId));
        return Replace(project, new(project.PresetApplications.Items.Select(item => item.Id == applicationId ? item with { Enabled = enabled } : item)));
    }

    /// <summary>一次替换应用集合，并注册完整固定资源；不创建重复实例真值。</summary>
    public static Project Replace(Project project, PresetApplications applications)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(applications);
        var effects = project.Effects;
        foreach (var package in applications.Items.SelectMany(item => item.Snapshot.Packages)) effects = ProjectEffectsEditor.Register(effects, package);
        if (applications.Items.SelectMany(item => item.Identities).Any(identity => effects.Instances.Any(instance => instance.Id == identity.InstanceId)))
            throw new ArgumentException("预设实例身份与工程直接实例冲突。", nameof(applications));
        return project.PresetApplications.Equals(applications) && project.Effects.Equals(effects) ? project
            : project with { PresetApplications = applications, Effects = effects };
    }

    private static ulong NewSeed() => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
}
