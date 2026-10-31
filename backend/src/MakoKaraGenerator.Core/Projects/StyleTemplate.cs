using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>模板适用的相对对象类型。</summary>
public enum StyleTemplateKind
{
    /// <summary>行模板。</summary>
    Line,

    /// <summary>元素模板。</summary>
    Token,
    /// <summary>按实际属性与配方能力筛选应用范围的组合预设。</summary>
    Composite,
}

/// <summary>独立于工程的静态样式与效果配方，携带完整固定资源。</summary>
public sealed record StyleTemplate
{
    /// <summary>显示名称；不遍历材质的类型限定访问器或完整包闭包。</summary>
    public override string ToString() => Name;

    /// <summary>稳定身份；名称可以重复。</summary>
    public required string Id { get; init; }

    /// <summary>显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>行、元素或通用组合预设类型。</summary>
    public required StyleTemplateKind Kind { get; init; }

    /// <summary>完整样式值；只套用 <see cref="Properties"/> 中的属性。</summary>
    public required LyricStyle Style { get; init; }

    /// <summary>模板实际写入的属性。</summary>
    public required ImmutableArray<LyricStyleProperty> Properties { get; init; }

    /// <summary>有序效果配方，没有源目标或运行身份。</summary>
    public ImmutableArray<TemplateEffectRecipe> EffectRecipes { get; init; } = [];

    /// <summary>配方所需完整不可变包修订。</summary>
    public ImmutableArray<EffectPackageContents> Packages { get; init; } = [];

    /// <summary>建立一份经过类型与属性校验的模板。</summary>
    public static StyleTemplate Create(
        string id, string name, StyleTemplateKind kind,
        LyricStyle style, IEnumerable<LyricStyleProperty> properties,
        IEnumerable<TemplateEffectRecipe>? effectRecipes = null, IEnumerable<EffectPackageContents>? packages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(properties);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        StyleBatchEditor.ValidateFinite(style);

        ImmutableArray<LyricStyleProperty> selected = [.. properties.Distinct()];
        ImmutableArray<TemplateEffectRecipe> recipes = effectRecipes is null ? [] : [.. effectRecipes];
        ImmutableArray<EffectPackageContents> resources = packages is null ? [] : [.. packages];
        if (selected.IsEmpty && recipes.IsEmpty) throw new ArgumentException("模板至少包含一项属性或效果配方。", nameof(properties));
        StyleTemplateEffects.Validate(kind, style, recipes, resources);
        foreach (LyricStyleProperty property in selected)
        {
            if (!Enum.IsDefined(property)
                || (kind == StyleTemplateKind.Token && !TokenStyleSupport.IsSupported(property))
                || (kind == StyleTemplateKind.Composite && property is LyricStyleProperty.VerticalCenterRatio or LyricStyleProperty.HorizontalAlignment))
            {
                throw new ArgumentException($"{kind} 模板不支持属性 {property}。", nameof(properties));
            }
        }

        return new StyleTemplate
        {
            Id = id,
            Name = name.Trim(),
            Kind = kind,
            Style = style,
            Properties = selected,
            EffectRecipes = recipes,
            Packages = resources,
        };
    }

    /// <summary>固定内容相等；读取、验证或分享不会因数组和包对象引用不同制造变化。</summary>
    public bool Equals(StyleTemplate? other) => other is not null && Id == other.Id && Name == other.Name && Kind == other.Kind
        && Style == other.Style && Properties.SequenceEqual(other.Properties) && EffectRecipes.SequenceEqual(other.EffectRecipes)
        && Packages.Select(p => (p.PackageId, p.Revision)).ToHashSet().SetEquals(other.Packages.Select(p => (p.PackageId, p.Revision)));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Id); hash.Add(Name); hash.Add(Kind); hash.Add(Style);
        foreach (var property in Properties) hash.Add(property);
        foreach (var recipe in EffectRecipes) hash.Add(recipe);
        foreach (var package in Packages.OrderBy(p => p.PackageId, StringComparer.Ordinal).ThenBy(p => p.Revision, StringComparer.Ordinal))
        { hash.Add(package.PackageId); hash.Add(package.Revision); }
        return hash.ToHashCode();
    }
}
