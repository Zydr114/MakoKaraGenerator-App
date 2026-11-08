using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureAuthorPackageFilesEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/drafts/{id}/package-files", ReadAuthorPackageFiles);
        app.MapPut("/api/v1/drafts/{id}/package-files", SaveAuthorPackageFilesAsync);
        app.MapPost("/api/v1/drafts/{id}/package-file-replacement-inspection", InspectAuthorPackageFileReplacementAsync);
    }

    private static async Task<IResult> InspectAuthorPackageFileReplacementAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Count != 0)
            return Error(new(BackendErrorCode.InvalidRequest, "替换检查不接受查询参数。", "query"));
        if (request.ContentLength is > 64 * 1024)
            return Error(new(BackendErrorCode.ResourceTooLarge, "替换检查请求JSON超过64KiB。", "body"), 413);
        (AuthorPackageFileReplacementInspectionRequest? input, IResult? error) = await ReadJsonAsync<AuthorPackageFileReplacementInspectionRequest>(request);
        if (error is not null) return error;
        return Result(await registry.InspectAuthorPackageFileReplacementAsync(Client(request), id, input!, request.HttpContext.RequestAborted));
    }

    private static IResult ReadAuthorPackageFiles(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Count != 2 || request.Query.Any(pair => pair.Key is not ("expectedDraftRevision" or "sourceRevision")
            || pair.Value.Count != 1 || string.IsNullOrWhiteSpace(pair.Value[0])))
            return Error(new(BackendErrorCode.InvalidRequest, "查询必须恰好包含单次expectedDraftRevision和sourceRevision。", "query"));
        return Result(registry.ReadAuthorPackageFiles(Client(request), id,
            request.Query["expectedDraftRevision"][0]!, request.Query["sourceRevision"][0]!));
    }

    private static async Task<IResult> SaveAuthorPackageFilesAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        if (request.Query.Count != 0)
            return Error(new(BackendErrorCode.InvalidRequest, "保存包文件不接受查询参数。", "query"));
        if (!request.HasJsonContentType())
            return Error(new(BackendErrorCode.UnsupportedMediaType, "请求必须使用JSON媒体类型。", "contentType"), 415);
        const int wireLimit = 32 * 1024 * 1024;
        if (request.ContentLength is > wireLimit)
            return Error(new(BackendErrorCode.ResourceTooLarge, "包文件请求JSON超过32MiB。", "body"), 413);
        using MemoryStream body = new();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, wireLimit + 1L - body.Length)),
                request.HttpContext.RequestAborted);
            if (read == 0) break;
            body.Write(buffer, 0, read);
            if (body.Length > wireLimit)
                return Error(new(BackendErrorCode.ResourceTooLarge, "包文件请求JSON超过32MiB。", "body"), 413);
        }
        try
        {
            using JsonDocument json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            EnsureUniqueJsonProperties(json.RootElement);
            AuthorPackageFilesRequest? input = json.RootElement.Deserialize<AuthorPackageFilesRequest>(StrictRequestJson);
            if (input is null) return Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。"));
            return Result(await tracker.RunAsync(() => registry.SaveAuthorPackageFilesAsync(Client(request), id, input, CancellationToken.None)));
        }
        catch (JsonException)
        { return Error(new(BackendErrorCode.InvalidRequest, "包文件请求JSON字段无效、重复或缺失。")); }
    }
}
