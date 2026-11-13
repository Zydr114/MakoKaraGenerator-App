using System.Globalization;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>真实源材质执行失败；不具有脚本实例、编译代次或发生序号。</summary>
/// <param name="Owner">真实源对象与材质部位。</param><param name="Stage">实际执行阶段。</param>
/// <param name="TimeUs">规范十进制微秒时刻。</param><param name="Code">稳定失败编号。</param>
/// <param name="Budget">实际表面超限时的安全数值。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BackendSourceMaterialFailureDto(BackendDiagnosticSourceOwnerDto Owner, string Stage,
    string TimeUs, string Code, BackendImageSurfaceBudgetDto? Budget = null);

/// <summary>真实申请之前的中间表面预算数值；字节使用规范十进制字符串。</summary>
/// <param name="CurrentSurfaces">当前活动表面数。</param><param name="AdditionalSurfaces">申请表面数。</param>
/// <param name="MaximumSurfaces">固定表面上限。</param><param name="CurrentBytes">当前估算字节。</param>
/// <param name="AdditionalBytes">申请估算字节。</param><param name="MaximumBytes">固定字节上限。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BackendImageSurfaceBudgetDto(int CurrentSurfaces, int AdditionalSurfaces, int MaximumSurfaces,
    string CurrentBytes, string AdditionalBytes, string MaximumBytes);

/// <summary>源材质失败的字段和完整字节预算；不允许截断来源。</summary>
public static class BackendSourceMaterialFailureContract
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    /// <summary>单条完整源失败诊断的 UTF8 JSON 上限。</summary>
    public const int MaximumJsonBytes = 64 * 1024;

    /// <summary>校验安全数值及真实源字段；固定工程归属由宿主另外核对。</summary>
    /// <param name="value">完整源材质失败。</param>
    public static void Validate(BackendSourceMaterialFailureDto value)
    {
        if (value?.Owner is not { } owner || string.IsNullOrWhiteSpace(value.Stage) || !Number(value.TimeUs)
            || string.IsNullOrWhiteSpace(value.Code) || string.IsNullOrWhiteSpace(owner.Path)
            || string.IsNullOrWhiteSpace(owner.Region) || owner.Slot < 0 || owner.SourceMaterial is not { } material
            || string.IsNullOrWhiteSpace(material.PackageId) || string.IsNullOrWhiteSpace(material.ExportId)
            || material.Revision is not { Length: 71 } revision || !revision.StartsWith("sha256:", StringComparison.Ordinal)
            || revision.AsSpan(7).ContainsAnyExcept(LowerHex) || owner.ParametersJson is null)
            throw Invalid();
        if (value.Budget is { } budget && (value.Code != "ImageSurfaceBudgetExceeded" || budget.CurrentSurfaces < 0
            || budget.AdditionalSurfaces < 0 || budget.MaximumSurfaces <= 0 || !Number(budget.CurrentBytes)
            || !Number(budget.AdditionalBytes) || !Number(budget.MaximumBytes) || budget.MaximumBytes == "0"))
            throw Invalid();
        if (value.Budget is { } actual && (long)actual.CurrentSurfaces + actual.AdditionalSurfaces <= actual.MaximumSurfaces
            && (Int128)long.Parse(actual.CurrentBytes, CultureInfo.InvariantCulture) + long.Parse(actual.AdditionalBytes, CultureInfo.InvariantCulture)
                <= long.Parse(actual.MaximumBytes, CultureInfo.InvariantCulture)) throw Invalid();
        if (JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions).Length > MaximumJsonBytes)
            throw new WorkerProtocolException("完整源材质失败诊断超过导出报告预算。", "WorkerExportReportBudgetExceeded");
    }

    private static bool Number(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number)
        && number >= 0 && value == number.ToString(CultureInfo.InvariantCulture);
    private static WorkerProtocolException Invalid() => new("源材质失败诊断字段无效。", "WorkerExportProtocolInvalid");
}
