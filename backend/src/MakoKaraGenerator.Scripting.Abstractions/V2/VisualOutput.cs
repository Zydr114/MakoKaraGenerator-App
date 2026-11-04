using System.Collections.Immutable;

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
    private readonly Dictionary<OutputId, OverlayBatch> _batches = [];
    private long _count;
    private bool _closed;

    internal VisualOutput(EffectInstanceId owner, ImmutableArray<string> capabilities,
        ImmutableArray<VisualOutputDeclaration> declarations, ResourceCatalog resources,
        VisualOutputBudget? budget = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!owner.IsValid || capabilities.IsDefault || declarations.IsDefault || capabilities.Any(string.IsNullOrWhiteSpace)
            || declarations.Any(static item => item is null || !item.Id.IsValid || !Enum.IsDefined(item.Primitive) || !Enum.IsDefined(item.Layer))
            || declarations.Select(static item => item.Id).Distinct().Count() != declarations.Length)
            throw new EffectOutputException("InvalidVisualDeclaration", "晚期输出需要真实实例及唯一、封闭的具名声明。");
        _owner = owner; _capabilities = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        _declarations = declarations.ToImmutableDictionary(static item => item.Id); _resources = resources;
        _budget = budget ?? VisualOutputBudget.Default; _token = token;
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

    internal ImmutableArray<OverlayBatch> Complete()
    {
        RequireOpen(); _closed = true;
        return [.. _batches.Values.OrderBy(static item => item.Id.Value, StringComparer.Ordinal)];
    }
    internal void Invalidate() { _closed = true; _batches.Clear(); }
    private void RequireOpen()
    {
        if (_closed) throw new EffectOutputException("InvalidVisualOutputStage", "晚期输出已经离开本次调用。");
        _token.ThrowIfCancellationRequested();
    }
    private void Draw(OverlayBatch batch, VisualPrimitiveKind primitive, string capability)
    {
        RequireOpen();
        if (!_capabilities.Contains(capability)) throw new EffectOutputException("UndeclaredCapability", "包未声明能力：" + capability);
        if (!_declarations.TryGetValue(batch.Id, out var declaration) || declaration.Primitive != primitive || declaration.Layer != batch.Layer)
            throw new EffectOutputException("UndeclaredPackageOutput", "本次晚期输出的名字、类型或层未在 Declare 中声明。");
        if (!batch.Id.IsValid || !Enum.IsDefined(batch.Layer) || !batch.IsValid)
            throw new EffectOutputException("InvalidOverlay", "图元批次必须是有效的封闭描述。");
        if (_batches.ContainsKey(batch.Id)) throw new EffectOutputException("DuplicateOverlayBatch", "本实例已经写入同名图元批次。");
        if (batch is SpriteBatch sprites && sprites.Sprites.Any(sprite => !_resources.Contains(sprite.Image)))
            throw new EffectOutputException("InvalidSpriteResource", "精灵句柄不属于本实例固定包修订。");
        long requested = checked(_count + batch.ItemCount);
        if (requested > _budget.DrawItemLimit) throw new EffectOutputException("VisualOutputBudgetExceeded", "本实例图元超过工程输出预算。");
        _count = requested; _batches.Add(batch.Id, batch);
    }
}
