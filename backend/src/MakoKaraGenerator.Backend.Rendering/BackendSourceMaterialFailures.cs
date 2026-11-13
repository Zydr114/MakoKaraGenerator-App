using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>源材质失败的安全投影及固定工程核对；不创建脚本发生身份。</summary>
public static class BackendSourceMaterialFailures
{
    /// <summary>只编码真实来源、阶段、时刻、错误码与表面预算。</summary>
    /// <param name="value">实际源材质失败。</param><returns>完整安全传输值。</returns>
    public static BackendSourceMaterialFailureDto Encode(RenderSourceMaterialFailure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var source = value.Owner.Reference; var material = source.Material;
        var result = new BackendSourceMaterialFailureDto(new(source.Path, source.Region, (int)value.Owner.Slot,
            new(material.PackageId, material.Revision, material.ExportId), material.Parameters.Json), value.Stage,
            Text(value.TimeUs), value.Code, value.Budget is not { } budget ? null : new(budget.CurrentSurfaces,
                budget.AdditionalSurfaces, budget.MaximumSurfaces, Text(budget.CurrentBytes), Text(budget.AdditionalBytes), Text(budget.MaximumBytes)));
        BackendSourceMaterialFailureContract.Validate(result);
        return result;
    }

    /// <summary>对照本次固定工程恢复源身份；拒绝伪造路径、参数、包、部位和时刻。</summary>
    /// <param name="value">完整安全传输值。</param><param name="snapshot">任务固定工程。</param>
    /// <returns>属于固定工程的源失败。</returns>
    public static RenderSourceMaterialFailure Decode(BackendSourceMaterialFailureDto value, ProjectSnapshot snapshot)
    {
        BackendSourceMaterialFailureContract.Validate(value);
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var owner = value.Owner; var material = owner.SourceMaterial;
            var parameters = EffectParameterOverrides.Parse(owner.ParametersJson);
            var reference = new SourceMaterialReference(owner.Path, owner.Region,
                new(material.PackageId, material.Revision, material.ExportId, parameters));
            var slot = (PreparedMaterialSlot)owner.Slot;
            long timeUs = long.Parse(value.TimeUs, CultureInfo.InvariantCulture);
            if (parameters.Json != owner.ParametersJson || !Enum.IsDefined(slot) || !PreparedMaterialSlots.Accepts(reference, slot)
                || !SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications).Contains(reference)
                || snapshot.Composition.OutputDurationUs is { } duration && timeUs >= duration)
                throw Invalid();
            var package = snapshot.Effects.Packages.FirstOrDefault(p => p.PackageId == material.PackageId && p.Revision == material.Revision)
                ?? throw Invalid();
            SourceMaterialCatalog.Validate(reference, EffectPackageDefinition.Read(package));
            return new(new(reference, slot), value.Stage, timeUs, value.Code, value.Budget is not { } budget ? null
                : new(budget.CurrentSurfaces, budget.AdditionalSurfaces, budget.MaximumSurfaces,
                    long.Parse(budget.CurrentBytes, CultureInfo.InvariantCulture), long.Parse(budget.AdditionalBytes, CultureInfo.InvariantCulture),
                    long.Parse(budget.MaximumBytes, CultureInfo.InvariantCulture)));
        }
        catch (Exception failure) when (failure is ArgumentException or EffectPackageException or System.Text.Json.JsonException)
        { throw Invalid(); }
    }

    /// <summary>公开安全说明和真实结构值；不使用内部异常消息或堆栈。</summary>
    /// <param name="value">已核对的完整源失败。</param><returns>来源为 sourceMaterialFailure 的错误诊断。</returns>
    public static BackendDiagnosticDto Project(RenderSourceMaterialFailure value) => new(value.Code, "error",
        value.Code == "ImageSurfaceBudgetExceeded" ? "源材质执行超过中间表面预算，导出已停止。" : "源材质执行失败，导出已停止。",
        null, OriginKind: "sourceMaterialFailure", SourceMaterialFailure: Encode(value));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static WorkerProtocolException Invalid() => new("源材质失败身份不属于固定导出工程。", "WorkerExportProtocolInvalid");
}
