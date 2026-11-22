using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureBasicMediaEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/project-sessions/{id}/resources", GetProjectResourcesAsync);
    }

    private static async Task<IResult> GetProjectResourcesAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Keys.Any(key => key is not ("expectedRevision" or "offset" or "limit"))
            || !TryReadWindow(request, out string? revision, out int offset, out int limit)) return InvalidWindow();
        return Result(await registry.ReadProjectResourcesAsync(Client(request), id, revision, offset, limit,
            request.HttpContext.RequestAborted).ConfigureAwait(false));
    }
}
