using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>一个配方在具体相对成员上的固定运行身份；不保存运行历史。</summary>
public sealed record PresetEffectIdentity
{
    /// <summary>创建明确身份、绑定和种子。</summary>
    public PresetEffectIdentity(string recipeId, ProjectEffectBinding binding, string instanceId, ulong seed, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId); ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        RecipeId = recipeId; Binding = binding; InstanceId = instanceId; Seed = seed; Enabled = enabled;
    }
    /// <summary>快照中的稳定配方身份。</summary>
    public string RecipeId { get; }
    /// <summary>真实源／场景绑定。</summary>
    public ProjectEffectBinding Binding { get; }
    /// <summary>效果实例身份。</summary>
    public string InstanceId { get; }
    /// <summary>应用建立的种子；更新同配方时保留。</summary>
    public ulong Seed { get; }
    /// <summary>独立于整个预设启停的实例状态。</summary>
    public bool Enabled { get; init; }
}

/// <summary>某个具体对象唯一的直接组合预设；快照与闭包不随库更新。</summary>
public sealed record PresetApplication
{
    /// <summary>建立严格结构；对象存在性由事务与诊断检查，不阻止保存失效引用。</summary>
    public PresetApplication(string id, ProjectEffectBinding target, StyleTemplate snapshot,
        IEnumerable<PresetEffectIdentity>? identities = null, IReadOnlyDictionary<string, string>? branchInputs = null, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(snapshot);
        _ = PresetApplications.ObjectKey(target);
        _ = StyleTemplate.Create(snapshot.Id, snapshot.Name, snapshot.Kind, snapshot.Style, snapshot.Properties, snapshot.EffectRecipes, snapshot.Packages);
        if (snapshot.Kind == StyleTemplateKind.Token && target.Kind != ProjectEffectBindingKind.Tokens
            || target.Kind == ProjectEffectBindingKind.Tokens && (snapshot.Properties.Any(property => !TokenStyleSupport.IsSupported(property))
                || snapshot.EffectRecipes.Any(recipe => recipe.Target == TemplateEffectTarget.Line)))
            throw new ArgumentException("快照的属性或相对配方不支持该具体对象。", nameof(snapshot));
        var inputs = branchInputs is null ? ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal)
            : branchInputs.ToImmutableSortedDictionary(StringComparer.Ordinal);
        if (inputs.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            || !snapshot.EffectRecipes.Where(r => r.ReadScope == ScriptReadScope.BranchInput).Select(r => r.BranchInput!).ToHashSet(StringComparer.Ordinal).SetEquals(inputs.Keys))
            throw new ArgumentException("应用必须恰好映射快照声明的具名分支输入。", nameof(branchInputs));
        ImmutableArray<PresetEffectIdentity> members = identities is null ? [] : [.. identities];
        if (members.Any(member => member is null) || members.Select(member => member.InstanceId).Distinct(StringComparer.Ordinal).Count() != members.Length
            || members.Select(member => (member.RecipeId, member.Binding)).Distinct().Count() != members.Length)
            throw new ArgumentException("配方实例身份或成员槽位重复。", nameof(identities));
        foreach (var member in members)
        {
            var recipe = snapshot.EffectRecipes.FirstOrDefault(r => r.RecipeId == member.RecipeId) ?? throw new ArgumentException("实例身份缺少对应配方。", nameof(identities));
            bool scope = target.Kind is ProjectEffectBindingKind.Project or ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node;
            if ((recipe.Target == TemplateEffectTarget.Application || scope) && !member.Binding.Equals(target)
                || recipe.Target != TemplateEffectTarget.Application && !scope && member.Binding.Ids.Length != 1)
                throw new ArgumentException("配方绑定必须符合应用范围或单个相对成员。", nameof(identities));
            _ = recipe.ToConfiguration(snapshot.Packages.Single(p => p.PackageId == recipe.PackageId && p.Revision == recipe.Revision))
                .CreateInstance(member.InstanceId, member.Binding, member.Seed, inputs);
        }
        if (snapshot.EffectRecipes.Any(recipe => !members.Any(m => m.RecipeId == recipe.RecipeId)
            || (recipe.Target == TemplateEffectTarget.Application || target.Kind is ProjectEffectBindingKind.Project or ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node)
                && members.Count(m => m.RecipeId == recipe.RecipeId) != 1))
            throw new ArgumentException("每个配方必须有实际成员；应用范围配方必须保留恰好一个实例。", nameof(identities));
        Id = id; Target = target; Snapshot = snapshot; Identities = members; BranchInputs = inputs; Enabled = enabled;
    }

    /// <summary>稳定应用身份。</summary>
    public string Id { get; }
    /// <summary>单个具体对象；集合绑定仅允许一个成员。</summary>
    public ProjectEffectBinding Target { get; }
    /// <summary>完整固定预设定义与资源。</summary>
    public StyleTemplate Snapshot { get; }
    /// <summary>每个配方／相对成员的持久身份。</summary>
    public ImmutableArray<PresetEffectIdentity> Identities { get; }
    /// <summary>相对输入名到实际歌词分支ID的映射。</summary>
    public ImmutableSortedDictionary<string, string> BranchInputs { get; }
    /// <summary>整个应用是否生效。</summary>
    public bool Enabled { get; init; }

    /// <summary>按配方顺序投影实例；不读取外部库，不重建身份或种子。</summary>
    public ImmutableArray<ProjectEffectInstance> ProjectInstances()
    {
        List<ProjectEffectInstance> values = [];
        foreach (var recipe in Snapshot.EffectRecipes)
        {
            var configuration = recipe.ToConfiguration(Snapshot.Packages.Single(p => p.PackageId == recipe.PackageId && p.Revision == recipe.Revision));
            foreach (var identity in Identities.Where(member => member.RecipeId == recipe.RecipeId))
                values.Add(configuration.CreateInstance(identity.InstanceId, identity.Binding, identity.Seed, BranchInputs)
                    with { Enabled = Enabled && identity.Enabled, Seed = identity.Seed });
        }
        return [.. values];
    }

    /// <summary>固定内容相等，重复应用不制造历史。</summary>
    public bool Equals(PresetApplication? other) => other is not null && Id == other.Id && Target.Equals(other.Target)
        && Snapshot.Equals(other.Snapshot) && Enabled == other.Enabled && Identities.SequenceEqual(other.Identities)
        && BranchInputs.SequenceEqual(other.BranchInputs);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Id); hash.Add(Target); hash.Add(Snapshot); hash.Add(Enabled);
        foreach (var identity in Identities) hash.Add(identity);
        foreach (var pair in BranchInputs) { hash.Add(pair.Key); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }
}

