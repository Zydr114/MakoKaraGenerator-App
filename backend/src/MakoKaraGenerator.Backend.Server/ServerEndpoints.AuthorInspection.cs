using Microsoft.AspNetCore.Http.Features;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static async Task<IResult> ReadAuthorSourceInspectionAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!TryReadAuthorExportQuery(request, ["expectedDraftRevision", "sourceRevision"], [], out IResult? error)) return error!;
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            return Error(new(BackendErrorCode.InvalidRequest, "源码声明检查不接受请求体。", "body"));
        return Result(await registry.InspectAuthorSourceAsync(Client(request), id, request.Query["expectedDraftRevision"][0]!,
            request.Query["sourceRevision"][0]!, request.HttpContext.RequestAborted));
    }
}
