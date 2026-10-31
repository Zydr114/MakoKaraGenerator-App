using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static readonly JsonSerializerOptions FileLyricsRequestJson = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static void ConfigureFileLyricsEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/lyrics-import-plans", ReadLyricsImportPlanAsync);
        app.MapGet("/api/v1/file-roots/{rootGrantId}/entries", ReadDirectoryEntries);
        app.MapPost("/api/v1/project-sessions/{id}/lyrics-export-plans", PlanLyricsAsync);
        app.MapPost("/api/v1/project-sessions/{id}/lyrics-exports", ExportLyricsAsync);
        app.MapMethods("/api/v1/lyrics-exports/{id}/content", ["GET", "HEAD"], ReadLyricsExportAsync);
    }

    private static async Task<IResult> ReadLyricsImportPlanAsync(HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        if (request.Query.Count != 0)
            return Error(new(BackendErrorCode.InvalidRequest, "歌词导入信息请求不接受查询参数。"));
        var (input, error) = await ReadJsonAsync<LyricsImportPlanRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.ReadLyricsImportPlanAsync(client, input)));
    }

    private static IResult ReadDirectoryEntries(string rootGrantId, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Any(pair => pair.Key is not ("relativePath" or "offset" or "limit") || pair.Value.Count != 1))
            return Error(new(BackendErrorCode.InvalidRequest, "目录查询只接受单次relativePath、offset和limit。"));
        string relative = request.Query["relativePath"].FirstOrDefault() ?? "";
        int offset = 0, limit = 100;
        if (request.Query.TryGetValue("offset", out var offsetText) && !int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            || request.Query.TryGetValue("limit", out var limitText) && !int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
            return Error(new(BackendErrorCode.InvalidRequest, "offset和limit必须为非负十进制整数。"));
        return Result(registry.ReadProjectDirectoryEntries(Client(request), rootGrantId, relative, offset, limit));
    }

    private static async Task<IResult> PlanLyricsAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        var (input, error) = await ReadFileLyricsJsonAsync<LyricsExportPlanRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.PlanLyricsExportAsync(client, id, input!, CancellationToken.None)), 201);
    }

    private static async Task<IResult> ExportLyricsAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        var (input, error) = await ReadFileLyricsJsonAsync<LyricsExportRequest>(request);
        if (error is not null) return error;
        string client = Client(request);
        return Result(await tracker.RunAsync(() => registry.ExportLyricsAsync(client, id, input!, CancellationToken.None)), 201);
    }

    private static async Task ReadLyricsExportAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry)
    {
        if (request.Query.Count != 0)
        { await WriteError(request.HttpContext, new(BackendErrorCode.InvalidRequest, "歌词内容不接受查询参数。"), 400); return; }
        var acquired = registry.AcquireLyricsExport(Client(request), id);
        if (!acquired.Accepted) { await WriteError(request.HttpContext, acquired.Error!, StatusCode(acquired.Error!.Code)); return; }
        using LyricsExportContentLease lease = acquired.Response!;
        string extension = lease.Response.Format.StartsWith("ttml.", StringComparison.Ordinal) ? ".ttml" : ".lrc";
        string contentType = extension == ".ttml" ? "application/ttml+xml; charset=utf-8" : "text/plain; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"MakoKara-lyrics{extension}\"";
        await ReadImmutableContentAsync(request, response, lease.Stream, lease.Response.SizeBytes, contentType, lease.Response.Sha256, "Content-Disposition");
    }

    private static async Task<(T? Input, IResult? Error)> ReadFileLyricsJsonAsync<T>(HttpRequest request) where T : class
    {
        if (request.Query.Count != 0) return (null, Error(new(BackendErrorCode.InvalidRequest, "歌词导出请求不接受查询参数。")));
        if (!request.HasJsonContentType()) return (null, Error(new(BackendErrorCode.UnsupportedMediaType, "请求必须使用JSON媒体类型。"), 415));
        const int limit = 16 * 1024;
        byte[] buffer = new byte[limit + 1]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await request.Body.ReadAsync(buffer.AsMemory(count), request.HttpContext.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) return (null, Error(new(BackendErrorCode.ResourceTooLarge, "歌词导出请求超过16KiB预算。"), 413));
        try
        {
            using JsonDocument json = JsonDocument.Parse(buffer.AsMemory(0, count));
            RequireUniqueJsonProperties(json.RootElement);
            T? input = json.Deserialize<T>(FileLyricsRequestJson);
            return input is null ? (null, Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。"))) : (input, null);
        }
        catch (JsonException) { return (null, Error(new(BackendErrorCode.InvalidRequest, "歌词导出JSON字段无效、重复或缺失。"))); }
    }

    private static void RequireUniqueJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("Duplicate property.");
                RequireUniqueJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RequireUniqueJsonProperties(child);
    }
}
