using System.Buffers;
using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>固定包修订中的逻辑资源身份；不是文件路径或 native 对象。</summary>
public sealed record ResourceIdentity
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");
    /// <summary>指定包、完整 sha256 修订和导出／资源身份。</summary>
    public ResourceIdentity(string packageId, string revision, string exportId)
    {
        ValidateRevision(packageId, revision); ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        PackageId = packageId; Revision = revision; ExportId = exportId;
    }
    internal static void ValidateRevision(string packageId, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (revision is null || revision.Length != 71 || !revision.StartsWith("sha256:", StringComparison.Ordinal)
            || revision.AsSpan(7).ContainsAnyExcept(HexDigits))
            throw new ArgumentException("资源修订必须为完整小写 sha256。", nameof(revision));
    }
    /// <summary>固定包身份。</summary>
    public string PackageId { get; }
    /// <summary>完整内容修订。</summary>
    public string Revision { get; }
    /// <summary>包内逻辑身份。</summary>
    public string ExportId { get; }
}

/// <summary>固定材质导出句柄，不包含编译器或 shader。</summary>
public sealed record MaterialHandle
{
    internal MaterialHandle(ResourceIdentity identity) { Identity = identity; }
    /// <summary>固定逻辑身份。</summary>
    public ResourceIdentity Identity { get; }
}

/// <summary>固定渲染图导出句柄，不在帧内解析图字符串。</summary>
public sealed record GraphHandle
{
    internal GraphHandle(ResourceIdentity identity) { Identity = identity; }
    /// <summary>固定逻辑身份。</summary>
    public ResourceIdentity Identity { get; }
}

/// <summary>固定图片资源句柄，图片解析及生命周期属于宿主。</summary>
public sealed record ImageHandle
{
    internal ImageHandle(ResourceIdentity identity) { Identity = identity; }
    /// <summary>固定逻辑身份。</summary>
    public ResourceIdentity Identity { get; }
}

