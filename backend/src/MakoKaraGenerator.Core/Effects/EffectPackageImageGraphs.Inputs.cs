using System.Collections.Immutable;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

public static partial class EffectPackageImageGraphs
{
    private static GraphInputDefinition ReadInput(JsonElement input, string path, ImmutableDictionary<string, EffectPackageFile> files,
        ImmutableDictionary<string, EffectGraphCapture>.Builder captures)
    {
        string sourceText = RequiredText(input, "source", path), id = RequiredText(input, "id", path);
        string? choice = null; Dictionary<string, GraphInputSource> choices = new(StringComparer.Ordinal);
        GraphInputSource source;
        if (sourceText.StartsWith("parameter:", StringComparison.Ordinal) && sourceText.Length > 10)
        {
            choice = sourceText[10..];
            if (!input.TryGetProperty("choices", out var items) || items.ValueKind != JsonValueKind.Object) throw Type(path + ".choices");
            foreach (var item in items.EnumerateObject())
                if (!choices.TryAdd(item.Name, Source(String(item.Value, path + ".choices." + item.Name), path + ".choices." + item.Name)))
                    throw Error("PackageJsonDuplicateKey", path + ".choices." + item.Name, "来源选项重复。");
            if (choices.Count == 0) throw Error("PackageGraphChoicesEmpty", path + ".choices", "条件来源必须显式声明全部选项。");
            // Core 的 Source 在条件输入时不参与求值；所有实际帧严格从 Choices 选取，绝不回退到此占位值。
            source = choices.First().Value;
        }
        else source = Source(sourceText, path + ".source");
        var sources = choice is null ? [source] : choices.Values.ToArray();
        bool capture = sources.Any(IsCapture), image = sources.Contains(GraphInputSource.ResourceImage);
        string[] required = ["id", "source", "type", "availability", "backgroundRequirement", "space",
            .. choice is null ? System.Array.Empty<string>() : ["choices"], .. capture ? ["capture"] : System.Array.Empty<string>(),
            .. image ? ["resource"] : System.Array.Empty<string>()];
        Keys(input, path, required, []);
        if (capture)
        {
            var value = input.GetProperty("capture"); string at = path + ".capture";
            Keys(value, at, ["includeRuby", "includeStroke", "includeShadow", "includeDescendantGroups", "includeOwnedOverlay", "region"], []);
            var region = RequiredText(value, "region", at) switch
            {
                "fill" => EffectCaptureRegion.Fill, "stroke" => EffectCaptureRegion.Stroke,
                "fillAndStroke" => EffectCaptureRegion.FillAndStroke, "materialRegion" => EffectCaptureRegion.MaterialRegion,
                _ => throw Error("PackageGraphCaptureRegionUnsupported", at + ".region", "未知捕获区域。"),
            };
            var recipe = new EffectGraphCapture(Boolean(value.GetProperty("includeRuby"), at + ".includeRuby"),
                Boolean(value.GetProperty("includeStroke"), at + ".includeStroke"), Boolean(value.GetProperty("includeShadow"), at + ".includeShadow"),
                Boolean(value.GetProperty("includeDescendantGroups"), at + ".includeDescendantGroups"),
                Boolean(value.GetProperty("includeOwnedOverlay"), at + ".includeOwnedOverlay"), region);
            if (!captures.TryAdd(id, recipe)) throw Error("GraphDuplicateIdentity", path + ".id", "输入身份重复。");
        }
        string? resource = image ? RequiredText(input, "resource", path) : null;
        if (resource is not null)
        {
            EffectPackageContents.ValidatePath(resource);
            if (!files.TryGetValue(resource, out var file) || file.Role != "image")
                throw Error("PackageGraphImageMissing", path + ".resource", "图图片须引用同闭包 role:image 文件。");
        }
        bool requiredInput = RequiredText(input, "availability", path) switch
        {
            "required" => true, "optional" => false,
            _ => throw Error("PackageGraphAvailabilityUnsupported", path + ".availability", "未知输入可用性。"),
        };
        var requirement = RequiredText(input, "backgroundRequirement", path) switch
        {
            "none" => GraphBackgroundRequirement.None, "media" => GraphBackgroundRequirement.Media, "opaqueCanvas" => GraphBackgroundRequirement.OpaqueCanvas,
            _ => throw Error("PackageGraphBackgroundUnsupported", path + ".backgroundRequirement", "未知背景需求。"),
        };
        return new(id, source, Surface(RequiredText(input, "type", path), path + ".type"), requiredInput, requirement,
            Space(RequiredText(input, "space", path), path + ".space"), choice, choices, resource);
    }
    private static bool Boolean(JsonElement value, string path)
        => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Type(path);
    private static bool IsCapture(GraphInputSource value) => value is GraphInputSource.TargetColor or GraphInputSource.TargetRegionCoverage
        or GraphInputSource.GlyphCoverage or GraphInputSource.StrokeCoverage or GraphInputSource.SungCoverage;
    private static GraphInputSource Source(string value, string path) => value switch
    {
        "GlyphCoverage" => GraphInputSource.GlyphCoverage, "StrokeCoverage" => GraphInputSource.StrokeCoverage,
        "SungCoverage" => GraphInputSource.SungCoverage, "TargetRegionCoverage" => GraphInputSource.TargetRegionCoverage,
        "TargetColor" => GraphInputSource.TargetColor, "BackgroundMedia" => GraphInputSource.BackgroundMedia,
        "BackdropBelow" => GraphInputSource.BackdropBelow, "BackgroundColor" => GraphInputSource.BackgroundColor,
        "ResourceImage" => GraphInputSource.ResourceImage,
        _ => throw Error("PackageGraphSourceUnsupported", path, "未知图输入来源。"),
    };
}
