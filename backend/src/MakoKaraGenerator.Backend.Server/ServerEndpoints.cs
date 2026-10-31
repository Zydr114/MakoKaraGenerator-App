using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private const string ClientItem = "Mako.AuthenticatedClient";
    private static readonly string[] EnabledPreviewCapabilities = ["preview.create.static-project", "preview.input.static-project", "preview.create.template-sample", "preview.frame-requests"];
    private static readonly string[] EnabledGpuPreviewCapabilities = ["preview.gpu-streams"];
    public static void Configure(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            IDisposable? operationPin = null;
            try
            {
                LoopbackSecurity security = context.RequestServices.GetRequiredService<LoopbackSecurity>();
                if (!security.IsAllowed(context, out bool originAllowed))
                { await WriteError(context, new(BackendErrorCode.UnauthorizedClient, "请求的监听地址、Host或Origin不受允许。"), 403); return; }
                if (context.Request.Query.Keys.Any(key => key.Equals("token", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("access_token", StringComparison.OrdinalIgnoreCase) || key.Equals("accessToken", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("bootstrapToken", StringComparison.OrdinalIgnoreCase) || key.Equals("resumeToken", StringComparison.OrdinalIgnoreCase)))
                { await WriteError(context, new(BackendErrorCode.InvalidRequest, "访问凭证必须使用Authorization头，不允许放入URL。", "authorization"), 400); return; }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                if (originAllowed)
                {
                    context.Response.Headers.AccessControlAllowOrigin = context.Request.Headers.Origin;
                    context.Response.Headers.Vary = "Origin";
                }
                if (context.Request.Method == "OPTIONS")
                {
                    if (!originAllowed) { await WriteError(context, new(BackendErrorCode.UnauthorizedClient, "预检Origin不受允许。"), 403); return; }
                    string method = context.Request.Headers.AccessControlRequestMethod.ToString();
                    string[] headers = context.Request.Headers.AccessControlRequestHeaders.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    string[] allowedHeaders = ["Authorization", "Content-Type", "X-Mako-Client-Id", "X-Mako-Host-Instance-Id", "X-Mako-Request-Id", "X-Mako-Valid-Until-Utc", "X-Mako-Asset-Name", "Last-Event-ID", "Range", "If-None-Match", "If-Range"];
                    if (method is not ("GET" or "POST" or "PUT" or "DELETE" or "HEAD") || headers.Any(header => !allowedHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)))
                    { await WriteError(context, new(BackendErrorCode.InvalidRequest, "预检方法或请求头不受支持。"), 400); return; }
                    context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, HEAD";
                    context.Response.Headers.AccessControlAllowHeaders = string.Join(", ", allowedHeaders);
                    context.Response.StatusCode = 204; return;
                }
                bool publicEndpoint = context.Request.Path is { Value: "/health/live" or "/api/v1/health/live" or "/api/v1/clients" };
                string[] pathParts = (context.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
                bool privateGpuClaim = context.Request.Method == "POST" && pathParts.Length == 4 &&
                    pathParts[0] == "internal" && pathParts[1] == "gpu-preview-streams" && pathParts[3] == "claim" &&
                    context.RequestServices.GetRequiredService<PreviewBackendService>().GpuStreamsConfigured;
                if (!publicEndpoint && !privateGpuClaim)
                {
                    ClientRegistry clients = context.RequestServices.GetRequiredService<ClientRegistry>();
                    string? client = context.Request.Headers["X-Mako-Client-Id"].Count == 1 ? context.Request.Headers["X-Mako-Client-Id"][0] : null;
                    BackendErrorResponse? authentication = clients.TryAcquireOperation(client, LoopbackSecurity.Bearer(context.Request), out operationPin);
                    if (authentication is not null) { await WriteError(context, authentication, StatusCode(authentication.Code)); return; }
                    string? suppliedHost = context.Request.Headers["X-Mako-Host-Instance-Id"].FirstOrDefault();
                    if (suppliedHost is not null && suppliedHost != clients.HostInstanceId)
                    { await WriteError(context, new(BackendErrorCode.HostRestarted, "服务实例已重启，请重新建立会话。", "hostInstanceId"), 409); return; }
                    context.Items[ClientItem] = client;
                }
                if (app.Lifetime.ApplicationStopping.IsCancellationRequested && context.Request.Method is "POST" or "PUT" or "DELETE")
                { await WriteError(context, new(BackendErrorCode.SessionClosing, "服务正在关闭，停止接受新操作。"), 503); return; }
                await next().ConfigureAwait(false);
                if (!context.Response.HasStarted && context.Response.StatusCode is 404 or 405)
                    await WriteError(context, new(BackendErrorCode.InvalidRequest, "HTTP路由或方法不受支持。", "path"), context.Response.StatusCode);
            }
            catch (ResourceStoreException exception)
            { await WriteError(context, new(exception.Code, exception.Message, exception.Field), StatusCode(exception.Code)); }
            catch (BadHttpRequestException exception)
            {
                bool tooLarge = exception.StatusCode == 413;
                await WriteError(context, new(tooLarge ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.InvalidRequest,
                    tooLarge ? "请求超过大小上限。" : "HTTP请求无效。"), tooLarge ? 413 : 400);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception)
            { await WriteError(context, new(BackendErrorCode.InternalError, "服务未能完成请求，请读取当前状态核对。"), 500); }
            finally { operationPin?.Dispose(); }
        });
        app.MapGet("/health/live", (ProjectSessionRegistry registry) => Results.Json(new HealthResponse("live", registry.HostInstanceId)));
        app.MapGet("/api/v1/health/live", (ProjectSessionRegistry registry) => Results.Json(new HealthResponse("live", registry.HostInstanceId)));
        app.MapGet("/capabilities", Capabilities);
        app.MapGet("/api/v1/capabilities", Capabilities);
        app.MapPost("/api/v1/clients", RegisterClientAsync);
        app.MapPost("/api/v1/clients/{id}/heartbeat", Heartbeat);
        app.MapGet("/api/v1/clients/{id}/state", ClientStateAsync);
        app.MapGet("/api/v1/events", EventsAsync);
        app.MapPost("/assets", UploadAssetAsync);
        app.MapPost("/api/v1/assets", UploadAssetAsync);
        app.MapMethods("/api/v1/assets/{id}/content", ["GET", "HEAD"], ReadAssetAsync);
        app.MapMethods("/api/v1/preview-frames/{id}/content", ["GET", "HEAD"], ReadPreviewFrameAsync);
        app.MapGet("/api/v1/preview-sessions/{id}/diagnostics", ReadPreviewDiagnosticsAsync);
        app.MapPost("/project-sessions", CreateSessionAsync);
        app.MapPost("/api/v1/project-sessions", CreateSessionAsync);
        app.MapGet("/project-sessions/{id}", GetSessionAsync);
        app.MapGet("/api/v1/project-sessions/{id}", GetSessionAsync);
        app.MapGet("/api/v1/project-sessions/{id}/history", GetHistoryAsync);
        app.MapGet("/api/v1/project-sessions/{id}/lyrics/lines", GetLyricsAsync);
        app.MapGet("/api/v1/project-sessions/{id}/resolved-style", GetResolvedStyleAsync);
        app.MapPost("/api/v1/project-sessions/{id}/commands", ExecuteCommandAsync);
        ConfigurePersistenceEndpoints(app);
        ConfigureFileLyricsEndpoints(app);
        ConfigureBasicMediaEndpoints(app);
        ConfigurePreviewEndpoints(app);
        if (app.Services.GetRequiredService<PreviewBackendService>().GpuStreamsConfigured) ConfigureGpuStreamEndpoints(app);
        ConfigureValidationEndpoints(app);
        ConfigureExportEndpoints(app);
        ConfigureAnalysisEndpoints(app);
        ConfigureReadModelEndpoints(app);
        ConfigureFontEndpoints(app);
        ConfigureSceneMeasurementEndpoints(app);
        ConfigureTemplateLibraryEndpoints(app);
        ConfigureTemplateTransferEndpoints(app);
        ConfigureWaveformEndpoints(app);
        ConfigureAuthorEndpoints(app);
        ConfigureAuthorRecoveryEndpoints(app);
        ConfigureAuthorPackageFilesEndpoints(app);
        ConfigureAuthorHistoryExportEndpoints(app);
        ConfigureScriptConfigurationEndpoints(app);
        app.MapPost("/api/v1/effect-packages/inspect", InspectEffectPackageAsync);
    }

    private static string Client(HttpRequest request) => (string)request.HttpContext.Items[ClientItem]!;
    private static async Task<IResult> Capabilities(ResourceStore store, ProjectSessionRegistry registry, ClientRegistry clients, PreviewBackendService previews)
    {
        bool gpuAvailable = await previews.GpuStreamsAvailableAsync().ConfigureAwait(false);
        return Results.Json(new BackendCapabilitiesResponse(
        BackendProtocol.Version, registry.HostInstanceId, typeof(ServerEndpoints).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        store.MaxAssetBytes, store.MaxAssetQuotaBytes, ResourceStore.AllowedMimeTypes.ToArray(),
        ["effect-packages.inspect", "template-libraries.read", "template-libraries.templates.read", "template-libraries.templates.detail",
            "template-libraries.commands.create", "template-libraries.commands.replace", "template-libraries.commands.copy",
            "template-libraries.commands.rename", "template-libraries.commands.delete",
            "file-roots.entries", "project-sessions.lyrics-export-plans", "project-sessions.lyrics-exports", "lyrics-exports.content.range",
            "template-libraries.imports.preflight", "template-libraries.imports", "template-libraries.exports", "template-transfers.archive.range",
            "project-sessions.resolved-style.read", "project-sessions.commands.orchestration.setClipMembers",
            "project-sessions.commands.orchestration.splitClip", "project-sessions.commands.orchestration.mergeClips",
            "project-sessions.commands.audio.replaceAsset", "project-sessions.commands.audio.clear",
            "project-sessions.commands.project.rename", "project-sessions.resources.read", "project-sessions.resources.package-asset",
            "clients.register", "clients.heartbeat", "clients.state", "events.replay", "assets.upload", "assets.content.range",
            "project-sessions.create", "project-sessions.summary", "project-sessions.lyrics.read", "project-sessions.history.read",
            "project-sessions.lyrics.branches.read", "project-sessions.lyrics.window.read", "project-sessions.settings.read", "project-sessions.orchestration.read", "project-sessions.orchestration.geometry-reference.read", "project-sessions.orchestration.line-local-transform.read", "project-sessions.orchestration.line-presentation.read",
            "project-sessions.commands.orchestration.setLineLocalTransform", "orchestration.measurements.scene-measurement-v1",
            "project-sessions.preset-applications.read",
            "project-sessions.preset-application-previews.create", "template-preview-sessions.create", "template-preview-options.read",
            "render-backends.read", "preview-sessions.render-backend.update",
            "project-sessions.commands.lyrics.setRubySegmentTime", "project-sessions.commands.lyrics.clearRubySegmentTime",
            "project-sessions.commands.lyrics.setRubySegmentText", "project-sessions.commands.lyrics.addRubySegment",
            "project-sessions.commands.lyrics.removeRubySegment", "project-sessions.commands.lyrics.reorderRubySegments",
            "project-sessions.commands.lyrics.setRubySegmentTimes",
            "project-sessions.commands.lyrics.setTimeRange", "project-sessions.commands.history",
            "project-sessions.commands.lyrics.stampTokenStart", "project-sessions.commands.lyrics.stampTokenEnd",
            "project-sessions.commands.lyrics.clearTokenTime", "project-sessions.commands.lyrics.nudgeTokenTime",
            "project-sessions.commands.lyrics.shiftLine", "project-sessions.commands.lyrics.setTokenTimes",
            "project-sessions.commands.lyrics.setLineText", "project-sessions.commands.lyrics.setRubyText",
            "project-sessions.commands.lyrics.addLine", "project-sessions.commands.lyrics.duplicateLine",
            "project-sessions.commands.lyrics.removeLine", "project-sessions.commands.composition.setCanvasSize",
            "project-sessions.commands.composition.setFrameRate", "project-sessions.commands.composition.setBackgroundColor",
            "project-sessions.commands.composition.setBackgroundAsset", "project-sessions.commands.composition.clearBackground",
            "project-sessions.commands.composition.setBackgroundFit", "project-sessions.commands.composition.setBackgroundPlayback",
            "project-sessions.commands.composition.setOutputDuration", "project-sessions.commands.composition.useLyricsEnd",
            "project-sessions.commands.composition.useAudioDuration", "project-sessions.commands.orchestration.setDefaultContainer",
                "project-sessions.commands.orchestration.setLayoutDefaultPlacement",
                "project-sessions.commands.orchestration.setNodeGeometry",
                "project-sessions.commands.orchestration.setLayoutSettings",
                "project-sessions.commands.orchestration.setSlotAnimation",
                "project-sessions.commands.orchestration.setProgramAnimationPolicy",
                "project-sessions.commands.orchestration.previewBasicLayout", "project-sessions.commands.orchestration.applyBasicLayout",
                "project-sessions.commands.orchestration.setNode", "project-sessions.commands.orchestration.reparent",
                "project-sessions.commands.orchestration.createNode", "project-sessions.commands.orchestration.copySubtree",
                "project-sessions.commands.orchestration.reorderChildren", "project-sessions.commands.orchestration.addSlot",
                "project-sessions.commands.orchestration.removeNode", "project-sessions.commands.orchestration.reorderSlots",
                "project-sessions.commands.orchestration.createCountdown", "project-sessions.commands.orchestration.createLeadIn",
                "project-sessions.commands.orchestration.setLayoutKind", "project-sessions.commands.orchestration.setLineEnterAnimation", "project-sessions.commands.orchestration.setProjectEnterAnimation",
                "project-sessions.commands.orchestration.setLeadInGeneration", "project-sessions.commands.orchestration.previewLeadInGeneration", "project-sessions.commands.orchestration.applyLeadInGeneration",
                "project-sessions.commands.orchestration.setLeadInAttachment",
                "project-sessions.commands.orchestration.deleteLeadInAndExclude", "project-sessions.commands.orchestration.addMark", "project-sessions.commands.orchestration.setMarkText",
                "project-sessions.commands.orchestration.reorderMarks", "project-sessions.commands.orchestration.spaceMarks", "project-sessions.commands.orchestration.setCountdownAppearance", "project-sessions.commands.orchestration.setMarkAppearance",
                "project-sessions.commands.orchestration.updateLeadIn", "project-sessions.commands.orchestration.deleteLeadIn",
                "project-sessions.commands.orchestration.createClip", "project-sessions.commands.orchestration.renameClip",
                "project-sessions.commands.orchestration.setClipContainer", "project-sessions.commands.orchestration.deleteClip",
                "project-sessions.commands.lyrics.addTranslationBranch", "project-sessions.commands.lyrics.configureBranch",
                "project-sessions.commands.lyrics.removeBranch", "project-sessions.commands.lyrics.linkPrimaryLine",
                "project-sessions.commands.lyrics.splitToken", "project-sessions.commands.lyrics.mergeToken",
                "project-sessions.commands.lyrics.reorderToken", "project-sessions.commands.lyrics.resplitLine",
                "project-sessions.commands.lyrics.resplitBranch",
                "project-sessions.commands.lyrics.importAsset", "project-sessions.commands.lyrics.appendText",
                "project-sessions.commands.presetApplications.apply", "project-sessions.commands.presetApplications.update",
                "project-sessions.commands.presetApplications.setEnabled", "project-sessions.commands.presetApplications.remove",
                "project-sessions.style.read", "project-sessions.commands.styles.patch", "project-sessions.commands.styles.restore",
                "project-sessions.waveform.read", "project-sessions.waveform.peaks.read",
            "project-sessions.commands.audio.analysis.apply", "project-sessions.commands.audio.analysis.clear", "file-handles.create",
            "author.drafts.profile-list", "author.drafts.claim", "author.drafts.package-files.read", "author.drafts.package-files.write", "author.drafts.source-revisions", "author.drafts.package.download", "script-configurations.package.download",
            "author.drafts.create", "author.drafts.list", "author.drafts.read", "author.drafts.sources", "author.drafts.configuration", "author.drafts.compilation.read", "author.drafts.delete", "author.drafts.source-revision", "author.drafts.compile", "author.drafts.adopt-source-revision", "script-configurations.read", "script-configurations.detail", "script-configurations.commands.create", "script-configurations.commands.replace", "script-configurations.commands.copy", "script-configurations.commands.rename", "script-configurations.commands.delete",
            "project-sessions.open-jobs", "project-sessions.save-jobs", "project-sessions.close-jobs", "project-sessions.recover-jobs",
            "jobs.read", "jobs.cancel", "recovery-candidates.read", "recovery-jobs.read",
            "preview.frame.content.range", "preview.diagnostics", "preview.runtime-info", "preview.state", "preview.close",
            "validation.capabilities", "export.capabilities",
            .. (registry.ValidationCapabilities.Enabled ? EnabledValidationCapabilities : []),
            .. (registry.ExportCapabilities.Enabled ? EnabledExportCapabilities : []),
            .. (registry.AnalysisCapabilities.Enabled ? EnabledAnalysisCapabilities : []),
            .. (gpuAvailable ? EnabledGpuPreviewCapabilities : []),
            .. (previews.RuntimeInfo.Enabled ? EnabledPreviewCapabilities : [])],
        registry.ServerTimeUtc, registry.MaxCommandRetryWindowSeconds, registry.MaxCommandReceiptsPerSession,
        clients.Options.TokenLifetimeSeconds, clients.Options.LeaseLifetimeSeconds, clients.Options.MaxClients,
        clients.Options.MaxSessionsPerClient, store.UnreferencedAssetLifetimeSeconds, clients.Options.MaxEventsPerClient,
        registry.Receipts.MaxReceiptsPerClient, registry.Receipts.MaxReceiptBytes, registry.Receipts.MaxReceiptBytesPerClient,
        MaxAssets: store.MaxAssets, AudioPreparation: registry.AudioPreparationCapabilities,
        GpuPreview: gpuAvailable ? new(2560, 1440, 3, 67108864, 2, false) : null));
    }

    private static async Task<IResult> RegisterClientAsync(HttpRequest request, ClientRegistry clients,
        ProjectSessionRegistry projects, PreviewBackendService previews)
    {
        (ClientRegistrationRequest? input, IResult? error) = await ReadJsonAsync<ClientRegistrationRequest>(request);
        if (error is not null) return error;
        return Result(await clients.RegisterAsync(LoopbackSecurity.Bearer(request) ?? string.Empty, input,
            client => projects.TryReclaimClientAsync(client, previews.DrainClientAsync)), 201);
    }
    private static IResult Heartbeat(string id, HttpRequest request, ClientRegistry clients) => id != Client(request)
        ? Error(new(BackendErrorCode.UnauthorizedClient, "客户端不属于当前访问凭证。"), 403) : Results.Json(clients.Heartbeat(id));
    private static async Task<IResult> ClientStateAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (id != Client(request)) return Error(new(BackendErrorCode.UnauthorizedClient, "客户端不属于当前访问凭证。"), 403);
        return Results.Json(await registry.ReadClientStateAsync(id, request.HttpContext.RequestAborted));
    }

    private static async Task<IResult> UploadAssetAsync(HttpRequest request, ResourceStore store, ProjectSessionRegistry registry,
        UploadPreparationLimits preparationLimits, AcceptedOperationTracker accepted)
    {
        string client = Client(request);
        using IDisposable? preparation = preparationLimits.TryAcquire(client);
        if (preparation is null) return Error(new(BackendErrorCode.RequestQuotaExceeded, "上传准备并发已达到上限。"), 429);
        string? fileName = request.Query["fileName"].FirstOrDefault() ?? request.Headers["X-Mako-Asset-Name"].FirstOrDefault();
        string? contentType = request.ContentType;
        RequestMetadataDto metadata = HeaderMetadata(request);
        bool retainedRetry = registry.Receipts.HasUnexpiredReceipt(client, metadata.RequestId);
        if (!request.HasFormContentType && request.ContentLength > store.MaxAssetBytes)
            return Error(new(BackendErrorCode.ResourceTooLarge, "资源超过单文件大小上限。", "content"), 413);
        if (!request.HasFormContentType && !retainedRetry && request.ContentLength > store.MaxAssetQuotaBytes - store.UsedBytes)
            return Error(new(BackendErrorCode.ResourceQuotaExceeded, "资源总配额不足。", "content"), 429);
        ResourceStore.PreparedUpload? upload = null;
        try
        {
            if (request.HasFormContentType)
            {
                if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
                    || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                    return Error(new(BackendErrorCode.InvalidRequest, "上传必须包含一个文件。", "content"), 400);
                string? boundary = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
                if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
                    return Error(new(BackendErrorCode.InvalidRequest, "上传分段边界无效。", "content"), 400);
                Microsoft.AspNetCore.WebUtilities.MultipartReader reader = new(boundary, request.Body);
                while (await reader.ReadNextSectionAsync(request.HttpContext.RequestAborted) is { } section)
                {
                    if (!Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                        return Error(new(BackendErrorCode.InvalidRequest, "上传分段格式无效。", "content"), 400);
                    bool isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                    if (!isFile) { await section.Body.CopyToAsync(Stream.Null, request.HttpContext.RequestAborted); continue; }
                    if (upload is not null) return Error(new(BackendErrorCode.InvalidRequest, "上传必须包含一个文件。", "content"), 400);
                    string? sectionName = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(
                        disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                    upload = await store.PrepareUploadAsync(client, fileName ?? sectionName ?? string.Empty,
                        section.ContentType, section.Body, retainedRetry, request.HttpContext.RequestAborted);
                }
                if (upload is null) return Error(new(BackendErrorCode.InvalidRequest, "上传必须包含一个文件。", "content"), 400);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(fileName)) return Error(new(BackendErrorCode.InvalidRequest, "必须提供资源文件名。", "fileName"), 400);
                upload = await store.PrepareUploadAsync(client, fileName, contentType, request.Body, retainedRetry, request.HttpContext.RequestAborted);
            }
            request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            BackendOperationResult<AssetResponse> result = await accepted.RunAsync(() => registry.UploadAssetAsync(client,
                metadata, upload, CancellationToken.None));
            return Result(result, 201);
        }
        catch (InvalidDataException) { return Error(new(BackendErrorCode.InvalidRequest, "上传分段格式无效。", "content"), 400); }
        finally { upload?.Dispose(); }
    }

    private static async Task<IResult> CreateSessionAsync(HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker accepted)
    {
        (CreateProjectSessionRequest? input, IResult? error) = await ReadJsonAsync<CreateProjectSessionRequest>(request);
        if (error is not null) return error;
        if (input!.Metadata is null) return Error(new(BackendErrorCode.HostRestarted, "新建请求必须提供hostInstanceId与重试元信息。", "metadata"), 409);
        string client = Client(request);
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        ProjectSessionCreateResult result = await accepted.RunAsync(() => registry.CreateAsync(client, input, CancellationToken.None));
        if (!result.Accepted) return Error(result.Error!);
        return Results.Json(result.Summary ?? (await registry.ReadSummaryAsync(client, result.Session!.ProjectSessionId))!, statusCode: 201);
    }
    private static async Task<IResult> GetSessionAsync(string id, HttpRequest request, ProjectSessionRegistry registry) =>
        (await registry.ReadSummaryAsync(Client(request), id, request.HttpContext.RequestAborted)) is { } summary
            ? Results.Json(summary) : Error(ProjectSessionRegistry.MissingSessionError(id));
    private static async Task<IResult> GetHistoryAsync(string id, HttpRequest request, ProjectSessionRegistry registry) =>
        (await registry.ReadHistoryAsync(Client(request), id, request.HttpContext.RequestAborted)) is { } history
            ? Results.Json(history) : Error(ProjectSessionRegistry.MissingSessionError(id));
    private static async Task<IResult> GetLyricsAsync(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        string? branchId = request.Query["branchId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(branchId)) return Error(new(BackendErrorCode.InvalidRequest, "必须提供branchId。", "branchId"));
        LyricsLinesResponse? lyrics = await registry.ReadLyricsAsync(Client(request), id, branchId, request.HttpContext.RequestAborted);
        if (lyrics is not null) return Results.Json(lyrics);
        return !registry.TryGet(Client(request), id, out _) ? Error(ProjectSessionRegistry.MissingSessionError(id))
            : Error(new(BackendErrorCode.CommandRejected, "歌词来源不存在。", "branchId"), 404);
    }
    private static async Task<IResult> ExecuteCommandAsync(string id, HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker accepted)
    {
        (ProjectCommandRequest? input, IResult? error) = await ReadJsonAsync<ProjectCommandRequest>(request);
        if (error is not null) return error;
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        ProjectCommandExecutionResult result = await accepted.RunAsync(() => registry.ExecuteCommandAsync(Client(request), id, input, CancellationToken.None));
        return result.Accepted ? Results.Json(result.Response) : Error(result.Error!);
    }

    private static RequestMetadataDto HeaderMetadata(HttpRequest request) => new(request.Headers["X-Mako-Request-Id"].FirstOrDefault(),
        request.Headers["X-Mako-Host-Instance-Id"].FirstOrDefault(),
        DateTimeOffset.TryParse(request.Headers["X-Mako-Valid-Until-Utc"].FirstOrDefault(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset deadline) ? deadline : null);
    private static IResult Result<T>(BackendOperationResult<T> result, int status = 200) => result.Accepted ? Results.Json(result.Response, statusCode: status) : Error(result.Error!);
    private static IResult Error(BackendErrorResponse error, int? status = null) => Results.Json(error, contentType: "application/problem+json", statusCode: status ?? StatusCode(error.Code));
    private static async Task WriteError(HttpContext context, BackendErrorResponse error, int status)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(error, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
    private static int StatusCode(BackendErrorCode code) => code switch
    {
        BackendErrorCode.UnauthorizedClient or BackendErrorCode.ClientTokenExpired or BackendErrorCode.ResourceUnauthorized => 401,
        BackendErrorCode.ResourceNotFound or BackendErrorCode.SessionNotFound or BackendErrorCode.JobNotFound => 404,
        BackendErrorCode.PreviewSessionNotFound or BackendErrorCode.PreviewFrameNotFound or BackendErrorCode.ExportArtifactNotFound => 404,
        BackendErrorCode.ResourceExpired => 410,
        BackendErrorCode.PreviewFrameExpired or BackendErrorCode.ExportArtifactExpired => 410,
        BackendErrorCode.ResourceTooLarge => 413,
        BackendErrorCode.UnsupportedMediaType => 415,
        BackendErrorCode.RequestQuotaExceeded or BackendErrorCode.ResourceQuotaExceeded or BackendErrorCode.ClientQuotaExceeded or BackendErrorCode.SessionQuotaExceeded => 429,
        BackendErrorCode.HostRestarted or BackendErrorCode.SessionConflict or BackendErrorCode.RevisionConflict or BackendErrorCode.RequestExpired
            or BackendErrorCode.RequestIdReuse or BackendErrorCode.WriteLeaseConflict or BackendErrorCode.SessionClosing or BackendErrorCode.DirtySession
            or BackendErrorCode.ActiveJobs or BackendErrorCode.OperationNotCancellable or BackendErrorCode.PreviewGenerationConflict
            or BackendErrorCode.PreviewRequestOutOfOrder or BackendErrorCode.DiagnosticCursorExpired => 409,
        BackendErrorCode.ReadOnlySession => 403,
        BackendErrorCode.CommandRejected or BackendErrorCode.CommandNotSupported or BackendErrorCode.LyricsImportFailed or BackendErrorCode.EmptyLyrics
            or BackendErrorCode.NoAudioStream or BackendErrorCode.AudioPreparationFailed or BackendErrorCode.ProjectLoadFailed => 422,
        BackendErrorCode.InternalError or BackendErrorCode.TaskExecutionFailed => 500,
        BackendErrorCode.ValidationFailed or BackendErrorCode.ExportInputRejected or BackendErrorCode.ExportAudioUnsupported => 422,
        BackendErrorCode.PreviewUnavailable or BackendErrorCode.TaskUnavailable => 503,
        _ => 400,
    };
    private static async Task<(T? Input, IResult? Error)> ReadJsonAsync<T>(HttpRequest request) where T : class
    {
        if (!request.HasJsonContentType()) return (null, Error(new(BackendErrorCode.UnsupportedMediaType, "请求必须使用JSON媒体类型。", "contentType"), 415));
        try
        {
            T? input = await request.ReadFromJsonAsync<T>(cancellationToken: request.HttpContext.RequestAborted);
            return input is null ? (null, Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。"))) : (input, null);
        }
        catch (TemplateLibraryBudgetExceededException)
        { return (null, Error(new(BackendErrorCode.ResourceTooLarge, "模板库命令超过新增输入预算。", "command"), 413)); }
        catch (StyleCommandBudgetExceededException)
        { return (null, Error(new(BackendErrorCode.ResourceTooLarge, "样式命令超过新增输入预算。", "command"), 413)); }
        catch (ProjectCommandBudgetExceededException)
        { return (null, Error(new(BackendErrorCode.ResourceTooLarge, "segments 超过单次操作的项数预算。", "command.segments"), 413)); }
        catch (JsonException) { return (null, Error(new(BackendErrorCode.InvalidRequest, "请求JSON无效。"))); }
    }

    private static readonly JsonSerializerOptions StrictRequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions StrictGpuRequestJson = new(StrictRequestJson)
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static async Task<(T? Input, IResult? Error)> ReadStrictJsonAsync<T>(HttpRequest request, bool gpu = false) where T : class
    {
        if (!request.HasJsonContentType()) return (null, Error(new(BackendErrorCode.UnsupportedMediaType, "请求必须使用JSON媒体类型。", "contentType"), 415));
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            EnsureUniqueJsonProperties(document.RootElement);
            T? input = document.RootElement.Deserialize<T>(gpu ? StrictGpuRequestJson : StrictRequestJson);
            return input is null ? (null, Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。"))) : (input, null);
        }
        catch (JsonException) { return (null, Error(new(BackendErrorCode.InvalidRequest, "请求JSON无效。"))); }
    }

    private static void EnsureUniqueJsonProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("重复JSON属性。");
                EnsureUniqueJsonProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) EnsureUniqueJsonProperties(item);
    }
    public static int ResolveInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;
    public static long ResolveLong(string name, long fallback) => long.TryParse(Environment.GetEnvironmentVariable(name), out long value) && value > 0 ? value : fallback;
    public static IReadOnlyDictionary<string, string> ResolveRoots()
    {
        string? input = Environment.GetEnvironmentVariable("MAKO_BACKEND_ALLOWED_ROOTS");
        if (string.IsNullOrWhiteSpace(input)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(input) ?? throw new ArgumentException("Allowed roots must be a JSON object.");
    }
}
