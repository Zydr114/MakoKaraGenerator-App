using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigurePreviewEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/runtime-info", (PreviewBackendService service) => Results.Json(service.RuntimeInfo));
        app.MapGet("/api/v1/render-backends", ReadRenderBackendsAsync);
        app.MapPost("/api/v1/preview-sessions", CreatePreviewAsync);
        app.MapPost("/api/v1/template-preview-sessions", CreateTemplatePreviewAsync);
        app.MapPost("/api/v1/template-preview-options", ReadTemplatePreviewOptionsAsync);
        app.MapPost("/api/v1/project-sessions/{id}/preset-application-previews", CreatePresetApplicationPreviewAsync);
        app.MapPut("/api/v1/preview-sessions/{id}/input", UpdatePreviewAsync);
        app.MapPut("/api/v1/preview-sessions/{id}/render-backend", SwitchPreviewRenderBackendAsync);
        app.MapPost("/api/v1/preview-sessions/{id}/frame-requests", RequestPreviewFrameAsync);
        app.MapGet("/api/v1/preview-sessions/{id}", (string id, HttpRequest request, PreviewBackendService service) => Result(service.Read(Client(request), id)));
        app.MapPost("/api/v1/preview-sessions/{id}/close", ClosePreviewAsync);
    }

    private static async Task<IResult> ReadRenderBackendsAsync(HttpRequest request, PreviewBackendService service,
        AcceptedOperationTracker tracker, IHostApplicationLifetime lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted,
            lifetime.ApplicationStopping);
        return Result(await tracker.RunAsync(() => service.ReadRenderBackendsAsync(Client(request), linked.Token)));
    }

    private static async Task<IResult> SwitchPreviewRenderBackendAsync(string id, HttpRequest request,
        PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (PreviewRenderBackendRequest? input, IResult? error) = await ReadStrictJsonAsync<PreviewRenderBackendRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => service.SwitchRenderBackendAsync(Client(request), id, input!)));
    }

    private static async Task<IResult> ReadTemplatePreviewOptionsAsync(HttpRequest request, PreviewBackendService service)
    {
        (TemplatePreviewOptionsRequest? input, IResult? error) = await ReadJsonAsync<TemplatePreviewOptionsRequest>(request);
        if (error is not null) return error;
        return Result(await service.ReadTemplatePreviewOptionsAsync(Client(request), input!, request.HttpContext.RequestAborted));
    }

    private static async Task<IResult> CreatePresetApplicationPreviewAsync(string id, HttpRequest request,
        PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (PresetApplicationPreviewRequest? input, IResult? error) = await ReadJsonAsync<PresetApplicationPreviewRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => service.CreatePresetApplicationPreviewAsync(Client(request), id, input!)), 201);
    }

    private static async Task<IResult> CreatePreviewAsync(HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (PreviewSessionRequest? input, IResult? error) = await ReadJsonAsync<PreviewSessionRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => service.CreateAsync(Client(request), input!)), 201);
    }

    private static async Task<IResult> CreateTemplatePreviewAsync(HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (TemplatePreviewSessionRequest? input, IResult? error) = await ReadJsonAsync<TemplatePreviewSessionRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => service.CreateTemplatePreviewAsync(Client(request), input!)), 201);
    }

    private static async Task<IResult> UpdatePreviewAsync(string id, HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (PreviewProjectInputRequest? input, IResult? error) = await ReadJsonAsync<PreviewProjectInputRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => service.UpdateAsync(Client(request), id, input!)));
    }

    private static async Task<IResult> RequestPreviewFrameAsync(string id, HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (PreviewFrameCommandRequest? input, IResult? error) = await ReadJsonAsync<PreviewFrameCommandRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        // Acknowledged requests belong to the preview, not this HTTP response lifetime.
        return Result(await tracker.RunAsync(() => service.RequestFrameAsync(Client(request), id, input!)), 202);
    }

    private static async Task<IResult> ClosePreviewAsync(string id, HttpRequest request, PreviewBackendService service, AcceptedOperationTracker tracker)
    {
        (ClosePreviewSessionRequest? input, IResult? error) = await ReadJsonAsync<ClosePreviewSessionRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        return Result(await tracker.RunAsync(() => service.CloseAsync(Client(request), id, input!)));
    }
}
