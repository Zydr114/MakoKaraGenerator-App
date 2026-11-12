using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

public static partial class ProjectStyleEditUseCase
{
    private static ProjectStyleEditError? ValidateChange(StyleEditChange change)
    {
        StyleEditProperty expected = change switch
        {
            StyleEditChange.FontFamilyName => StyleEditProperty.TypographyFontFamilyName,
            StyleEditChange.FallbackFamilyNames => StyleEditProperty.TypographyFallbackFamilyNames,
            StyleEditChange.FontSize => StyleEditProperty.TypographyFontSizePx,
            StyleEditChange.FontWeight => StyleEditProperty.TypographyFontWeight,
            StyleEditChange.IsBold => StyleEditProperty.TypographyIsBold,
            StyleEditChange.IsItalic => StyleEditProperty.TypographyIsItalic,
            StyleEditChange.LetterSpacing => StyleEditProperty.TypographyLetterSpacingPx,
            StyleEditChange.UnsungMaterial => StyleEditProperty.UnsungMaterial,
            StyleEditChange.SungMaterial => StyleEditProperty.SungMaterial,
            StyleEditChange.Stroke => StyleEditProperty.Stroke,
            StyleEditChange.StrokeMaterial => StyleEditProperty.StrokeMaterial,
            StyleEditChange.StrokeWidth => StyleEditProperty.StrokeWidthPx,
            StyleEditChange.StrokeJoin => StyleEditProperty.StrokeJoin,
            StyleEditChange.StrokePosition => StyleEditProperty.StrokePosition,
            StyleEditChange.Shadow => StyleEditProperty.Shadow,
            StyleEditChange.ShadowColor => StyleEditProperty.ShadowColor,
            StyleEditChange.ShadowOpacity => StyleEditProperty.ShadowOpacity,
            StyleEditChange.ShadowAngle => StyleEditProperty.ShadowAngleDegrees,
            StyleEditChange.ShadowDistance => StyleEditProperty.ShadowDistancePx,
            StyleEditChange.ShadowBlur => StyleEditProperty.ShadowBlurRadiusPx,
            StyleEditChange.Glow => StyleEditProperty.Glow,
            StyleEditChange.GlowColor => StyleEditProperty.GlowColor,
            StyleEditChange.GlowOpacity => StyleEditProperty.GlowOpacity,
            StyleEditChange.GlowBlur => StyleEditProperty.GlowBlurRadiusPx,
            StyleEditChange.GlowSpread => StyleEditProperty.GlowSpreadPx,
            StyleEditChange.VerticalCenter => StyleEditProperty.VerticalCenterRatio,
            StyleEditChange.HorizontalAlignment => StyleEditProperty.HorizontalAlignment,
            StyleEditChange.Opacity => StyleEditProperty.Opacity,
            StyleEditChange.Scale => StyleEditProperty.Scale,
            _ => (StyleEditProperty)(-1),
        };
        if (!Enum.IsDefined(expected) || expected != change.Property)
            return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        bool valid = change switch
        {
            StyleEditChange.UnsungMaterial c => IsWritableMaterial(c.Value),
            StyleEditChange.SungMaterial c => IsWritableMaterial(c.Value),
            StyleEditChange.StrokeMaterial c => IsWritableMaterial(c.Value),
            StyleEditChange.Stroke { Value: { } value } => IsWritableMaterial(value.Material),
            StyleEditChange.FallbackFamilyNames c => !c.Value.IsDefault,
            _ => true,
        };
        if (!valid) return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        try
        {
            var overlay = SetValue(LyricStyleOverride.Empty, change, LyricStyle.Default);
            if (!LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: overlay).Style.IsValid)
                return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        }
        catch (ArgumentException error)
        { return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property, Detail: error.Message); }
        return null;
    }

    private static bool IsWritableMaterial(MaterialSpec? material) => material is MaterialSpec.Solid
        || material is MaterialSpec.Gradient { Value.IsValid: true };

    private static LyricStyleOverride? UpdateOverlay(LyricStyleOverride? current, StyleEditProperty[] properties, StyleEditChange[]? changes,
        LyricStyle inherited)
    {
        var updated = ApplyChanges(current ?? LyricStyleOverride.Empty, properties, changes, inherited);
        if (updated == current || current is null && updated.IsEmpty) return current;
        return updated.IsEmpty ? null : updated;
    }

    // inherited：本层之下解析出的样式；描边分量的写入与恢复以它为基线（过渡期，见 WithStrokeLeaf）。
    private static LyricStyleOverride ApplyChanges(LyricStyleOverride overlay, StyleEditProperty[] properties, StyleEditChange[]? changes,
        LyricStyle inherited)
    {
        if (changes is not null) return SetValues(overlay, changes, inherited);
        foreach (StyleEditProperty property in properties) overlay = RestoreValue(overlay, property, inherited);
        return overlay;
    }

    /// <summary>把一批有类型的成员值写入覆盖；文字标记外观与样式编辑共用同一套写入规则。</summary>
    /// <param name="overlay">当前覆盖。</param>
    /// <param name="changes">成员值，按顺序写入。</param>
    /// <param name="inherited">本层之下解析出的样式；过渡期内描边分量的写入以它为基线。</param>
    /// <returns>写入后的覆盖；无变化时与原覆盖相等。</returns>
    public static LyricStyleOverride ApplyChanges(LyricStyleOverride overlay, IEnumerable<StyleEditChange> changes, LyricStyle inherited)
    {
        ArgumentNullException.ThrowIfNull(overlay); ArgumentNullException.ThrowIfNull(changes); ArgumentNullException.ThrowIfNull(inherited);
        StyleEditChange[] values = [.. changes];
        if (values.Any(static change => change is null)) throw new ArgumentException("成员值不能为 null。", nameof(changes));
        return SetValues(overlay, values, inherited);
    }

    // 过渡期：同一批的描边分量先依次合成一层再写入一次。逐项写入时，先写的分量若使这一层宽度为 0
    // （即无描边）会被丢掉，同一批重放也不再幂等。其余成员与描边无关，按原顺序写入。
    private static LyricStyleOverride SetValues(LyricStyleOverride overlay, IReadOnlyList<StyleEditChange> changes, LyricStyle inherited)
    {
        Func<StrokeSpec, StrokeSpec>? strokeLeaves = null;
        foreach (StyleEditChange change in changes)
        {
            if (StrokeLeaf(change) is not { } leaf) { overlay = SetValue(overlay, change, inherited); continue; }
            Func<StrokeSpec, StrokeSpec>? previous = strokeLeaves;
            strokeLeaves = previous is null ? leaf : stroke => leaf(previous(stroke));
        }
        return strokeLeaves is null ? overlay : WithStrokeLeaf(overlay, inherited, strokeLeaves);
    }

    private static Func<StrokeSpec, StrokeSpec>? StrokeLeaf(StyleEditChange change) => change switch
    {
        StyleEditChange.StrokeMaterial c => stroke => stroke with { Material = c.Value },
        StyleEditChange.StrokeWidth c => stroke => stroke with { WidthPx = c.Value },
        StyleEditChange.StrokeJoin c => stroke => stroke with { Join = c.Value },
        _ => null,
    };

    private static LyricStyleOverride SetValue(LyricStyleOverride overlay, StyleEditChange change, LyricStyle inherited) => change switch
    {
        StyleEditChange.FontFamilyName c => overlay with { Typography = Typography(overlay) with { FontFamilyName = OptionalValue.Set<string?>(c.Value) } },
        StyleEditChange.FallbackFamilyNames c => overlay.Typography?.FallbackFamilyNames is { } current && current.SequenceEqual(c.Value) ? overlay : overlay with { Typography = Typography(overlay) with { FallbackFamilyNames = c.Value } },
        StyleEditChange.FontSize c => overlay with { Typography = Typography(overlay) with { FontSizePx = c.Value } },
        StyleEditChange.FontWeight c => overlay with { Typography = Typography(overlay) with { FontWeight = c.Value } },
        StyleEditChange.IsBold c => overlay with { Typography = Typography(overlay) with { IsBold = c.Value } },
        StyleEditChange.IsItalic c => overlay with { Typography = Typography(overlay) with { IsItalic = c.Value } },
        StyleEditChange.LetterSpacing c => overlay with { Typography = Typography(overlay) with { LetterSpacingPx = c.Value } },
        StyleEditChange.UnsungMaterial c => overlay with { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(c.Value) }) },
        StyleEditChange.SungMaterial c => overlay with { SungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(c.Value) }) },
        StyleEditChange.Stroke c => overlay with { Stroke = OptionalValue.Set<StrokeSpec?>(c.Value) },
        StyleEditChange.StrokeMaterial or StyleEditChange.StrokeWidth or StyleEditChange.StrokeJoin => WithStrokeLeaf(overlay, inherited, StrokeLeaf(change)!),
        StyleEditChange.StrokePosition => overlay,
        StyleEditChange.Shadow c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(c.Value is { } value ? ShadowSpecOverride.FromSpec(value) : null) },
        StyleEditChange.ShadowColor c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { Color = c.Value }) },
        StyleEditChange.ShadowOpacity c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { Opacity = c.Value }) },
        StyleEditChange.ShadowAngle c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { AngleDegrees = c.Value }) },
        StyleEditChange.ShadowDistance c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { DistancePx = c.Value }) },
        StyleEditChange.ShadowBlur c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { BlurRadiusPx = c.Value }) },
        StyleEditChange.Glow c => overlay with { Glow = OptionalValue.Set<GlowSpecOverride?>(c.Value is { } value ? GlowSpecOverride.FromSpec(value) : null) },
        StyleEditChange.GlowColor c => overlay with { Glow = OptionalValue.Set<GlowSpecOverride?>(Glow(overlay) with { Color = c.Value }) },
        StyleEditChange.GlowOpacity c => overlay with { Glow = OptionalValue.Set<GlowSpecOverride?>(Glow(overlay) with { Opacity = c.Value }) },
        StyleEditChange.GlowBlur c => overlay with { Glow = OptionalValue.Set<GlowSpecOverride?>(Glow(overlay) with { BlurRadiusPx = c.Value }) },
        StyleEditChange.GlowSpread c => overlay with { Glow = OptionalValue.Set<GlowSpecOverride?>(Glow(overlay) with { SpreadPx = c.Value }) },
        StyleEditChange.VerticalCenter c => overlay with { VerticalCenterRatio = c.Value },
        StyleEditChange.HorizontalAlignment c => overlay with { HorizontalAlignment = c.Value },
        StyleEditChange.Opacity c => overlay with { Opacity = c.Value },
        StyleEditChange.Scale c => overlay with { Scale = c.Value },
        _ => throw new ArgumentException("未知的规范样式值。", nameof(change)),
    };

    private static TypographySpecOverride Typography(LyricStyleOverride overlay) => overlay.Typography ?? new();
    // 过渡期（P3 第 2～6 步）：HTTP 仍按单层描边的分量写入，而描边已改为整表覆盖。分量写入以本层已有描边、
    // 否则（本层未设或已关闭）以下层继承的描边为基线，都没有时用透明的空描边，写成完整的一层；更外的层原样保留。
    // 这与旧的「关闭后设置分量转为部分覆盖、其余分量取上层」在写入时刻的结果一致。宽度为 0 的单层即无描边。
    private static LyricStyleOverride WithStrokeLeaf(LyricStyleOverride overlay, LyricStyle inherited, Func<StrokeSpec, StrokeSpec> update)
    {
        StrokeLayers current = overlay.UnsungStrokes is { Count: > 0 } own ? own : inherited.Unsung.Strokes;
        StrokeSpec basis = current.Innermost ?? EmptyStroke;
        return overlay with { UnsungStrokes = ReplaceInnermost(current, update(basis)) };
    }

    private static StrokeLayers ReplaceInnermost(StrokeLayers strokes, StrokeSpec innermost)
        => strokes.Count <= 1 ? StrokeLayers.FromStroke(innermost) : StrokeLayers.From([innermost, .. strokes.Skip(1)]);

    private static readonly StrokeSpec EmptyStroke = new() { Material = new MaterialSpec.Solid(RgbaColor.Transparent), WidthPx = 0f };
    private static ShadowSpecOverride Shadow(LyricStyleOverride overlay) => overlay.Shadow.Value ?? new();
    private static GlowSpecOverride Glow(LyricStyleOverride overlay) => overlay.Glow.Value ?? new();

    private static LyricStyleOverride RestoreValue(LyricStyleOverride overlay, StyleEditProperty property, LyricStyle inherited)
    {
        if (property is >= StyleEditProperty.TypographyFontFamilyName and <= StyleEditProperty.TypographyLetterSpacingPx)
        {
            if (overlay.Typography is not { } value) return overlay;
            var updated = property switch
            {
                StyleEditProperty.TypographyFontFamilyName => value with { FontFamilyName = default },
                StyleEditProperty.TypographyFallbackFamilyNames => value with { FallbackFamilyNames = null },
                StyleEditProperty.TypographyFontSizePx => value with { FontSizePx = null },
                StyleEditProperty.TypographyFontWeight => value with { FontWeight = null },
                StyleEditProperty.TypographyIsBold => value with { IsBold = null },
                StyleEditProperty.TypographyIsItalic => value with { IsItalic = null },
                StyleEditProperty.TypographyLetterSpacingPx => value with { LetterSpacingPx = null },
                _ => value,
            };
            return updated == value ? overlay : overlay with { Typography = updated.IsEmpty ? null : updated };
        }
        if (IsStrokeLeaf(property))
        {
            // 过渡期：分量恢复为下层继承的分量；整层与继承相同则整表恢复继承。
            if (overlay.UnsungStrokes is not { Innermost: { } own } strokes) return overlay;
            StrokeSpec basis = inherited.Unsung.Strokes.Innermost ?? EmptyStroke;
            var restored = property switch
            {
                StyleEditProperty.StrokeMaterial => own with { Material = basis.Material },
                StyleEditProperty.StrokeWidthPx => own with { WidthPx = basis.WidthPx },
                StyleEditProperty.StrokeJoin => own with { Join = basis.Join },
                _ => own,
            };
            StrokeLayers next = ReplaceInnermost(strokes, restored);
            if (next == inherited.Unsung.Strokes) return overlay with { UnsungStrokes = null };
            return next == strokes ? overlay : overlay with { UnsungStrokes = next };
        }
        if (IsGlowLeaf(property))
        {
            if (overlay.Glow.Value is not { } value) return overlay;
            var updated = property switch
            {
                StyleEditProperty.GlowColor => value with { Color = null },
                StyleEditProperty.GlowOpacity => value with { Opacity = null },
                StyleEditProperty.GlowBlurRadiusPx => value with { BlurRadiusPx = null },
                StyleEditProperty.GlowSpreadPx => value with { SpreadPx = null },
                _ => value,
            };
            return updated == value ? overlay : overlay with { Glow = updated.IsEmpty ? default : OptionalValue.Set<GlowSpecOverride?>(updated) };
        }
        if (IsShadowLeaf(property))
        {
            if (overlay.Shadow.Value is not { } value) return overlay;
            var updated = property switch
            {
                StyleEditProperty.ShadowColor => value with { Color = null },
                StyleEditProperty.ShadowOpacity => value with { Opacity = null },
                StyleEditProperty.ShadowAngleDegrees => value with { AngleDegrees = null },
                StyleEditProperty.ShadowDistancePx => value with { DistancePx = null },
                StyleEditProperty.ShadowBlurRadiusPx => value with { BlurRadiusPx = null },
                _ => value,
            };
            return updated == value ? overlay : overlay with { Shadow = updated.IsEmpty ? default : OptionalValue.Set<ShadowSpecOverride?>(updated) };
        }
        return property switch
        {
            StyleEditProperty.UnsungMaterial => overlay with { UnsungFill = default },
            StyleEditProperty.SungMaterial => overlay with { SungFill = default },
            StyleEditProperty.Stroke => overlay with { Stroke = default },
            StyleEditProperty.Shadow => overlay with { Shadow = default },
            StyleEditProperty.Glow => overlay with { Glow = default },
            StyleEditProperty.VerticalCenterRatio => overlay with { VerticalCenterRatio = null },
            StyleEditProperty.HorizontalAlignment => overlay with { HorizontalAlignment = null },
            StyleEditProperty.Opacity => overlay with { Opacity = null },
            StyleEditProperty.Scale => overlay with { Scale = null },
            _ => throw new ArgumentException("未知的规范样式成员。", nameof(property)),
        };
    }

    private static LyricStyleProperty ToCoreProperty(StyleEditProperty property) => property switch
    {
        StyleEditProperty.TypographyFontFamilyName => LyricStyleProperty.FontFamilyName,
        StyleEditProperty.TypographyFallbackFamilyNames => LyricStyleProperty.FallbackFamilyNames,
        StyleEditProperty.TypographyFontSizePx => LyricStyleProperty.FontSizePx,
        StyleEditProperty.TypographyFontWeight => LyricStyleProperty.FontWeight,
        StyleEditProperty.TypographyIsBold => LyricStyleProperty.IsBold,
        StyleEditProperty.TypographyIsItalic => LyricStyleProperty.IsItalic,
        StyleEditProperty.TypographyLetterSpacingPx => LyricStyleProperty.LetterSpacingPx,
        StyleEditProperty.UnsungMaterial => LyricStyleProperty.UnsungFill,
        StyleEditProperty.SungMaterial => LyricStyleProperty.SungFill,
        StyleEditProperty.Stroke or StyleEditProperty.StrokeMaterial or StyleEditProperty.StrokeWidthPx
            or StyleEditProperty.StrokeJoin or StyleEditProperty.StrokePosition => LyricStyleProperty.UnsungStrokes,
        StyleEditProperty.Shadow => LyricStyleProperty.UnsungShadow,
        StyleEditProperty.ShadowColor => LyricStyleProperty.UnsungShadowColor,
        StyleEditProperty.ShadowOpacity => LyricStyleProperty.UnsungShadowOpacity,
        StyleEditProperty.ShadowAngleDegrees => LyricStyleProperty.UnsungShadowAngleDegrees,
        StyleEditProperty.ShadowDistancePx => LyricStyleProperty.UnsungShadowDistancePx,
        StyleEditProperty.ShadowBlurRadiusPx => LyricStyleProperty.UnsungShadowBlurRadiusPx,
        StyleEditProperty.Glow => LyricStyleProperty.UnsungGlow,
        StyleEditProperty.GlowColor => LyricStyleProperty.UnsungGlowColor,
        StyleEditProperty.GlowOpacity => LyricStyleProperty.UnsungGlowOpacity,
        StyleEditProperty.GlowBlurRadiusPx => LyricStyleProperty.UnsungGlowBlurRadiusPx,
        StyleEditProperty.GlowSpreadPx => LyricStyleProperty.UnsungGlowSpreadPx,
        StyleEditProperty.VerticalCenterRatio => LyricStyleProperty.VerticalCenterRatio,
        StyleEditProperty.HorizontalAlignment => LyricStyleProperty.HorizontalAlignment,
        StyleEditProperty.Opacity => LyricStyleProperty.Opacity,
        StyleEditProperty.Scale => LyricStyleProperty.Scale,
        _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };

    private static int IndexOf<T>(ImmutableArray<T> values, Func<T, bool> match)
    {
        for (int index = 0; index < values.Length; index++) if (match(values[index])) return index;
        return -1;
    }
}
