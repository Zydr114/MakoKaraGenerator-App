using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Core.Media;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>Strict media-query structure reading without opening the pending media execution capability.</summary>
public static class EffectPackageMediaQueries
{
    /// <summary>The one package capability; structural knowledge is separate from execution support.</summary>
    public const string Capability = "media.features";

    /// <summary>Reads the complete shared package structure and returns a non-executable query catalog.</summary>
    /// <param name="contents">The fixed accepted byte closure.</param>
    /// <param name="cancellationToken">Cancels bounded package reading.</param>
    /// <returns>A catalog that cannot be passed to the script compiler as a Definition.</returns>
    public static EffectPackageMediaQueryCatalog Read(EffectPackageContents contents, CancellationToken cancellationToken = default)
        => new(contents, EffectPackageDefinition.ReadStructure(contents, cancellationToken));

    internal static ImmutableArray<EffectMediaQueryDefinition> ReadDeclarations(EffectPackageContents contents, JsonElement root,
        ImmutableArray<EffectParameterDefinition> parameters, CancellationToken token)
    {
        if (!root.TryGetProperty("mediaQueries", out var entries)) return [];
        if (!EffectPackageProfiles.IsV2(contents.Profile)) throw Error("PackageMediaQueryVersionUnsupported", "mediaQueries", "仅 v2 支持媒体查询声明。");
        var result = ImmutableArray.CreateBuilder<EffectMediaQueryDefinition>(); HashSet<string> ids = new(StringComparer.Ordinal);
        var parameterMap = parameters.ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        foreach (var entry in Array(entries, "mediaQueries"))
        {
            token.ThrowIfCancellationRequested(); string path = "mediaQueries[" + result.Count + "]";
            Keys(entry, path, ["id", "space", "window"], []);
            string id = Identity(RequiredText(entry, "id", path), path + ".id");
            if (!ids.Add(id)) throw Error("PackageMediaQueryDuplicate", path + ".id", "媒体查询身份重复。");
            EffectMediaCoordinateSpace space = RequiredText(entry, "space", path) switch
            {
                "sourceNormalized" => EffectMediaCoordinateSpace.SourceNormalized,
                "canvasPixels" => EffectMediaCoordinateSpace.CanvasPixels,
                _ => throw Error("PackageMediaQuerySpaceUnsupported", path + ".space", "未知媒体查询坐标空间。"),
            };
            var window = entry.GetProperty("window"); string location = path + ".window";
            string kind = RequiredText(window, "kind", location); EffectMediaWindow recipe;
            if (kind == "literal")
            {
                Keys(window, location, ["kind", "durationUs"], []);
                long value = Whole(window.GetProperty("durationUs"), location + ".durationUs");
                ValidateWindow(value, location + ".durationUs"); recipe = new EffectMediaWindow.Literal(value);
            }
            else if (kind == "parameter")
            {
                Keys(window, location, ["kind", "parameter"], []);
                string parameter = Identity(RequiredText(window, "parameter", location), location + ".parameter");
                if (!parameterMap.TryGetValue(parameter, out var definition) || definition.Constraint is not EffectParameterConstraint.Duration duration)
                    throw Error("PackageMediaQueryParameterInvalid", location + ".parameter", "查询窗口须引用同包 duration 参数。");
                // First-version declaration restriction: the entire allowed parameter interval must be legal.
                ValidateWindow(duration.Minimum, location + ".parameter.min"); ValidateWindow(duration.Maximum, location + ".parameter.max");
                recipe = new EffectMediaWindow.Parameter(parameter);
            }
            else throw Error("PackageMediaQueryWindowUnsupported", location + ".kind", "未知媒体查询窗口种类。");
            result.Add(new(id, space, recipe));
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<EffectMediaQueryReference> ReadReferences(JsonElement item, string exportKind,
        ImmutableArray<EffectOutputDefinition> outputs, string path, ImmutableArray<EffectMediaQueryDefinition> declarations,
        ImmutableArray<string> parameters, CancellationToken token)
    {
        if (!item.TryGetProperty("mediaQueries", out var entries)) return [];
        var result = ImmutableArray.CreateBuilder<EffectMediaQueryReference>(); HashSet<string> ids = new(StringComparer.Ordinal);
        var queries = declarations.ToDictionary(query => query.Id, StringComparer.Ordinal);
        foreach (var entry in Array(entries, path + ".mediaQueries"))
        {
            token.ThrowIfCancellationRequested(); string location = path + ".mediaQueries[" + result.Count + "]";
            Keys(entry, location, ["query", "fallback"], []);
            string id = Identity(RequiredText(entry, "query", location), location + ".query");
            if (!ids.Add(id)) throw Error("PackageMediaQueryReferenceDuplicate", location + ".query", "同导出媒体查询引用重复。");
            if (!queries.TryGetValue(id, out var query)) throw Error("PackageMediaQueryMissing", location + ".query", "引用未声明媒体查询。");
            if (query.Window is EffectMediaWindow.Parameter parameter && !parameters.Contains(parameter.ParameterId, StringComparer.Ordinal))
                throw Error("PackageMediaQueryParameterMissing", location + ".query", "导出必须显式引用查询窗口参数。");
            var fallback = entry.GetProperty("fallback"); string fallbackPath = location + ".fallback";
            Keys(fallback, fallbackPath, ["pending", "unavailable", "noMedia"], []);
            result.Add(new(id, ReadFallback(fallback.GetProperty("pending"), exportKind, outputs, fallbackPath + ".pending", token),
                ReadFallback(fallback.GetProperty("unavailable"), exportKind, outputs, fallbackPath + ".unavailable", token),
                ReadFallback(fallback.GetProperty("noMedia"), exportKind, outputs, fallbackPath + ".noMedia", token)));
        }
        return [.. result.OrderBy(reference => reference.QueryId, MediaDependencyEncoding.Utf8Comparer)];
    }

    private static EffectMediaFallbackReference ReadFallback(JsonElement value, string exportKind,
        ImmutableArray<EffectOutputDefinition> outputs, string path, CancellationToken token)
    {
        Keys(value, path, ["kind", "outputs"], []); string kind = RequiredText(value, "kind", path);
        var ids = Strings(value.GetProperty("outputs"), path + ".outputs");
        foreach (string id in ids) { token.ThrowIfCancellationRequested(); _ = Identity(id, path + ".outputs"); }
        if (outputs.IsEmpty || ids.Length != outputs.Length || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(outputs.Select(output => output.Id)))
            throw Error("PackageMediaFallbackOutputsInvalid", path + ".outputs", "首版媒体回退必须精确覆盖当前导出的全部输出。");
        ids = [.. ids.Order(MediaDependencyEncoding.Utf8Comparer)];
        if (exportKind == "controller")
        {
            if (kind != "authorBranch") throw Error("PackageMediaFallbackKindInvalid", path + ".kind", "控制器必须明确 authorBranch。");
            return new EffectMediaFallbackReference.AuthorBranch(ids);
        }
        if (kind != "outputBypass" || outputs.Any(output => exportKind == "material"
            ? output.Bypass is not EffectOutputBypass.MaterialFallback : output.Bypass is not EffectOutputBypass.GraphInput))
            throw Error("PackageMediaFallbackKindInvalid", path, "材质或图必须引用其已有的合法输出旁路。");
        return new EffectMediaFallbackReference.OutputBypass(ids);
    }

    internal static void ValidateUsage(EffectPackageContents contents, ImmutableArray<EffectMediaQueryDefinition> declarations,
        IEnumerable<EffectExportDefinition> exports)
    {
        bool hasReferences = exports.Any(export => !export.MediaQueries.IsEmpty);
        bool capability = contents.Requires.Contains(Capability, StringComparer.Ordinal);
        if ((!declarations.IsEmpty || hasReferences) && !capability)
            throw Error("PackageCapabilityMissing", "manifest.json.requires", "媒体查询须声明 media.features。");
        if (capability && (declarations.IsEmpty || !hasReferences))
            throw Error("PackageMediaQueryDeclarationsMissing", "mediaQueries", "媒体能力须有非空目录和实际导出引用。");
    }

    internal static void ValidateWindow(long value, string path)
    {
        if (value < 0 || value > MediaAbCapacityV1.MaximumWindowUs)
            throw Error("PackageMediaQueryWindowInvalid", path, "媒体窗口须为 0 至 2000000 整数微秒。");
    }

    internal static string Identity(string value, string path)
    {
        try { MediaAbRules.ValidateCanonicalText(value, MediaAbFailure.ProfileUnsupported); }
        catch (MediaAbException error) { throw Error("PackageMediaQueryIdentityInvalid", path, "媒体查询身份须为规范文本。", error); }
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw Error("PackageMediaQueryIdentityInvalid", path, "媒体查询身份须为无控制字符的非空规范文本。");
        return value;
    }
}
