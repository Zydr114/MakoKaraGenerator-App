namespace MakoKaraGenerator.Core.Projects;

/// <summary>批量样式的写入层级。</summary>
public enum StyleTargetLevel
{
    /// <summary>工程默认。</summary>
    Project,
    /// <summary>歌词行覆盖。</summary>
    Line,
    /// <summary>元素覆盖。</summary>
    Token,
}

/// <summary>一次样式应用的目标位置；工程层忽略下标。</summary>
/// <param name="LineIndex">行下标。</param>
/// <param name="TokenIndex">元素下标；行目标为 -1。</param>
public readonly record struct StyleTarget(int LineIndex, int TokenIndex = -1);

/// <summary>原子的批量样式应用；不修改输入工程，失败时不产生部分结果。</summary>
public static class StyleBatchEditor
{
    /// <summary>应用设计稿中选定的属性，或使这些属性恢复继承。</summary>
    /// <param name="project">原工程。</param>
    /// <param name="level">写入层级。</param>
    /// <param name="targets">目标位置，重复项只处理一次。</param>
    /// <param name="style">设计稿的静态样式。</param>
    /// <param name="properties">参与此次应用的属性。</param>
    /// <param name="includeAnimation">是否同时应用动画。</param>
    /// <param name="animation">设计稿动画；行级空值表示继承。</param>
    /// <param name="restoreInheritance">是否移除目标层的选定覆盖。</param>
    /// <returns>完整结果；无变化时返回原工程。</returns>
    public static Project Apply(Project project, StyleTargetLevel level, IEnumerable<StyleTarget> targets,
        LyricStyle style, IEnumerable<LyricStyleProperty> properties, bool includeAnimation = false,
        LineAnimation? animation = null, bool restoreInheritance = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(properties);
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        LyricStyleProperty[] selected = [.. properties.Distinct()];
        StyleTarget[] positions = [.. targets.Distinct()];
        foreach (LyricStyleProperty property in selected)
        {
            if (!Enum.IsDefined(property)) throw new ArgumentOutOfRangeException(nameof(properties));
            if (level == StyleTargetLevel.Token && !TokenStyleSupport.IsSupported(property))
                throw new ArgumentException("元素不支持选定的样式属性。", nameof(properties));
        }
        if (level == StyleTargetLevel.Token && includeAnimation)
            throw new ArgumentException("元素不支持进入动画。", nameof(includeAnimation));
        if (level == StyleTargetLevel.Project && restoreInheritance)
            throw new ArgumentException("工程默认不能恢复上层继承。", nameof(restoreInheritance));
        if (level != StyleTargetLevel.Project)
        {
            if (positions.Length == 0) throw new ArgumentException("没有应用目标。", nameof(targets));
            foreach (StyleTarget target in positions)
            {
                if (target.LineIndex < 0 || target.LineIndex >= project.Lyrics.Lines.Length
                    || (level == StyleTargetLevel.Token && (target.TokenIndex < 0
                        || target.TokenIndex >= project.Lyrics.Lines[target.LineIndex].Tokens.Length)))
                    throw new ArgumentOutOfRangeException(nameof(targets), "应用目标已失效。");
            }
        }
        if (includeAnimation && !restoreInheritance) ValidateAnimation(animation);
        if (selected.Length == 0 && !includeAnimation) return project;

        LyricStyleOverride patch = CopyProperties(LyricStyleOverride.Empty, style, selected, restoreInheritance);
        Project result = project;
        if (level == StyleTargetLevel.Project)
        {
            LyricStyle baseline = LyricStyleResolver.Resolve(project.LyricStyle, lineOverride: patch).Style;
            ValidateFinite(baseline);
            if (baseline != project.LyricStyle) result = LyricStyleEditor.SetBaseline(result, baseline);
            return includeAnimation ? LineAnimationEditor.SetProjectAnimation(result, animation) : result;
        }
        foreach (StyleTarget target in positions)
        {
            var line = result.Lyrics.Lines[target.LineIndex];
            LyricStyleOverride existing = (level == StyleTargetLevel.Line
                ? line.StyleOverride : line.Tokens[target.TokenIndex].StyleOverride) ?? LyricStyleOverride.Empty;
            LyricStyleOverride updated = CopyProperties(existing, style, selected, restoreInheritance);
            LyricStyle resolved = LyricStyleResolver.Resolve(result.LyricStyle,
                lineOverride: level == StyleTargetLevel.Line ? updated : line.StyleOverride,
                tokenOverride: level == StyleTargetLevel.Token ? updated : null).Style;
            ValidateFinite(resolved);
            result = level == StyleTargetLevel.Line
                ? LyricStyleEditor.SetLineOverride(result, target.LineIndex, updated)
                : LyricStyleEditor.SetTokenOverride(result, target.LineIndex, target.TokenIndex, updated);
            if (includeAnimation)
                result = LineAnimationEditor.SetLineAnimation(result, target.LineIndex, restoreInheritance ? null : animation);
        }
        return result;
    }

