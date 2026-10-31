using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static async Task<IResult> GetLineLocalTransformAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query["expectedRevision"].Count != 1 || request.Query["branchId"].Count != 1
            || request.Query["lineId"].Count != 1
            || request.Query.Keys.Any(key => key is not ("expectedRevision" or "branchId" or "lineId")))
            return Error(new(BackendErrorCode.InvalidRequest, "必须提供唯一 expectedRevision、branchId 和 lineId。", "query"));
        return Result(await registry.ReadLineLocalTransformAsync(Client(request), id,
            request.Query["expectedRevision"][0], request.Query["branchId"][0], request.Query["lineId"][0], request.HttpContext.RequestAborted));
    }
}
