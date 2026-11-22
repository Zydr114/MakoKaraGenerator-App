using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigurePersistenceEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/file-roots", () => Results.Json(new { rootGrantIds = ResolveRoots().Keys.Order(StringComparer.Ordinal).ToArray(), protocolVersion = BackendProtocol.Version }));
        app.MapPost("/api/v1/file-handles", FileHandleAsync);
        app.MapPost("/api/v1/project-sessions/open-jobs", OpenAsync);
        app.MapPost("/api/v1/project-sessions/recover-jobs", RecoverAsync);
        app.MapPost("/api/v1/project-sessions/{id}/save-jobs", SaveAsync);
        app.MapPost("/api/v1/project-sessions/{id}/close", CloseAsync);
        app.MapGet("/api/v1/jobs/{id}", ReadJob);
        app.MapPost("/api/v1/jobs/{id}/cancel", CancelAsync);
        app.MapGet("/api/v1/recovery-candidates", (HttpRequest request, ProjectSessionRegistry registry) =>
            Results.Json(new { candidates = registry.ReadRecoveryCandidates(Client(request)), protocolVersion = BackendProtocol.Version }));
    }
    private static async Task<IResult> FileHandleAsync(HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (FileHandleRequest? input, IResult? error) = await ReadJsonAsync<FileHandleRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.CreateFileHandleAsync(client, input!)), 201);
    }
    private static async Task<IResult> OpenAsync(HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (OpenProjectSessionRequest? input, IResult? error) = await ReadJsonAsync<OpenProjectSessionRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartOpenAsync(client, input!)), 202);
    }
    private static async Task<IResult> RecoverAsync(HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (RecoverProjectSessionRequest? input, IResult? error) = await ReadJsonAsync<RecoverProjectSessionRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartRecoverAsync(client, input!)), 202);
    }
    private static async Task<IResult> SaveAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (SaveProjectRequest? input, IResult? error) = await ReadJsonAsync<SaveProjectRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartSaveAsync(client, id, input!)), 202);
    }
    private static async Task<IResult> CloseAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (CloseProjectRequest? input, IResult? error) = await ReadJsonAsync<CloseProjectRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.StartCloseAsync(client, id, input!)), 202);
    }
    private static IResult ReadJob(string id, HttpRequest request, ProjectSessionRegistry registry) => Result(registry.ReadJob(Client(request), id));
    private static async Task<IResult> CancelAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (RequestMetadataDto? input, IResult? error) = await ReadJsonAsync<RequestMetadataDto>(request);
        if (error is not null) return error;
        string client = Client(request);
        // The Hosting registry owns the receipt. Keep the accepted cancellation alive
        // after a client disconnects just like every other mutating endpoint.
        return Result(await tracker.RunAsync(() => registry.CancelJobAsync(client, id, input, CancellationToken.None)));
    }
}