    /// <summary>把选中属性写入现有覆盖；恢复继承时只清除这些属性。</summary>
    /// <remarks>
    /// 已唱的整体项按原样复制「跟随」；已唱的分量项以 <paramref name="style"/> 展开跟随后的已唱值为来源，
    /// 写入后该项在目标层变为显式值（设计 §10.5）。整体项与其分量同时选中时以整体项为准，分量不再单独写入。
    /// </remarks>
    public static LyricStyleOverride CopyProperties(LyricStyleOverride overlay, LyricStyle style,
        IEnumerable<LyricStyleProperty> properties, bool restoreInheritance = false)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(properties);
        bool restore = restoreInheritance;
        TextAppearance sung = style.ResolveSung();
        LyricStyleProperty[] selected = [.. properties];
        foreach (LyricStyleProperty property in selected)
        {
            if (WholeOf(property) is { } whole && whole != property && selected.Contains(whole)) continue;
            LyricStyleEffectComponent component = LyricStylePropertyExtensions.Component(property);
            overlay = property switch
            {
                LyricStyleProperty.FontFamilyName => overlay with { FontFamilyName = restore ? default(OptionalValue<string?>) : OptionalValue.Set<string?>(style.FontFamilyName) },
                LyricStyleProperty.FallbackFamilyNames => overlay with { FallbackFamilyNames = restore ? null : style.FallbackFamilyNames },
                LyricStyleProperty.FontSizePx => overlay with { FontSizePx = restore ? null : style.FontSizePx },
                LyricStyleProperty.FontWeight => overlay with { FontWeight = restore ? null : style.FontWeight },
                LyricStyleProperty.IsBold => overlay with { IsBold = restore ? null : style.IsBold },
                LyricStyleProperty.IsItalic => overlay with { IsItalic = restore ? null : style.IsItalic },
                LyricStyleProperty.LetterSpacingPx => overlay with { LetterSpacingPx = restore ? null : style.LetterSpacingPx },
                LyricStyleProperty.VerticalCenterRatio => overlay with { VerticalCenterRatio = restore ? null : style.VerticalCenterRatio },
                LyricStyleProperty.HorizontalAlignment => overlay with { HorizontalAlignment = restore ? null : style.HorizontalAlignment },
                LyricStyleProperty.Opacity => overlay with { Opacity = restore ? null : style.Opacity },
                LyricStyleProperty.Scale => overlay with { Scale = restore ? null : style.Scale },
                LyricStyleProperty.UnsungFill => overlay with { UnsungFill = restore ? default : OptionalValue.Set<FillSpecOverride?>(new() { Material = style.Unsung.Fill.Material }) },
                LyricStyleProperty.SungFill => overlay with { SungFill = restore ? default : OptionalValue.Set<FillSpecOverride?>(new() { Material = style.Sung.Fill.Material }) },
                LyricStyleProperty.UnsungStrokes => overlay with { UnsungStrokes = restore ? null : style.Unsung.Strokes },
                LyricStyleProperty.SungStrokes => WithSung(overlay, value => value with { Strokes = restore ? default : OptionalValue.Set(style.Sung.Strokes) }),
                LyricStyleProperty.UnsungShadow => WithUnsung(overlay, value => value with
                {
                    Shadow = restore ? default : OptionalValue.Set<ShadowSpecOverride?>(style.Unsung.Shadow is { } shadow ? ShadowSpecOverride.FromSpec(shadow) : null),
                }),
                LyricStyleProperty.SungShadow => WithSung(overlay, value => value with
                {
                    Shadow = restore ? default : OptionalValue.Set<Followable<ShadowSpecOverride?>>(style.Sung.Shadow.FollowsUnsung ? Followable.Follow<ShadowSpecOverride?>()
                        : Followable.Set<ShadowSpecOverride?>(style.Sung.Shadow.Value is { } shadow ? ShadowSpecOverride.FromSpec(shadow) : null)),
                }),
                LyricStyleProperty.UnsungGlow => WithUnsung(overlay, value => value with
                {
                    Glow = restore ? default : OptionalValue.Set<GlowSpecOverride?>(style.Unsung.Glow is { } glow ? GlowSpecOverride.FromSpec(glow) : null),
                }),
                LyricStyleProperty.SungGlow => WithSung(overlay, value => value with
                {
                    Glow = restore ? default : OptionalValue.Set<Followable<GlowSpecOverride?>>(style.Sung.Glow.FollowsUnsung ? Followable.Follow<GlowSpecOverride?>()
                        : Followable.Set<GlowSpecOverride?>(style.Sung.Glow.Value is { } glow ? GlowSpecOverride.FromSpec(glow) : null)),
                }),
                LyricStyleProperty.UnsungShadowColor or LyricStyleProperty.UnsungShadowOpacity or LyricStyleProperty.UnsungShadowAngleDegrees
                    or LyricStyleProperty.UnsungShadowDistancePx or LyricStyleProperty.UnsungShadowBlurRadiusPx
                    => WithUnsung(overlay, value => value with { Shadow = CopyLeaf(value.Shadow, style.Unsung.Shadow, restore, current => CopyShadowLeaf(current, style.Unsung.Shadow, component, restore)) }),
                LyricStyleProperty.SungShadowColor or LyricStyleProperty.SungShadowOpacity or LyricStyleProperty.SungShadowAngleDegrees
                    or LyricStyleProperty.SungShadowDistancePx or LyricStyleProperty.SungShadowBlurRadiusPx
                    => WithSung(overlay, value => value with { Shadow = CopySungLeaf(value.Shadow, sung.Shadow, restore, current => CopyShadowLeaf(current, sung.Shadow, component, restore)) }),
                LyricStyleProperty.UnsungGlowColor or LyricStyleProperty.UnsungGlowOpacity or LyricStyleProperty.UnsungGlowBlurRadiusPx
                    or LyricStyleProperty.UnsungGlowSpreadPx
                    => WithUnsung(overlay, value => value with { Glow = CopyLeaf(value.Glow, style.Unsung.Glow, restore, current => CopyGlowLeaf(current, style.Unsung.Glow, component, restore)) }),
                LyricStyleProperty.SungGlowColor or LyricStyleProperty.SungGlowOpacity or LyricStyleProperty.SungGlowBlurRadiusPx
                    or LyricStyleProperty.SungGlowSpreadPx
                    => WithSung(overlay, value => value with { Glow = CopySungLeaf(value.Glow, sung.Glow, restore, current => CopyGlowLeaf(current, sung.Glow, component, restore)) }),
                LyricStyleProperty.WipeFeatherRatio => WithWipe(overlay, value => value with { FeatherRatio = restore ? null : style.Wipe.FeatherRatio }),
                LyricStyleProperty.WipeHighlight => WithWipe(overlay, value => value with
                {
                    Highlight = restore ? default : OptionalValue.Set<WipeHighlightOverride?>(style.Wipe.Highlight is { } highlight ? WipeHighlightOverride.FromSpec(highlight) : null),
                }),
                LyricStyleProperty.WipeHighlightColor or LyricStyleProperty.WipeHighlightWidthRatio or LyricStyleProperty.WipeHighlightOpacity
                    or LyricStyleProperty.WipeHighlightBlend
                    => WithWipe(overlay, value => value with { Highlight = CopyLeaf(value.Highlight, style.Wipe.Highlight, restore, current => CopyHighlightLeaf(current, style.Wipe.Highlight, component, restore)) }),
                _ => throw new ArgumentOutOfRangeException(nameof(properties)),
            };
        }
        return overlay;
    }

    private static LyricStyleProperty? WholeOf(LyricStyleProperty property)
    {
        foreach (IReadOnlyList<LyricStyleProperty> group in EffectGroups)
            if (group.Contains(property)) return group[0];
        return null;
    }

    // 每组第一项是整体项，其余是它的分量。
    private static readonly IReadOnlyList<LyricStyleProperty>[] EffectGroups =
    [
        LyricStylePropertyExtensions.UnsungShadowProperties, LyricStylePropertyExtensions.SungShadowProperties,
        LyricStylePropertyExtensions.UnsungGlowProperties, LyricStylePropertyExtensions.SungGlowProperties,
        LyricStylePropertyExtensions.WipeHighlightProperties,
    ];

    private static LyricStyleOverride WithUnsung(LyricStyleOverride overlay, Func<TextAppearanceOverride, TextAppearanceOverride> change)
    {
        TextAppearanceOverride value = change(overlay.Unsung ?? new());
        return overlay with { Unsung = value.IsEmpty ? null : value };
    }

    private static LyricStyleOverride WithSung(LyricStyleOverride overlay, Func<SungAppearanceOverride, SungAppearanceOverride> change)
    {
        SungAppearanceOverride value = change(overlay.Sung ?? new());
        return overlay with { Sung = value.IsEmpty ? null : value };
    }

    private static LyricStyleOverride WithWipe(LyricStyleOverride overlay, Func<WipeSpecOverride, WipeSpecOverride> change)
    {
        WipeSpecOverride value = change(overlay.Wipe ?? new());
        return overlay with { Wipe = value.IsEmpty ? null : value };
    }

    /// <summary>
    /// 分量复制：目标层已显式关闭时，恢复继承不动它，来源也关闭时同样不动；
    /// 其余情况在现有部分覆盖上写入或清除这一分量，清空后视为未设置。
    /// </summary>
    private static OptionalValue<TOverride?> CopyLeaf<TOverride, TSpec>(OptionalValue<TOverride?> current, TSpec? source, bool restore,
        Func<TOverride?, TOverride> change) where TOverride : class where TSpec : class
    {
        if (current.Value is null && (restore || current.IsSet && source is null)) return current;
        TOverride value = change(current.Value);
        return IsEmptyLeaf(value) ? default : OptionalValue.Set<TOverride?>(value);
    }

    /// <summary>已唱分量复制：目标层是「跟随」时，恢复继承不动它；写入时从空的部分覆盖开始，以展开后的已唱值为来源。</summary>
    private static OptionalValue<Followable<TOverride?>> CopySungLeaf<TOverride, TSpec>(OptionalValue<Followable<TOverride?>> current,
        TSpec? source, bool restore, Func<TOverride?, TOverride> change) where TOverride : class where TSpec : class
    {
        if (current is { IsSet: true, Value.FollowsUnsung: true } && restore) return current;
        OptionalValue<TOverride?> inner = current is { IsSet: true, Value.FollowsUnsung: false } ? OptionalValue.Set<TOverride?>(current.Value.Value) : default;
        OptionalValue<TOverride?> result = CopyLeaf(inner, source, restore, change);
        return result.IsSet ? OptionalValue.Set(Followable.Set<TOverride?>(result.Value)) : default;
    }

    private static bool IsEmptyLeaf(object value) => value switch
    {
        ShadowSpecOverride shadow => shadow.IsEmpty,
        GlowSpecOverride glow => glow.IsEmpty,
        WipeHighlightOverride highlight => highlight.IsEmpty,
        _ => false,
    };

    private static ShadowSpecOverride CopyShadowLeaf(ShadowSpecOverride? current, ShadowSpec? source, LyricStyleEffectComponent component, bool restore)
    {
        ShadowSpecOverride value = current ?? new();
        ShadowSpec basis = source ?? new() { Color = RgbaColor.Transparent, DistancePx = 0, BlurRadiusPx = 0, AngleDegrees = 0 };
        return component switch
        {
            LyricStyleEffectComponent.Color => value with { Color = restore ? null : basis.Color },
            LyricStyleEffectComponent.Opacity => value with { Opacity = restore ? null : basis.Opacity },
            LyricStyleEffectComponent.AngleDegrees => value with { AngleDegrees = restore ? null : basis.AngleDegrees },
            LyricStyleEffectComponent.DistancePx => value with { DistancePx = restore ? null : basis.DistancePx },
            LyricStyleEffectComponent.BlurRadiusPx => value with { BlurRadiusPx = restore ? null : basis.BlurRadiusPx },
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
    }

    private static GlowSpecOverride CopyGlowLeaf(GlowSpecOverride? current, GlowSpec? source, LyricStyleEffectComponent component, bool restore)
    {
        GlowSpecOverride value = current ?? new();
        GlowSpec basis = source ?? new() { Color = RgbaColor.Transparent, BlurRadiusPx = 0, SpreadPx = 0 };
        return component switch
        {
            LyricStyleEffectComponent.Color => value with { Color = restore ? null : basis.Color },
            LyricStyleEffectComponent.Opacity => value with { Opacity = restore ? null : basis.Opacity },
            LyricStyleEffectComponent.BlurRadiusPx => value with { BlurRadiusPx = restore ? null : basis.BlurRadiusPx },
            LyricStyleEffectComponent.SpreadPx => value with { SpreadPx = restore ? null : basis.SpreadPx },
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
    }

    private static WipeHighlightOverride CopyHighlightLeaf(WipeHighlightOverride? current, WipeHighlightSpec? source,
        LyricStyleEffectComponent component, bool restore)
    {
        WipeHighlightOverride value = current ?? new();
        WipeHighlightSpec basis = source ?? new() { Color = RgbaColor.Transparent };
        return component switch
        {
            LyricStyleEffectComponent.Color => value with { Color = restore ? null : basis.Color },
            LyricStyleEffectComponent.WidthRatio => value with { WidthRatio = restore ? null : basis.WidthRatio },
            LyricStyleEffectComponent.Opacity => value with { Opacity = restore ? null : basis.Opacity },
            LyricStyleEffectComponent.Blend => value with { Blend = restore ? null : basis.Blend },
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
    }

    /// <summary>检查非有限数值；领域解析器另外负责样式范围校验。</summary>
    public static void ValidateFinite(LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!float.IsFinite(style.FontSizePx) || !float.IsFinite(style.LetterSpacingPx)
            || style.FontWeight is < 100 or > 900 || !double.IsFinite(style.VerticalCenterRatio)
            || !float.IsFinite(style.Opacity) || !float.IsFinite(style.Scale)
            || style.Unsung is not { IsValid: true } || style.Sung is not { IsValid: true } || style.Wipe is not { IsValid: true })
            throw new ArgumentOutOfRangeException(nameof(style), "样式数值必须是有限数。");
    }

    /// <summary>校验完整动画参数；空动画合法。</summary>
    public static void ValidateAnimation(LineAnimation? animation)
    {
        if (animation is null) return;
        if (animation.DurationUs is < 0 or > 10_000_000 || !Enum.IsDefined(animation.Easing)
            || !float.IsFinite(animation.StartOpacity) || animation.StartOpacity is < 0 or > 1
            || !float.IsFinite(animation.StartScale) || animation.StartScale is <= 0 or > 4
            || !float.IsFinite(animation.StartOffsetPx) || Math.Abs(animation.StartOffsetPx) > 10_000)
            throw new ArgumentOutOfRangeException(nameof(animation), "进入动画参数超出范围。");
    }
}
