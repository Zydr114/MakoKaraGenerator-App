using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>效果执行失败；与取消、背景绕过和普通占位帧错误分开传播。</summary>
public sealed class EffectFailureException : Exception
{
    /// <summary>构造实例、输入代次和调用阶段明确的失败。</summary>
    public EffectFailureException(EffectInstanceId instanceId, string artifactHash, string stage,
        long timeUs, string code, Exception innerException, EffectPackageIdentity? packageIdentity = null)
        : base("效果 " + instanceId.Value + (packageIdentity is null ? "" :
            $"（包 {packageIdentity.PackageId}，修订 {packageIdentity.Revision}，导出 {packageIdentity.ExportId}）")
            + " 在 " + stage + " 失败：" + innerException.Message, innerException)
    { InstanceId = instanceId; ArtifactHash = artifactHash; Stage = stage; TimeUs = timeUs; Code = code; PackageIdentity = packageIdentity; }
    /// <summary>固定实例。</summary>
    public EffectInstanceId InstanceId { get; }
    /// <summary>编译输入代次。</summary>
    public string ArtifactHash { get; }
    /// <summary>调用阶段。</summary>
    public string Stage { get; }
    /// <summary>失败时刻，初始化为零。</summary>
    public long TimeUs { get; }
    /// <summary>稳定诊断代码。</summary>
    public string Code { get; }
    /// <summary>作者报告 Error 时保留其位置和说明；普通执行异常为 null。</summary>
    public RuntimeDiagnostic? Diagnostic { get; init; }
    /// <summary>报告Error的失败阶段截至当时的完整稳定记录；无需物化兼容数组。</summary>
    public RuntimeDiagnosticSnapshot? DiagnosticSnapshot { get; init; }
    /// <summary>真实控制器包／修订／导出；独立源码接缝没有包时为 null。</summary>
    public EffectPackageIdentity? PackageIdentity { get; }
}
