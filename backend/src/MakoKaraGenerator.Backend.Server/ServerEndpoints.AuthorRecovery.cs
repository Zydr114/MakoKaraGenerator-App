using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureAuthorRecoveryEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/profile/author-drafts", ReadProfileAuthorDrafts);
        app.MapPost("/api/v1/profile/author-drafts/{id}/claim", ClaimProfileAuthorDraftAsync);
    }

    private static IResult ReadProfileAuthorDrafts(HttpRequest request, ProjectSessionRegistry registry)
    {
        foreach (var query in request.Query)
            if (query.Key is not ("limit" or "afterDraftId") || query.Value.Count != 1)
                return Error(new(BackendErrorCode.InvalidRequest, "查询参数无效或重复。", query.Key));
        int limit = 100;
        if (request.Query.TryGetValue("limit", out var value)
            && !int.TryParse(value.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit))
            return Error(new(BackendErrorCode.InvalidRequest, "limit必须在1到100之间。", "limit"));
        string? after = request.Query.TryGetValue("afterDraftId", out var cursor) ? cursor.ToString() : null;
        return Result(registry.ReadProfileAuthorDrafts(Client(request), limit, after));
    }

    private static async Task<IResult> ClaimProfileAuthorDraftAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        if (request.Query.Count != 0)
            return Error(new(BackendErrorCode.InvalidRequest, "认领请求不接受查询参数。", request.Query.Keys.First()));
        (AuthorDraftClaimRequest? input, IResult? error) = await ReadJsonAsync<AuthorDraftClaimRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.ClaimProfileAuthorDraftAsync(Client(request), id, input!, CancellationToken.None)));
    }
}
