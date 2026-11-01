using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using System.Text.Json;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    // Same canonical material/gradient/color mapping as the already-delivered style API.
    internal static LyricStyle ImportTemplateLibraryStyle(CanonicalStyleDto style, bool fullTemplate = false) => new()
    {
        Typography = new()
        {
            FontFamilyName = style.Typography.FontFamilyName, FallbackFamilyNames = [.. style.Typography.FallbackFamilyNames],
            FontSizePx = style.Typography.FontSizePx, FontWeight = style.Typography.FontWeight,
            IsBold = style.Typography.IsBold, IsItalic = style.Typography.IsItalic, LetterSpacingPx = style.Typography.LetterSpacingPx,
        },
        UnsungFill = new() { Material = TemplateMaterial(style.UnsungFill.Material, fullTemplate) },
        SungFill = new() { Material = TemplateMaterial(style.SungFill.Material, fullTemplate) },
        Stroke = style.Stroke is not { } stroke ? null : new()
        { Material = TemplateMaterial(stroke.Material, fullTemplate), WidthPx = stroke.WidthPx, Join = (StrokeJoin)stroke.Join, Position = (StrokePosition)stroke.Position },
        Shadow = CoreShadow(style.Shadow), Glow = CoreGlow(style.Glow),
        VerticalCenterRatio = style.VerticalCenterRatio, HorizontalAlignment = (LyricHorizontalAlignment)style.HorizontalAlignment,
        Opacity = style.Opacity, Scale = style.Scale,
    };
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
            StyleMaterialDto Complete(StyleMaterialDto projected, MaterialSpec source) => source is MaterialSpec.PackageMaterial package
                ? new PackageStyleMaterialDto(package.PackageId, package.Revision, package.ExportId, package.Parameters.Json)
                : projected;
            return result with
            {
                UnsungFill = new(Complete(result.UnsungFill.Material, style.UnsungFill.Material)),
                SungFill = new(Complete(result.SungFill.Material, style.SungFill.Material)),
                Stroke = result.Stroke is not { } stroke ? null : stroke with { Material = Complete(stroke.Material, style.Stroke!.Material) },
            };
        }
        catch (ReadModelBudgetExceededException) { throw new TemplateLibraryResponseBudgetException(); }
    }
}
