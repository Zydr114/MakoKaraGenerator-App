using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static readonly string[] EnabledValidationCapabilities = ["project-sessions.validation-jobs", "jobs.validation-result", "jobs.validation-diagnostics", "recovery-jobs.validation-result"];

    private static void ConfigureValidationEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/validation-capabilities", (ProjectSessionRegistry registry) => Results.Json(registry.ValidationCapabilities));
        app.MapPost("/api/v1/project-sessions/{id}/validation-jobs", ValidationAsync);
        app.MapGet("/api/v1/jobs/{id}/validation-result", (string id, HttpRequest request, ProjectSessionRegistry registry) =>
            ValidationReportResult(registry.ReadValidationResult(Client(request), id)));
        app.MapGet("/api/v1/jobs/{id}/validation-diagnostics", ValidationDiagnostics);
        app.MapGet("/api/v1/recovery-jobs/{id}/validation-result", (string id, HttpRequest request, ProjectSessionRegistry registry) =>
            ValidationReportResult(registry.ReadRecoveredValidationResult(Client(request), id)));
    }

    private static async Task<IResult> ValidationAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (ValidationJobRequest? input, IResult? error) = await ReadJsonAsync<ValidationJobRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartValidationAsync(client, id, input!, CancellationToken.None)), 202);
    }

    private static IResult ValidationReportResult(BackendOperationResult<byte[]> report) => report.Accepted
        ? Results.Bytes(report.Response!, "application/json") : Error(report.Error!);

    private static IResult ValidationDiagnostics(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TryReadDiagnosticsPage(request, out int? limit, out IResult? error)) return error!;
        return Result(registry.ReadValidationDiagnostics(Client(request), id, request.Query["cursor"].FirstOrDefault(), limit));
    }

    private static bool TryReadDiagnosticsPage(HttpRequest request, out int? limit, out IResult? error)
    {
        limit = null; error = null;
        if (request.Query["cursor"].Count > 1 || request.Query["limit"].Count > 1)
        { error = Error(new(BackendErrorCode.InvalidRequest, "分页参数只能出现一次。")); return false; }
        if (request.Query.TryGetValue("limit", out var text))
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            { error = Error(new(BackendErrorCode.InvalidRequest, "诊断页大小必须为整数。", "limit")); return false; }
            limit = value;
        }
        return true;
    }
}
