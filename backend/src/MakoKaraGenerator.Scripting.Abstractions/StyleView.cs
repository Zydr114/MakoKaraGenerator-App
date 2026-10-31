using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>外部描边连接方式。</summary>
public enum StrokeJoin
{
    /// <summary>尖角。</summary>
    Miter,
    /// <summary>圆角。</summary>
    Round,
    /// <summary>斜切。</summary>
    Bevel,
}
/// <summary>描边位置合同。</summary>
public enum StrokePosition
{
    /// <summary>字形外部。</summary>
    Outside,
}
/// <summary>中立外部描边，几何和材质分开。</summary>
public sealed record StrokeValue
{
    /// <summary>有限非负逻辑宽度、明确连接／位置及非空材质。</summary>
    public StrokeValue(double widthPx, MaterialValue material, StrokeJoin join = StrokeJoin.Round, StrokePosition position = StrokePosition.Outside)
    {
        Point2.Finite(widthPx); ArgumentOutOfRangeException.ThrowIfNegative(widthPx); ArgumentNullException.ThrowIfNull(material);
        if (!Enum.IsDefined(join) || !Enum.IsDefined(position)) throw new ArgumentOutOfRangeException(nameof(join));
        WidthPx = widthPx; Material = material; Join = join; Position = position;
    }
    /// <summary>画布逻辑像素宽度。</summary>
    public double WidthPx { get; }
    /// <summary>轮廓区域材质。</summary>
    public MaterialValue Material { get; }
    /// <summary>连接方式。</summary>
    public StrokeJoin Join { get; }
    /// <summary>位置。</summary>
    public StrokePosition Position { get; }
}
/// <summary>中立阴影；方向角及距离保持源合同。</summary>
public sealed record ShadowValue
{
    /// <summary>有限合法的颜色、不透明度、方向角和非负距离／模糊半径。</summary>
    public ShadowValue(ColorRgba color, double opacity = 1, double angleDegrees = 45, double distancePx = 4, double blurRadiusPx = 8)
    {
        ColorRgba.Unit(opacity); Point2.Finite(angleDegrees); Point2.Finite(distancePx); Point2.Finite(blurRadiusPx);
        ArgumentOutOfRangeException.ThrowIfNegative(distancePx); ArgumentOutOfRangeException.ThrowIfNegative(blurRadiusPx);
        Color = color; Opacity = opacity; AngleDegrees = angleDegrees; DistancePx = distancePx; BlurRadiusPx = blurRadiusPx;
    }
    /// <summary>直乘颜色。</summary>
    public ColorRgba Color { get; }
    /// <summary>独立阴影不透明度 [0,1]。</summary>
    public double Opacity { get; }
    /// <summary>0 度向右，90 度向下。</summary>
    public double AngleDegrees { get; }
    /// <summary>逻辑像素距离。</summary>
    public double DistancePx { get; }
    /// <summary>逻辑像素模糊半径。</summary>
    public double BlurRadiusPx { get; }
}
/// <summary>静态解析后的只读外观；字号和源排版不属于样式 writer。</summary>
public sealed record StyleView
{
    /// <summary>固定两态材质、装饰及 [0,1] 样式透明度。</summary>
    public StyleView(MaterialValue unsungMaterial, MaterialValue sungMaterial, StrokeValue? stroke, ShadowValue? shadow, double opacity)
    {
        ArgumentNullException.ThrowIfNull(unsungMaterial); ArgumentNullException.ThrowIfNull(sungMaterial); ColorRgba.Unit(opacity);
        UnsungMaterial = unsungMaterial; SungMaterial = sungMaterial; Stroke = stroke; Shadow = shadow; Opacity = opacity;
    }
    /// <summary>未唱材质。</summary>
    public MaterialValue UnsungMaterial { get; }
    /// <summary>已唱材质。</summary>
    public MaterialValue SungMaterial { get; }
    /// <summary>静态描边，空为关闭。</summary>
    public StrokeValue? Stroke { get; }
    /// <summary>静态阴影，空为关闭。</summary>
    public ShadowValue? Shadow { get; }
    /// <summary>静态样式不透明度；不包含入场或脚本 alpha。</summary>
    public double Opacity { get; }
    /// <summary>明确要求已唱纯色；渐变／包材质报告错误，不采样猜基色。</summary>
    public ColorRgba RequireSungSolidColor() => SungMaterial is MaterialValue.SolidMaterial solid ? solid.Color
        : throw new EffectOutputException("BaselineMaterialNotSolid", "该作者效果要求已唱纯色材质。请替换为纯色或使用材质参数效果。");
}
/// <summary>固定完整源元素／行、组和标记基线；各实例读同一份，不读前一个实例的输出。</summary>
public sealed class BaselineStyles
{
    private readonly ImmutableDictionary<TokenId, StyleView> _tokens;
    private readonly ImmutableDictionary<LineId, StyleView> _lines;
    private readonly ImmutableDictionary<NodeId, StyleView> _marks;
    private readonly ImmutableDictionary<NodeId, GroupStyleView> _groups;
    /// <summary>复制真实源元素基线，拒绝空值和重复身份。</summary>
    public BaselineStyles(IEnumerable<KeyValuePair<TokenId, StyleView>> tokens)
        : this(tokens, [], [], []) { }
    /// <summary>固定完整源元素／行、文字标记和组；不包含运行装载／入场透明度。</summary>
    public BaselineStyles(IEnumerable<KeyValuePair<TokenId, StyleView>> tokens, IEnumerable<KeyValuePair<LineId, StyleView>> lines,
        IEnumerable<KeyValuePair<NodeId, StyleView>> marks, IEnumerable<KeyValuePair<NodeId, GroupStyleView>> groups)
    {
        ArgumentNullException.ThrowIfNull(tokens); _tokens = tokens.ToImmutableDictionary();
        ArgumentNullException.ThrowIfNull(lines); _lines = lines.ToImmutableDictionary();
        ArgumentNullException.ThrowIfNull(marks); _marks = marks.ToImmutableDictionary();
        ArgumentNullException.ThrowIfNull(groups); _groups = groups.ToImmutableDictionary();
        if (_tokens.Any(static pair => !pair.Key.IsValid || pair.Value is null)) throw new ArgumentException("基线需要真实元素和非空外观。", nameof(tokens));
        if (_lines.Any(static pair => !pair.Key.IsValid || pair.Value is null)) throw new ArgumentException("行基线身份或外观无效。", nameof(lines));
        if (_marks.Any(static pair => !pair.Key.IsValid || pair.Value is null)) throw new ArgumentException("标记基线身份或外观无效。", nameof(marks));
        if (_groups.Any(static pair => !pair.Key.IsValid || pair.Value is null) || _groups.Keys.Any(_marks.ContainsKey))
            throw new ArgumentException("组基线身份或角色无效。", nameof(groups));
    }
    /// <summary>无源元素的明确空基线。</summary>
    public static BaselineStyles Empty { get; } = new([]);
    /// <summary>真实源元素基线，装载不改变静态继承结果。</summary>
    public StyleView Get(TokenId id) => _tokens.TryGetValue(id, out var view) ? view : throw new EffectOutputException("MissingBaselineTarget", "源外观不存在：" + id.Value);
    /// <summary>读取基础字及注音共同使用的基础字外观，不以联合墨迹决定颜色。</summary>
    public StyleView Get(TokenFamilyTarget target)
    { ArgumentNullException.ThrowIfNull(target); if (!target.BindingId.IsValid) throw new ArgumentException("装载身份无效。", nameof(target)); return Get(target.TokenId); }
    /// <summary>完整源行的工程／分支／行基线，不把元素覆盖混成一个颜色。</summary>
    public StyleView Get(LineId id) => _lines.TryGetValue(id, out var view) ? view
        : throw new EffectOutputException("MissingBaselineTarget", "源行外观不存在：" + id.Value);
    /// <summary>真实源行装载的静态基线。</summary>
    public StyleView Get(LineTarget target)
    { ArgumentNullException.ThrowIfNull(target); if (!target.BindingId.IsValid) throw new ArgumentException("行装载身份无效。", nameof(target)); return Get(target.LineId); }
    /// <summary>文字标记的完整静态基线；两态对应 Pending／Elapsed。</summary>
    public StyleView Get(TextMarkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.CueId.IsValid || !target.BindingId.IsValid) throw new ArgumentException("提示装载身份无效。", nameof(target));
        return _marks.TryGetValue(target.NodeId, out var view) ? view : throw new EffectOutputException("MissingBaselineTarget", "文字标记外观不存在：" + target.NodeId.Value);
    }
    /// <summary>读取组自己的 alpha；组没有两态文字材质。</summary>
    public GroupStyleView Get(GroupTarget target)
    { ArgumentNullException.ThrowIfNull(target); return _groups.TryGetValue(target.NodeId, out var view) ? view
        : throw new EffectOutputException("MissingBaselineTarget", "组基线不存在：" + target.NodeId.Value); }
}
