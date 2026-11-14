namespace MakoKaraGenerator.Scripting;

/// <summary>作者诊断严重度；Error 使阶段失败，其他级别仅记录。</summary>
public enum DiagnosticSeverity
{
    /// <summary>说明。</summary>
    Info,
    /// <summary>警告。</summary>
    Warning,
    /// <summary>求值失败，禁止发布帧和继续导出。</summary>
    Error,
}

/// <summary>包内逻辑源码位置，不依赖 Roslyn 或本机文件路径。</summary>
public sealed record SourceLocation
{
    /// <summary>创建一基行列位置。</summary>
    public SourceLocation(string path, int line, int column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1); ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        Path = path; Line = line; Column = column;
    }
    /// <summary>逻辑源码名称。</summary>
    public string Path { get; }
    /// <summary>一基行。</summary>
    public int Line { get; }
    /// <summary>一基列。</summary>
    public int Column { get; }
}

/// <summary>不可变作者诊断；宿主追加运行身份。</summary>
public sealed record EffectDiagnostic
{
    /// <summary>创建有效诊断，不静默修正代码或位置。</summary>
    public EffectDiagnostic(DiagnosticSeverity severity, string code, string message, SourceLocation? location = null)
    {
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Severity = severity; Code = code; Message = message; Location = location;
    }
    /// <summary>严重度。</summary>
    public DiagnosticSeverity Severity { get; }
    /// <summary>作者稳定代码。</summary>
    public string Code { get; }
    /// <summary>说明。</summary>
    public string Message { get; }
    /// <summary>可选逻辑位置。</summary>
    public SourceLocation? Location { get; }
}

/// <summary>宿主附加身份的只读运行诊断。</summary>
/// <param name="InstanceId">实例。</param><param name="ArtifactHash">固定产物。</param>
/// <param name="Stage">阶段。</param><param name="TimeUs">整数微秒时刻。</param><param name="Diagnostic">作者报告。</param>
public sealed record RuntimeDiagnostic(EffectInstanceId InstanceId, string ArtifactHash, string Stage, long TimeUs, EffectDiagnostic Diagnostic)
{
    /// <summary>宿主固定的真实包来源；受控独立源码没有包时为 null。</summary>
    public EffectPackageIdentity? PackageIdentity { get; init; }
}
