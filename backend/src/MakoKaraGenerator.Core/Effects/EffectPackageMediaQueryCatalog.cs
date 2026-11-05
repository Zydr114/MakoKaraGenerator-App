using System.Collections.Immutable;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>A structurally validated, non-executable package catalog; never a compiler input or an unchecked Definition.</summary>
public sealed class EffectPackageMediaQueryCatalog
{
    private readonly ImmutableDictionary<string, EffectExportDefinition> _exports;
    private readonly ImmutableDictionary<string, EffectMediaQueryDefinition> _queries;
    private readonly ImmutableDictionary<string, EffectParameterDefinition> _parameters;
    private readonly ImmutableDictionary<string, EffectPackageFile> _files;

    internal EffectPackageMediaQueryCatalog(EffectPackageContents contents, EffectPackageStructure structure)
    {
        Contents = contents; Queries = structure.Queries; Exports = structure.Exports;
        _exports = Exports.ToImmutableDictionary(export => export.Id, StringComparer.Ordinal);
        _queries = Queries.ToImmutableDictionary(query => query.Id, StringComparer.Ordinal);
        _parameters = structure.Parameters.ToImmutableDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        _files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
    }
    /// <summary>The original immutable byte closure; its capabilities may still be pending.</summary>
    public EffectPackageContents Contents { get; }
    /// <summary>All validated package declarations in manifest order, including unused entries.</summary>
    public ImmutableArray<EffectMediaQueryDefinition> Queries { get; }
    /// <summary>Existing export metadata with fixed query references; this catalog cannot produce a Definition.</summary>
    public ImmutableArray<EffectExportDefinition> Exports { get; }
    /// <summary>Finds the exact export without choosing another kind or revision.</summary>
    /// <param name="exportId">The exact package-local identity.</param>
    /// <returns>The shared immutable export metadata.</returns>
    public EffectExportDefinition GetExport(string exportId) => _exports.TryGetValue(exportId, out var value) ? value
        : throw Error("PackageExportMissing", "exports." + exportId, "导出不存在。");
    /// <summary>Finds an exact declared query; an undeclared query is a contract error.</summary>
    /// <param name="queryId">The exact package-local identity.</param>
    /// <returns>The strict query declaration.</returns>
    public EffectMediaQueryDefinition GetQuery(string queryId) => _queries.TryGetValue(queryId, out var value) ? value
        : throw Error("PackageMediaQueryMissing", "mediaQueries." + queryId, "媒体查询不存在。");
    /// <summary>Resolves defaults and overrides using the same parser used by the executable package Definition.</summary>
    /// <param name="exportId">The exact export.</param>
    /// <param name="overrides">An optional complete JSON override object.</param>
    /// <param name="cancellationToken">Cancels strict reading.</param>
    /// <returns>The complete typed parameter values, with no implicit type conversion.</returns>
    public ImmutableDictionary<string, EffectParameterValue> ResolveParameters(string exportId, ReadOnlyMemory<byte> overrides = default,
        CancellationToken cancellationToken = default) => EffectPackageParameterParser.Resolve(GetExport(exportId), _parameters, _files, overrides, cancellationToken);
}

// Internal parser result only. No executable Definition is retained or exposed through the catalog.
internal sealed record EffectPackageStructure(ImmutableArray<EffectParameterDefinition> Parameters,
    ImmutableArray<EffectExportDefinition> Exports, ImmutableDictionary<string, ShaderProgramDefinition> Programs,
    ImmutableDictionary<string, EffectPackageImageGraph> Graphs, EffectPackageSvgResources SvgResources,
    ImmutableArray<EffectMediaQueryDefinition> Queries);
