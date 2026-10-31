using System.Collections.Immutable;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Internal;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>工程持久效果实例；不保存 DLL 或任何运行状态。</summary>
public sealed record ProjectEffectInstance
{
    /// <summary>稳定实例标识。</summary>
    public required string Id { get; init; }
    /// <summary>用户名称。</summary>
    public required string Name { get; init; }
    /// <summary>是否进入执行计划。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>包身份。</summary>
    public required string PackageId { get; init; }
    /// <summary>不可变包修订。</summary>
    public required string Revision { get; init; }
    /// <summary>导出入口。</summary>
    public required string ExportId { get; init; }
    /// <summary>写入绑定。</summary>
    public ProjectEffectBinding Binding { get; init; } = new(ProjectEffectBindingKind.Project);
    /// <summary>独立读取范围。</summary>
    public ProjectEffectSubscription Subscription { get; init; } = new();
    /// <summary>确定性随机种子。</summary>
    public ulong Seed { get; init; } = 1;
    /// <summary>规范参数草稿，唯一包解析器负责语义检查。</summary>
    public EffectParameterOverrides Parameters { get; init; } = EffectParameterOverrides.Empty;
    /// <summary>模板应用槽位；直接实例为空。</summary>
    public ProjectEffectTemplateOrigin? TemplateOrigin { get; init; }
    /// <summary>宿主固定的临时图元层覆盖。</summary>
    public EffectOverlayLayer OverlayLayer { get; init; } = EffectOverlayLayer.Package;
}

/// <summary>工程保存的可调整资源限额，数值不得被静默降低。</summary>
public sealed record ProjectEffectBudgets
{
    /// <summary>会话中间表面上限。</summary>
    public long SurfaceMemoryBytes { get; init; } = 536870912;
    /// <summary>全部实例活粒子上限。</summary>
    public int LiveParticles { get; init; } = 20000;
    /// <summary>全部拖尾样本上限。</summary>
    public int TrailSamples { get; init; } = 200000;
    /// <summary>每固定区间全部出生记录上限。</summary>
    public int EmissionRecords { get; init; } = 100000;
    /// <summary>全部绘图命令上限。</summary>
    public int DrawCommands { get; init; } = 100000;
    /// <summary>限额结构有效。</summary>
    public bool IsValid => SurfaceMemoryBytes > 0 && LiveParticles > 0 && TrailSamples > 0 && EmissionRecords > 0 && DrawCommands > 0;
}

/// <summary>固定包闭包、执行顺序及预算的不可变工程源数据。</summary>
public sealed class ProjectEffects : IEquatable<ProjectEffects>
{
    /// <summary>创建集合；包与实例的重复身份属于结构错误。</summary>
    public ProjectEffects(IEnumerable<EffectPackageContents>? packages = null, IEnumerable<ProjectEffectInstance>? instances = null, ProjectEffectBudgets? budgets = null)
    {
        Packages = packages is null ? [] : [.. packages]; Instances = instances is null ? [] : [.. instances]; Budgets = budgets ?? new();
        if (!Budgets.IsValid) throw new ArgumentException("效果预算必须为正数。", nameof(budgets));
        if (Packages.Any(p => p is null) || Packages.Select(p => (p.PackageId, p.Revision)).Distinct().Count() != Packages.Length)
            throw new ArgumentException("包修订身份重复或为空。", nameof(packages));
        if (Instances.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Name)
            || string.IsNullOrWhiteSpace(i.PackageId) || string.IsNullOrWhiteSpace(i.Revision) || string.IsNullOrWhiteSpace(i.ExportId)
            || i.Binding is null || i.Subscription is null || i.Parameters is null || !Enum.IsDefined(i.OverlayLayer))
            || Instances.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != Instances.Length)
            throw new ArgumentException("实例身份／必填字段无效或重复。", nameof(instances));
        var origins = Instances.Where(i => i.TemplateOrigin is not null).ToArray();
        if (origins.Any(i => i.Binding.Kind is not (ProjectEffectBindingKind.Lines or ProjectEffectBindingKind.Tokens) || i.Binding.Ids.Length != 1
            || i.TemplateOrigin!.OwnerKind == Projects.StyleTemplateKind.Token && (i.Binding.Kind != ProjectEffectBindingKind.Tokens || i.Binding.Ids[0] != i.TemplateOrigin.OwnerId)
            || i.Binding.Kind == ProjectEffectBindingKind.Lines && (i.TemplateOrigin!.OwnerKind != Projects.StyleTemplateKind.Line || i.Binding.Ids[0] != i.TemplateOrigin.OwnerId))
            || origins.Select(i => (i.Binding.Kind, i.Binding.Ids[0], i.TemplateOrigin!.TemplateId, i.TemplateOrigin.RecipeId)).Distinct().Count() != origins.Length)
            throw new ArgumentException("模板应用来源与单目标绑定不符或槽位重复。", nameof(instances));
    }
    /// <summary>没有注册包或实例的工程。</summary>
    public static ProjectEffects Empty { get; } = new();
    /// <summary>工程持有的完整字节闭包。</summary>
    public ImmutableArray<EffectPackageContents> Packages { get; }
    /// <summary>数组顺序是唯一执行顺序。</summary>
    public ImmutableArray<ProjectEffectInstance> Instances { get; }
    /// <summary>固定执行预算。</summary>
    public ProjectEffectBudgets Budgets { get; }
    /// <summary>固定效果源数据指纹；存在启用实例时不能绕过准备帧。</summary>
    public string? InputsFingerprint => Instances.Any(i => i.Enabled)
        ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(ProjectEffectsJson.Encode(this))) : null;
    /// <summary>取得明确注册的修订；不存在则失败，不能回退到另一修订。</summary>
    public EffectPackageContents GetPackage(string id, string revision) => Packages.FirstOrDefault(p => p.PackageId == id && p.Revision == revision)
        ?? throw new EffectPackageException("EffectPackageReferenceMissing", "effects.packages", "实例引用的固定包修订未注册。");
    /// <summary>内容相等。</summary>
    public bool Equals(ProjectEffects? other) => other is not null && Budgets == other.Budgets
        && Packages.Select(p => (p.PackageId, p.Revision)).SequenceEqual(other.Packages.Select(p => (p.PackageId, p.Revision)))
        && SequenceEquality.ContentEquals(Instances, other.Instances);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ProjectEffects other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode()
    { HashCode hash = new(); hash.Add(Budgets); foreach (var p in Packages) { hash.Add(p.PackageId); hash.Add(p.Revision); } SequenceEquality.AddContentHash(ref hash, Instances); return hash.ToHashCode(); }
}
