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

    private static StrokeSpecDto? ToDto(StrokeSpec? stroke) => stroke is null ? null : new()
    {
        Geometry = new() { WidthPx = stroke.WidthPx, Join = stroke.Join.ToString(), Position = stroke.Position.ToString() },
        Material = WriteMaterial(stroke.Material),
    };

    private static JsonElement ToOptionalStrokeDto(OptionalValue<StrokeSpecOverride?> value)
        => !value.IsSet ? default : JsonSerializer.SerializeToElement(value.Value is { } stroke ? new StrokeSpecOverrideDto
        {
            Geometry = stroke.WidthPx is null && stroke.Join is null && stroke.Position is null ? null : new()
                { WidthPx = stroke.WidthPx, Join = stroke.Join?.ToString(), Position = stroke.Position?.ToString() },
            Material = stroke.Material.IsSet ? WriteMaterial(stroke.Material.Value ?? throw new ArgumentException("描边材质不能为 null。")) : default,
        } : null, ProjectJson.Options);

    private static StrokeSpec? MapStrokeSpec(StrokeSpecDto? dto, string location, List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        if (dto is null) return null;
        StrokeGeometryDto geometry = dto.Geometry ?? throw Error("SourceStrokeGeometryMissing", location + ".geometry", "缺少完整描边几何。");
        if (geometry.WidthPx is null || geometry.Join is null || geometry.Position is null)
            throw Error("SourceStrokeGeometryMissing", location + ".geometry", "完整描边几何不能省略字段。");
        return ReadStrokeGeometry(geometry, location) with { Material = ReadMaterial(dto.Material, location + ".material", issues, allowUnsupportedGradients) };
    }

    private static StrokeSpec ReadStrokeGeometry(StrokeGeometryDto geometry, string path)
    {
        if (geometry.WidthPx is { } width && (!float.IsFinite(width) || width < 0f)) throw Range(path + ".geometry.widthPx");
        if (geometry.Join is { } join && (!Enum.TryParse<StrokeJoin>(join, false, out var parsedJoin) || !Enum.IsDefined(parsedJoin))) throw Type(path + ".geometry.join");
        if (geometry.Position is { } position && (!Enum.TryParse<StrokePosition>(position, false, out var parsedPosition) || !Enum.IsDefined(parsedPosition))) throw Type(path + ".geometry.position");
        return new() { WidthPx = geometry.WidthPx ?? 0f, Join = geometry.Join is null ? StrokeJoin.Round : Enum.Parse<StrokeJoin>(geometry.Join),
            Position = geometry.Position is null ? StrokePosition.Outside : Enum.Parse<StrokePosition>(geometry.Position) };
    }

    private static OptionalValue<StrokeSpecOverride?> MapOptionalStrokeOverride(JsonElement value, string location, List<ProjectIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return default;
        if (value.ValueKind == JsonValueKind.Null) return OptionalValue.Set<StrokeSpecOverride?>(null);
        Keys(value, location, [], ["geometry", "material"]);
        if (value.TryGetProperty("geometry", out var geometryValue)) Keys(geometryValue, location + ".geometry", [], ["widthPx", "join", "position"]);
        StrokeSpecOverrideDto dto = JsonSerializer.Deserialize<StrokeSpecOverrideDto>(value, ProjectJson.Options)!;
        StrokeSpec geometry = ReadStrokeGeometry(dto.Geometry ?? new(), location);
        return OptionalValue.Set<StrokeSpecOverride?>(new StrokeSpecOverride
        {
            WidthPx = dto.Geometry?.WidthPx, Join = dto.Geometry?.Join is null ? null : geometry.Join,
            Position = dto.Geometry?.Position is null ? null : geometry.Position,
            Material = dto.Material.ValueKind == JsonValueKind.Undefined ? default : OptionalValue.Set<MaterialSpec?>(ReadMaterial(dto.Material, location + ".material", issues)),
        });
    }
}