/// <summary>固定包可用资源的只读目录；按类型查找，缺资源不回退到其他类型。</summary>
public sealed class ResourceCatalog
{
    private readonly ImmutableDictionary<string, MaterialHandle> _materials;
    private readonly ImmutableDictionary<string, GraphHandle> _graphs;
    private readonly ImmutableDictionary<string, ImageHandle> _images;
    private readonly ImmutableDictionary<string, VectorHandle> _vectors;
    private readonly ImmutableDictionary<string, ImmutableDictionary<string, SpriteHandle>> _sprites;
    private ResourceCatalog()
    {
        _materials = ImmutableDictionary<string, MaterialHandle>.Empty;
        _graphs = ImmutableDictionary<string, GraphHandle>.Empty;
        _images = ImmutableDictionary<string, ImageHandle>.Empty;
        _vectors = ImmutableDictionary<string, VectorHandle>.Empty;
        _sprites = ImmutableDictionary<string, ImmutableDictionary<string, SpriteHandle>>.Empty;
    }
    /// <summary>由宿主建立一个已经验证的固定包目录；重复身份拒绝，不触及磁盘。</summary>
    public ResourceCatalog(string packageId, string revision, ImmutableArray<string> materials,
        ImmutableArray<string> graphs, ImmutableArray<string> images)
        : this(packageId, revision, materials, graphs, images, [], ImmutableDictionary<string, ImmutableArray<string>>.Empty)
    { }
    /// <summary>建立含强类型 SVG 资源的固定目录；资源与档位身份均独立验证，不触及磁盘。</summary>
    public ResourceCatalog(string packageId, string revision, ImmutableArray<string> materials,
        ImmutableArray<string> graphs, ImmutableArray<string> images, ImmutableArray<string> vectors,
        ImmutableDictionary<string, ImmutableArray<string>> sprites)
    {
        ValidateIds(materials); ValidateIds(graphs); ValidateIds(images); ValidateIds(vectors);
        ArgumentNullException.ThrowIfNull(sprites);
        ValidateIds([.. sprites.Keys]);
        foreach (var variants in sprites.Values)
        {
            ValidateIds(variants);
            if (variants.IsEmpty) throw new ArgumentException("Atlas 资源须明确声明至少一个档位。", nameof(sprites));
        }
        if (vectors.Any(id => sprites.Keys.Contains(id, StringComparer.Ordinal)))
            throw new ArgumentException("同一 SVG 逻辑身份不能同时声明为 vector 和 atlas。", nameof(sprites));
        ResourceIdentity.ValidateRevision(packageId, revision);
        _materials = materials.ToImmutableDictionary(static id => id, id => new MaterialHandle(new(packageId, revision, id)), StringComparer.Ordinal);
        _graphs = graphs.ToImmutableDictionary(static id => id, id => new GraphHandle(new(packageId, revision, id)), StringComparer.Ordinal);
        _images = images.ToImmutableDictionary(static id => id, id => new ImageHandle(new(packageId, revision, id)), StringComparer.Ordinal);
        _vectors = vectors.ToImmutableDictionary(static id => id, id => new VectorHandle(new(packageId, revision, id)), StringComparer.Ordinal);
        _sprites = sprites.ToImmutableDictionary(static pair => pair.Key,
            pair => pair.Value.ToImmutableDictionary(static variant => variant,
                variant => new SpriteHandle(new(packageId, revision, pair.Key), variant), StringComparer.Ordinal), StringComparer.Ordinal);
        PackageId = packageId; Revision = revision;
    }
    /// <summary>未加载包资源的真实空目录；不制造默认图片。</summary>
    public static ResourceCatalog Empty { get; } = new();
    /// <summary>包身份；尚无包的空目录为 null。</summary>
    public string? PackageId { get; }
    /// <summary>固定修订；尚无包的空目录为 null。</summary>
    public string? Revision { get; }
    /// <summary>读取材质导出；缺失报告身份。</summary>
    public MaterialHandle GetMaterial(string exportId) => Require(_materials, exportId, "material");
    /// <summary>读取图导出；缺失报告身份。</summary>
    public GraphHandle GetGraph(string exportId) => Require(_graphs, exportId, "graph");
    /// <summary>读取图片；缺失报告身份。</summary>
    public ImageHandle GetImage(string assetId) => Require(_images, assetId, "image");
    /// <summary>读取 SVG 矢量资源；缺失不回退到图片或 atlas。</summary>
    public VectorHandle GetVector(string resourceId) => Require(_vectors, resourceId, "svg.vector");
    /// <summary>读取 SVG atlas 的精确档位；不拼接资源和档位身份。</summary>
    public SpriteHandle GetSprite(string resourceId, string variantId)
        => Require(Require(_sprites, resourceId, "svg.atlas"), variantId, "svg.atlas.variant");
    /// <summary>检查材质是否属于本固定包的真实声明，不能用同名另修订替代。</summary>
    public bool Contains(MaterialHandle handle) => handle is not null && _materials.TryGetValue(handle.Identity.ExportId, out var expected) && expected == handle;
    /// <summary>检查图是否属于本固定包的真实声明。</summary>
    public bool Contains(GraphHandle handle) => handle is not null && _graphs.TryGetValue(handle.Identity.ExportId, out var expected) && expected == handle;
    /// <summary>检查图片是否属于本固定包的真实声明。</summary>
    public bool Contains(ImageHandle handle) => handle is not null && _images.TryGetValue(handle.Identity.ExportId, out var expected) && expected == handle;
    /// <summary>检查 SVG 矢量是否属于本目录的完整包身份、修订和真实资源声明。</summary>
    public bool Contains(VectorHandle handle) => handle is not null && _vectors.TryGetValue(handle.Identity.ExportId, out var expected) && expected == handle;
    /// <summary>检查 SVG atlas 的完整包身份、修订、资源和档位是否属于本目录。</summary>
    public bool Contains(SpriteHandle handle) => handle is not null
        && _sprites.TryGetValue(handle.Identity.ExportId, out var variants)
        && variants.TryGetValue(handle.VariantId, out var expected) && expected == handle;
    private static T Require<T>(ImmutableDictionary<string, T> values, string id, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return values.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException("MissingResource: " + kind + ":" + id);
    }
    private static void ValidateIds(ImmutableArray<string> ids)
    {
        if (ids.IsDefault || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("资源集合须明确、非空身份且不可重复。", nameof(ids));
    }
}
