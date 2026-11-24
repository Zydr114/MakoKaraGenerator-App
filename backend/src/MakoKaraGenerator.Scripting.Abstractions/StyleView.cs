using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>描边拐角连接方式。</summary>
public enum StrokeJoin
{
    /// <summary>尖角。</summary>
    Miter,
    /// <summary>圆角。</summary>
    Round,
    /// <summary>斜切。</summary>
    Bevel,
}
/// <summary>一层描边，几何和材质分开；位置由层序决定（由内向外）。</summary>
public sealed record StrokeValue
{
    /// <summary>正的有限逻辑宽度、明确拐角及非空材质。</summary>
    public StrokeValue(double widthPx, MaterialValue material, StrokeJoin join = StrokeJoin.Round)
    {
        Point2.Finite(widthPx); ArgumentNullException.ThrowIfNull(material);
        if (widthPx <= 0) throw new ArgumentOutOfRangeException(nameof(widthPx), "描边宽度必须为正。");
        if (!Enum.IsDefined(join)) throw new ArgumentOutOfRangeException(nameof(join));
        WidthPx = widthPx; Material = material; Join = join;
    }
    /// <summary>画布逻辑像素宽度。</summary>
    public double WidthPx { get; }
    /// <summary>这一圈的材质；包材质只能用在最内层。</summary>
    public MaterialValue Material { get; }
    /// <summary>拐角连接方式。</summary>
    public StrokeJoin Join { get; }
}
/// <summary>中立阴影；方向角及距离保持源合同。</summary>
public sealed record ShadowValue
{
    /// <summary>有限合法的颜色、不透明度、方向角和非负距离／模糊半径。</summary>
    public ShadowValue(ColorRgba color, double opacity = 1, double angleDegrees = 45, double distancePx = 4, double blurRadiusPx = 8)
        : this(color, opacity, angleDegrees, distancePx, blurRadiusPx, spreadPx: 0, gradient: null) { }

