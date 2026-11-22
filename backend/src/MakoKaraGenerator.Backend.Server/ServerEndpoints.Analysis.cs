using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static readonly string[] EnabledAnalysisCapabilities = ["project-sessions.analysis-jobs", "jobs.analysis-result"];
    private static void ConfigureAnalysisEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/analysis-capabilities", (ProjectSessionRegistry registry) => Results.Json(registry.AnalysisCapabilities));
        app.MapPost("/api/v1/project-sessions/{id}/analysis-jobs", AnalysisAsync);
        app.MapMethods("/api/v1/jobs/{id}/analysis-result", ["GET", "HEAD"],
            (string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry) => ReadAnalysisAsync(id, request, response, registry, false));
    }
    private static async Task<IResult> AnalysisAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AnalysisJobRequest? input, IResult? error) = await ReadJsonAsync<AnalysisJobRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartAnalysisAsync(client, id, input!, CancellationToken.None)), 202);
    }
    private static async Task ReadAnalysisAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry, bool recovered)
    {
        BackendOperationResult<BackendAnalysisReadLease> acquired = registry.AcquireAnalysisRead(Client(request), id, recovered);
        if (!acquired.Accepted) { await WriteError(request.HttpContext, acquired.Error!, StatusCode(acquired.Error!.Code)); return; }
        using BackendAnalysisReadLease lease = acquired.Response!;
        response.Headers.ContentDisposition = "attachment; filename=\"MakoKara-analysis-" + id[4..] + ".json\"";
        await ReadImmutableContentAsync(request, response, lease.Stream, lease.Reference.SizeBytes, "application/json", lease.Reference.Sha256, "Content-Disposition");
    }
}
