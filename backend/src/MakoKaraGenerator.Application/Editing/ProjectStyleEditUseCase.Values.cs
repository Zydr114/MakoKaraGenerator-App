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
            var overlay = SetValue(LyricStyleOverride.Empty, change);
            if (!LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: overlay).Style.IsValid)
                return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property);
        }
        catch (ArgumentException error)
        { return new(ProjectStyleEditErrorCode.InvalidValue, Property: change.Property, Detail: error.Message); }
        return null;
    }

    private static bool IsWritableMaterial(MaterialSpec? material) => material is MaterialSpec.Solid
        || material is MaterialSpec.Gradient { Value.IsValid: true };

    private static LyricStyleOverride? UpdateOverlay(LyricStyleOverride? current, StyleEditProperty[] properties, StyleEditChange[]? changes)
    {
        var updated = ApplyChanges(current ?? LyricStyleOverride.Empty, properties, changes);
        if (updated == current || current is null && updated.IsEmpty) return current;
        return updated.IsEmpty ? null : updated;
    }

    private static LyricStyleOverride ApplyChanges(LyricStyleOverride overlay, StyleEditProperty[] properties, StyleEditChange[]? changes)
    {
        for (int index = 0; index < properties.Length; index++)
            overlay = changes is null ? RestoreValue(overlay, properties[index]) : SetValue(overlay, changes[index]);
        return overlay;
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
        StyleEditChange.UnsungMaterial c => overlay with { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(c.Value) }) },
        StyleEditChange.SungMaterial c => overlay with { SungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(c.Value) }) },
        StyleEditChange.Stroke c => overlay with { Stroke = OptionalValue.Set<StrokeSpecOverride?>(c.Value is { } value ? StrokeSpecOverride.FromSpec(value) : null) },
        StyleEditChange.StrokeMaterial c => overlay with { Stroke = OptionalValue.Set<StrokeSpecOverride?>(Stroke(overlay) with { Material = OptionalValue.Set<MaterialSpec?>(c.Value) }) },
        StyleEditChange.StrokeWidth c => overlay with { Stroke = OptionalValue.Set<StrokeSpecOverride?>(Stroke(overlay) with { WidthPx = c.Value }) },
        StyleEditChange.StrokeJoin c => overlay with { Stroke = OptionalValue.Set<StrokeSpecOverride?>(Stroke(overlay) with { Join = c.Value }) },
        StyleEditChange.StrokePosition c => overlay with { Stroke = OptionalValue.Set<StrokeSpecOverride?>(Stroke(overlay) with { Position = c.Value }) },
        StyleEditChange.Shadow c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(c.Value is { } value ? ShadowSpecOverride.FromSpec(value) : null) },
        StyleEditChange.ShadowColor c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { Color = c.Value }) },
        StyleEditChange.ShadowOpacity c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { Opacity = c.Value }) },
        StyleEditChange.ShadowAngle c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { AngleDegrees = c.Value }) },
        StyleEditChange.ShadowDistance c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { DistancePx = c.Value }) },
        StyleEditChange.ShadowBlur c => overlay with { Shadow = OptionalValue.Set<ShadowSpecOverride?>(Shadow(overlay) with { BlurRadiusPx = c.Value }) },
        StyleEditChange.VerticalCenter c => overlay with { VerticalCenterRatio = c.Value },
        StyleEditChange.HorizontalAlignment c => overlay with { HorizontalAlignment = c.Value },
        StyleEditChange.Opacity c => overlay with { Opacity = c.Value },
        StyleEditChange.Scale c => overlay with { Scale = c.Value },
        _ => throw new ArgumentException("未知的规范样式值。", nameof(change)),
    };

    private static TypographySpecOverride Typography(LyricStyleOverride overlay) => overlay.Typography ?? new();
    private static StrokeSpecOverride Stroke(LyricStyleOverride overlay) => overlay.Stroke.Value ?? new();
    private static ShadowSpecOverride Shadow(LyricStyleOverride overlay) => overlay.Shadow.Value ?? new();

    private static LyricStyleOverride RestoreValue(LyricStyleOverride overlay, StyleEditProperty property)
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
            if (overlay.Stroke.Value is not { } value) return overlay;
            var updated = property switch
            {
                StyleEditProperty.StrokeMaterial => value with { Material = default },
                StyleEditProperty.StrokeWidthPx => value with { WidthPx = null },
                StyleEditProperty.StrokeJoin => value with { Join = null },
                StyleEditProperty.StrokePosition => value with { Position = null },
                _ => value,
            };
            return updated == value ? overlay : overlay with { Stroke = updated.IsEmpty ? default : OptionalValue.Set<StrokeSpecOverride?>(updated) };
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
        StyleEditProperty.UnsungMaterial => LyricStyleProperty.UnsungColor,
        StyleEditProperty.SungMaterial => LyricStyleProperty.SungColor,
        StyleEditProperty.Stroke => LyricStyleProperty.StrokeSpec,
        StyleEditProperty.StrokeMaterial => LyricStyleProperty.StrokeColor,
        StyleEditProperty.StrokeWidthPx => LyricStyleProperty.StrokeWidthPx,
        StyleEditProperty.StrokeJoin => LyricStyleProperty.StrokeSpec,
        StyleEditProperty.StrokePosition => LyricStyleProperty.StrokeSpec,
        StyleEditProperty.Shadow => LyricStyleProperty.ShadowSpec,
        StyleEditProperty.ShadowColor => LyricStyleProperty.ShadowColor,
        StyleEditProperty.ShadowOpacity => LyricStyleProperty.ShadowSpec,
        StyleEditProperty.ShadowAngleDegrees => LyricStyleProperty.ShadowSpec,
        StyleEditProperty.ShadowDistancePx => LyricStyleProperty.ShadowSpec,
        StyleEditProperty.ShadowBlurRadiusPx => LyricStyleProperty.ShadowBlurPx,
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
