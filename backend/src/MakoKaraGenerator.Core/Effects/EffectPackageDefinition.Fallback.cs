using System.Text.Json;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

public sealed partial class EffectPackageDefinition
{
    private static EffectMaterialFallback ReadFallback(JsonElement value, string path, CancellationToken token)
    {
        string kind = RequiredText(value, "kind", path);
        if (kind == "solid")
        {
            Keys(value, path, ["kind", "color"], []);
            return new EffectMaterialFallback.Solid(ReadColor(value.GetProperty("color"), path + ".color"));
        }
        if (kind != "gradient") throw Error("PackageMaterialFallbackInvalid", path + ".kind", "回退仅允许纯色或完整渐变。");
        Keys(value, path, ["kind", "gradient"], []);
        var gradient = value.GetProperty("gradient"); string location = path + ".gradient";
        Keys(gradient, location, ["kind", "angleDegrees", "centerX", "centerY", "radiusX", "radiusY", "coordinateSpace", "colorSpace", "huePath", "stops"], []);
        var stops = new List<GradientStop>(); int index = 0;
        foreach (var stop in Array(gradient.GetProperty("stops"), location + ".stops"))
        {
            token.ThrowIfCancellationRequested(); string point = location + ".stops[" + index++ + "]";
            Keys(stop, point, ["offset", "color"], []);
            stops.Add(new(Number(stop.GetProperty("offset"), point + ".offset"), ReadColor(stop.GetProperty("color"), point + ".color")));
        }
        var result = new GradientSpec
        {
            Kind = Named<GradientKind>(gradient, "kind", location), AngleDegrees = Number(gradient.GetProperty("angleDegrees"), location + ".angleDegrees"),
            CenterX = Number(gradient.GetProperty("centerX"), location + ".centerX"), CenterY = Number(gradient.GetProperty("centerY"), location + ".centerY"),
            RadiusX = Number(gradient.GetProperty("radiusX"), location + ".radiusX"), RadiusY = Number(gradient.GetProperty("radiusY"), location + ".radiusY"),
            CoordinateSpace = Named<GradientCoordinateSpace>(gradient, "coordinateSpace", location), ColorSpace = Named<GradientColorSpace>(gradient, "colorSpace", location),
            HuePath = Named<GradientHuePath>(gradient, "huePath", location), Stops = [.. stops],
        };
        if (!result.IsValid) throw Error("PackageMaterialFallbackInvalid", location, "渐变必须完整有效，不排序、补端点或夹值。");
        return new EffectMaterialFallback.Gradient(result);
    }
    private static RgbaColor ReadColor(JsonElement value, string path)
    {
        string text = String(value, path);
        if (text.Length != 9 || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit) || !RgbaColor.TryParse(text, out var color))
            throw Error("PackageMaterialFallbackInvalid", path, "颜色须完整 #RRGGBBAA，不补 alpha 或修剪空格。");
        return color;
    }
    private static T Named<T>(JsonElement value, string field, string path) where T : struct, Enum
    {
        string name = RequiredText(value, field, path);
        return Enum.GetNames<T>().Contains(name, StringComparer.Ordinal) ? Enum.Parse<T>(name)
            : throw Error("PackageMaterialFallbackInvalid", path + "." + field, "未知渐变枚举名称。");
    }
}
