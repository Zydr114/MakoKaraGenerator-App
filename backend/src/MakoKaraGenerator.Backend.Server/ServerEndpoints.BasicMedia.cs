using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureBasicMediaEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/project-sessions/{id}/resources", GetProjectResourcesAsync);
        app.MapPost("/api/v1/project-sessions/{id}/resources/package-asset", GetPackageResourceAssetAsync);
    }

    private static async Task<IResult> GetProjectResourcesAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Keys.Any(key => key is not ("expectedRevision" or "offset" or "limit"))
            || !TryReadWindow(request, out string? revision, out int offset, out int limit)) return InvalidWindow();
        return Result(await registry.ReadProjectResourcesAsync(Client(request), id, revision, offset, limit,
            request.HttpContext.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> GetPackageResourceAssetAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (ProjectPackageResourceAssetRequest? input, IResult? error) = await ReadJsonAsync<ProjectPackageResourceAssetRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => registry.MaterializePackageResourceAsync(Client(request), id, input!,
            request.HttpContext.RequestAborted)), 201);
    }
}