    /// <summary>有限合法的颜色、不透明度、方向角、非负距离／模糊半径／外扩张及可选渐变。</summary>
    public ShadowValue(ColorRgba color, double opacity, double angleDegrees, double distancePx, double blurRadiusPx, double spreadPx, GradientValue? gradient = null)
    {
        ColorRgba.Unit(opacity); Point2.Finite(angleDegrees); Point2.Finite(distancePx); Point2.Finite(blurRadiusPx); Point2.Finite(spreadPx);
        ArgumentOutOfRangeException.ThrowIfNegative(distancePx); ArgumentOutOfRangeException.ThrowIfNegative(blurRadiusPx); ArgumentOutOfRangeException.ThrowIfNegative(spreadPx);
        Color = color; Opacity = opacity; AngleDegrees = angleDegrees; DistancePx = distancePx; BlurRadiusPx = blurRadiusPx; SpreadPx = spreadPx; Gradient = gradient;
    }
    /// <summary>直乘颜色。</summary>
    public ColorRgba Color { get; }
    /// <summary>可选渐变；非空时替代纯色着色。</summary>
    public GradientValue? Gradient { get; }
    /// <summary>独立阴影不透明度 [0,1]。</summary>
    public double Opacity { get; }
    /// <summary>0 度向右，90 度向下。</summary>
    public double AngleDegrees { get; }
    /// <summary>逻辑像素距离。</summary>
    public double DistancePx { get; }
    /// <summary>逻辑像素模糊半径；为 0 时绘制硬边阴影。</summary>
    public double BlurRadiusPx { get; }
    /// <summary>整体轮廓向外扩张的逻辑像素宽度。</summary>
    public double SpreadPx { get; }
}
/// <summary>中立外发光；以整体轮廓（字形加全部描边）为来源。</summary>
public sealed record GlowValue
{
    /// <summary>固定颜色、不透明度、Gaussian sigma 与向外扩张宽度。</summary>
    public GlowValue(ColorRgba color, double opacity = 1, double blurRadiusPx = 8, double spreadPx = 0)
    {
        ColorRgba.Unit(opacity); Point2.Finite(blurRadiusPx); Point2.Finite(spreadPx);
        ArgumentOutOfRangeException.ThrowIfNegative(blurRadiusPx); ArgumentOutOfRangeException.ThrowIfNegative(spreadPx);
        Color = color; Opacity = opacity; BlurRadiusPx = blurRadiusPx; SpreadPx = spreadPx;
    }
    /// <summary>直乘颜色。</summary>
    public ColorRgba Color { get; }
    /// <summary>独立外发光不透明度 [0,1]。</summary>
    public double Opacity { get; }
    /// <summary>逻辑像素 Gaussian sigma。</summary>
    public double BlurRadiusPx { get; }
    /// <summary>整体轮廓向外扩张的逻辑像素宽度。</summary>
    public double SpreadPx { get; }
}
/// <summary>一态（未唱或已唱）的完整外观：填充、由内向外的描边、阴影、辉光。</summary>
public sealed record AppearanceView
{
    /// <summary>描边层数上限。</summary>
    public const int MaxStrokes = 4;
    /// <summary>非空填充；描边 0～4 层、元素非空，包材质只能在第 0 层（最内层）。</summary>
    public AppearanceView(MaterialValue fill, ImmutableArray<StrokeValue> strokes = default, ShadowValue? shadow = null, GlowValue? glow = null)
    {
        ArgumentNullException.ThrowIfNull(fill);
        Fill = fill; Strokes = ValidateStrokes(strokes); Shadow = shadow; Glow = glow;
    }
    /// <summary>填充材质。</summary>
    public MaterialValue Fill { get; }
    /// <summary>描边，由内向外；空为无描边。</summary>
    public ImmutableArray<StrokeValue> Strokes { get; }
    /// <summary>阴影，空为关闭。</summary>
    public ShadowValue? Shadow { get; }
    /// <summary>辉光，空为关闭。</summary>
    public GlowValue? Glow { get; }
    /// <summary>最内层描边；无描边时为空。描边材质槽指的就是这一层的材质。</summary>
    public StrokeValue? InnermostStroke => Strokes.IsEmpty ? null : Strokes[0];
    /// <summary>按内容比较，描边逐层比较。</summary>
    public bool Equals(AppearanceView? other) => ReferenceEquals(this, other) || other is not null
        && Fill == other.Fill && Strokes.SequenceEqual(other.Strokes) && Shadow == other.Shadow && Glow == other.Glow;
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Fill); hash.Add(Shadow); hash.Add(Glow);
        foreach (var stroke in Strokes) hash.Add(stroke);
        return hash.ToHashCode();
    }
    internal static ImmutableArray<StrokeValue> ValidateStrokes(ImmutableArray<StrokeValue> strokes)
    {
        if (strokes.IsDefaultOrEmpty) return [];
        if (strokes.Length > MaxStrokes) throw new EffectOutputException("InvalidStrokeLayers", "描边至多 4 层。");
        for (int index = 0; index < strokes.Length; index++)
        {
            if (strokes[index] is null) throw new EffectOutputException("InvalidStrokeLayers", "描边层不能为空。");
            if (index > 0 && strokes[index].Material is MaterialValue.PackageMaterial)
                throw new EffectOutputException("InvalidStrokeLayers", "包材质只能用在最内层描边。");
        }
        return strokes;
    }
}
/// <summary>扫字线高亮的混合方式。</summary>
public enum WipeHighlightBlend
{
    /// <summary>普通叠加。</summary>
    Normal,
    /// <summary>滤色。</summary>
    Screen,
    /// <summary>相加。</summary>
    Add,
}
/// <summary>扫字线高亮：以演唱位置为中心的峰，叠在本态填充与各圈描边上。</summary>
public sealed record WipeHighlightValue
{
    /// <summary>高亮宽度比例上限。</summary>
    public const double MaxWidthRatio = 2;
    /// <summary>颜色、峰全宽与字号之比 (0, 2]、强度 [0,1] 与混合方式。</summary>
    public WipeHighlightValue(ColorRgba color, double widthRatio = .25, double opacity = 1, WipeHighlightBlend blend = WipeHighlightBlend.Screen)
    {
        Point2.Finite(widthRatio); ColorRgba.Unit(opacity);
        if (widthRatio is <= 0 or > MaxWidthRatio) throw new ArgumentOutOfRangeException(nameof(widthRatio), "高亮宽度比例须在 (0, 2]。");
        if (!Enum.IsDefined(blend)) throw new ArgumentOutOfRangeException(nameof(blend));
        Color = color; WidthRatio = widthRatio; Opacity = opacity; Blend = blend;
    }
    /// <summary>直乘颜色；强度再乘颜色 α。</summary>
    public ColorRgba Color { get; }
    /// <summary>峰的全宽与字号之比。</summary>
    public double WidthRatio { get; }
    /// <summary>高亮强度 [0,1]。</summary>
    public double Opacity { get; }
    /// <summary>混合方式。</summary>
    public WipeHighlightBlend Blend { get; }
}
/// <summary>扫字样式：过渡带羽化与扫字线高亮；两态共享，文字标记没有。</summary>
public sealed record WipeValue
{
    /// <summary>羽化比例上限：过渡带至多两倍字号。</summary>
    public const double MaxFeatherRatio = 2;
    /// <summary>羽化宽度与字号之比 [0, 2]（0 为硬切换）与可选高亮。</summary>
    public WipeValue(double featherRatio = 0, WipeHighlightValue? highlight = null)
    {
        Point2.Finite(featherRatio);
        if (featherRatio is < 0 or > MaxFeatherRatio) throw new ArgumentOutOfRangeException(nameof(featherRatio), "羽化比例须在 [0, 2]。");
        FeatherRatio = featherRatio; Highlight = highlight;
    }
    /// <summary>无羽化、无高亮的硬切换。</summary>
    public static WipeValue Hard { get; } = new();
    /// <summary>过渡带宽度与字号之比；0 为硬切换。</summary>
    public double FeatherRatio { get; }
    /// <summary>扫字线高亮，空为关闭。</summary>
    public WipeHighlightValue? Highlight { get; }
}
/// <summary>静态解析后的只读外观：两态各一套完整外观（已唱的「跟随未唱」已展开）、扫字样式与样式不透明度。</summary>
/// <remarks>字号和源排版不属于样式输出。文字标记的两态对应 Pending／Elapsed，扫字样式恒为硬切换。</remarks>
public sealed record StyleView
{
    /// <summary>两态外观、[0,1] 样式不透明度与扫字样式（空为硬切换）。</summary>
    public StyleView(AppearanceView unsung, AppearanceView sung, double opacity, WipeValue? wipe = null)
    {
        ArgumentNullException.ThrowIfNull(unsung); ArgumentNullException.ThrowIfNull(sung); ColorRgba.Unit(opacity);
        Unsung = unsung; Sung = sung; Opacity = opacity; Wipe = wipe ?? WipeValue.Hard;
    }
    /// <summary>未唱外观。</summary>
    public AppearanceView Unsung { get; }
    /// <summary>已唱外观。</summary>
    public AppearanceView Sung { get; }
    /// <summary>静态样式不透明度；不包含入场或脚本 alpha。</summary>
    public double Opacity { get; }
    /// <summary>扫字样式。</summary>
    public WipeValue Wipe { get; }
    /// <summary>读取一个材质槽当前的材质：填充槽为该态填充，描边槽为该态最内层描边的材质（无描边时为空）。</summary>
    public MaterialValue? Material(MaterialSlot slot) => slot switch
    {
        MaterialSlot.UnsungFill => Unsung.Fill,
        MaterialSlot.SungFill => Sung.Fill,
        MaterialSlot.UnsungStroke => Unsung.InnermostStroke?.Material,
        MaterialSlot.SungStroke => Sung.InnermostStroke?.Material,
        _ => throw new EffectOutputException("InvalidMaterialSlot", "未知材质部位。"),
    };
    /// <summary>明确要求已唱纯色；渐变／包材质报告错误，不采样猜基色。</summary>
    public ColorRgba RequireSungSolidColor() => Sung.Fill is MaterialValue.SolidMaterial solid ? solid.Color
        : throw new EffectOutputException("BaselineMaterialNotSolid", "该作者效果要求已唱纯色材质。请替换为纯色或使用材质参数效果。");
}
/// <summary>固定完整源元素／行、组和标记基线；各实例读同一份，不读前一个实例的输出。</summary>
public sealed class BaselineStyles
{
    private readonly ImmutableDictionary<TokenId, StyleView> _tokens;
    private readonly ImmutableDictionary<LineId, StyleView> _lines;
    private readonly ImmutableDictionary<NodeId, StyleView> _marks;
    private readonly ImmutableDictionary<NodeId, GroupStyleView> _groups;
    private readonly ImmutableDictionary<TokenFamilyTarget, StyleView> _loadedTokens = ImmutableDictionary<TokenFamilyTarget, StyleView>.Empty;
    private readonly ImmutableDictionary<LineTarget, StyleView> _loadedLines = ImmutableDictionary<LineTarget, StyleView>.Empty;
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
    /// <summary>附加真实装载基线；源身份查询保持兼容，装载查询可区分退出与当前槽位外观。</summary>
    public BaselineStyles WithLoaded(IEnumerable<KeyValuePair<TokenFamilyTarget, StyleView>> tokens,
        IEnumerable<KeyValuePair<LineTarget, StyleView>> lines)
    {
        ArgumentNullException.ThrowIfNull(tokens); ArgumentNullException.ThrowIfNull(lines);
        var loadedTokens = tokens.ToImmutableDictionary(); var loadedLines = lines.ToImmutableDictionary();
        if (loadedTokens.Any(static pair => pair.Key is null || !pair.Key.BindingId.IsValid || !pair.Key.TokenId.IsValid || pair.Value is null)
            || loadedLines.Any(static pair => pair.Key is null || !pair.Key.BindingId.IsValid || !pair.Key.LineId.IsValid || pair.Value is null))
            throw new ArgumentException("装载基线须包含真实身份和只读外观。");
        return new(_tokens, _lines, _marks, _groups, loadedTokens, loadedLines);
    }
    // 已校验的源部分原样共享，不再逐项复核（逐帧附加装载时避免随全曲元素数重复校验）。
    private BaselineStyles(ImmutableDictionary<TokenId, StyleView> tokens, ImmutableDictionary<LineId, StyleView> lines,
        ImmutableDictionary<NodeId, StyleView> marks, ImmutableDictionary<NodeId, GroupStyleView> groups,
        ImmutableDictionary<TokenFamilyTarget, StyleView> loadedTokens, ImmutableDictionary<LineTarget, StyleView> loadedLines)
    {
        _tokens = tokens; _lines = lines; _marks = marks; _groups = groups; _loadedTokens = loadedTokens; _loadedLines = loadedLines;
    }
    /// <summary>
    /// 宿主逐帧派生：替换给定源元素／行外观并整体换用标记基线，组基线不变、装载基线清空。
    /// 替换项须为已有源身份；只校验新值，成本随替换项而非全曲元素数增长。
    /// </summary>
    internal BaselineStyles WithSources(IEnumerable<KeyValuePair<TokenId, StyleView>> tokens,
        IEnumerable<KeyValuePair<LineId, StyleView>> lines, IEnumerable<KeyValuePair<NodeId, StyleView>> marks)
    {
        ArgumentNullException.ThrowIfNull(tokens); ArgumentNullException.ThrowIfNull(lines); ArgumentNullException.ThrowIfNull(marks);
        var tokenItems = tokens.ToArray(); var lineItems = lines.ToArray(); var markItems = marks.ToImmutableDictionary();
        if (tokenItems.Any(pair => !pair.Key.IsValid || pair.Value is null || !_tokens.ContainsKey(pair.Key)))
            throw new ArgumentException("基线需要真实元素和非空外观。", nameof(tokens));
        if (lineItems.Any(pair => !pair.Key.IsValid || pair.Value is null || !_lines.ContainsKey(pair.Key)))
            throw new ArgumentException("行基线身份或外观无效。", nameof(lines));
        if (markItems.Any(static pair => !pair.Key.IsValid || pair.Value is null)) throw new ArgumentException("标记基线身份或外观无效。", nameof(marks));
        if (_groups.Keys.Any(markItems.ContainsKey)) throw new ArgumentException("组基线身份或角色无效。", nameof(marks));
        return new(_tokens.SetItems(tokenItems), _lines.SetItems(lineItems), markItems, _groups,
            ImmutableDictionary<TokenFamilyTarget, StyleView>.Empty, ImmutableDictionary<LineTarget, StyleView>.Empty);
    }
    /// <summary>无源元素的明确空基线。</summary>
    public static BaselineStyles Empty { get; } = new([]);
    /// <summary>真实源元素基线，装载不改变静态继承结果。</summary>
    public StyleView Get(TokenId id) => _tokens.TryGetValue(id, out var view) ? view : throw new EffectOutputException("MissingBaselineTarget", "源外观不存在：" + id.Value);
    /// <summary>读取基础字及注音共同使用的基础字外观，不以联合墨迹决定颜色。</summary>
    public StyleView Get(TokenFamilyTarget target)
    { ArgumentNullException.ThrowIfNull(target); if (!target.BindingId.IsValid) throw new ArgumentException("装载身份无效。", nameof(target));
        return _loadedTokens.TryGetValue(target, out var loaded) ? loaded : Get(target.TokenId); }
    /// <summary>完整源行的工程／分支／行基线，不把元素覆盖混成一个颜色。</summary>
    public StyleView Get(LineId id) => _lines.TryGetValue(id, out var view) ? view
        : throw new EffectOutputException("MissingBaselineTarget", "源行外观不存在：" + id.Value);
    /// <summary>真实源行装载的静态基线。</summary>
    public StyleView Get(LineTarget target)
    { ArgumentNullException.ThrowIfNull(target); if (!target.BindingId.IsValid) throw new ArgumentException("行装载身份无效。", nameof(target));
        return _loadedLines.TryGetValue(target, out var loaded) ? loaded : Get(target.LineId); }
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
