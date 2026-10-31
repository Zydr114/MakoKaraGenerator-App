namespace MakoKaraGenerator.Core.Projects;

/// <summary>场景标记外观，复用规范样式值对象，不包含歌词布局。</summary>
public sealed record SceneTextAppearance
{
    /// <summary>三点创建预设的外观。</summary>
    public static SceneTextAppearance Default { get; } = new();
    /// <summary>字体及排版。</summary>
    public TypographySpec Typography { get; init; } = TypographySpec.Default with { FontSizePx = 32 };
    /// <summary>尚未经过的标记填充。</summary>
    public FillSpec PendingFill { get; init; } = new() { Color = new(255, 255, 255, 255) };
    /// <summary>已经经过的标记填充。</summary>
    public FillSpec ElapsedFill { get; init; } = new() { Color = new(95, 95, 95, 255) };
    /// <summary>外部描边；为空关闭。</summary>
    public StrokeSpec? Stroke { get; init; }
    /// <summary>阴影；为空关闭。</summary>
    public ShadowSpec? Shadow { get; init; }
    /// <summary>标记自身透明度。</summary>
    public float Opacity { get; init; } = 1;
    /// <summary>所有外观值是否有效。</summary>
    public bool IsValid => ToLyricStyle().IsValid;
    /// <summary>复用现有样式解析和绘制，只映射外观字段。</summary>
    public LyricStyle ToLyricStyle() => new() { Typography = Typography, UnsungFill = PendingFill,
        SungFill = ElapsedFill, Stroke = Stroke, Shadow = Shadow, Opacity = Opacity };
    /// <summary>从模板按值读取适用外观，排除位置、缩放和动画。</summary>
    public static SceneTextAppearance FromStyle(LyricStyle style) => new() { Typography = style.Typography,
        PendingFill = style.UnsungFill, ElapsedFill = style.SungFill, Stroke = style.Stroke, Shadow = style.Shadow, Opacity = style.Opacity };
    /// <summary>应用部分覆盖；相同结果保留原实例。</summary>
    public SceneTextAppearance Resolve(SceneTextAppearanceOverride? overlay)
    {
        if (overlay is null) return this;
        var result = FromStyle(LyricStyleResolver.Resolve(ToLyricStyle(), lineOverride: overlay.ToStyleOverride()).Style);
        return result == this ? this : result;
    }
}

/// <summary>文字标记的部分外观覆盖，未写继承，显式空关闭描边或阴影。</summary>
public sealed record SceneTextAppearanceOverride
{
    /// <summary>字体的部分覆盖。</summary>
    public TypographySpecOverride? Typography { get => _typography; init => _typography = value is { IsEmpty: false } ? value : null; }
    private readonly TypographySpecOverride? _typography;
    /// <summary>待倒计时填充的部分覆盖。</summary>
    public OptionalValue<FillSpecOverride?> PendingFill { get; init; }
    /// <summary>已经过填充的部分覆盖。</summary>
    public OptionalValue<FillSpecOverride?> ElapsedFill { get; init; }
    /// <summary>描边的部分覆盖或显式关闭。</summary>
    public OptionalValue<StrokeSpecOverride?> Stroke { get; init; }
    /// <summary>阴影的部分覆盖或显式关闭。</summary>
    public OptionalValue<ShadowSpecOverride?> Shadow { get; init; }
    /// <summary>透明度覆盖。</summary>
    public float? Opacity { get; init; }
    /// <summary>复用现有覆盖解析器，不向其中传入布局字段。</summary>
    public LyricStyleOverride ToStyleOverride() => new() { Typography = Typography, UnsungFill = PendingFill,
        SungFill = ElapsedFill, Stroke = Stroke, Shadow = Shadow, Opacity = Opacity };
    /// <summary>提取现有样式覆盖的外观字段。</summary>
    public static SceneTextAppearanceOverride FromStyleOverride(LyricStyleOverride style) => new() { Typography = style.Typography,
        PendingFill = style.UnsungFill, ElapsedFill = style.SungFill, Stroke = style.Stroke, Shadow = style.Shadow, Opacity = style.Opacity };
}
