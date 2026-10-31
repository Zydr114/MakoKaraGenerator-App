using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static readonly UploadPreparationLimits EventConnections = new();
    private static async Task EventsAsync(HttpRequest request, HttpResponse response, ProjectSessionRegistry registry, ClientRegistry clients,
        IOptions<JsonOptions> jsonOptions)
    {
        string client = Client(request);
        using IDisposable? connection = EventConnections.TryAcquire(client);
        if (connection is null) { await WriteError(request.HttpContext, new(BackendErrorCode.RequestQuotaExceeded, "事件连接已达到上限。"), 429); return; }
        string? cursor = request.Headers["Last-Event-ID"].FirstOrDefault() ?? request.Query["after"].FirstOrDefault();
        if (cursor is not null && ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            cursor = registry.HostInstanceId + ":" + client + ":" + cursor;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(request.HttpContext.RequestAborted);
        while (!request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            BackendErrorResponse? auth = clients.Authenticate(client, LoopbackSecurity.Bearer(request));
            if (auth is not null)
            {
                await response.WriteAsync("event: client.expired\ndata: " + JsonSerializer.Serialize(auth, jsonOptions.Value.SerializerOptions) + "\n\n", request.HttpContext.RequestAborted);
                await response.Body.FlushAsync(request.HttpContext.RequestAborted); return;
            }
            ClientEventsResponse batch = registry.ReadEvents(client, cursor);
            if (batch.ResyncRequired)
            {
                await response.WriteAsync("event: resync.required\ndata: " + JsonSerializer.Serialize(batch, jsonOptions.Value.SerializerOptions) + "\n\n", request.HttpContext.RequestAborted);
                await response.Body.FlushAsync(request.HttpContext.RequestAborted); return;
            }
            foreach (BackendEventResponse item in batch.Events)
                await response.WriteAsync("id: " + item.EventSeq + "\nevent: " + item.Type + "\ndata: " + JsonSerializer.Serialize(item, jsonOptions.Value.SerializerOptions) + "\n\n", request.HttpContext.RequestAborted);
            cursor = batch.EventCursor;
            if (batch.Events.Length > 0) await response.Body.FlushAsync(request.HttpContext.RequestAborted);
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            try { await registry.WaitForEventsAsync(client, cursor, wait.Token); }
            catch (OperationCanceledException) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
            {
                await response.WriteAsync(": heartbeat\n\n", request.HttpContext.RequestAborted);
                await response.Body.FlushAsync(request.HttpContext.RequestAborted);
            }
        }
    }

    private static async Task ReadAssetAsync(string id, HttpRequest request, HttpResponse response, ResourceStore store)
    {
        using ResourceReadLease lease = store.AcquireRead(Client(request), id);
        StoredAsset asset = lease.Asset;
        await ReadImmutableContentAsync(request, response, lease.Stream, asset.Size, asset.ContentType, asset.Sha256);
    }

    private static async Task ReadPreviewFrameAsync(string id, HttpRequest request, HttpResponse response,
        PreviewFrameHandleStore store)
    {
        if (!store.TryAcquireRead(Client(request), id, DateTimeOffset.UtcNow, out PreviewFrameReadLease? lease,
                out BackendErrorResponse? error))
        {
            await WriteError(request.HttpContext, error!, StatusCode(error!.Code));
            return;
        }

        using PreviewFrameReadLease read = lease!;
        PreviewFrameMetadataDto metadata = read.Metadata;
        response.Headers["X-Mako-Preview-Expires-At"] = metadata.Content.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await ReadImmutableContentAsync(request, response, read.Stream, read.Stream.Length, metadata.Content.ContentType,
            metadata.Content.Sha256, "X-Mako-Preview-Expires-At");
    }

    private static async Task ReadImmutableContentAsync(HttpRequest request, HttpResponse response, Stream input, long size,
        string contentType, string sha256, string? additionalExposedHeader = null)
    {
        string etag = "\"" + sha256 + "\"";
        response.Headers.ETag = etag;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.AccessControlExposeHeaders = "ETag, Accept-Ranges, Content-Range, Content-Length"
            + (additionalExposedHeader is null ? string.Empty : ", " + additionalExposedHeader);
        response.ContentType = contentType;
        if (request.Headers.IfNoneMatch.Any(value => value?.Split(',', StringSplitOptions.TrimEntries)
            .Any(tag => tag == "*" || (tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag) == etag) == true))
        { response.StatusCode = 304; return; }
        long start = 0, end = size - 1;
        string? range = request.Headers.Range.FirstOrDefault();
        if (range is not null && (request.Headers.IfRange.Count == 0 || request.Headers.IfRange.ToString() == etag))
        {
            if (request.Headers.Range.Count != 1 || !TryRange(range, size, out start, out end))
            {
                response.StatusCode = 416;
                response.Headers.ContentRange = "bytes */" + size.ToString(CultureInfo.InvariantCulture);
                response.ContentLength = 0; return;
            }
            response.StatusCode = 206;
            response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
        }
        else response.StatusCode = 200;
        long length = end - start + 1; response.ContentLength = length;
        if (request.Method == "HEAD") return;
        input.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[64 * 1024];
        while (length > 0)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), request.HttpContext.RequestAborted);
            if (count == 0) throw new EndOfStreamException("The immutable content ended before its declared size.");
            await response.Body.WriteAsync(buffer.AsMemory(0, count), request.HttpContext.RequestAborted);
            length -= count;
        }
    }

    private static IResult ReadPreviewDiagnosticsAsync(string id, HttpRequest request, PreviewDiagnosticsStore store)
    {
        string? cursor = request.Query["cursor"].FirstOrDefault();
        string? limitText = request.Query["limit"].FirstOrDefault();
        int? limit = null;
        if (limitText is not null)
        {
            if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                return Error(new(BackendErrorCode.InvalidRequest, "诊断分页参数无效。", "limit"));
            limit = parsed;
        }
        return store.TryRead(Client(request), id, cursor, limit,
            out PreviewDiagnosticsPageDto? page, out BackendErrorResponse? error)
            ? Results.Json(page)
            : Error(error!);
    }

    private static bool TryRange(string value, long size, out long start, out long end)
    {
        start = 0; end = size - 1;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return false;
        string[] parts = value[6..].Split('-');
        if (parts.Length != 2 || size <= 0) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= size) return false;
        if (parts[1].Length == 0) return true;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
        end = Math.Min(end, size - 1); return true;
    }
}
