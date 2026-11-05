using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private sealed record EffectPackageInspectionProjection(JsonElement Manifest, bool StructureValid,
        EffectPackageParameterSchemaDto[] Parameters, EffectPackageExportSchemaDto[] Exports, string[] BindingKinds,
        EffectPackageInputsDto[] Inputs, EffectPackageFileSummaryDto[] Files, EffectPackageStructureDiagnosticDto[] Diagnostics);

    // Reference-free declaration projection. No compiler, native SVG or asset publication.
    private static EffectPackageInspectionProjection ProjectEffectPackageDeclarations(EffectPackageContents package,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(package.Manifest.AsMemory());
        var manifest = document.RootElement;
        EffectPackageDefinition? definition = null;
        EffectPackageStructureDiagnosticDto[] diagnostics = [];
        try { definition = EffectPackageDefinition.Read(package, cancellationToken); }
        catch (EffectPackageException error) { diagnostics = [new(error.Code, error.Path, error.Message)]; }
        // Only validated declarations become typed UI schemas; malformed input remains available in Manifest.
        EffectPackageParameterSchemaDto[] parameters = definition is null ? [] : [.. manifest.GetProperty("parameters").EnumerateArray().Select(value =>
            new EffectPackageParameterSchemaDto(value.GetProperty("id").GetString()!, value.GetProperty("type").GetString()!,
                value.GetProperty("label").GetString()!, value.TryGetProperty("unit", out var unit) ? unit.GetString() : null,
                value.GetProperty("default").GetRawText(), value.TryGetProperty("min", out var min) ? min.GetRawText() : null,
                value.TryGetProperty("max", out var max) ? max.GetRawText() : null,
                value.TryGetProperty("values", out var options) ? [.. options.EnumerateArray().Select(option =>
                    new EffectPackageParameterOptionDto(option.GetProperty("value").GetString()!, option.GetProperty("label").GetString()!))] : []))];
        EffectPackageExportSchemaDto[] exports = definition is null ? [] : [.. manifest.GetProperty("exports").EnumerateArray().Select(value =>
            new EffectPackageExportSchemaDto(value.GetProperty("id").GetString()!, value.GetProperty("kind").GetString()!,
                value.GetProperty("name").GetString()!, [.. value.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()!)],
                value.TryGetProperty("bindingKinds", out var kinds) ? [.. kinds.EnumerateArray().Select(item => item.GetString()!)] : [], value.Clone()))];
        List<EffectPackageInputsDto> inputs = [];
        if (definition is not null) foreach (var file in package.Files.Where(value => value.Role == "graph"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The shared semantic reader accepts a UTF-8 BOM; this read-only projection must accept the same files.
            ReadOnlyMemory<byte> content = file.Content.AsMemory();
            if (content.Span.StartsWith("\uFEFF"u8)) content = content[3..];
            using var graph = JsonDocument.Parse(content);
            if (graph.RootElement.TryGetProperty("inputs", out var declarations)) inputs.Add(new(file.Path, declarations.Clone()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(manifest.Clone(), definition is not null, parameters, exports,
            [.. exports.SelectMany(value => value.BindingKinds).Distinct(StringComparer.Ordinal)], [.. inputs],
            [.. package.Files.Select(file => new EffectPackageFileSummaryDto(file.Path, file.Role, file.Content.Length, file.Sha256))], diagnostics);
    }
}
