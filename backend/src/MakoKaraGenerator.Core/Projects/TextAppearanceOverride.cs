namespace MakoKaraGenerator.Core.Projects;

/// <summary>未唱外观的部分覆盖；未写的成员继承上一层。</summary>
/// <remarks>描边按整个列表覆盖（D-P3-13）；阴影、辉光按分量覆盖，外层显式 null 关闭整个效果。</remarks>
public sealed record TextAppearanceOverride
{
    /// <summary>填充材质整体覆盖。</summary>
    public OptionalValue<FillSpecOverride?> Fill { get; init; }

    /// <summary>描边整表覆盖；<see langword="null"/> 为继承，空表为关闭。</summary>
    public StrokeLayers? Strokes { get; init; }

    /// <summary>阴影部分覆盖；显式 null 关闭。</summary>
    public OptionalValue<ShadowSpecOverride?> Shadow { get; init; }

    /// <summary>辉光部分覆盖；显式 null 关闭。</summary>
    public OptionalValue<GlowSpecOverride?> Glow { get; init; }

    /// <summary>是否没有任何覆盖。</summary>
    public bool IsEmpty => IsEmptyFill(Fill) && Strokes is null
        && (!Shadow.IsSet || Shadow.Value is { IsEmpty: true })
        && (!Glow.IsSet || Glow.Value is { IsEmpty: true });

    /// <summary>已设置的成员是否有效。</summary>
    public bool IsValid => IsValidFill(Fill) && Strokes is null or { IsValid: true }
        && (!Shadow.IsSet || Shadow.Value is null || Shadow.Value.IsValid)
        && (!Glow.IsSet || Glow.Value is null || Glow.Value.IsValid);

    /// <summary>叠到上一层的未唱外观上。</summary>
    /// <param name="current">上一层解析出的未唱外观。</param>
    /// <returns>叠加后的未唱外观。</returns>
    public TextAppearance Apply(TextAppearance current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            Fill = ApplyFill(Fill, current.Fill),
            Strokes = Strokes ?? current.Strokes,
            Shadow = Shadow.IsSet ? Shadow.Value?.Apply(current.Shadow) : current.Shadow,
            Glow = Glow.IsSet ? Glow.Value?.Apply(current.Glow) : current.Glow,
        };
    }

    /// <summary>建立替换全部成员的覆盖。</summary>
    /// <param name="appearance">完整外观。</param>
    /// <returns>覆盖。</returns>
    public static TextAppearanceOverride FromAppearance(TextAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new()
        {
            Fill = OptionalValue.Set<FillSpecOverride?>(new() { Material = appearance.Fill.Material }),
            Strokes = appearance.Strokes,
            Shadow = OptionalValue.Set<ShadowSpecOverride?>(appearance.Shadow is { } shadow ? ShadowSpecOverride.FromSpec(shadow) : null),
            Glow = OptionalValue.Set<GlowSpecOverride?>(appearance.Glow is { } glow ? GlowSpecOverride.FromSpec(glow) : null),
        };
    }

    internal static bool IsEmptyFill(OptionalValue<FillSpecOverride?> fill) => !fill.IsSet || fill.Value is { IsEmpty: true };

    internal static bool IsValidFill(OptionalValue<FillSpecOverride?> fill)
        => !fill.IsSet || fill.Value is { } value && (!value.Material.IsSet || value.Material.Value is { IsValid: true });

    internal static FillSpec ApplyFill(OptionalValue<FillSpecOverride?> fill, FillSpec current)
    {
        if (!fill.IsSet) return current;
        FillSpecOverride value = fill.Value ?? throw new ArgumentException("填充覆盖不能设置为空。", nameof(fill));
        if (!value.Material.IsSet) return current;
        return new FillSpec { Material = value.Material.Value ?? throw new ArgumentException("填充材质不能为空。", nameof(fill)) };
    }
}

/// <summary>已唱外观的部分覆盖；描边、阴影、辉光还可以改为「跟随未唱」。</summary>
/// <remarks>
/// 分量覆盖（如已唱阴影的颜色）叠在已唱一侧在这一层的基线上；基线是跟随时，以同一层解析出的未唱值为基线，
/// 叠加后变为显式值（P3 设计 §10.5）。
/// </remarks>
public sealed record SungAppearanceOverride
{
    /// <summary>填充材质整体覆盖。</summary>
    public OptionalValue<FillSpecOverride?> Fill { get; init; }

