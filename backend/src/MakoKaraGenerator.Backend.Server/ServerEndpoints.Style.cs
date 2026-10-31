using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static async Task<IResult> GetStyleAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query["expectedRevision"].Count != 1 || request.Query["kind"].Count != 1) return InvalidStyleQuery();
        string? kind = request.Query["kind"][0];
        string[] allowed = kind switch
        {
            "project" => ["expectedRevision", "kind"],
            "branch" => ["expectedRevision", "kind", "branchId"],
            "line" => ["expectedRevision", "kind", "branchId", "lineId"],
            "token" => ["expectedRevision", "kind", "branchId", "lineId", "tokenId"],
            _ => [],
        };
        if (allowed.Length == 0 || request.Query.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))
            || allowed.Any(key => request.Query[key].Count != 1)) return InvalidStyleQuery();
        StyleTargetDto target = kind switch
        {
            "project" => new ProjectStyleTargetDto(),
            "branch" => new BranchStyleTargetDto(request.Query["branchId"][0]!),
            "line" => new LineStyleTargetDto(request.Query["branchId"][0]!, request.Query["lineId"][0]!),
            _ => new TokenStyleTargetDto(request.Query["branchId"][0]!, request.Query["lineId"][0]!, request.Query["tokenId"][0]!),
        };
        var result = await registry.ReadStyleAsync(Client(request), id, request.Query["expectedRevision"][0], target,
            request.HttpContext.RequestAborted);
        return result.Error is { Code: BackendErrorCode.CommandRejected, FailureCode: "TargetStale" } ? Error(result.Error, 404) : Result(result);
    }
    private static IResult InvalidStyleQuery() => Error(new(BackendErrorCode.InvalidRequest,
        "style 需要唯一 expectedRevision、kind 与该 kind 完整且唯一的稳定标识；不接受其他查询字段。", "query"));
}
