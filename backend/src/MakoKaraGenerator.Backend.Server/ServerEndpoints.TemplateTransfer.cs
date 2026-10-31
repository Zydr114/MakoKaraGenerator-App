using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static readonly JsonSerializerOptions TemplateTransferJson = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static void ConfigureTemplateTransferEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/template-libraries/{id}/imports/preflight", PreflightTemplateImportAsync);
        app.MapPost("/api/v1/template-libraries/{id}/imports", ImportTemplatesAsync);
        app.MapPost("/api/v1/template-libraries/{id}/exports", ExportTemplatesAsync);
        app.MapMethods("/api/v1/template-transfers/{id}/archive", ["GET", "HEAD"], ReadTemplateArchiveAsync);
    }
    private static async Task<IResult> PreflightTemplateImportAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        var (input, error) = await ReadTemplateTransferJsonAsync<TemplateImportPreflightRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.PreflightTemplateImportAsync(client, id, input!, CancellationToken.None)), 201);
    }
    private static async Task<IResult> ImportTemplatesAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        var (input, error) = await ReadTemplateTransferJsonAsync<TemplateImportRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.ImportTemplatesAsync(client, id, input!, CancellationToken.None)));
    }
    private static async Task<IResult> ExportTemplatesAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        var (input, error) = await ReadTemplateTransferJsonAsync<TemplateArchiveExportRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.ExportTemplatesAsync(client, id, input!, CancellationToken.None)), 201);
    }
    private static async Task ReadTemplateArchiveAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry)
    {
        if (request.Query.Count != 0) { await WriteError(request.HttpContext, new(BackendErrorCode.InvalidRequest, "模板归档不接受查询参数。"), 400); return; }
        var acquired = registry.AcquireTemplateArchive(Client(request), id);
        if (!acquired.Accepted) { await WriteError(request.HttpContext, acquired.Error!, StatusCode(acquired.Error!.Code)); return; }
        using var lease = acquired.Response!;
        response.Headers.ContentDisposition = "attachment; filename=\"MakoKara-templates.mktpl\"";
        await ReadImmutableContentAsync(request, response, lease.Stream, lease.Response.SizeBytes, "application/octet-stream", lease.Response.Sha256, "Content-Disposition");
    }
    private static async Task<(T? Input, IResult? Error)> ReadTemplateTransferJsonAsync<T>(HttpRequest request) where T : class
    {
        if (request.Query.Count != 0) return (null, Error(new(BackendErrorCode.InvalidRequest, "模板传输不接受查询参数。")));
        if (!request.HasJsonContentType()) return (null, Error(new(BackendErrorCode.UnsupportedMediaType, "请求必须使用JSON。"), 415));
        const int max = 16 * 1024;
        byte[] buffer = new byte[max + 1]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await request.Body.ReadAsync(buffer.AsMemory(count), request.HttpContext.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count > max) return (null, Error(new(BackendErrorCode.ResourceTooLarge, "模板传输请求超过16KiB。"), 413));
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            RequireUniqueJsonProperties(json.RootElement);
            T? input = json.Deserialize<T>(TemplateTransferJson);
            return input is null ? (null, Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。"))) : (input, null);
        }
        catch (JsonException) { return (null, Error(new(BackendErrorCode.InvalidRequest, "模板传输JSON字段无效、重复或缺失。"))); }
    }
}
