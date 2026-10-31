namespace MakoKaraGenerator.Core.Diagnostics;

/// <summary>
/// 诊断列表的写入辅助，供校验器与读取层共用，避免两处各写一份组装代码。
/// </summary>
internal static class DiagnosticList
{
    internal static void Add(
        List<ProjectIssue> issues,
        DiagnosticCode code,
        IssueSeverity severity,
        string message,
        string? location = null)
    {
        issues.Add(new ProjectIssue
        {
            Code = code,
            Severity = severity,
            Message = message,
            Location = location,
        });
    }
}
