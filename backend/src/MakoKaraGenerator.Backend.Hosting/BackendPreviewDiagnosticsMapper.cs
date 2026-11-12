using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>公共诊断完整投影；每种来源保留真实身份，不制造运行时发生。</summary>
internal static class BackendPreviewDiagnosticsMapper
{
    internal static readonly JsonSerializerOptions PublicJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    internal static BackendDiagnosticDto[] Project(RuntimeDiagnosticSnapshot? runtime,
        ImmutableArray<RenderSourceDiagnostic> source, ImmutableArray<RenderImageEffectDiagnostic> image,
        ImmutableArray<RenderMaterialDiagnostic> material, PreviewLimitsDto limits, int maxPages)
    {
        source = source.IsDefault ? [] : source; image = image.IsDefault ? [] : image; material = material.IsDefault ? [] : material;
        int runtimeCount = runtime?.Count ?? 0;
        long count = (long)runtimeCount + source.Length + image.Length + material.Length;
        if (runtimeCount > BackendPreviewDiagnosticsLimits.MaximumItemsPerKind || source.Length > BackendPreviewDiagnosticsLimits.MaximumItemsPerKind || image.Length > BackendPreviewDiagnosticsLimits.MaximumItemsPerKind || material.Length > BackendPreviewDiagnosticsLimits.MaximumItemsPerKind
            || (long)source.Length + image.Length + material.Length > BackendPreviewDiagnosticsLimits.MaximumPackageItems || count > BackendPreviewDiagnosticsLimits.MaximumItems
            || count > (long)Math.Min(maxPages, 32) * limits.MaxDiagnosticsPageSize) throw Budget();
        List<BackendDiagnosticDto> values = [];
        if (runtime is not null)
            foreach (var item in runtime.ReadAll())
            {
                var r = item.Diagnostic; var d = r.Diagnostic;
                values.Add(new(d.Code, d.Severity.ToString().ToLowerInvariant(), d.Message, null,
                    new(item.Epoch, item.Sequence.ToString(CultureInfo.InvariantCulture), r.InstanceId.Value, r.ArtifactHash,
                        r.Stage, r.TimeUs.ToString(CultureInfo.InvariantCulture),
                        d.Location is null ? null : new(d.Location.Path, d.Location.Line, d.Location.Column),
                        r.PackageIdentity is null ? null : new(r.PackageIdentity.PackageId, r.PackageIdentity.Revision, r.PackageIdentity.ExportId)), "runtime"));
            }
        foreach (var s in source)
            values.Add(new(s.Code, s.IsError ? "error" : "warning", s.Message, null, OriginKind: "compileSource",
                Source: new(s.PackageId, s.Revision, s.InstanceId, s.ExportId, s.Code, s.Message, s.IsError, s.Path, s.Line, s.Column)));
        foreach (var i in image) values.Add(BackendFallbackDiagnostics.Map(i));
        foreach (var m in material) values.Add(BackendFallbackDiagnostics.Map(m));
        BackendDiagnosticDto[] result = [.. values];
        if (JsonSerializer.SerializeToUtf8Bytes(result, PublicJsonOptions).Length > BackendPreviewDiagnosticsLimits.MaximumJsonBytes) throw Budget();
        return result;
    }
    private static RenderSessionFailureException Budget() => new("PreviewDiagnosticsBudgetExceeded", "完整诊断超过预览预算。");
}
