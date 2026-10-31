using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>验证报告保留原格式与身份，文件IO复用任务sidecar基础。</summary>
internal sealed class BackendValidationReportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BackendValidationOptions _options;
    private readonly BackendJobSidecarStore _files;
    internal BackendValidationReportStore(string stateRoot, BackendValidationOptions options)
    {
        _options = options;
        _files = new(Path.Combine(stateRoot, "validation-reports"), "report-", "ValidationReport", options.MaxReportBytes, options.MaxReportStorageBytes);
    }

    internal byte[] Encode(ValidationReportDto report, int framesPerOutput)
    {
        if (report?.Issues is null || report.Diagnostics is null || report.Issues.Length > _options.MaxIssues
            || report.Diagnostics.Length > _options.MaxOccurrences || report.OpaqueFrames < 0 || report.TransparentFrames < 0
            || report.OpaqueFrames > framesPerOutput || report.TransparentFrames > framesPerOutput
            || report.Issues.Any(value => value is null || string.IsNullOrWhiteSpace(value.Code))
            || report.Diagnostics.Any(value => value?.Diagnostic is null))
            throw new ValidationTaskException("ValidationReportInvalid", "验证结果不符合固定报告合同。");
        bool errors = report.Issues.Any(value => value.IsError) || report.Diagnostics.Any(value => value.Diagnostic.Severity == "error");
        if (report.Succeeded && (errors || report.OpaqueFrames != framesPerOutput || report.TransparentFrames != framesPerOutput)
            || !report.Succeeded && !errors)
            throw new ValidationTaskException("ValidationReportInvalid", "验证成功标志与真实帧计数或诊断不一致。");
        byte[] bytes;
        try { bytes = WorkerValidationContract.EncodeReport(report, framesPerOutput); }
        catch (WorkerProtocolException) { throw new ValidationTaskException("ValidationReportInvalid", "完整报告不符合有界合同。"); }
        if (bytes.Length > _options.MaxReportBytes)
            throw new ValidationTaskException("ValidationReportBudgetExceeded", "完整报告超过字节预算，未截断。");
        return bytes;
    }

    internal BackendJobReportReferenceDto Publish(string jobId, byte[] bytes)
    {
        try { return _files.Publish(jobId, bytes); }
        catch (JobSidecarException error) { throw new ValidationTaskException(error.Code, error.Message); }
    }
    internal byte[] Read(string jobId, BackendJobReportReferenceDto reference) => _files.Read(jobId, reference);
    internal static ValidationReportDto Decode(byte[] bytes) => JsonSerializer.Deserialize<ValidationReportDto>(bytes, Json)
        ?? throw new IOException("任务报告无效。");

    internal void Prune(IEnumerable<string> retainedReportIds) => _files.Prune(retainedReportIds);
}

internal sealed class ValidationTaskException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
