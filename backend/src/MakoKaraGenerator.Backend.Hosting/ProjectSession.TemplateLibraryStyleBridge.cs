using System.Collections.Immutable;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using System.Text.Json;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    // Same canonical material/gradient/color mapping as the already-delivered style API.
    internal static LyricStyle ImportTemplateLibraryStyle(CanonicalStyleDto style, bool fullTemplate = false)
    {
        MaterialSpec Material(StyleMaterialDto material) => TemplateMaterial(material, fullTemplate);
        return new()
        {
            Typography = CoreTypography(style.Typography), Unsung = CoreAppearance(style.Unsung, Material), Sung = CoreSungAppearance(style.Sung, Material),
            Wipe = style.Wipe is { } wipe ? new() { FeatherRatio = wipe.FeatherRatio, Highlight = CoreHighlight(wipe.Highlight) } : throw new JsonException("缺少扫字样式。"),
            VerticalCenterRatio = style.VerticalCenterRatio, HorizontalAlignment = (LyricHorizontalAlignment)style.HorizontalAlignment,
            Opacity = style.Opacity, Scale = style.Scale,
        };
    }
    private static MaterialSpec TemplateMaterial(StyleMaterialDto material, bool fullTemplate) => fullTemplate && material is PackageStyleMaterialDto package
        ? new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, package.ExportId,
            EffectParameterOverrides.Parse(package.ParametersJson ?? throw new JsonException("完整包材质缺少参数对象。")))
        : CoreMaterial(material);
    internal static CanonicalStyleDto ExportTemplateLibraryStyle(LyricStyle style, bool fullTemplate = false)
    {
        try
        {
            var result = ProjectCanonicalStyle(style, new ReadModelBudget());
            if (!fullTemplate) return result;
            // 完整定义的包材质附带参数对象；只出现在填充与第 0 层描边（D-P3-9）。
            static StyleMaterialDto Complete(StyleMaterialDto projected, MaterialSpec source) => source is MaterialSpec.PackageMaterial package
                ? new PackageStyleMaterialDto(package.PackageId, package.Revision, package.ExportId, package.Parameters.Json)
                : projected;
            static StyleStrokeDto[] Strokes(StyleStrokeDto[] projected, StrokeLayers source)
                => [.. projected.Select((stroke, index) => stroke with { Material = Complete(stroke.Material, source[index].Material) })];
            return result with
            {
                Unsung = result.Unsung with { Fill = Complete(result.Unsung.Fill, style.Unsung.Fill.Material), Strokes = Strokes(result.Unsung.Strokes, style.Unsung.Strokes) },
                Sung = result.Sung with
                {
                    Fill = Complete(result.Sung.Fill, style.Sung.Fill.Material),
                    Strokes = result.Sung.Strokes.Value is { } strokes ? result.Sung.Strokes with { Value = Strokes(strokes, style.Sung.Strokes.Value!) } : result.Sung.Strokes,
                },
            };
        }
        catch (ReadModelBudgetExceededException) { throw new TemplateLibraryResponseBudgetException(); }
    }
    /// <summary>模板与预设的属性清单：与 styles.patch 相同的属性路径，保持原顺序。</summary>
    internal static ImmutableArray<LyricStyleProperty> ImportTemplateProperties(IEnumerable<string> properties)
    {
        ImmutableArray<LyricStyleProperty> result = [.. properties.Select(CoreStyleProperty)];
        return result.Distinct().Count() == result.Length ? result : throw new JsonException("模板属性不能重复。");
    }
    /// <summary>导出模板属性清单为属性路径，保持模板保存的顺序。</summary>
    internal static string[] ExportTemplateProperties(IEnumerable<LyricStyleProperty> properties)
        => [.. properties.Select(LyricStylePropertyExtensions.ToJsonName)];
}
