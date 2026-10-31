using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureWaveformEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/project-sessions/{id}/waveform", ReadWaveformAsync);
        app.MapMethods("/api/v1/project-sessions/{id}/waveform/peaks", ["GET", "HEAD"], ReadWaveformPeaksAsync);
    }

    private static async Task<IResult> ReadWaveformAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (request.Query.Keys.FirstOrDefault() is { } unknown)
            return Error(new(BackendErrorCode.InvalidRequest, "波形元数据不接受查询参数。", unknown));
        return Result(await registry.ReadWaveformAsync(Client(request), id, request.HttpContext.RequestAborted));
    }

    private static async Task ReadWaveformPeaksAsync(string id, HttpRequest request, HttpResponse response, ProjectSessionRegistry registry)
    {
        string? unknown = request.Query.Keys.FirstOrDefault(key => key is not ("waveformId" or "level" or "start" or "count"));
        string? invalid = unknown ?? (request.Headers.ContainsKey("Range") ? "Range"
            : request.Query["waveformId"].Count != 1 || string.IsNullOrWhiteSpace(request.Query["waveformId"][0]) ? "waveformId" : null);
        int level = 0, start = 0, count = 0;
        if (invalid is null && !TryWaveformInteger(request, "level", out level)) invalid = "level";
        if (invalid is null && !TryWaveformInteger(request, "start", out start)) invalid = "start";
        if (invalid is null && !TryWaveformInteger(request, "count", out count)) invalid = "count";
        if (invalid is not null)
        {
            await WaveformErrorAsync(request, new(BackendErrorCode.InvalidRequest, "波形窗口查询无效，不支持字节Range。", invalid));
            return;
        }
        BackendOperationResult<WaveformWindow> result = await registry.ReadWaveformWindowAsync(
            Client(request), id, request.Query["waveformId"][0]!, level, start, count, request.HttpContext.RequestAborted);
        if (!result.Accepted) { await WaveformErrorAsync(request, result.Error!); return; }
        WaveformWindow window = result.Response!;
        response.Headers.ETag = window.ETag;
        response.Headers.AccessControlExposeHeaders = "ETag, Content-Length";
        response.ContentType = "application/octet-stream";
        if (request.Headers.IfNoneMatch.Any(value => value?.Split(',', StringSplitOptions.TrimEntries)
                .Any(tag => tag == "*" || (tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag) == window.ETag) == true))
        { response.StatusCode = 304; return; }
        response.ContentLength = window.Bytes.Length;
        response.StatusCode = 200;
        if (request.Method != "HEAD") await response.Body.WriteAsync(window.Bytes, request.HttpContext.RequestAborted);
    }

    private static bool TryWaveformInteger(HttpRequest request, string name, out int value)
    {
        value = 0;
        return request.Query[name].Count == 1
            && int.TryParse(request.Query[name][0], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static Task WaveformErrorAsync(HttpRequest request, BackendErrorResponse error)
    {
        if (request.Method == "HEAD")
        {
            request.HttpContext.Response.StatusCode = StatusCode(error.Code);
            request.HttpContext.Response.ContentType = "application/problem+json";
            return Task.CompletedTask;
        }
        return WriteError(request.HttpContext, error, StatusCode(error.Code));
    }
}
