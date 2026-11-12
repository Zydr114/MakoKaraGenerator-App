using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

// 材质判别编码只有一套读取规则；非法对象不能退回纯色后被重新保存。
internal static partial class ProjectDtoMapper
{
    private static JsonElement ToDto(FillSpec fill) => WriteMaterial(fill.Material);

    private static JsonElement WriteMaterial(MaterialSpec material) => material switch
    {
        MaterialSpec.Solid solid => JsonSerializer.SerializeToElement(new { kind = "solid", color = solid.Color.ToHex() }),
        MaterialSpec.Gradient gradient => JsonSerializer.SerializeToElement(new { kind = "gradient", gradient = ToDto(gradient.Value) }, ProjectJson.Options),
        MaterialSpec.PackageMaterial package => JsonSerializer.SerializeToElement(new
        {
            kind = "packageMaterial", packageId = package.PackageId, revision = package.Revision, exportId = package.ExportId,
            parameters = JsonSerializer.Deserialize<JsonElement>(package.Parameters.Json),
        }, ProjectJson.Options),
        _ => throw new ArgumentException("未知源材质。", nameof(material)),
    };

    private static MaterialSpec ReadMaterial(JsonElement value, string path, List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        string kind = RequiredText(value, "kind", path);
        switch (kind)
        {
            case "solid":
                Keys(value, path, ["kind", "color"], []);
                return new MaterialSpec.Solid(ReadMaterialColor(value.GetProperty("color"), path + ".color"));
            case "gradient":
                Keys(value, path, ["kind", "gradient"], []);
                JsonElement gradient = value.GetProperty("gradient");
                Keys(gradient, path + ".gradient", ["kind", "angleDegrees", "centerX", "centerY", "radiusX", "radiusY", "coordinateSpace", "colorSpace", "huePath", "stops"], []);
                ReadMaterialEnum<GradientKind>(gradient, "kind", path + ".gradient");
                ReadMaterialEnum<GradientCoordinateSpace>(gradient, "coordinateSpace", path + ".gradient");
                ReadMaterialEnum<GradientColorSpace>(gradient, "colorSpace", path + ".gradient");
                ReadMaterialEnum<GradientHuePath>(gradient, "huePath", path + ".gradient");
                List<double> offsets = [];
                foreach (var stop in Array(gradient.GetProperty("stops"), path + ".gradient.stops"))
                {
                    Keys(stop, path + ".gradient.stops", ["offset", "color"], []);
                    _ = ReadMaterialColor(stop.GetProperty("color"), path + ".gradient.stops.color");
                    offsets.Add(Number(stop.GetProperty("offset"), path + ".gradient.stops.offset"));
                }
                if (offsets.Count < 2 || offsets[0] != 0 || offsets[^1] != 1 || offsets.Any(o => o is < 0 or > 1)
                    || !offsets.SequenceEqual(offsets.Order())) throw Error("SourceMaterialInvalid", path, "渐变色标必须按顺序完整覆盖0至1，不能静默修正。");
                GradientSpecDto dto = JsonSerializer.Deserialize<GradientSpecDto>(gradient, ProjectJson.Options)!;
                if (dto.AngleDegrees is null || dto.CenterX is null || dto.CenterY is null || dto.RadiusX is null || dto.RadiusY is null)
                    throw Error("SourceMaterialInvalid", path, "渐变几何不能为空。");
                return new MaterialSpec.Gradient(MapGradientSpec(dto, path + ".gradient", issues, allowUnsupportedGradients)
                    ?? throw Error("SourceMaterialInvalid", path, "渐变描述无效，已取消读取。"));
            case "packageMaterial":
                Keys(value, path, ["kind", "packageId", "revision", "exportId", "parameters"], []);
                return new MaterialSpec.PackageMaterial(RequiredText(value, "packageId", path), RequiredText(value, "revision", path),
                    RequiredText(value, "exportId", path), EffectParameterOverrides.Parse(value.GetProperty("parameters").GetRawText()));
            default:
                throw Error("SourceMaterialKindUnknown", path + ".kind", "未知材质类型。");
        }
    }

    private static void ReadMaterialEnum<T>(JsonElement value, string key, string path) where T : struct, Enum
    {
        if (!Enum.GetNames<T>().Contains(RequiredText(value, key, path), StringComparer.Ordinal)) throw Type(path + "." + key);
    }

    private static RgbaColor ReadMaterialColor(JsonElement value, string path)
    {
        string color = String(value, path);
        return color.Length == 9 && color[0] == '#' && RgbaColor.TryParse(color, out var result)
            ? result : throw Error("SourceMaterialColorInvalid", path, "源材质颜色必须为 #RRGGBBAA。");
    }

    private static JsonElement ToOptionalFillOverrideDto(OptionalValue<FillSpecOverride?> value)
        => !value.IsSet || value.Value is { Material.IsSet: false } ? default
            : WriteMaterial(value.Value?.Material.Value ?? throw new ArgumentException("填充覆盖不能为 null。"));

    private static OptionalValue<FillSpecOverride?> MapOptionalFillOverride(JsonElement value, string location, List<ProjectIssue> issues)
        => value.ValueKind == JsonValueKind.Undefined ? default
            : OptionalValue.Set<FillSpecOverride?>(new FillSpecOverride { Material = OptionalValue.Set<MaterialSpec?>(ReadMaterial(value, location, issues)) });
}
