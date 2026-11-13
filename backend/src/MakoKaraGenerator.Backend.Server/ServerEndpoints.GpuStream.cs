using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record GpuClaimRequest(string ConsumerId, string HostInstanceId, string ExpectedInputGeneration);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record GpuConsumerFailureRequest(string ConsumerId, string HostInstanceId, string InputGeneration, string ProducerEpoch);

    private static void ConfigureGpuStreamEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/gpu-preview-streams", async (HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker) =>
        {
            var (input, error) = await ReadStrictJsonAsync<GpuPreviewStreamCreateRequest>(request, gpu: true);
            if (error is not null) return error;
            request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            return Result(await tracker.RunAsync(() => service.CreateGpuStreamAsync(Client(request), input!)), 201);
        });
        app.MapGet("/api/v1/gpu-preview-streams/{id}", async (string id, HttpRequest request, PreviewBackendService service) =>
            Result(await service.ReadGpuStreamAsync(Client(request), id)));
        app.MapPut("/api/v1/gpu-preview-streams/{id}/source", async (string id, HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker) =>
        {
            var (input, error) = await ReadStrictJsonAsync<GpuPreviewStreamReplaceRequest>(request, gpu: true);
            if (error is not null) return error;
            request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            return Result(await tracker.RunAsync(() => service.ReplaceGpuStreamAsync(Client(request), id, input!)));
        });
        app.MapPut("/api/v1/gpu-preview-streams/{id}/size", async (string id, HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker) =>
        {
            var (input, error) = await ReadStrictJsonAsync<GpuPreviewStreamResizeRequest>(request, gpu: true);
            if (error is not null) return error;
            request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            return Result(await tracker.RunAsync(() => service.ResizeGpuStreamAsync(Client(request), id, input!)));
        });
        app.MapDelete("/api/v1/gpu-preview-streams/{id}", async (string id, HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker) =>
        {
            var (input, error) = await ReadStrictJsonAsync<GpuPreviewStreamCloseRequest>(request, gpu: true);
            if (error is not null) return error;
            request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            return Result(await tracker.RunAsync(() => service.CloseGpuStreamAsync(Client(request), id, input!)));
        });
        app.MapPost("/internal/gpu-preview-streams/{id}/claim", async (string id, HttpRequest request, PreviewBackendService service) =>
        {
            var (input, error) = await ReadStrictJsonAsync<GpuClaimRequest>(request, gpu: true);
            if (error is not null) return error;
            string? secret = request.Headers["X-Mako-Desktop-Secret"].Count == 1 ? request.Headers["X-Mako-Desktop-Secret"][0] : null;
            var claim = service.ClaimGpuStream(id, input!.ConsumerId, input.HostInstanceId, input.ExpectedInputGeneration, secret);
            return claim is null ? Error(new(BackendErrorCode.UnauthorizedClient, "GPU 领取被拒绝。")) : Results.Json(claim);
        });
        app.MapPost("/internal/gpu-preview-streams/{id}/consumer-failed", async (string id, HttpRequest request,
            PreviewBackendService service, AcceptedOperationTracker tracker) =>
        {
            var (input, error) = await ReadStrictJsonAsync<GpuConsumerFailureRequest>(request, gpu: true);
            if (error is not null) return error;
            string? secret = request.Headers["X-Mako-Desktop-Secret"].Count == 1 ? request.Headers["X-Mako-Desktop-Secret"][0] : null;
            bool terminated = await tracker.RunAsync(() => service.AbortGpuConsumerAsync(id, input!.ConsumerId,
                input.HostInstanceId, input.InputGeneration, input.ProducerEpoch, secret));
            return terminated ? Results.Json(new { terminated = true, drained = false })
                : Error(new(BackendErrorCode.UnauthorizedClient, "GPU 故障隔离未确认。"));
        });
    }
}
