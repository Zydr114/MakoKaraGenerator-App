using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureTemplateLibraryEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/template-libraries", GetTemplateLibraries);
        app.MapGet("/api/v1/template-libraries/{id}/templates", GetTemplatesAsync);
        app.MapGet("/api/v1/template-libraries/{id}/templates/detail", GetTemplateAsync);
        app.MapPost("/api/v1/template-libraries/{id}/commands", TemplateLibraryCommandAsync);
    }
    private static IResult GetTemplateLibraries(HttpRequest request, ProjectSessionRegistry registry) =>
        request.Query.Count == 0 ? Results.Json(registry.ReadTemplateLibraries()) : InvalidTemplateQuery();
    private static async Task<IResult> GetTemplatesAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TemplateQuery(request, ["expectedRevision", "offset", "limit"], [])
            || !TemplatePageNumber(request, "offset", 0, out int offset) || !TemplatePageNumber(request, "limit", 50, out int limit)) return InvalidTemplateQuery();
        return Result(await registry.ReadTemplatesAsync(id, request.Query["expectedRevision"].FirstOrDefault(), offset, limit,
            request.HttpContext.RequestAborted));
    }
    private static async Task<IResult> GetTemplateAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TemplateQuery(request, ["expectedRevision", "templateId", "definitionFormat"], ["expectedRevision", "templateId"])) return InvalidTemplateQuery();
        if (request.Query.TryGetValue("definitionFormat", out var format) && (format.Count != 1 || format[0] != "full")) return InvalidTemplateQuery();
        return Result(await registry.ReadTemplateAsync(id, request.Query["expectedRevision"][0]!, request.Query["templateId"][0]!,
            request.Query["definitionFormat"].FirstOrDefault(), request.HttpContext.RequestAborted));
    }
    private static async Task<IResult> TemplateLibraryCommandAsync(string id, HttpRequest request, ProjectSessionRegistry registry,
        AcceptedOperationTracker tracker)
    {
        if (request.Query.Count != 0) return InvalidTemplateQuery();
        (TemplateLibraryCommandRequest? input, IResult? error) = await ReadJsonAsync<TemplateLibraryCommandRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        // The existing receipt owner retains the accepted mutation after an HTTP waiter disconnects.
        return Result(await tracker.RunAsync(() => registry.ExecuteTemplateLibraryCommandAsync(client, id, input!, CancellationToken.None)));
    }
    private static bool TemplateQuery(HttpRequest request, string[] allowed, string[] required) =>
        !request.Query.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal) || request.Query[key].Count != 1)
        && required.All(key => request.Query[key].Count == 1);
    private static bool TemplatePageNumber(HttpRequest request, string key, int fallback, out int value)
    {
        value = fallback;
        return !request.Query.ContainsKey(key) || int.TryParse(request.Query[key][0], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
    private static IResult InvalidTemplateQuery() => Error(new(BackendErrorCode.InvalidRequest,
        "模板库 query 需要该操作声明的唯一字段；分页必须是非负十进制 Int32。", "query"));
}
