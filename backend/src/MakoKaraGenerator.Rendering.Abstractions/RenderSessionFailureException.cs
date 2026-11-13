namespace MakoKaraGenerator.Rendering;

/// <summary>渲染会话的中立具名失败；只携带实际不可变诊断，禁止以占位像素代替失败。</summary>
public sealed class RenderSessionFailureException : Exception
{
    /// <summary>建立具名失败并保留实际发生快照与内部原始异常。</summary>
    /// <remarks>没有实际完整快照时必须传 null；HTTP 边界另行脱敏，不展示内部异常栈或路径。</remarks>
    public RenderSessionFailureException(string code, string message, RuntimeDiagnosticSnapshot? diagnosticSnapshot = null,
        Exception? innerException = null, System.Collections.Immutable.ImmutableArray<RenderSourceDiagnostic> sourceDiagnostics = default) : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code; DiagnosticSnapshot = diagnosticSnapshot; SourceDiagnostics = sourceDiagnostics.IsDefault ? [] : sourceDiagnostics;
    }
    /// <summary>非空稳定失败编号。</summary>
    public string Code { get; }
    /// <summary>真实完整编译来源诊断，不构造Runtime occurrence。</summary>
    public System.Collections.Immutable.ImmutableArray<RenderSourceDiagnostic> SourceDiagnostics { get; }
    /// <summary>失败同帧实际图效果旁报；不伪造 runtime occurrence。</summary>
    public System.Collections.Immutable.ImmutableArray<RenderImageEffectDiagnostic> ImageEffectDiagnostics { get; init; } = [];
    /// <summary>失败同帧实际材质旁报及完整 owner。</summary>
    public System.Collections.Immutable.ImmutableArray<RenderMaterialDiagnostic> MaterialDiagnostics { get; init; } = [];
    /// <summary>实际源材质失败及可用预算数值；不制造脚本运行时发生。</summary>
    public RenderSourceMaterialFailure? SourceMaterialFailure { get; init; }
    /// <summary>实际 Runtime 完整不可变发生快照；预算或畸形输入无实际快照时为 null。</summary>
    public RuntimeDiagnosticSnapshot? DiagnosticSnapshot { get; }
}
