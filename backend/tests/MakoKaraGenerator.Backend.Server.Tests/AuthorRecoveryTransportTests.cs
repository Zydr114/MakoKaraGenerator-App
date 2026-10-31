using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering.Skia;
using MakoKaraGenerator.Backend.Server;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class AuthorRecoveryTransportTests
{
    [Fact]
    public async Task Profile草稿列表已映射并要求认证()
    {
        using TemporaryDirectory directory = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        using LoopbackSecurity security = new(directory.FilePath("resources"));
        ClientRegistry clients = new("recovery-http", security.BootstrapToken);
        ClientRegistrationResponse client = clients.Register(security.BootstrapToken, new()).Response!;
        await using ProjectSessionRegistry projects = new("recovery-http", resources, NewProjectPreparationService.CreateDefault(), clients: clients);
        projects.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
        projects.ConfigureAuthorWorkflows(null);
        PreviewFrameHandleStore frames = new();
        PreviewDiagnosticsStore diagnostics = new();
        BackendPreviewSessionRegistry sessions = new(frames, diagnostics, new SkiaBackendPreviewPngEncoder());
        PreviewRuntimeInfoResponse runtime = new(false, "disabled", "static-lyrics-scene-v1", "worker-process", 1280, 720,
            8 * 1024 * 1024, new(BackendErrorCode.PreviewUnavailable, "测试未配置预览Worker。"));
        await using PreviewBackendService previews = new(projects, sessions, resources, clients, null, runtime);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddSingleton(resources);
        builder.Services.AddSingleton(clients);
        builder.Services.AddSingleton(projects);
        builder.Services.AddSingleton(security);
        builder.Services.AddSingleton(previews);
        builder.Services.AddSingleton(new AcceptedOperationTracker());
        builder.Services.AddSingleton<UploadPreparationLimits>();
        builder.Services.AddSingleton(frames);
        builder.Services.AddSingleton(diagnostics);
        await using WebApplication app = builder.Build();
        ServerEndpoints.Configure(app);
        await app.StartAsync();
        security.Started(app.Services, clients.HostInstanceId);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using HttpClient http = new() { BaseAddress = new(address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/profile/author-drafts")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", client.AccessToken);
        http.DefaultRequestHeaders.Add("X-Mako-Client-Id", client.ClientId);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/profile/author-drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/profile/author-drafts?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/profile/author-drafts?limit=1&limit=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/profile/author-drafts?unknown=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/profile/author-drafts?afterDraftId=bad")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/v1/profile/author-drafts/draft-00000000000000000000000000000000/claim?unknown=1", null)).StatusCode);
        await app.StopAsync();
    }
}
