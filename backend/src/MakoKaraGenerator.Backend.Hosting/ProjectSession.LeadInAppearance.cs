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
    /// <param name="changes">标记外观的成员值。</param>
    /// <param name="inherited">所属倒计时组的外观；过渡期内描边分量的写入以它为基线，仅作校验时用默认外观。</param>
    private static SceneTextAppearanceOverride? CoreMarkAppearance(StyleChangeDto[]? changes, SceneTextAppearance? inherited = null)
    {
        if (changes is null || changes.Length > 26) throw new JsonException();
        HashSet<string> names = new(StringComparer.Ordinal);
        List<StyleEditChange> values = [];
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
            values.Add(CoreStyleChange(change));
        }
        if (names.Contains("stroke") && names.Any(n => n.StartsWith("stroke.", StringComparison.Ordinal))
            || names.Contains("shadow") && names.Any(n => n.StartsWith("shadow.", StringComparison.Ordinal))
            || names.Contains("glow") && names.Any(n => n.StartsWith("glow.", StringComparison.Ordinal))) throw new JsonException();
        LyricStyleOverride overlay = ProjectStyleEditUseCase.ApplyChanges(LyricStyleOverride.Empty, values, (inherited ?? SceneTextAppearance.Default).ToLyricStyle());
        // Resolve the complete overlay independently, including fields that would be hidden
        // by another override, before admitting it to a persistent mark.
        if (!LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: overlay).Style.IsValid) throw new JsonException();
        return overlay.IsEmpty ? null : SceneTextAppearanceOverride.FromStyleOverride(overlay);
    }
}
