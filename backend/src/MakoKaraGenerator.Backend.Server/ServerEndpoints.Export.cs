using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static readonly string[] EnabledExportCapabilities = ["project-sessions.export-jobs", "jobs.export-result", "jobs.export-content", "jobs.export-diagnostics"];
    private static void ConfigureExportEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/export-capabilities", (ProjectSessionRegistry registry) => Results.Json(registry.ExportCapabilities));
        app.MapPost("/api/v1/project-sessions/{id}/export-jobs", ExportAsync);
        app.MapGet("/api/v1/jobs/{id}/export-result", (string id, HttpRequest request, ProjectSessionRegistry registry) =>
            ValidationReportResult(registry.ReadExportResult(Client(request), id)));
        app.MapMethods("/api/v1/jobs/{id}/export-content", ["GET", "HEAD"],
            (string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry) => ReadExportAsync(id, request, response, registry, false));
        app.MapGet("/api/v1/jobs/{id}/export-diagnostics", ExportDiagnostics);
    }
    private static async Task<IResult> ExportAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (ExportJobRequest? input, IResult? error) = await ReadJsonAsync<ExportJobRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartExportAsync(client, id, input!, CancellationToken.None)), 202);
    }
    private static IResult ExportDiagnostics(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TryReadDiagnosticsPage(request, out int? limit, out IResult? error)) return error!;
        return Result(registry.ReadExportDiagnostics(Client(request), id, request.Query["cursor"].FirstOrDefault(), limit));
    }
    private static async Task ReadExportAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry, bool recovered)
    {
        BackendOperationResult<BackendExportReadLease> acquired = registry.AcquireExportRead(Client(request), id, recovered);
        if (!acquired.Accepted) { await WriteError(request.HttpContext, acquired.Error!, StatusCode(acquired.Error!.Code)); return; }
        using BackendExportReadLease lease = acquired.Response!;
        if (!await lease.VerifyHashAsync(request.HttpContext.RequestAborted))
        { await WriteError(request.HttpContext, new(BackendErrorCode.TaskExecutionFailed, "已登记成片完整性校验失败。"), 500); return; }
        string extension = lease.Artifact.ContentType == "video/mp4" ? ".mp4" : ".mov";
        response.Headers.ContentDisposition = "attachment; filename=\"MakoKara-" + id[4..] + extension + "\"";
        await ReadImmutableContentAsync(request, response, lease.Stream, lease.Artifact.SizeBytes, lease.Artifact.ContentType, lease.Artifact.Sha256, "Content-Disposition");
    }
}
