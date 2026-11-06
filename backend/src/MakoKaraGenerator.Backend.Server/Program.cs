using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering.Skia;
using MakoKaraGenerator.Backend.Server;
using MakoKaraGenerator.Scripting.Compilation;
using Microsoft.AspNetCore.Hosting.Server.Features;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// Hosting's default request diagnostics include the query string. Credentials are never a log field.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.WebHost.UseUrls(LoopbackSecurity.ResolveUrls(args));
builder.WebHost.ConfigureKestrel(options =>
{
    long? maxAssetBytes = OptionalPositiveLong("MAKO_BACKEND_MAX_ASSET_BYTES");
    options.Limits.MaxRequestBodySize = maxAssetBytes is { } maximum ? Math.Min(maximum, long.MaxValue - 64 * 1024) + 64 * 1024 : null;
    options.Limits.MaxConcurrentConnections = 64;
    options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
string hostInstanceId = "host-" + Guid.NewGuid().ToString("N");
BackendHostOptions hostOptions = new()
{
    ResourceRoot = Environment.GetEnvironmentVariable("MAKO_BACKEND_RESOURCE_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "makokara-backend", hostInstanceId),
    MaxAssetBytes = OptionalPositiveLong("MAKO_BACKEND_MAX_ASSET_BYTES"),
    MaxAssetQuotaBytes = OptionalPositiveLong("MAKO_BACKEND_MAX_ASSET_QUOTA_BYTES"),
    UnreferencedAssetLifetimeSeconds = ServerEndpoints.ResolveInt("MAKO_BACKEND_ASSET_TTL_SECONDS", 600),
};
using LoopbackSecurity security = new(hostOptions.ResourceRoot);
ClientLifecycleOptions clientOptions = new()
{
    TokenLifetimeSeconds = ServerEndpoints.ResolveInt("MAKO_BACKEND_CLIENT_TOKEN_SECONDS", 120),
    LeaseLifetimeSeconds = ServerEndpoints.ResolveInt("MAKO_BACKEND_CLIENT_LEASE_SECONDS", 300),
    MaxEventsPerClient = ServerEndpoints.ResolveInt("MAKO_BACKEND_MAX_EVENTS", 256),
};
ClientRegistry clients = new(hostInstanceId, security.BootstrapToken, clientOptions);
ResourceStore resources = new(hostOptions);
NewProjectPreparationService preparation = NewProjectPreparationService.CreateDefault();
ProjectSessionRegistry sessions = new(hostInstanceId, resources, preparation, clients: clients);
ProjectPersistenceOptions persistenceOptions = new()
{
    StateRoot = Environment.GetEnvironmentVariable("MAKO_BACKEND_STATE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakoKaraGenerator", "backend", "state"),
    AllowedProjectRoots = ServerEndpoints.ResolveRoots(),
    AutoSaveInterval = TimeSpan.FromSeconds(ServerEndpoints.ResolveInt("MAKO_BACKEND_AUTOSAVE_SECONDS", 30)),
};
sessions.ConfigurePersistence(persistenceOptions);
BackendMediaFeatureService? mediaFeatures = null;
try { mediaFeatures = BackendMediaFeatureService.TryCreate(persistenceOptions.StateRoot); }
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{ Console.Error.WriteLine($"BACKEND_MEDIA_FEATURES_UNAVAILABLE {error.GetType().Name}"); }
sessions.ConfigureMediaFeatures(mediaFeatures);
string scriptReferences = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
IScriptCompilationService? scriptCompiler = null;
if (Directory.Exists(scriptReferences))
{
    try { scriptCompiler = new ScriptCompiler(scriptReferences, Path.Combine(persistenceOptions.StateRoot, "script-artifacts")); }
    catch (ArgumentException) { }
}
sessions.ConfigureAuthorWorkflows(scriptReferences, scriptCompiler);
sessions.ConfigureScriptConfigurations();
sessions.ConfigureTemplateLibrary();
AcceptedOperationTracker accepted = new();
PreviewWorkerConfiguration previewWorker = await PreviewWorkerConfiguration.ResolveAsync().ConfigureAwait(false);
if (previewWorker.Options is { } workerOptions)
    previewWorker = previewWorker with { Options = workerOptions with { ScriptCompilationService = scriptCompiler } };
BackendRenderBackendConfiguration renderBackends = new(previewWorker.Options);
sessions.ConfigureRenderBackends(renderBackends);
sessions.ConfigureSceneMeasurements(previewWorker.Options);
sessions.ConfigureFontWorker(previewWorker.Options);
int audioPreparationTimeoutSeconds = 60;
string? configuredPreparationTimeout = Environment.GetEnvironmentVariable("MAKO_BACKEND_AUDIO_PREPARATION_TIMEOUT_SECONDS");
if (configuredPreparationTimeout is not null && (!int.TryParse(configuredPreparationTimeout, NumberStyles.None,
    CultureInfo.InvariantCulture, out audioPreparationTimeoutSeconds) || audioPreparationTimeoutSeconds is < 1 or > 120))
    throw new InvalidOperationException("音频准备超时必须为1至120秒。");
AudioPreparationWorkerConfiguration audioPreparationWorker = await AudioPreparationWorkerConfiguration.ResolveAsync(
    previewWorker.Options, audioPreparationTimeoutSeconds).ConfigureAwait(false);
sessions.ConfigureAudioPreparation(audioPreparationWorker.Executor, audioPreparationWorker.Capabilities,
    operationTimeoutSeconds: audioPreparationTimeoutSeconds);
int validationTimeoutSeconds = 60;
string? configuredValidationTimeout = Environment.GetEnvironmentVariable("MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS");
if (configuredValidationTimeout is not null && (!int.TryParse(configuredValidationTimeout, NumberStyles.None,
    CultureInfo.InvariantCulture, out validationTimeoutSeconds) || validationTimeoutSeconds is < 1 or > 60))
    throw new InvalidOperationException("正式验证超时必须为1至60秒。");
sessions.ConfigureValidationTasks(previewWorker.Options is null ? null : new WorkerBackendValidationExecutor(previewWorker.Options,
    TimeSpan.FromSeconds(validationTimeoutSeconds)), operationTimeoutSeconds: validationTimeoutSeconds);
int? exportTimeoutSeconds = OptionalExportInt("MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS");
BackendExportOptions exportOptions = new()
{
    MaxFrames = OptionalExportInt("MAKO_BACKEND_EXPORT_MAX_FRAMES"),
    MaxDurationUs = OptionalPositiveLong("MAKO_BACKEND_EXPORT_MAX_DURATION_US"),
    MaxFrameRate = OptionalExportInt("MAKO_BACKEND_EXPORT_MAX_FPS"),
    MaxCanvasWidth = OptionalExportInt("MAKO_BACKEND_EXPORT_MAX_CANVAS_WIDTH"),
    MaxCanvasHeight = OptionalExportInt("MAKO_BACKEND_EXPORT_MAX_CANVAS_HEIGHT"),
    MaxRawFrameBytes = OptionalPositiveLong("MAKO_BACKEND_EXPORT_MAX_RAW_FRAME_BYTES"),
    MaxMediaBytes = OptionalPositiveLong("MAKO_BACKEND_EXPORT_MAX_MEDIA_BYTES"),
    MaxAudioBytes = OptionalPositiveLong("MAKO_BACKEND_EXPORT_MAX_AUDIO_BYTES"),
    MaxCompressedAudioDurationUs = OptionalPositiveLong("MAKO_BACKEND_EXPORT_MAX_COMPRESSED_AUDIO_DURATION_US"),
    MaxArtifactStorageBytes = OptionalPositiveLong("MAKO_BACKEND_EXPORT_MAX_ARTIFACT_STORAGE_BYTES"),
};
ExportWorkerConfiguration exportWorker = await ExportWorkerConfiguration.ResolveAsync(previewWorker.Options, exportTimeoutSeconds, exportOptions).ConfigureAwait(false);
sessions.ConfigureExportTasks(exportWorker.Executor, exportWorker.Capabilities, exportOptions, operationTimeoutSeconds: exportTimeoutSeconds);
int analysisTimeoutSeconds = 60;
string? configuredAnalysisTimeout = Environment.GetEnvironmentVariable("MAKO_BACKEND_ANALYSIS_TIMEOUT_SECONDS");
if (configuredAnalysisTimeout is not null && (!int.TryParse(configuredAnalysisTimeout, NumberStyles.None,
    CultureInfo.InvariantCulture, out analysisTimeoutSeconds) || analysisTimeoutSeconds is < 1 or > 60))
    throw new InvalidOperationException("音频分析超时必须为1至60秒。");
AnalysisWorkerConfiguration analysisWorker = await AnalysisWorkerConfiguration.ResolveAsync(previewWorker.Options, analysisTimeoutSeconds).ConfigureAwait(false);
sessions.ConfigureAnalysisTasks(analysisWorker.Executor, analysisWorker.Capabilities, operationTimeoutSeconds: analysisTimeoutSeconds);
int frameLeaseSeconds = ServerEndpoints.ResolveInt("MAKO_BACKEND_PREVIEW_FRAME_LEASE_SECONDS", 30);
if (frameLeaseSeconds > 300) throw new InvalidOperationException("预览帧租约配置必须为1至300秒。");
PreviewFrameHandleStore previewFrames = new();
PreviewDiagnosticsStore previewDiagnostics = new();
BackendPreviewSessionRegistry previewSessions = new(previewFrames, previewDiagnostics, new SkiaBackendPreviewPngEncoder(),
    new() { Limits = new(1280, 720, 1, 2, 8 * 1024 * 1024,
        frameLeaseSeconds, 128, 4 * 1024 * 1024) });
PreviewBackendService previewService = new(sessions, previewSessions, resources, clients,
    previewWorker.Options is null ? null : new ProjectPreviewFrameSessionFactory(
        new WorkerRenderFrameSessionFactory(previewWorker.Options with { RenderBackend = renderBackends.DefaultBackend }),
        new WorkerPackageRenderFrameSessionFactory(previewWorker.Options with { RenderBackend = renderBackends.DefaultBackend })),
    previewWorker.Info with { FrameLeaseLifetimeSeconds = frameLeaseSeconds }, renderBackends);
previewService.ConfigureGpuStreams(DesktopConsumerIdentity.FromEnvironment(), previewWorker.Options);
builder.Services.AddSingleton(resources);
builder.Services.AddSingleton(sessions);
builder.Services.AddSingleton(hostOptions);
builder.Services.AddSingleton(clients);
builder.Services.AddSingleton(security);
builder.Services.AddSingleton(accepted);
builder.Services.AddSingleton<UploadPreparationLimits>();
builder.Services.AddSingleton(previewFrames);
builder.Services.AddSingleton(previewDiagnostics);
builder.Services.AddSingleton(previewService);
await using WebApplication app = builder.Build();
Task audioPreparationShutdown = Task.CompletedTask;
app.Lifetime.ApplicationStopping.Register(() =>
{
    accepted.StopAccepting();
    previewService.StopAccepting();
    audioPreparationShutdown = sessions.StopAudioPreparationAsync();
});
ServerEndpoints.Configure(app);
await app.StartAsync().ConfigureAwait(false);
security.Started(app.Services, hostInstanceId);
Task resourceCleanup = resources.RunCleanupAsync(app.Lifetime.ApplicationStopping);
Task previewCleanup = previewService.RunLeaseCleanupAsync(app.Lifetime.ApplicationStopping);
string address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
    .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? throw new InvalidOperationException("No listening address.");
Console.WriteLine($"BACKEND_READY {address} {hostInstanceId}");
await app.WaitForShutdownAsync().ConfigureAwait(false);
await resourceCleanup.ConfigureAwait(false);
await previewCleanup.ConfigureAwait(false);
await audioPreparationShutdown.ConfigureAwait(false);
await accepted.DrainAsync().ConfigureAwait(false);
if (!await ServerShutdown.DisposeAsync(previewService.DisposeAsync, sessions.DisposeAsync, renderBackends.Dispose,
    Console.Error).ConfigureAwait(false)) Environment.ExitCode = 1;
if (mediaFeatures is not null)
{
    try { await mediaFeatures.DisposeAsync().ConfigureAwait(false); }
    catch (Exception error)
    {
        Console.Error.WriteLine($"BACKEND_SHUTDOWN_FAILED media-features {error.GetType().Name}");
        Environment.ExitCode = 1;
    }
}

static long? OptionalPositiveLong(string variable)
{
    string? value = Environment.GetEnvironmentVariable(variable);
    if (value is null) return null;
    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
        throw new InvalidOperationException($"{variable} 必须为正整数；省略表示不配置此限额。");
    return number;
}

static int? OptionalExportInt(string variable)
{
    long? value = OptionalPositiveLong(variable);
    if (value > int.MaxValue) throw new InvalidOperationException($"{variable} 超出整数表示范围。");
    return (int?)value;
}
