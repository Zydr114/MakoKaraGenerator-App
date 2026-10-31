using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering.Skia;
using MakoKaraGenerator.Backend.Server;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class AuthorWorkflowTransportTests
{
    [Fact]
    public async Task HTTP作者草稿创建编译完整诊断读取删除并释放保护()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        EffectPackageContents package = EffectPackageFixture.Contents();
        string archive = fixture.Directory.FilePath("package.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "package.mkfx", "application/octet-stream", stream);

        using HttpResponseMessage createResponse = await fixture.Http.PostAsJsonAsync("/api/v1/drafts",
            new AuthorDraftCreateRequest(asset.AssetId, "scale", "transport", fixture.Metadata()));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        AuthorDraftResponse draft = (await createResponse.Content.ReadFromJsonAsync<AuthorDraftResponse>())!;

        using HttpResponseMessage compileResponse = await fixture.Http.PostAsJsonAsync($"/api/v1/drafts/{draft.DraftId}/compilation-jobs",
            new AuthorCompilationRequest(draft.DraftRevision, fixture.Metadata()));
        Assert.Equal(HttpStatusCode.OK, compileResponse.StatusCode);
        AuthorCompilationResponse compiled = (await compileResponse.Content.ReadFromJsonAsync<AuthorCompilationResponse>())!;
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Diagnostics.Select(diagnostic => diagnostic.Message)));

        using HttpResponseMessage readCompilation = await fixture.Http.GetAsync($"/api/v1/drafts/{draft.DraftId}/compilation");
        Assert.Equal(HttpStatusCode.OK, readCompilation.StatusCode);
        AuthorCompilationResponse persisted = (await readCompilation.Content.ReadFromJsonAsync<AuthorCompilationResponse>())!;
        Assert.Equal(compiled.Diagnostics, persisted.Diagnostics);

        using HttpRequestMessage preflight = new(HttpMethod.Options, $"/api/v1/drafts/{draft.DraftId}");
        preflight.Headers.TryAddWithoutValidation("Origin", fixture.Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "DELETE");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,content-type,x-mako-client-id");
        using HttpResponseMessage preflightResponse = await fixture.Http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Contains("DELETE", string.Join(',', preflightResponse.Headers.GetValues("Access-Control-Allow-Methods")), StringComparison.Ordinal);

        using HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/drafts/{draft.DraftId}")
        {
            Content = JsonContent.Create(new AuthorDraftDeleteRequest(draft.DraftRevision, fixture.Metadata()))
        };
        using HttpResponseMessage deleted = await fixture.Http.SendAsync(delete);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(fixture.Projects.HasAuthorDraft(fixture.Client.ClientId));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TemporaryDirectory Directory;
        internal readonly ResourceStore Resources;
        internal readonly ClientRegistry Clients;
        internal readonly ClientRegistrationResponse Client;
        internal readonly ProjectSessionRegistry Projects;
        internal readonly HttpClient Http;
        private readonly WebApplication _app;
        private readonly LoopbackSecurity _security;
        private readonly PreviewBackendService _previews;

        private Fixture(TemporaryDirectory directory, ResourceStore resources, ClientRegistry clients, ClientRegistrationResponse client,
            ProjectSessionRegistry projects, HttpClient http, WebApplication app, LoopbackSecurity security, PreviewBackendService previews)
        {
            Directory = directory; _previews = previews;
            Resources = resources; Clients = clients; Client = client; Projects = projects; Http = http; _app = app; _security = security;
        }

        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Clients.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));

        internal static async Task<Fixture> CreateAsync()
        {
            TemporaryDirectory directory = new();
            try
            {
                string resourceRoot = directory.FilePath("resources");
                ResourceStore resources = new(new() { ResourceRoot = resourceRoot });
                LoopbackSecurity security = new(resourceRoot);
                ClientRegistry clients = new("author-http-host", security.BootstrapToken);
                ClientRegistrationResponse client = clients.Register(security.BootstrapToken, new()).Response!;
                ProjectSessionRegistry projects = new("author-http-host", resources, NewProjectPreparationService.CreateDefault(), clients: clients);
                projects.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
                projects.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
                PreviewFrameHandleStore frames = new();
                PreviewDiagnosticsStore diagnostics = new();
                BackendPreviewSessionRegistry previewSessions = new(frames, diagnostics, new SkiaBackendPreviewPngEncoder());
                PreviewRuntimeInfoResponse runtime = new(false, "disabled", "static-lyrics-scene-v1", "worker-process", 1280, 720,
                    8 * 1024 * 1024, new(BackendErrorCode.PreviewUnavailable, "测试未配置预览Worker。"));
                PreviewBackendService previewService = new(projects, previewSessions, resources, clients, null, runtime);

                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
                builder.Services.AddSingleton(resources);
                builder.Services.AddSingleton(clients);
                builder.Services.AddSingleton(projects);
                builder.Services.AddSingleton(security);
                builder.Services.AddSingleton(previewService);
                builder.Services.AddSingleton(new AcceptedOperationTracker());
                builder.Services.AddSingleton<UploadPreparationLimits>();
                builder.Services.AddSingleton(frames);
                builder.Services.AddSingleton(diagnostics);
                WebApplication app = builder.Build();
                ServerEndpoints.Configure(app);
                await app.StartAsync();
                security.Started(app.Services, clients.HostInstanceId);
                string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                HttpClient http = new() { BaseAddress = new(address) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", client.AccessToken);
                http.DefaultRequestHeaders.Add("X-Mako-Client-Id", client.ClientId);
                return new Fixture(directory, resources, clients, client, projects, http, app, security, previewService);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _previews.DisposeAsync();
            await Projects.DisposeAsync();
            await _app.DisposeAsync();
            _security.Dispose();
            Directory.Dispose();
        }
    }
}
