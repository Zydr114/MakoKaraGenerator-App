using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Projects;
namespace MakoKaraGenerator.Backend.Hosting;
public sealed partial class ProjectSession
{
    private static SceneTextAppearanceDto ProjectSceneAppearance(SceneTextAppearance appearance, ReadModelBudget budget)
    {
        var value = ProjectCanonicalStyle(appearance.ToLyricStyle(), budget);
        return new(value.Typography, value.UnsungFill, value.SungFill, value.Stroke, value.Shadow, value.Opacity, value.Glow);
    }
    private static SceneTextAppearance CoreSceneAppearance(SceneTextAppearanceDto? value)
    {
        if (value?.Typography is not { } t || value.PendingFill is null || value.ElapsedFill is null
            || t.FallbackFamilyNames is null || t.FallbackFamilyNames.Length > StyleEditingLimits.MaxFallbacks
            || t.FontFamilyName?.Length > StyleEditingLimits.MaxFontLength
            || t.FallbackFamilyNames.Any(n => n is null || n.Length > StyleEditingLimits.MaxFontLength)) throw new JsonException();
        CheckSceneMaterial(value.PendingFill.Material); CheckSceneMaterial(value.ElapsedFill.Material);
        if (value.Stroke is { } stroke) CheckSceneMaterial(stroke.Material);
        return new() { Typography = new() { FontFamilyName = t.FontFamilyName, FallbackFamilyNames = [.. t.FallbackFamilyNames],
            FontSizePx = t.FontSizePx, FontWeight = t.FontWeight, IsBold = t.IsBold, IsItalic = t.IsItalic, LetterSpacingPx = t.LetterSpacingPx },
            PendingFill = new() { Material = CoreMaterial(value.PendingFill.Material) }, ElapsedFill = new() { Material = CoreMaterial(value.ElapsedFill.Material) },
            Stroke = CoreStroke(value.Stroke), Shadow = CoreShadow(value.Shadow), Glow = CoreGlow(value.Glow), Opacity = value.Opacity };
    }
    private static void CheckSceneMaterial(StyleMaterialDto? value)
    {
        if (value is GradientStyleMaterialDto { Gradient: { } gradient })
        {
            if (gradient.Stops is null || gradient.Stops.Length > StyleEditingLimits.MaxStops || gradient.Stops.Any(s => s?.Color is null)) throw new JsonException();
        }
        else if (value is not SolidStyleMaterialDto { Color: not null }) throw new JsonException("新外观材质仅接受静态solid/gradient。");
    }
    private static SceneTextAppearanceOverride? CoreMarkAppearance(StyleChangeDto[]? changes)
    {
        if (changes is null || changes.Length > 26) throw new JsonException();
        HashSet<string> names = new(StringComparer.Ordinal);
        LyricStyleOverride overlay = LyricStyleOverride.Empty;
        foreach (var change in changes)
        {
            if (change is null || !names.Add(change.Property) || change.Property is "scale" or "verticalCenterRatio" or "horizontalAlignment") throw new JsonException();
            switch (change)
            {
                case StyleFontFamilyChangeDto font when font.Value?.Length > StyleEditingLimits.MaxFontLength: throw new JsonException();
                case StyleFallbackFamiliesChangeDto fallback when fallback.Value is null || fallback.Value.Length > StyleEditingLimits.MaxFallbacks
                    || fallback.Value.Any(n => n is null || n.Length > StyleEditingLimits.MaxFontLength): throw new JsonException();
                case StyleUnsungMaterialChangeDto material: CheckSceneMaterial(material.Value); break;
                case StyleSungMaterialChangeDto material: CheckSceneMaterial(material.Value); break;
                case StyleStrokeMaterialChangeDto material: CheckSceneMaterial(material.Value); break;
                case StyleStrokeChangeDto { Value: { } stroke }: CheckSceneMaterial(stroke.Material); break;
            }
            overlay = SetAppearanceValue(overlay, CoreStyleChange(change));
        }
        if (names.Contains("stroke") && names.Any(n => n.StartsWith("stroke.", StringComparison.Ordinal))
            || names.Contains("shadow") && names.Any(n => n.StartsWith("shadow.", StringComparison.Ordinal))
            || names.Contains("glow") && names.Any(n => n.StartsWith("glow.", StringComparison.Ordinal))) throw new JsonException();
        // Resolve the complete overlay independently, including fields that would be hidden
        // by another override, before admitting it to a persistent mark.
        if (!LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: overlay).Style.IsValid) throw new JsonException();
        return overlay.IsEmpty ? null : SceneTextAppearanceOverride.FromStyleOverride(overlay);
    }
    private static LyricStyleOverride SetAppearanceValue(LyricStyleOverride overlay, StyleEditChange change) => change switch
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
    private static StrokeSpecOverride Stroke(LyricStyleOverride overlay) => overlay.Stroke.Value ?? new();
    private static ShadowSpecOverride Shadow(LyricStyleOverride overlay) => overlay.Shadow.Value ?? new();
    private static GlowSpecOverride Glow(LyricStyleOverride overlay) => overlay.Glow.Value ?? new();

}
