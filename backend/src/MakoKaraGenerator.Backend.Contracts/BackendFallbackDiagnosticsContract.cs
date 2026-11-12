using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>
/// 导出与同帧测量的兜底诊断：图效果或材质缺背景时已按声明降级继续，结果里只列去重后的诊断。
/// 条数与公共 JSON 字节都有上限；导出超过上限时只标截断，不让导出失败。
/// </summary>
public static class BackendFallbackDiagnosticsContract
{
    /// <summary>去重后最多保留的诊断条数。</summary>
    public const int MaximumItems = 128;

    /// <summary>去重后诊断数组按 <see cref="JsonOptions"/> 序列化的最大字节数。</summary>
    public const int MaximumJsonBytes = 96 * 1024;

    /// <summary>计算字节预算用的公共 JSON 形状（省略 null）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>检查兜底诊断数组：非空引用、来源只能是图效果或材质、级别为 warning、不重复且不超预算。</summary>
    /// <param name="items">待检查的数组。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    public static bool IsValid(BackendDiagnosticDto[]? items)
    {
        if (items is null || items.Length > MaximumItems) return false;
        HashSet<BackendDiagnosticDto> seen = [];
        foreach (BackendDiagnosticDto item in items)
            if (item is null || item.OriginKind is not ("imageEffect" or "material") || string.IsNullOrWhiteSpace(item.Message)
                || !PreviewTransportValidation.ValidPackageDiagnostic(item) || !seen.Add(item))
                return false;
        return JsonSerializer.SerializeToUtf8Bytes(items, JsonOptions).Length <= MaximumJsonBytes;
    }
}
