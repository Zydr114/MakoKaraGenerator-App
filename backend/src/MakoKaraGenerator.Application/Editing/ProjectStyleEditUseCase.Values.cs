using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

public static partial class ProjectStyleEditUseCase
{
    private static ProjectStyleEditError? ValidateChange(StyleEditChange change)
    {
        LyricStyleProperty? expected = change switch
        {
            StyleEditChange.FontFamilyName => LyricStyleProperty.FontFamilyName,
            StyleEditChange.FallbackFamilyNames => LyricStyleProperty.FallbackFamilyNames,
            StyleEditChange.FontSize => LyricStyleProperty.FontSizePx,
            StyleEditChange.FontWeight => LyricStyleProperty.FontWeight,
            StyleEditChange.IsBold => LyricStyleProperty.IsBold,
            StyleEditChange.IsItalic => LyricStyleProperty.IsItalic,
            StyleEditChange.LetterSpacing => LyricStyleProperty.LetterSpacingPx,
            StyleEditChange.RubyRelativeScale => LyricStyleProperty.RubyRelativeScale,
            StyleEditChange.RubyGap => LyricStyleProperty.RubyGapPx,
            StyleEditChange.Fill c => c.Sung ? LyricStyleProperty.SungFill : LyricStyleProperty.UnsungFill,
            StyleEditChange.UnsungStrokes => LyricStyleProperty.UnsungStrokes,
            StyleEditChange.SungStrokes => LyricStyleProperty.SungStrokes,
            StyleEditChange.UnsungShadow => LyricStyleProperty.UnsungShadow,
            StyleEditChange.SungShadow => LyricStyleProperty.SungShadow,
            StyleEditChange.UnsungGlow => LyricStyleProperty.UnsungGlow,
            StyleEditChange.SungGlow => LyricStyleProperty.SungGlow,
            StyleEditChange.EffectColor c when LyricStylePropertyExtensions.Component(c.Leaf) == LyricStyleEffectComponent.Color => c.Leaf,
            StyleEditChange.EffectGradient c when LyricStylePropertyExtensions.Component(c.Leaf) == LyricStyleEffectComponent.Gradient => c.Leaf,
            StyleEditChange.EffectNumber c when LyricStylePropertyExtensions.Component(c.Leaf) is LyricStyleEffectComponent.Opacity
                or LyricStyleEffectComponent.AngleDegrees or LyricStyleEffectComponent.DistancePx or LyricStyleEffectComponent.BlurRadiusPx
                or LyricStyleEffectComponent.SpreadPx or LyricStyleEffectComponent.WidthRatio => c.Leaf,
            StyleEditChange.WipeFeather => LyricStyleProperty.WipeFeatherRatio,
            StyleEditChange.WipeHighlight => LyricStyleProperty.WipeHighlight,
            StyleEditChange.HighlightBlend => LyricStyleProperty.WipeHighlightBlend,
            StyleEditChange.VerticalCenter => LyricStyleProperty.VerticalCenterRatio,
            StyleEditChange.HorizontalAlignment => LyricStyleProperty.HorizontalAlignment,
            StyleEditChange.Opacity => LyricStyleProperty.Opacity,
            StyleEditChange.Scale => LyricStyleProperty.Scale,
            _ => null,
        };
        if (expected is null || expected != change.Property)
            return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        bool valid = change switch
        {
            StyleEditChange.Fill c => IsWritableMaterial(c.Value),
            StyleEditChange.UnsungStrokes c => IsWritableStrokes(c.Value),
            StyleEditChange.SungStrokes c => c.Value.FollowsUnsung || IsWritableStrokes(c.Value.Value),
            StyleEditChange.UnsungShadow c => IsWritableGradient(c.Value?.Gradient),
            StyleEditChange.SungShadow c => c.Value.FollowsUnsung || IsWritableGradient(c.Value.Value?.Gradient),
            StyleEditChange.EffectGradient c => IsWritableGradient(c.Value),
            StyleEditChange.FallbackFamilyNames c => !c.Value.IsDefault,
            _ => true,
        };
        if (!valid) return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        try
        {
            var overlay = SetValue(LyricStyleOverride.Empty, change);
            if (!LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: overlay).Style.IsValid)
                return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        }
        catch (ArgumentException error)
        { return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property, Detail: error.Message); }
        return null;
    }

    private static bool IsWritableGradient(GradientSpec? gradient) => gradient is null || gradient.IsValid && GradientSupport.IsSupported(gradient);

    private static bool IsWritableMaterial(MaterialSpec? material) => material is MaterialSpec.Solid
        || material is MaterialSpec.Gradient { Value.IsValid: true };

    // 新写入的描边只接受静态材质；包材质只能来自模板或预设。
    private static bool IsWritableStrokes(StrokeLayers? strokes) => strokes is not null && strokes.All(stroke => IsWritableMaterial(stroke.Material));

    private static LyricStyleOverride? UpdateOverlay(LyricStyleOverride? current, LyricStyleProperty[] properties, StyleEditChange[]? changes)
    {
        var updated = ApplyChanges(current ?? LyricStyleOverride.Empty, properties, changes);
        if (current is null) return updated.IsEmpty ? null : updated;
        if (updated == Normalize(current)) return current;
        return updated.IsEmpty ? null : updated;
    }

    private static LyricStyleOverride ApplyChanges(LyricStyleOverride overlay, LyricStyleProperty[] properties, StyleEditChange[]? changes)
    {
        if (changes is not null) return ApplyChanges(overlay, changes);
        // 恢复继承与模板、预设共用 Core 的逐属性复制规则：整体项与其分量同批时以整体项为准。
        return Normalize(StyleBatchEditor.CopyProperties(overlay, LyricStyle.Default, properties, restoreInheritance: true));
    }

    /// <summary>把一批有类型的成员值按顺序写入覆盖；文字标记外观与样式编辑共用同一套写入规则。</summary>
    /// <param name="overlay">当前覆盖。</param>
    /// <param name="changes">成员值，按顺序写入。</param>
    /// <returns>写入后的覆盖；空的子覆盖归一为未设置，无变化时与原覆盖相等。</returns>
    /// <remarks>
    /// 分量写入是部分覆盖：叠在该态在下层解析出的值上，下层关闭时以透明默认值补齐。已唱一侧本层是「跟随」时，
    /// 写入分量会把本层改为只含该分量的显式部分覆盖（与 Core 的模板复制规则相同）。
    /// </remarks>
    public static LyricStyleOverride ApplyChanges(LyricStyleOverride overlay, IEnumerable<StyleEditChange> changes)
    {
        ArgumentNullException.ThrowIfNull(overlay); ArgumentNullException.ThrowIfNull(changes);
        StyleEditChange[] values = [.. changes];
        if (values.Any(static change => change is null)) throw new ArgumentException("成员值不能为 null。", nameof(changes));
        foreach (StyleEditChange change in values) overlay = SetValue(overlay, change);
        return Normalize(overlay);
    }

    private static LyricStyleOverride SetValue(LyricStyleOverride overlay, StyleEditChange change) => change switch
    {
        StyleEditChange.FontFamilyName c => overlay with { Typography = Typography(overlay) with { FontFamilyName = OptionalValue.Set<string?>(c.Value) } },
        StyleEditChange.FallbackFamilyNames c => overlay.Typography?.FallbackFamilyNames is { } current && current.SequenceEqual(c.Value) ? overlay : overlay with { Typography = Typography(overlay) with { FallbackFamilyNames = c.Value } },
        StyleEditChange.FontSize c => overlay with { Typography = Typography(overlay) with { FontSizePx = c.Value } },
        StyleEditChange.FontWeight c => overlay with { Typography = Typography(overlay) with { FontWeight = c.Value } },
        StyleEditChange.IsBold c => overlay with { Typography = Typography(overlay) with { IsBold = c.Value } },
        StyleEditChange.IsItalic c => overlay with { Typography = Typography(overlay) with { IsItalic = c.Value } },
        StyleEditChange.LetterSpacing c => overlay with { Typography = Typography(overlay) with { LetterSpacingPx = c.Value } },
        StyleEditChange.RubyRelativeScale c => overlay with { Ruby = (overlay.Ruby ?? new()) with { RelativeScale = OptionalValue.Set<double?>(c.Value) } },
        StyleEditChange.RubyGap c => overlay with { Ruby = (overlay.Ruby ?? new()) with { GapPx = OptionalValue.Set<double?>(c.Value) } },
        StyleEditChange.Fill { Sung: false } c => WithUnsung(overlay, value => value with { Fill = Fill(c.Value) }),
        StyleEditChange.Fill c => WithSung(overlay, value => value with { Fill = Fill(c.Value) }),
        StyleEditChange.UnsungStrokes c => WithUnsung(overlay, value => value with { Strokes = c.Value ?? throw new ArgumentException("描边表不能为 null。", nameof(change)) }),
        StyleEditChange.SungStrokes c => WithSung(overlay, value => value with { Strokes = OptionalValue.Set(c.Value) }),
        StyleEditChange.UnsungShadow c => WithUnsung(overlay, value => value with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Whole(c.Value, ShadowSpecOverride.FromSpec)) }),
        StyleEditChange.SungShadow c => WithSung(overlay, value => value with
        {
            Shadow = OptionalValue.Set(c.Value.FollowsUnsung ? Followable.Follow<ShadowSpecOverride?>() : Followable.Set<ShadowSpecOverride?>(Whole(c.Value.Value, ShadowSpecOverride.FromSpec))),
        }),
        StyleEditChange.UnsungGlow c => WithUnsung(overlay, value => value with { Glow = OptionalValue.Set<GlowSpecOverride?>(Whole(c.Value, GlowSpecOverride.FromSpec)) }),
        StyleEditChange.SungGlow c => WithSung(overlay, value => value with
        {
            Glow = OptionalValue.Set(c.Value.FollowsUnsung ? Followable.Follow<GlowSpecOverride?>() : Followable.Set<GlowSpecOverride?>(Whole(c.Value.Value, GlowSpecOverride.FromSpec))),
        }),
        StyleEditChange.EffectColor or StyleEditChange.EffectGradient or StyleEditChange.EffectNumber or StyleEditChange.HighlightBlend => SetLeaf(overlay, change),
        StyleEditChange.WipeFeather c => WithWipe(overlay, value => value with { FeatherRatio = c.Value }),
        StyleEditChange.WipeHighlight c => WithWipe(overlay, value => value with { Highlight = OptionalValue.Set<WipeHighlightOverride?>(Whole(c.Value, WipeHighlightOverride.FromSpec)) }),
        StyleEditChange.VerticalCenter c => overlay with { VerticalCenterRatio = c.Value },
        StyleEditChange.HorizontalAlignment c => overlay with { HorizontalAlignment = c.Value },
        StyleEditChange.Opacity c => overlay with { Opacity = c.Value },
        StyleEditChange.Scale c => overlay with { Scale = c.Value },
        _ => throw new ArgumentException("未知的规范样式值。", nameof(change)),
    };

    private static TypographySpecOverride Typography(LyricStyleOverride overlay) => overlay.Typography ?? new();

    private static OptionalValue<FillSpecOverride?> Fill(MaterialSpec material)
        => OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(material) });

    private static TOverride? Whole<TSpec, TOverride>(TSpec? value, Func<TSpec, TOverride> create) where TSpec : class where TOverride : class
        => value is null ? null : create(value);

    private static LyricStyleOverride WithUnsung(LyricStyleOverride overlay, Func<TextAppearanceOverride, TextAppearanceOverride> change)
        => overlay with { Unsung = change(overlay.Unsung ?? new()) };

    private static LyricStyleOverride WithSung(LyricStyleOverride overlay, Func<SungAppearanceOverride, SungAppearanceOverride> change)
        => overlay with { Sung = change(overlay.Sung ?? new()) };

    private static LyricStyleOverride WithWipe(LyricStyleOverride overlay, Func<WipeSpecOverride, WipeSpecOverride> change)
        => overlay with { Wipe = change(overlay.Wipe ?? new()) };

    // 本层已唱一侧的显式部分覆盖；跟随、未设置或关闭时没有可叠加的分量。
    private static T? Explicit<T>(OptionalValue<Followable<T?>> value) where T : class
        => value is { IsSet: true, Value.FollowsUnsung: false } ? value.Value.Value : null;

    private static LyricStyleOverride SetLeaf(LyricStyleOverride overlay, StyleEditChange change)
    {
        LyricStyleProperty property = change.Property;
        LyricStyleEffectComponent component = LyricStylePropertyExtensions.Component(property);
        if (LyricStylePropertyExtensions.UnsungShadowProperties.Contains(property))
            return WithUnsung(overlay, value => value with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(ShadowLeaf(value.Shadow.Value, component, change)) });
        if (LyricStylePropertyExtensions.SungShadowProperties.Contains(property))
            return WithSung(overlay, value => value with { Shadow = OptionalValue.Set(Followable.Set<ShadowSpecOverride?>(ShadowLeaf(Explicit(value.Shadow), component, change))) });
        if (LyricStylePropertyExtensions.UnsungGlowProperties.Contains(property))
            return WithUnsung(overlay, value => value with { Glow = OptionalValue.Set<GlowSpecOverride?>(GlowLeaf(value.Glow.Value, component, change)) });
        if (LyricStylePropertyExtensions.SungGlowProperties.Contains(property))
            return WithSung(overlay, value => value with { Glow = OptionalValue.Set(Followable.Set<GlowSpecOverride?>(GlowLeaf(Explicit(value.Glow), component, change))) });
        if (LyricStylePropertyExtensions.WipeHighlightProperties.Contains(property))
            return WithWipe(overlay, value => value with { Highlight = OptionalValue.Set<WipeHighlightOverride?>(HighlightLeaf(value.Highlight.Value, component, change)) });
        throw new ArgumentException("不是效果分量。", nameof(change));
    }

    private static ShadowSpecOverride ShadowLeaf(ShadowSpecOverride? current, LyricStyleEffectComponent component, StyleEditChange change)
    {
        ShadowSpecOverride value = current ?? new();
        return (component, change) switch
        {
            (LyricStyleEffectComponent.Color, StyleEditChange.EffectColor c) => value with { Color = c.Value },
            (LyricStyleEffectComponent.Gradient, StyleEditChange.EffectGradient g) => value with { Gradient = OptionalValue.Set<GradientSpec?>(g.Value) },
            (LyricStyleEffectComponent.Opacity, StyleEditChange.EffectNumber n) => value with { Opacity = (float)n.Value },
            (LyricStyleEffectComponent.AngleDegrees, StyleEditChange.EffectNumber n) => value with { AngleDegrees = n.Value },
            (LyricStyleEffectComponent.DistancePx, StyleEditChange.EffectNumber n) => value with { DistancePx = (float)n.Value },
            (LyricStyleEffectComponent.BlurRadiusPx, StyleEditChange.EffectNumber n) => value with { BlurRadiusPx = (float)n.Value },
            (LyricStyleEffectComponent.SpreadPx, StyleEditChange.EffectNumber n) => value with { SpreadPx = (float)n.Value },
            _ => throw new ArgumentException("阴影分量与值类型不一致。", nameof(change)),
        };
    }

    private static GlowSpecOverride GlowLeaf(GlowSpecOverride? current, LyricStyleEffectComponent component, StyleEditChange change)
    {
        GlowSpecOverride value = current ?? new();
        return (component, change) switch
        {
            (LyricStyleEffectComponent.Color, StyleEditChange.EffectColor c) => value with { Color = c.Value },
            (LyricStyleEffectComponent.Opacity, StyleEditChange.EffectNumber n) => value with { Opacity = (float)n.Value },
            (LyricStyleEffectComponent.BlurRadiusPx, StyleEditChange.EffectNumber n) => value with { BlurRadiusPx = (float)n.Value },
            (LyricStyleEffectComponent.SpreadPx, StyleEditChange.EffectNumber n) => value with { SpreadPx = (float)n.Value },
            _ => throw new ArgumentException("辉光分量与值类型不一致。", nameof(change)),
        };
    }

    private static WipeHighlightOverride HighlightLeaf(WipeHighlightOverride? current, LyricStyleEffectComponent component, StyleEditChange change)
    {
        WipeHighlightOverride value = current ?? new();
        return (component, change) switch
        {
            (LyricStyleEffectComponent.Color, StyleEditChange.EffectColor c) => value with { Color = c.Value },
            (LyricStyleEffectComponent.WidthRatio, StyleEditChange.EffectNumber n) => value with { WidthRatio = n.Value },
            (LyricStyleEffectComponent.Opacity, StyleEditChange.EffectNumber n) => value with { Opacity = (float)n.Value },
            (LyricStyleEffectComponent.Blend, StyleEditChange.HighlightBlend b) => value with { Blend = b.Value },
            _ => throw new ArgumentException("高亮分量与值类型不一致。", nameof(change)),
        };
    }

    // 空的子覆盖与未设置等价；归一后再比较，恢复未设置的成员不会被当成变化。
    private static LyricStyleOverride Normalize(LyricStyleOverride overlay)
    {
        LyricStyleOverride result = overlay with
        {
            Typography = overlay.Typography is { IsEmpty: true } ? null : overlay.Typography,
            Ruby = overlay.Ruby is { IsEmpty: true } ? null : overlay.Ruby,
            Unsung = overlay.Unsung is { IsEmpty: true } ? null : overlay.Unsung,
            Sung = overlay.Sung is { IsEmpty: true } ? null : overlay.Sung,
            Wipe = overlay.Wipe is { IsEmpty: true } ? null : overlay.Wipe,
        };
        return result == overlay ? overlay : result;
    }

    private static int IndexOf<T>(ImmutableArray<T> values, Func<T, bool> match)
    {
        for (int index = 0; index < values.Length; index++) if (match(values[index])) return index;
        return -1;
    }
}