/// <summary>唯一应用记录集合，与工程源历史共用不可变状态。</summary>
public sealed class PresetApplications : IEquatable<PresetApplications>
{
    /// <summary>验证每对象最多一个直接应用，以及全局唯一的运行实例身份。</summary>
    public PresetApplications(IEnumerable<PresetApplication>? items = null)
    {
        Items = items is null ? [] : [.. items];
        if (Items.Any(item => item is null) || Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Items.Length
            || Items.Select(item => ObjectKey(item.Target)).Distinct().Count() != Items.Length
            || Items.SelectMany(item => item.Identities).Select(member => member.InstanceId).Distinct(StringComparer.Ordinal).Count() != Items.Sum(item => item.Identities.Length))
            throw new ArgumentException("应用身份、对象或实例身份重复。", nameof(items));
    }
    /// <summary>没有直接应用。</summary>
    public static PresetApplications Empty { get; } = new();
    /// <summary>固定应用顺序；每条内部保留配方顺序。</summary>
    public ImmutableArray<PresetApplication> Items { get; }
    /// <summary>按具体对象定位应用，节点后代开关不建立第二条应用。</summary>
    public PresetApplication? Find(ProjectEffectBinding target) => Items.FirstOrDefault(item => ObjectKey(item.Target) == ObjectKey(target));
    internal static (ProjectEffectBindingKind Kind, string Id) ObjectKey(ProjectEffectBinding target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is ProjectEffectBindingKind.Lines or ProjectEffectBindingKind.Tokens or ProjectEffectBindingKind.Clips && target.Ids.Length != 1)
            throw new ArgumentException("一个应用记录只能指向一个具体对象；多选由事务建立多条记录。", nameof(target));
        return (target.Kind, target.Id ?? (target.Ids.IsEmpty ? string.Empty : target.Ids[0]));
    }
    /// <inheritdoc/>
    public bool Equals(PresetApplications? other) => other is not null && Items.SequenceEqual(other.Items);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PresetApplications other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() { HashCode hash = new(); foreach (var item in Items) hash.Add(item); return hash.ToHashCode(); }
}
