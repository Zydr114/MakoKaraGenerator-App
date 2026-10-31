using System.Collections.Immutable;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>纯源数据编辑；所有改动可由一次命令加入工程历史。</summary>
public static class ProjectEffectsEditor
{
    /// <summary>内容相同时返回原工程。</summary>
    public static Project Replace(Project project, ProjectEffects effects)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(effects);
        return project.Effects.Equals(effects) ? project : project with { Effects = effects };
    }
    /// <summary>注册完整不可变修订，已注册时不产生历史。</summary>
    public static ProjectEffects Register(ProjectEffects effects, EffectPackageContents package)
    {
        ArgumentNullException.ThrowIfNull(effects); ArgumentNullException.ThrowIfNull(package);
        return effects.Packages.Any(p => p.PackageId == package.PackageId && p.Revision == package.Revision) ? effects
            : new(effects.Packages.Add(package), effects.Instances, effects.Budgets);
    }

    /// <summary>在执行顺序末尾加入一个已经通过包语义检查的实例。</summary>
    public static ProjectEffects AddInstance(ProjectEffects effects, ProjectEffectInstance instance)
    {
        ArgumentNullException.ThrowIfNull(effects); ArgumentNullException.ThrowIfNull(instance);
        if (effects.Instances.Any(item => item.Id == instance.Id))
            throw new EffectPackageException("EffectInstanceDuplicate", "effects.instances", "效果实例 ID 已存在。");
        ValidateInstance(effects, instance);
        return new(effects.Packages, effects.Instances.Add(instance), effects.Budgets);
    }

    /// <summary>替换一个实例的源数据，保持它在执行顺序中的位置。</summary>
    public static ProjectEffects ReplaceInstance(ProjectEffects effects, ProjectEffectInstance instance)
    {
        ArgumentNullException.ThrowIfNull(effects); ArgumentNullException.ThrowIfNull(instance);
        int index = IndexOf(effects.Instances, instance.Id);
        if (index < 0) throw new EffectPackageException("EffectInstanceMissing", "effects.instances", "效果实例不存在。");
        ValidateInstance(effects, instance);
        var instances = effects.Instances.SetItem(index, instance);
        return effects.Instances.SequenceEqual(instances) ? effects : new(effects.Packages, instances, effects.Budgets);
    }

    /// <summary>删除一个效果实例；包闭包保留，便于撤销或其他实例继续使用。</summary>
    public static ProjectEffects RemoveInstance(ProjectEffects effects, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(effects); ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        int index = IndexOf(effects.Instances, instanceId);
        if (index < 0) throw new EffectPackageException("EffectInstanceMissing", "effects.instances", "效果实例不存在。");
        return new(effects.Packages, effects.Instances.RemoveAt(index), effects.Budgets);
    }

    /// <summary>移动执行顺序；越过边界时保持原位置。</summary>
    public static ProjectEffects MoveInstance(ProjectEffects effects, string instanceId, int offset)
    {
        ArgumentNullException.ThrowIfNull(effects); ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        int index = IndexOf(effects.Instances, instanceId);
        if (index < 0) throw new EffectPackageException("EffectInstanceMissing", "effects.instances", "效果实例不存在。");
        int target = Math.Clamp(index + offset, 0, effects.Instances.Length - 1);
        if (target == index) return effects;
        var instances = effects.Instances.ToBuilder(); ProjectEffectInstance value = instances[index];
        instances.RemoveAt(index); instances.Insert(target, value);
        return new(effects.Packages, instances, effects.Budgets);
    }
    /// <summary>注销工程中没有实例或源材质引用的包；不删除磁盘文件。</summary>
    public static Project Unregister(Project project, string packageId, string revision)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.PresetApplications.Items.Any(item => item.Snapshot.Packages.Any(package => package.PackageId == packageId && package.Revision == revision)))
            throw new EffectPackageException("EffectPackageInUse", "effects.packages", "包仍被固定预设快照引用，不能注销。");
        if (SourceMaterialCatalog.Collect(project).Any(r => r.Material.PackageId == packageId && r.Material.Revision == revision))
            throw new EffectPackageException("EffectPackageInUse", "effects.packages", "包仍被源材质引用，不能注销。");
        return Replace(project, Unregister(project.Effects, packageId, revision));
    }
    /// <summary>仅实例目录的低层注销；含源样式的调用者必须使用工程重载。</summary>
    public static ProjectEffects Unregister(ProjectEffects effects, string packageId, string revision)
    {
        ArgumentNullException.ThrowIfNull(effects);
        if (effects.Instances.Any(i => i.PackageId == packageId && i.Revision == revision))
            throw new EffectPackageException("EffectPackageInUse", "effects.packages", "仍被实例引用的包不能注销，包括禁用实例。");
        var packages = effects.Packages.Where(p => p.PackageId != packageId || p.Revision != revision).ToArray();
        return packages.Length == effects.Packages.Length ? effects : new(packages, effects.Instances, effects.Budgets);
    }
    /// <summary>复制源实例并生成独立身份与种子；不复制历史／粒子状态。</summary>
    public static ProjectEffects Duplicate(ProjectEffects effects, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var source = effects.Instances.FirstOrDefault(i => i.Id == instanceId) ?? throw new ArgumentException("实例不存在。", nameof(instanceId));
        ulong seed; do { seed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)); } while (seed == source.Seed);
        return new(effects.Packages, effects.Instances.Add(source with { Id = "fx:" + Guid.NewGuid().ToString("N"), Name = source.Name + " 副本", Seed = seed, TemplateOrigin = null }), effects.Budgets);
    }
    /// <summary>只升级显式选择的实例，保留其他实例的原修订与全部历史字节。</summary>
    public static ProjectEffects AcceptRevision(ProjectEffects effects, EffectPackageContents package, IEnumerable<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds); var selected = instanceIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(id => !effects.Instances.Any(i => i.Id == id && i.PackageId == package.PackageId)))
            throw new ArgumentException("升级对象不存在或包身份不同。", nameof(instanceIds));
        var registered = Register(effects, package);
        var result = new ProjectEffects(registered.Packages, effects.Instances.Select(i => selected.Contains(i.Id) ? i with { Revision = package.Revision } : i), effects.Budgets);
        return effects.Equals(result) ? effects : result;
    }

    private static void ValidateInstance(ProjectEffects effects, ProjectEffectInstance instance)
    {
        EffectPackageContents package = effects.GetPackage(instance.PackageId, instance.Revision);
        EffectPackageDefinition definition = EffectPackageDefinition.Read(package);
        EffectExportDefinition export = definition.GetExport(instance.ExportId);
        string kind = instance.Binding.Kind.ToString().ToLowerInvariant();
        ImmutableArray<string> allowed = export switch
        {
            EffectControllerDefinition controller => controller.BindingKinds,
            EffectGraphDefinition graph => graph.BindingKinds,
            _ => [],
        };
        if (!allowed.Contains(kind, StringComparer.Ordinal))
            throw new EffectPackageException("EffectBindingUnsupported", "effects.instances.binding", "导出不允许该绑定类型。");
        _ = definition.ResolveParameters(instance.ExportId, instance.Parameters.Encode());
        if (instance.OverlayLayer != EffectOverlayLayer.Package && !export.Outputs.Any(output => output.Kind == "Overlay"))
            throw new EffectPackageException("EffectOverlayLayerUnsupported", "effects.instances.overlayLayer", "图元层覆盖要求实际 Overlay 输出。");
    }

    private static int IndexOf(ImmutableArray<ProjectEffectInstance> instances, string id)
    {
        for (int index = 0; index < instances.Length; index++)
            if (instances[index].Id == id) return index;
        return -1;
    }
}
