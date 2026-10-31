using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureReadModelEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/project-sessions/{id}/lyrics/branches", GetLyricBranchesAsync);
        app.MapGet("/api/v1/project-sessions/{id}/lyrics/window", GetLyricsWindowAsync);
        app.MapGet("/api/v1/project-sessions/{id}/settings", GetSettingsAsync);
        app.MapGet("/api/v1/project-sessions/{id}/style", GetStyleAsync);
        app.MapGet("/api/v1/project-sessions/{id}/orchestration", GetOrchestrationAsync);
        app.MapGet("/api/v1/project-sessions/{id}/orchestration/line-presentation", GetLinePresentationAsync);
        app.MapGet("/api/v1/project-sessions/{id}/orchestration/line-local-transform", GetLineLocalTransformAsync);
        app.MapGet("/api/v1/project-sessions/{id}/orchestration/geometry", GetGeometryReferenceAsync);
        app.MapGet("/api/v1/project-sessions/{id}/preset-applications", GetPresetApplicationsAsync);
    }

    private static async Task<IResult> GetLyricBranchesAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TryReadWindow(request, out string? revision, out int offset, out int limit)) return InvalidWindow();
        return Result(await registry.ReadLyricBranchesAsync(Client(request), id, revision, offset, limit, request.HttpContext.RequestAborted));
    }

    private static async Task<IResult> GetLyricsWindowAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TryReadWindow(request, out string? revision, out int offset, out int limit)
            || request.Query["branchId"].Count != 1) return InvalidWindow();
        BackendOperationResult<ProjectLyricsWindowResponse> result = await registry.ReadLyricsWindowAsync(
            Client(request), id, request.Query["branchId"][0], revision, offset, limit, request.HttpContext.RequestAborted);
        return result.Error is { Code: BackendErrorCode.CommandRejected } ? Error(result.Error, 404) : Result(result);
    }

    private static async Task<IResult> GetSettingsAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query["expectedRevision"].Count != 1) return InvalidWindow();
        return Result(await registry.ReadSettingsAsync(Client(request), id, request.Query["expectedRevision"][0], request.HttpContext.RequestAborted));
    }

    private static async Task<IResult> GetPresetApplicationsAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query["expectedRevision"].Count != 1 || request.Query.Keys.Any(key => key != "expectedRevision"))
            return InvalidWindow();
        return Result(await registry.ReadPresetApplicationsAsync(Client(request), id,
            request.Query["expectedRevision"][0], request.HttpContext.RequestAborted));
    }

    private static bool TryReadWindow(HttpRequest request, out string? revision, out int offset, out int limit)
    {
        revision = request.Query["expectedRevision"].FirstOrDefault(); offset = 0; limit = 100;
        if (request.Query["expectedRevision"].Count != 1) return false;
        return TryReadPageNumber(request, "offset", 0, out offset) && TryReadPageNumber(request, "limit", 100, out limit);
    }

    private static bool TryReadPageNumber(HttpRequest request, string name, int fallback, out int value)
    {
        value = fallback;
        return !request.Query.ContainsKey(name) || (request.Query[name].Count == 1
            && int.TryParse(request.Query[name][0], NumberStyles.None, CultureInfo.InvariantCulture, out value));
    }

    private static IResult InvalidWindow() => Error(new(BackendErrorCode.InvalidRequest,
        "必须提供唯一 expectedRevision；offset 与 limit 必须是唯一非负十进制整数。", "query"));
    private static async Task<IResult> GetOrchestrationAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query["expectedRevision"].Count != 1 || request.Query.Keys.Any(key => key != "expectedRevision")) return InvalidWindow();
        return Result(await registry.ReadOrchestrationAsync(Client(request), id, request.Query["expectedRevision"][0], request.HttpContext.RequestAborted));
    }
}
