using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static async Task<IResult> InspectEffectPackageAsync(HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Count != 0) return Error(new(BackendErrorCode.InvalidRequest, "效果包检查不接受query。", "query"));
        (EffectPackageInspectRequest? input, IResult? error) = await ReadJsonAsync<EffectPackageInspectRequest>(request);
        if (error is not null) return error;
        return Result(await registry.InspectEffectPackageAsync(Client(request), input!, request.HttpContext.RequestAborted));
    }
}
