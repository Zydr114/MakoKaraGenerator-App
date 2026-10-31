using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>画布 overlay 的明确合成层级。</summary>
public enum OverlayLayer
{
    /// <summary>背景之后、全部文字之前。</summary>
    BehindText,
    /// <summary>全部文字、注音和提示之后。</summary>
    AboveText,
}

/// <summary>固定帧输出预算，由工程效果预算持久化。</summary>
public sealed record VisualOutputBudget
{
    /// <summary>创建明确的绘制项限制，不静默减少输出。</summary>
    public VisualOutputBudget(int drawItemLimit = 100_000)
    { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(drawItemLimit); DrawItemLimit = drawItemLimit; }
    /// <summary>全部实例合计的每帧绘制项上限。</summary>
    public int DrawItemLimit { get; }
    /// <summary>已接受合同 B 的初始预算。</summary>
    public static VisualOutputBudget Default { get; } = new();
}

/// <summary>最终画布逻辑空间的圆点，不修改文字或源布局。</summary>
public readonly record struct Dot
{
    /// <summary>创建正有限半径和直乘颜色的圆点。</summary>
    public Dot(Point2 position, double radius, ColorRgba color)
    {
        Position = position; Radius = radius; Color = color;
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(radius), "圆点半径和外边界须有限且半径大于零。");
    }
    /// <summary>最终画布逻辑位置，不再次乘文字矩阵。</summary>
    public Point2 Position { get; }
    /// <summary>逻辑像素半径。</summary>
    public double Radius { get; }
    /// <summary>sRGB 直乘颜色，后端只预乘一次。</summary>
    public ColorRgba Color { get; }
    /// <summary>default 圆点无效，外边界溢出也无效。</summary>
    public bool IsValid => double.IsFinite(Radius) && Radius > 0
        && double.IsFinite(Position.X - Radius) && double.IsFinite(Position.X + Radius)
        && double.IsFinite(Position.Y - Radius) && double.IsFinite(Position.Y + Radius);
}

/// <summary>稳定实例及命名批次的不可变圆点输出。</summary>
/// <param name="InstanceId">宿主实例身份。</param><param name="Id">本实例内唯一批次 ID。</param>
/// <param name="Layer">合成层级。</param><param name="Dots">批次内有序圆点。</param>
public sealed record DotBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<Dot> Dots)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Dots.IsDefault && Dots.All(static dot => dot.IsValid);
    /// <inheritdoc/>
    public override long ItemCount => Dots.Length;
}

/// <summary>仅 EvaluateVisuals 有效的输出，完成或失败后不能继续写入。</summary>
public sealed class VisualWriter
{
    private readonly EffectInstanceId _instance;
    private readonly EffectCapabilities _capabilities;
    private readonly VisualOutputBudget _budget;
    private readonly Dictionary<OutputId, OverlayBatch> _batches = [];
    private int _count;
    private bool _closed;
    /// <summary>宿主固定身份、声明能力和预算；作者自己创建的 writer 不会被采纳。</summary>
    public VisualWriter(EffectInstanceId instance, EffectCapabilities capabilities, VisualOutputBudget? budget = null)
    {
        if (!instance.IsValid) throw new ArgumentException("实例身份无效。", nameof(instance));
        if ((capabilities & ~(EffectCapabilities.Transforms | EffectCapabilities.Style | EffectCapabilities.Overlay | EffectCapabilities.ImageEffects)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        _instance = instance; _capabilities = capabilities; _budget = budget ?? VisualOutputBudget.Default; Overlay = new(this);
    }
    /// <summary>世界逻辑空间的临时图元；不含文字 writer。</summary>
    public OverlayWriter Overlay { get; }
    /// <summary>结束并按 ordinal 批次 ID 固定圆点输出；只结束一次。</summary>
    public ImmutableArray<DotBatch> Complete()
    {
        var output = CompleteOverlay();
        if (output.Any(static batch => batch is not DotBatch))
            throw new EffectOutputException("UnsupportedLegacyOverlayOutput", "非圆点输出必须由宿主 CompleteOverlay 收取，不可丢弃。");
        return [.. output.Cast<DotBatch>()];
    }
    /// <summary>结束并按 ordinal ID 收取全部类型；只结束一次。</summary>
    public ImmutableArray<OverlayBatch> CompleteOverlay()
    {
        RequireOpen(); _closed = true;
        return [.. _batches.Values.OrderBy(static batch => batch.Id.Value, StringComparer.Ordinal)];
    }
    /// <summary>离开或失败阶段时结束 writer；不发布失败输出。</summary>
    public void Invalidate() => _closed = true;
    private void RequireOpen()
    { if (_closed) throw new EffectOutputException("InvalidVisualOutputStage", "writer 已离开 EvaluateVisuals 阶段。"); }
    private void Draw(OverlayBatch batch)
    {
        RequireOpen();
        if (!_capabilities.HasFlag(EffectCapabilities.Overlay)) throw new EffectOutputException("UndeclaredCapability", "效果未声明 Overlay 能力。");
        if (!batch.Id.IsValid || !Enum.IsDefined(batch.Layer) || !batch.IsValid)
            throw new EffectOutputException("InvalidOverlay", "图元批次须有真实 ID、明确层级和有效不可变描述。");
        if (_batches.ContainsKey(batch.Id)) throw new EffectOutputException("DuplicateOverlayBatch", "本帧实例已写入同名批次：" + batch.Id.Value);
        long requested = _count + batch.ItemCount;
        if (requested > _budget.DrawItemLimit)
            throw new EffectOutputException("VisualOutputBudgetExceeded", $"图元绘制项请求 {requested}，限制 {_budget.DrawItemLimit}。");
        _count = (int)requested; _batches.Add(batch.Id, batch);
    }
    /// <summary>临时图元描述，只在所属视觉求值阶段有效。</summary>
    public sealed class OverlayWriter
    {
        private readonly VisualWriter _owner;
        internal OverlayWriter(VisualWriter owner) { _owner = owner; }
        /// <summary>绘制稳定命名、有序圆点批次；同帧重复 ID 报错，空批次合法。</summary>
        public void DrawDots(OutputId batchId, ImmutableArray<Dot> dots, OverlayLayer layer) => _owner.Draw(new DotBatch(_owner._instance, batchId, layer, dots));
        /// <summary>有序平端线段，共用批次名字及预算。</summary>
        public void DrawSegments(OutputId batchId, ImmutableArray<Segment> segments, OverlayLayer layer)
            => _owner.Draw(new SegmentBatch(_owner._instance, batchId, layer, segments));
        /// <summary>有序带状拖尾；每点计入预算。</summary>
        public void DrawTrails(OutputId batchId, ImmutableArray<Trail> trails, OverlayLayer layer)
            => _owner.Draw(new TrailBatch(_owner._instance, batchId, layer, trails));
        /// <summary>固定资源图片精灵，共用批次名字及预算；宿主校验当前包修订归属。</summary>
        public void DrawSprites(OutputId batchId, ImmutableArray<Sprite> sprites, OverlayLayer layer)
            => _owner.Draw(new SpriteBatch(_owner._instance, batchId, layer, sprites));
    }
}
