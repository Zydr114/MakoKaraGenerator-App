using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Projects;
namespace MakoKaraGenerator.Backend.Hosting;
public sealed partial class ProjectSession
{
    private static SceneTextAppearanceDto ProjectSceneAppearance(SceneTextAppearance appearance, ReadModelBudget budget)
        => new(ProjectTypography(appearance.Typography, budget), ProjectAppearance(appearance.Pending, budget),
            ProjectSungAppearance(appearance.Elapsed, budget), appearance.Opacity);
    private static SceneTextAppearance CoreSceneAppearance(SceneTextAppearanceDto? value)
    {
        if (value?.Typography is not { } t || value.Pending is null || value.Elapsed is null
            || t.FallbackFamilyNames is null || t.FallbackFamilyNames.Length > StyleEditingLimits.MaxFallbacks
            || t.FontFamilyName?.Length > StyleEditingLimits.MaxFontLength
            || t.FallbackFamilyNames.Any(n => n is null || n.Length > StyleEditingLimits.MaxFontLength)) throw new JsonException();
        return new() { Typography = CoreTypography(t), Pending = CoreAppearance(value.Pending, SceneMaterial),
            Elapsed = CoreSungAppearance(value.Elapsed, SceneMaterial), Opacity = value.Opacity };
    }
    private static MaterialSpec SceneMaterial(StyleMaterialDto value) { CheckSceneMaterial(value); return CoreMaterial(value); }
    private static void CheckSceneMaterial(StyleMaterialDto? value)
    {
        if (value is GradientStyleMaterialDto { Gradient: { } gradient })
        {
            if (gradient.Stops is null || gradient.Stops.Length > StyleEditingLimits.MaxStops || gradient.Stops.Any(s => s?.Color is null)) throw new JsonException();
        }
        else if (value is not SolidStyleMaterialDto { Color: not null }) throw new JsonException("新外观材质仅接受静态solid/gradient。");
    }
    /// <param name="changes">标记外观的成员值；按 34 条可用路径依次写入空覆盖。</param>
    private static SceneTextAppearanceOverride? CoreMarkAppearance(StyleChangeDto[]? changes)
    {
        if (changes is null || changes.Length > LeadInMarkPropertyCount) throw new JsonException();
        HashSet<string> names = new(StringComparer.Ordinal);
        List<StyleEditChange> values = [];
        foreach (var change in changes)
        {
            if (change is null || !names.Add(change.Property) || !IsMarkProperty(change.Property)) throw new JsonException();
            switch (change)
            {
                case StyleFontFamilyChangeDto font when font.Value?.Length > StyleEditingLimits.MaxFontLength: throw new JsonException();
                case StyleFallbackFamiliesChangeDto fallback when fallback.Value is null || fallback.Value.Length > StyleEditingLimits.MaxFallbacks
                    || fallback.Value.Any(n => n is null || n.Length > StyleEditingLimits.MaxFontLength): throw new JsonException();
                case StyleUnsungFillChangeDto material: CheckSceneMaterial(material.Value); break;
                case StyleSungFillChangeDto material: CheckSceneMaterial(material.Value); break;
                case StyleUnsungStrokesChangeDto strokes: CheckSceneStrokes(strokes.Value); break;
                case StyleSungStrokesChangeDto { Value.Value: { } strokes }: CheckSceneStrokes(strokes); break;
            }
            values.Add(CoreStyleChange(change));
        }
        // 整体项与其分量不能同批出现。
        if (names.Any(a => names.Any(b => b.StartsWith(a + ".", StringComparison.Ordinal)))) throw new JsonException();
        LyricStyleOverride overlay = ProjectStyleEditUseCase.ApplyChanges(LyricStyleOverride.Empty, values);
        // Resolve the complete overlay independently, including fields that would be hidden
        // by another override, before admitting it to a persistent mark.
        if (!LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: overlay).Style.IsValid) throw new JsonException();
        return overlay.IsEmpty ? null : SceneTextAppearanceOverride.FromStyleOverride(overlay);
    }
    // 文字标记外观不含扫字样式与 verticalCenterRatio、horizontalAlignment、scale，共 34 条路径。
    private const int LeadInMarkPropertyCount = 34;
    private static bool IsMarkProperty(string property) => LyricStylePropertyExtensions.TryParseJsonName(property, out LyricStyleProperty value)
        && value is not (LyricStyleProperty.VerticalCenterRatio or LyricStyleProperty.HorizontalAlignment or LyricStyleProperty.Scale
            or LyricStyleProperty.RubyRelativeScale or LyricStyleProperty.RubyGapPx)
        && value is < LyricStyleProperty.WipeFeatherRatio or > LyricStyleProperty.WipeHighlightBlend;
    private static void CheckSceneStrokes(StyleStrokeDto[]? strokes)
    {
        if (strokes is null || strokes.Length > StyleEditingLimits.MaxStrokes) throw new JsonException();
        foreach (var stroke in strokes) CheckSceneMaterial(stroke?.Material);
    }
}
