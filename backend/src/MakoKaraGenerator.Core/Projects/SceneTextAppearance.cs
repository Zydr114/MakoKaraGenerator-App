namespace MakoKaraGenerator.Core.Projects;

/// <summary>场景标记外观：字形、待唱外观、已过外观与不透明度，复用规范样式值对象，不含歌词布局与扫字样式。</summary>
/// <remarks>已过一侧的描边、阴影、辉光同样可以跟随待唱一侧（P3 设计 §10.5）。</remarks>
public sealed record SceneTextAppearance
{
    /// <summary>三点创建预设的外观。</summary>
    public static SceneTextAppearance Default { get; } = new();
    /// <summary>字体及排版。</summary>
    public TypographySpec Typography { get; init; } = TypographySpec.Default with { FontSizePx = 32 };
    /// <summary>尚未经过的标记外观。</summary>
    public TextAppearance Pending { get; init; } = new() { Fill = new() { Color = new(255, 255, 255, 255) } };
    /// <summary>已经经过的标记外观；描边、阴影、辉光默认跟随待唱。</summary>
    public SungAppearance Elapsed { get; init; } = SungAppearance.Following(new() { Color = new(95, 95, 95, 255) });
    /// <summary>标记自身透明度。</summary>
    public float Opacity { get; init; } = 1;
    /// <summary>待唱填充的源码访问外观。</summary>
    public FillSpec PendingFill { get => Pending.Fill; init => Pending = Pending with { Fill = value }; }
    /// <summary>已过填充的源码访问外观。</summary>
    public FillSpec ElapsedFill { get => Elapsed.Fill; init => Elapsed = Elapsed with { Fill = value }; }
    /// <summary>待唱单层描边的源码访问外观；写入把待唱描边整表换成这一层，空值关闭。</summary>
    public StrokeSpec? Stroke { get => Pending.Strokes.Innermost; init => Pending = Pending with { Strokes = StrokeLayers.FromStroke(value) }; }
    /// <summary>待唱阴影的源码访问外观。</summary>
    public ShadowSpec? Shadow { get => Pending.Shadow; init => Pending = Pending with { Shadow = value }; }
    /// <summary>待唱辉光的源码访问外观。</summary>
    public GlowSpec? Glow { get => Pending.Glow; init => Pending = Pending with { Glow = value }; }
    /// <summary>所有外观值是否有效。</summary>
    public bool IsValid => ToLyricStyle().IsValid;
    /// <summary>复用现有样式解析和绘制，只映射外观字段。</summary>
    public LyricStyle ToLyricStyle() => new() { Typography = Typography, Unsung = Pending, Sung = Elapsed, Opacity = Opacity };
    /// <summary>从模板按值读取适用外观，排除位置、缩放、动画与扫字样式。</summary>
    public static SceneTextAppearance FromStyle(LyricStyle style) => new()
    {
        Typography = style.Typography, Pending = style.Unsung, Elapsed = style.Sung, Opacity = style.Opacity,
    };
    /// <summary>应用部分覆盖；相同结果保留原实例。</summary>
    public SceneTextAppearance Resolve(SceneTextAppearanceOverride? overlay)
    {
        if (overlay is null) return this;
        var result = FromStyle(LyricStyleResolver.Resolve(ToLyricStyle(), lineOverride: overlay.ToStyleOverride()).Style);
        return result == this ? this : result;
    }
}

/// <summary>文字标记的部分外观覆盖，未写继承，显式空关闭描边、阴影或辉光。</summary>
public sealed record SceneTextAppearanceOverride
{
    /// <summary>字体的部分覆盖。</summary>
    public TypographySpecOverride? Typography { get => _typography; init => _typography = value is { IsEmpty: false } ? value : null; }
    private readonly TypographySpecOverride? _typography;
    /// <summary>待唱外观的部分覆盖。</summary>
    public TextAppearanceOverride? Pending { get => _pending; init => _pending = value is { IsEmpty: false } ? value : null; }
    private readonly TextAppearanceOverride? _pending;
    /// <summary>已过外观的部分覆盖。</summary>
    public SungAppearanceOverride? Elapsed { get => _elapsed; init => _elapsed = value is { IsEmpty: false } ? value : null; }
    private readonly SungAppearanceOverride? _elapsed;
    /// <summary>透明度覆盖。</summary>
    public float? Opacity { get; init; }
    /// <summary>待唱填充覆盖的源码访问外观。</summary>
    public OptionalValue<FillSpecOverride?> PendingFill { get => Pending?.Fill ?? default; init => Pending = (Pending ?? new()) with { Fill = value }; }
    /// <summary>已过填充覆盖的源码访问外观。</summary>
    public OptionalValue<FillSpecOverride?> ElapsedFill { get => Elapsed?.Fill ?? default; init => Elapsed = (Elapsed ?? new()) with { Fill = value }; }
    /// <summary>复用现有覆盖解析器，不向其中传入布局字段。</summary>
    public LyricStyleOverride ToStyleOverride() => new() { Typography = Typography, Unsung = Pending, Sung = Elapsed, Opacity = Opacity };
    /// <summary>提取现有样式覆盖的外观字段，丢弃扫字样式与布局。</summary>
    public static SceneTextAppearanceOverride FromStyleOverride(LyricStyleOverride style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new() { Typography = style.Typography, Pending = style.Unsung, Elapsed = style.Sung, Opacity = style.Opacity };
    }
}
