using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static async Task<IResult> GetResolvedStyleAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        string[] required = ["expectedRevision", "branchId", "lineId"];
        string[] allowed = [.. required, "tokenId", "clipId", "nodeId"];
        if (required.Any(key => request.Query[key].Count != 1) || request.Query.Any(field =>
            !allowed.Contains(field.Key, StringComparer.Ordinal) || field.Value.Count != 1))
            return Error(new(BackendErrorCode.InvalidRequest, "resolved-style 需要唯一 expectedRevision/branchId/lineId；仅允许可选 tokenId/clipId/nodeId。", "query"));
        return Result(await registry.ReadResolvedStyleAsync(Client(request), id, request.Query["expectedRevision"][0],
            request.Query["branchId"][0], request.Query["lineId"][0], request.Query["tokenId"].FirstOrDefault(),
            request.Query["clipId"].FirstOrDefault(), request.Query["nodeId"].FirstOrDefault(), request.HttpContext.RequestAborted));
    }
}
