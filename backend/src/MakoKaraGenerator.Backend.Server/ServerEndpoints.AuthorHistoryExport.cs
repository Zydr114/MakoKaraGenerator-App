using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureAuthorHistoryExportEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/drafts/{id}/source-revisions", ReadAuthorSourceRevisionsAsync);
        app.MapGet("/api/v1/drafts/{id}/package", DownloadAuthorPackageAsync);
        app.MapGet("/api/v1/script-configurations/{id}/package", DownloadScriptConfigurationPackageAsync);
    }

    private static async Task<IResult> ReadAuthorSourceRevisionsAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!TryReadAuthorExportQuery(request, ["expectedDraftRevision"], ["afterSourceRevision", "limit"], out IResult? error)) return error!;
        int? limit = null;
        if (request.Query.TryGetValue("limit", out var text))
        {
            if (!int.TryParse(text[0], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                return Error(new(BackendErrorCode.InvalidRequest, "历史页大小必须为正整数。", "limit"));
            limit = parsed;
        }
        return Result(await registry.ReadAuthorSourceRevisionsAsync(Client(request), id, request.Query["expectedDraftRevision"][0]!,
            request.Query.TryGetValue("afterSourceRevision", out var after) ? after[0] : null, limit, request.HttpContext.RequestAborted));
    }

    private static Task DownloadAuthorPackageAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry)
        => DownloadEffectPackageAsync(request, response, ["expectedDraftRevision", "sourceRevision"], token =>
            registry.OpenAuthorPackageAsync(Client(request), id, request.Query["expectedDraftRevision"][0]!, request.Query["sourceRevision"][0]!, token));

    private static Task DownloadScriptConfigurationPackageAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry)
        => DownloadEffectPackageAsync(request, response, ["expectedRevision"], token =>
            registry.OpenScriptConfigurationPackageAsync(id, request.Query["expectedRevision"][0]!, token));

    private static async Task DownloadEffectPackageAsync(HttpRequest request, HttpResponse response, string[] required,
        Func<CancellationToken, Task<BackendOperationResult<EffectPackageDownloadLease>>> acquire)
    {
        if (!TryReadAuthorExportQuery(request, required, [], out IResult? queryError))
        { await queryError!.ExecuteAsync(request.HttpContext); return; }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            BackendOperationResult<EffectPackageDownloadLease> acquired = await acquire(deadline.Token);
            if (!acquired.Accepted) { await WriteError(request.HttpContext, acquired.Error!, StatusCode(acquired.Error!.Code)); return; }
            using EffectPackageDownloadLease lease = acquired.Response!;
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lease.Stopping);
            response.StatusCode = 200;
            response.ContentType = "application/octet-stream";
            response.ContentLength = lease.Stream.Length;
            response.Headers.ContentDisposition = "attachment; filename=\"MakoKara-package.mkfx\"";
            response.Headers.ETag = "\"" + lease.Sha256 + "\"";
            response.Headers.CacheControl = "no-store";
            response.Headers.AccessControlExposeHeaders = "Content-Disposition, Content-Length, ETag";
            await lease.Stream.CopyToAsync(response.Body, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            if (request.HttpContext.RequestAborted.IsCancellationRequested) return;
            if (response.HasStarted) request.HttpContext.Abort();
            else await WriteError(request.HttpContext, new(BackendErrorCode.TaskUnavailable, "效果包下载已取消或超过30秒期限。", "package"), 503);
        }
    }

    private static bool TryReadAuthorExportQuery(HttpRequest request, string[] required, string[] optional, out IResult? error)
    {
        foreach (var query in request.Query)
        {
            if ((!required.Contains(query.Key, StringComparer.Ordinal) && !optional.Contains(query.Key, StringComparer.Ordinal))
                || query.Value.Count != 1 || string.IsNullOrWhiteSpace(query.Value[0]))
            { error = Error(new(BackendErrorCode.InvalidRequest, "查询参数未知、重复或为空。", query.Key)); return false; }
        }
        foreach (string name in required)
            if (!request.Query.ContainsKey(name))
            { error = Error(new(BackendErrorCode.InvalidRequest, "必须提供" + name + "查询参数。", name)); return false; }
        error = null;
        return true;
    }
}