    /// <summary>描边：未设置为继承；设置为跟随或整表。</summary>
    public OptionalValue<Followable<StrokeLayers>> Strokes { get; init; }

    /// <summary>阴影：未设置为继承；设置为跟随、关闭或部分覆盖。</summary>
    public OptionalValue<Followable<ShadowSpecOverride?>> Shadow { get; init; }

    /// <summary>辉光：未设置为继承；设置为跟随、关闭或部分覆盖。</summary>
    public OptionalValue<Followable<GlowSpecOverride?>> Glow { get; init; }

    /// <summary>是否没有任何覆盖。</summary>
    public bool IsEmpty => TextAppearanceOverride.IsEmptyFill(Fill) && !Strokes.IsSet
        && (!Shadow.IsSet || Shadow.Value is { FollowsUnsung: false, Value.IsEmpty: true })
        && (!Glow.IsSet || Glow.Value is { FollowsUnsung: false, Value.IsEmpty: true });

    /// <summary>已设置的成员是否有效；显式描边不能为 null。</summary>
    public bool IsValid => TextAppearanceOverride.IsValidFill(Fill)
        && (!Strokes.IsSet || Strokes.Value.FollowsUnsung || Strokes.Value.Value is { IsValid: true })
        && (!Shadow.IsSet || Shadow.Value.FollowsUnsung || Shadow.Value.Value is null || Shadow.Value.Value.IsValid)
        && (!Glow.IsSet || Glow.Value.FollowsUnsung || Glow.Value.Value is null || Glow.Value.Value.IsValid);

    /// <summary>叠到上一层的已唱外观上。</summary>
    /// <param name="current">上一层的已唱外观（可含跟随）。</param>
    /// <param name="unsung">同一层已叠加完覆盖的未唱外观，作为跟随项的分量覆盖基线。</param>
    /// <returns>叠加后的已唱外观。</returns>
    public SungAppearance Apply(SungAppearance current, TextAppearance unsung)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(unsung);
        return current with
        {
            Fill = TextAppearanceOverride.ApplyFill(Fill, current.Fill),
            Strokes = Strokes.IsSet ? Strokes.Value : current.Strokes,
            Shadow = !Shadow.IsSet ? current.Shadow : Shadow.Value.FollowsUnsung ? Followable.Follow<ShadowSpec?>()
                : Followable.Set<ShadowSpec?>(Shadow.Value.Value?.Apply(current.Shadow.Resolve(unsung.Shadow))),
            Glow = !Glow.IsSet ? current.Glow : Glow.Value.FollowsUnsung ? Followable.Follow<GlowSpec?>()
                : Followable.Set<GlowSpec?>(Glow.Value.Value?.Apply(current.Glow.Resolve(unsung.Glow))),
        };
    }

    /// <summary>建立替换全部成员的覆盖，保留跟随。</summary>
    /// <param name="appearance">已唱外观。</param>
    /// <returns>覆盖。</returns>
    public static SungAppearanceOverride FromAppearance(SungAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new()
        {
            Fill = OptionalValue.Set<FillSpecOverride?>(new() { Material = appearance.Fill.Material }),
            Strokes = OptionalValue.Set(appearance.Strokes),
            Shadow = OptionalValue.Set<Followable<ShadowSpecOverride?>>(appearance.Shadow.FollowsUnsung ? Followable.Follow<ShadowSpecOverride?>()
                : Followable.Set<ShadowSpecOverride?>(appearance.Shadow.Value is { } shadow ? ShadowSpecOverride.FromSpec(shadow) : null)),
            Glow = OptionalValue.Set<Followable<GlowSpecOverride?>>(appearance.Glow.FollowsUnsung ? Followable.Follow<GlowSpecOverride?>()
                : Followable.Set<GlowSpecOverride?>(appearance.Glow.Value is { } glow ? GlowSpecOverride.FromSpec(glow) : null)),
        };
    }
}

