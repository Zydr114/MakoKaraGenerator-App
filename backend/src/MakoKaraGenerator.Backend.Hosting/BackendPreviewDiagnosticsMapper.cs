using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
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
        foreach (var i in image)
            values.Add(new(i.Code, "warning", i.Code == "BackgroundMediaUnavailable" ? "背景媒体不可用（未设置、文件丢失或该时刻无帧），图效果已旁路为原样。" : "图效果使用了已声明的降级行为。", null, OriginKind: "imageEffect",
                ImageEffect: new(i.InstanceId.Value, i.ArtifactHash, Resource(i.Graph.Identity), i.InputId, i.Code)));
        foreach (var m in material)
            values.Add(new(m.Code, "warning", m.Code == "BackgroundMediaUnavailable" ? "背景媒体不可用（未设置、文件丢失或该时刻无帧），材质已退回包内兜底外观。" : "材质使用了已声明的降级行为。", null, OriginKind: "material",
                Material: new(Owner(m.Owner), Resource(m.Material.Identity), m.InputId, m.Code)));
        BackendDiagnosticDto[] result = [.. values];
        if (JsonSerializer.SerializeToUtf8Bytes(result, PublicJsonOptions).Length > BackendPreviewDiagnosticsLimits.MaximumJsonBytes) throw Budget();
        return result;
    }
    private static BackendDiagnosticResourceDto Resource(ResourceIdentity value) => new(value.PackageId, value.Revision, value.ExportId);
    private static BackendDiagnosticMaterialOwnerDto Owner(PreparedMaterialOwner owner) => owner switch
    {
        PreparedMaterialOwner.Script s => new("script", Script: new(s.InstanceId.Value, s.ArtifactHash,
            s.PackageIdentity is null ? null : new(s.PackageIdentity.PackageId, s.PackageIdentity.Revision, s.PackageIdentity.ExportId))),
        PreparedMaterialOwner.Source s => new("source", Source: new(s.Reference.Path, s.Reference.Region, (int)s.Slot,
            new(s.Reference.Material.PackageId, s.Reference.Material.Revision, s.Reference.Material.ExportId), s.Reference.Material.Parameters.Json)),
        _ => throw new RenderSessionFailureException("PreviewDiagnosticsInvalid", "材质诊断缺少真实来源。"),
    };
    private static RenderSessionFailureException Budget() => new("PreviewDiagnosticsBudgetExceeded", "完整诊断超过预览预算。");
}
