using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

internal static class BackendExportFailureReportCodec
{
    internal static ExportFailureReportDto Project(RenderSessionFailureException failure, JobResponse job, int maxOccurrences)
    {
        RuntimeDiagnosticSnapshot snapshot = failure.DiagnosticSnapshot ?? throw new IOException("没有实际发生报告。");
        if (snapshot.Count <= 0 || snapshot.Count > maxOccurrences)
            throw new ExportTaskException("ExportFailureReportBudgetExceeded", "实际失败诊断数量超过完整报告预算。");
        BackendDiagnosticDto[] diagnostics = [.. snapshot.ReadAll().Select(item =>
        {
            var source = item.Diagnostic; var diagnostic = source.Diagnostic;
            return new BackendDiagnosticDto(diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message, null,
                new(item.Epoch, item.Sequence.ToString(CultureInfo.InvariantCulture), source.InstanceId.Value, source.ArtifactHash,
                    source.Stage, source.TimeUs.ToString(CultureInfo.InvariantCulture),
                    diagnostic.Location is null ? null : new(diagnostic.Location.Path, diagnostic.Location.Line, diagnostic.Location.Column),
                    source.PackageIdentity is null ? null : new(source.PackageIdentity.PackageId, source.PackageIdentity.Revision, source.PackageIdentity.ExportId)), "runtime");
        })];
        return new("exportFailure", failure.Code, job.Input!, job.ExportInput!, diagnostics);
    }

    internal static byte[] Encode(ExportFailureReportDto report, int maxBytes, int maxOccurrences)
    { Validate(report, maxOccurrences); return WorkerExportContract.EncodeJson(report, maxBytes); }
    internal static ExportFailureReportDto Decode(byte[] bytes, int maxBytes, int maxOccurrences)
    {
        ExportFailureReportDto report = WorkerExportContract.DecodeJson<ExportFailureReportDto>(bytes, maxBytes);
        Validate(report, maxOccurrences); return report;
    }
    private static void Validate(ExportFailureReportDto report, int maxOccurrences)
    {
        if (report.Kind != "exportFailure" || string.IsNullOrWhiteSpace(report.FailureCode) || report.Source is null || report.Input is null
            || report.Source.SourceKind != "project" || !ProjectSessionRegistry.ValidExportSourceProfile(report.Source.SourceProfile, report.Input)
            || report.Diagnostics is null || report.Diagnostics.Length <= 0 || report.Diagnostics.Length > maxOccurrences)
            throw new IOException("失败报告缺少实际来源或超出预算。");
        Dictionary<Guid, long> sequences = [];
        foreach (BackendDiagnosticDto value in report.Diagnostics)
        {
            BackendDiagnosticOccurrenceDto? source = value?.Occurrence;
            if (value is null || value.OriginKind != "runtime" || value.Severity is not ("info" or "warning" or "error")
                || string.IsNullOrWhiteSpace(value.Code) || string.IsNullOrWhiteSpace(value.Message) || source is null || source.Epoch == Guid.Empty
                || !Number(source.Sequence, out long sequence) || sequence < 1 || !Number(source.TimeUs, out _)
                || string.IsNullOrWhiteSpace(source.InstanceId) || source.ArtifactHash is null || string.IsNullOrWhiteSpace(source.Stage)
                || source.Location is { } location && (string.IsNullOrWhiteSpace(location.Path) || location.Line < 1 || location.Column < 1)
                || source.Package is { } package && (string.IsNullOrWhiteSpace(package.PackageId) || string.IsNullOrWhiteSpace(package.Revision) || string.IsNullOrWhiteSpace(package.ExportId)))
                throw new IOException("失败报告缺少真实完整发生来源。");
            if (sequence != sequences.GetValueOrDefault(source.Epoch) + 1) throw new IOException("实际发生顺序无效。");
            sequences[source.Epoch] = sequence;
        }
    }
    private static bool Number(string? text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value >= 0 && text == value.ToString(CultureInfo.InvariantCulture);
}
