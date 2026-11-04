using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>晚期具名图元的临时输出；宿主独占创建、完成和失效生命周期。</summary>
public sealed class VisualOutput
{
    private readonly EffectInstanceId _owner;
    private readonly ImmutableHashSet<string> _capabilities;
    private readonly ImmutableDictionary<OutputId, VisualOutputDeclaration> _declarations;
    private readonly ResourceCatalog _resources;
    private readonly VisualOutputBudget _budget;
    private readonly CancellationToken _token;
    private readonly Action<long>? _reserveBytes;
    private readonly Dictionary<OutputId, OverlayBatch> _batches = [];
    private long _count;
    private long _bytes;
    private ExceptionDispatchInfo? _budgetFailure;
    private bool _closed;

    internal VisualOutput(EffectInstanceId owner, ImmutableArray<string> capabilities,
        ImmutableArray<VisualOutputDeclaration> declarations, ResourceCatalog resources,
        VisualOutputBudget? budget = null, CancellationToken token = default)
        : this(owner, capabilities, declarations, resources, budget, null, token) { }

    internal VisualOutput(EffectInstanceId owner, ImmutableArray<string> capabilities,
        ImmutableArray<VisualOutputDeclaration> declarations, ResourceCatalog resources,
        VisualOutputBudget? budget, Action<long>? reserveBytes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!owner.IsValid || capabilities.IsDefault || declarations.IsDefault || capabilities.Any(string.IsNullOrWhiteSpace)
            || declarations.Any(static item => !PreparedEffectDeclaration.IsValidVisualDeclaration(item))
            || declarations.Select(static item => item.Id).Distinct().Count() != declarations.Length)
            throw new EffectOutputException("InvalidVisualDeclaration", "晚期输出需要真实实例及唯一、封闭的具名声明。");
        _owner = owner; _capabilities = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        _declarations = declarations.ToImmutableDictionary(static item => item.Id); _resources = resources;
        _budget = budget ?? VisualOutputBudget.Default; _token = token; _reserveBytes = reserveBytes;
    }
    /// <summary>写最终画布逻辑空间的有序圆点。</summary>
    public void DrawDots(OutputId id, ImmutableArray<Dot> items, OverlayLayer layer)
        => Draw(new DotBatch(_owner, id, layer, items), VisualPrimitiveKind.Dots, "visuals.dots");
    /// <summary>写最终画布逻辑空间的有序平端线段。</summary>
    public void DrawSegments(OutputId id, ImmutableArray<Segment> items, OverlayLayer layer)
        => Draw(new SegmentBatch(_owner, id, layer, items), VisualPrimitiveKind.Segments, "visuals.segments");
    /// <summary>写有序带状拖尾，每个节点计入图元预算。</summary>
    public void DrawTrails(OutputId id, ImmutableArray<Trail> items, OverlayLayer layer)
        => Draw(new TrailBatch(_owner, id, layer, items), VisualPrimitiveKind.Trails, "visuals.trails");
    /// <summary>写完整仿射精灵，资源须属于本实例固定包修订。</summary>
    public void DrawSprites(OutputId id, ImmutableArray<Sprite> items, OverlayLayer layer)
        => Draw(new SpriteBatch(_owner, id, layer, items), VisualPrimitiveKind.Sprites, "visuals.sprites");
    /// <summary>写固定 SVG 矢量实例；同名声明需同时声明 Paths 和 SVG vector 资源能力。</summary>
    public void DrawPaths(OutputId id, ImmutableArray<VectorVisual> items, OverlayLayer layer)
        => Draw(new PathBatch(_owner, id, layer, items), VisualPrimitiveKind.Paths, "visuals.paths", VisualResourceKind.SvgVector);
    /// <summary>写固定 SVG atlas 档位实例；保留完整仿射，共用 Sprites 的名字和绘制项预算。</summary>
    public void DrawAtlasSprites(OutputId id, ImmutableArray<AtlasSprite> items, OverlayLayer layer)
        => Draw(new AtlasSpriteBatch(_owner, id, layer, items), VisualPrimitiveKind.Sprites, "visuals.sprites", VisualResourceKind.SvgAtlas);

    internal ImmutableArray<OverlayBatch> Complete()
    {
        RequireOpen(); _closed = true;
        return [.. _batches.Values.OrderBy(static item => item.Id.Value, StringComparer.Ordinal)];
    }
    internal void Invalidate() { _closed = true; _batches.Clear(); }
    private void RequireOpen()
    {
        if (_closed) throw new EffectOutputException("InvalidVisualOutputStage", "晚期输出已经离开本次调用。");
        _budgetFailure?.Throw();
        _token.ThrowIfCancellationRequested();
    }
    private void Draw(OverlayBatch batch, VisualPrimitiveKind primitive, string capability, VisualResourceKind resource = VisualResourceKind.None)
    {
        RequireOpen();
        if (!_capabilities.Contains(capability)) throw new EffectOutputException("UndeclaredCapability", "包未声明能力：" + capability);
        if (PreparedEffectDeclaration.ResourceCapability(resource) is { } resourceCapability && !_capabilities.Contains(resourceCapability))
            throw new EffectOutputException("UndeclaredCapability", "包未声明能力：" + resourceCapability);
        if (!_declarations.TryGetValue(batch.Id, out var declaration) || declaration.Primitive != primitive
            || declaration.Resource != resource || declaration.Layer != batch.Layer)
            throw new EffectOutputException("UndeclaredPackageOutput", "本次晚期输出的名字、类型、资源类型或层未在 Declare 中声明。");
        if (!batch.Id.IsValid || !Enum.IsDefined(batch.Layer) || !batch.IsValid)
            throw new EffectOutputException("InvalidOverlay", "图元批次必须是有效的封闭描述。");
        if (_batches.ContainsKey(batch.Id)) throw new EffectOutputException("DuplicateOverlayBatch", "本实例已经写入同名图元批次。");
        if (batch is SpriteBatch sprites && sprites.Sprites.Any(sprite => !_resources.Contains(sprite.Image)))
            throw new EffectOutputException("InvalidSpriteResource", "精灵句柄不属于本实例固定包修订。");
        if (batch is PathBatch paths && paths.Paths.Any(path => !_resources.Contains(path.Vector)))
            throw new EffectOutputException("InvalidVectorResource", "矢量句柄不属于本实例固定包修订。");
        if (batch is AtlasSpriteBatch atlas && atlas.Sprites.Any(sprite => !_resources.Contains(sprite.Sprite)))
            throw new EffectOutputException("InvalidSpriteResource", "Atlas 句柄或档位不属于本实例固定包修订。");
        long requested, bytes;
        try
        {
            requested = checked(_count + batch.ItemCount);
            if (requested > _budget.DrawItemLimit) throw new EffectOutputException("VisualOutputBudgetExceeded", "本实例图元超过工程输出预算。");
            try { bytes = OverlayMemoryCost.Estimate(batch); }
            catch (OverflowException) { throw MemoryExceeded(); }
            if (bytes > ParticleLimits.MaximumFrameBytes - _bytes) throw MemoryExceeded();
            _reserveBytes?.Invoke(bytes);
        }
        catch (Exception error)
        {
            // 作者可能捕获 Draw 的异常；宿主 Complete 仍必须使整个请求失败，
            // 并保留共享预算已经确定的实际 owner、错误码与原始异常。
            _budgetFailure = ExceptionDispatchInfo.Capture(error);
            _batches.Clear();
            throw;
        }
        _bytes += bytes; _count = requested; _batches.Add(batch.Id, batch);
    }
    private static EffectOutputException MemoryExceeded()
        => new("VisualOutputMemoryExceeded", "本实例全部图元输出超过固定帧字节预算。");
}
