using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static async Task<IResult> GetGeometryReferenceAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query["expectedRevision"].Count != 1 || request.Query["nodeId"].Count != 1
            || request.Query.Keys.Any(key => key is not ("expectedRevision" or "nodeId")))
            return Error(new(BackendErrorCode.InvalidRequest, "必须提供唯一 expectedRevision 与 nodeId；不接受其他查询字段。", "query"));
        return Result(await registry.ReadGeometryReferenceAsync(Client(request), id, request.Query["expectedRevision"][0],
            request.Query["nodeId"][0], request.HttpContext.RequestAborted));
    }
}
