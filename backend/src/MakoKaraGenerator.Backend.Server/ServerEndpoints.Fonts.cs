using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureFontEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/fonts", ListFontsAsync);
        app.MapPost("/api/v1/fonts/inspect", InspectFontsAsync);
    }

    private static async Task<IResult> ListFontsAsync(HttpRequest request,
        ProjectSessionRegistry registry)
    {
        if (request.Query.Count > 1 || request.Query["family"].Count > 1)
            return Error(new(BackendErrorCode.InvalidRequest, "family 查询无效。", "query"));
        // The query is intentionally advisory. Filtering here would hide the actual Worker
        // environment and would make an empty result look like an unavailable font catalog.
        return Result(await registry.ListFontsAsync(Client(request), request.HttpContext.RequestAborted));
    }

    private static async Task<IResult> InspectFontsAsync(HttpRequest request,
        ProjectSessionRegistry registry)
    {
        if (request.Query.Count != 0)
            return Error(new(BackendErrorCode.InvalidRequest, "inspect 不接受 query。", "query"));
        (FontInspectRequest? input, IResult? error) = await ReadJsonAsync<FontInspectRequest>(request);
        if (error is not null) return error;
        return Result(await registry.InspectFontAsync(Client(request), input!,
            request.HttpContext.RequestAborted));
    }
}