/// <summary>扫字样式的部分覆盖。</summary>
public sealed record WipeSpecOverride
{
    /// <summary>羽化比例覆盖。</summary>
    public double? FeatherRatio { get; init; }

    /// <summary>高亮部分覆盖；显式 null 关闭。</summary>
    public OptionalValue<WipeHighlightOverride?> Highlight { get; init; }

    /// <summary>是否没有任何覆盖。</summary>
    public bool IsEmpty => FeatherRatio is null && (!Highlight.IsSet || Highlight.Value is { IsEmpty: true });

    /// <summary>已设置的成员是否有效。</summary>
    public bool IsValid => (FeatherRatio is null || double.IsFinite(FeatherRatio.Value) && FeatherRatio is >= 0d and <= WipeSpec.MaxFeatherRatio)
        && (!Highlight.IsSet || Highlight.Value is null || Highlight.Value.IsValid);

    /// <summary>叠到上一层的扫字样式上。</summary>
    /// <param name="current">上一层的扫字样式。</param>
    /// <returns>叠加后的扫字样式。</returns>
    public WipeSpec Apply(WipeSpec current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            FeatherRatio = FeatherRatio ?? current.FeatherRatio,
            Highlight = Highlight.IsSet ? Highlight.Value?.Apply(current.Highlight) : current.Highlight,
        };
    }

    /// <summary>建立替换全部成员的覆盖。</summary>
    /// <param name="wipe">完整扫字样式。</param>
    /// <returns>覆盖。</returns>
    public static WipeSpecOverride FromSpec(WipeSpec wipe)
    {
        ArgumentNullException.ThrowIfNull(wipe);
        return new()
        {
            FeatherRatio = wipe.FeatherRatio,
            Highlight = OptionalValue.Set<WipeHighlightOverride?>(wipe.Highlight is { } highlight ? WipeHighlightOverride.FromSpec(highlight) : null),
        };
    }
}

/// <summary>扫字线高亮的部分覆盖。</summary>
public sealed record WipeHighlightOverride
{
    /// <summary>颜色覆盖。</summary>
    public RgbaColor? Color { get; init; }

    /// <summary>宽度比例覆盖。</summary>
    public double? WidthRatio { get; init; }

    /// <summary>强度覆盖。</summary>
    public float? Opacity { get; init; }

    /// <summary>混合方式覆盖。</summary>
    public WipeHighlightBlend? Blend { get; init; }

    /// <summary>是否没有成员覆盖。</summary>
    public bool IsEmpty => Color is null && WidthRatio is null && Opacity is null && Blend is null;

    /// <summary>是否明确替换全部成员。</summary>
    public bool IsComplete => Color is not null && WidthRatio is not null && Opacity is not null && Blend is not null;

    /// <summary>已设置的成员是否有效。</summary>
    public bool IsValid => (WidthRatio is null || double.IsFinite(WidthRatio.Value) && WidthRatio is > 0d and <= WipeHighlightSpec.MaxWidthRatio)
        && (Opacity is null || float.IsFinite(Opacity.Value) && Opacity is >= 0f and <= 1f)
        && (Blend is null || Enum.IsDefined(Blend.Value));

    /// <summary>叠到上一层的高亮上；上一层关闭时与阴影、辉光相同，从透明颜色的默认值补齐。</summary>
    /// <param name="current">上一层的高亮。</param>
    /// <returns>完整高亮。</returns>
    public WipeHighlightSpec Apply(WipeHighlightSpec? current)
    {
        WipeHighlightSpec basis = current ?? new WipeHighlightSpec { Color = RgbaColor.Transparent };
        return basis with
        {
            Color = Color ?? basis.Color,
            WidthRatio = WidthRatio ?? basis.WidthRatio,
            Opacity = Opacity ?? basis.Opacity,
            Blend = Blend ?? basis.Blend,
        };
    }

    /// <summary>从完整高亮建立全部成员覆盖。</summary>
    /// <param name="highlight">完整高亮。</param>
    /// <returns>覆盖。</returns>
    public static WipeHighlightOverride FromSpec(WipeHighlightSpec highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        return new() { Color = highlight.Color, WidthRatio = highlight.WidthRatio, Opacity = highlight.Opacity, Blend = highlight.Blend };
    }
}
