using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 切换同设备保持帧代次且回执重放不重新创建资源改变后端则冲突()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage frameRequest = await fixture.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests", fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, frameRequest.StatusCode);
        (await fixture.Factory.NextRender()).Release.SetResult();
        PreviewSessionResponse ready = (await fixture.WaitReady(preview.PreviewId)).Session;
        int profileChecks = fixture.Factory.ProfileChecks;
        string route = $"/api/v1/preview-sessions/{preview.PreviewId}/render-backend";
        PreviewRenderBackendRequest input = new(preview.PreviewGeneration, "cpu", fixture.Metadata());

        using HttpResponseMessage first = await fixture.Http.PutAsJsonAsync(route, input, Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        PreviewSessionResponse repeated = (await first.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal(ready.PreviewGeneration, repeated.PreviewGeneration);
        Assert.Equal(ready.LatestFrame, repeated.LatestFrame);
        using HttpResponseMessage replay = await fixture.Http.PutAsJsonAsync(route, input, Json);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal(profileChecks, fixture.Factory.ProfileChecks);
        Assert.Equal(1, fixture.Factory.Created);
        using HttpResponseMessage changed = await fixture.Http.PutAsJsonAsync(route, input with { RenderBackend = "gpu" }, Json);
        await Error(changed, HttpStatusCode.Conflict, BackendErrorCode.RequestIdReuse);

        using HttpResponseMessage missingMetadata = await fixture.Http.PutAsJsonAsync(route, input with { Metadata = null }, Json);
        await Error(missingMetadata, HttpStatusCode.Conflict, BackendErrorCode.HostRestarted);
        ClientRegistrationResponse other = fixture.Clients.Register(fixture._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreign = new(HttpMethod.Put, route)
        {
            Content = JsonContent.Create(input with { Metadata = fixture.Metadata() }, options: Json),
        };
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreign.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage refused = await fixture.Http.SendAsync(foreign);
        await Error(refused, HttpStatusCode.NotFound, BackendErrorCode.PreviewSessionNotFound);
        using HttpResponseMessage bytes = await fixture.Http.GetAsync(ready.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal(ready.PreviewGeneration, fixture.Previews.Read(fixture.Client.ClientId, preview.PreviewId).Status!.Session.PreviewGeneration);
    }

    [Fact]
    public async Task 未配置设备检查时GPU切换明确失败且保留旧帧固定快照和代次()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage frameRequest = await fixture.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests", fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, frameRequest.StatusCode);
        (await fixture.Factory.NextRender()).Release.SetResult();
        PreviewSessionResponse ready = (await fixture.WaitReady(preview.PreviewId)).Session;
        var captured = fixture.Previews.CaptureRenderBackendInput(fixture.Client.ClientId, preview.PreviewId, preview.PreviewGeneration);
        Assert.True(captured.Accepted);
        try
        {
            using HttpResponseMessage failed = await fixture.Http.PutAsJsonAsync(
                $"/api/v1/preview-sessions/{preview.PreviewId}/render-backend",
                new PreviewRenderBackendRequest(preview.PreviewGeneration, "gpu", fixture.Metadata()), Json);
            await Error(failed, HttpStatusCode.ServiceUnavailable, BackendErrorCode.PreviewUnavailable);
            PreviewSessionResponse current = (await fixture.Http.GetFromJsonAsync<PreviewSessionStateResponse>(
                $"/api/v1/preview-sessions/{preview.PreviewId}", Json))!.Session;
            Assert.Equal(ready.PreviewGeneration, current.PreviewGeneration);
            Assert.Equal(ready.Source, current.Source);
            Assert.Equal("cpu", current.RenderBackend);
            Assert.Equal(ready.LatestFrame, current.LatestFrame);
            var pinned = fixture.Previews.CaptureRenderBackendInput(fixture.Client.ClientId, preview.PreviewId, preview.PreviewGeneration);
            Assert.True(pinned.Accepted);
            try { Assert.Same(captured.Response!.Snapshot, pinned.Response!.Snapshot); }
            finally { pinned.Response!.RetentionLease!.Dispose(); }
            Assert.Equal(1, fixture.Factory.Created);
            using HttpResponseMessage content = await fixture.Http.GetAsync(current.LatestFrame!.Content.ContentUrl);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        }
        finally { captured.Response!.RetentionLease!.Dispose(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("not-a-number")]
    [InlineData("9223372036854775808")]
    public async Task 后端切换拒绝非规范代次且不改变预览(string? generation)
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage invalid = await fixture.Http.PutAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/render-backend",
            new PreviewRenderBackendRequest(generation!, "cpu", fixture.Metadata()), Json);
        BackendErrorResponse error = await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("expectedPreviewGeneration", error.Field);
        Assert.Equal(preview.PreviewGeneration, fixture.Previews.Read(fixture.Client.ClientId, preview.PreviewId).Status!.Session.PreviewGeneration);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Theory]
    [InlineData("missingGeneration")]
    [InlineData("missingBackend")]
    [InlineData("backendNull")]
    [InlineData("nativeBackendName")]
    [InlineData("unknownField")]
    [InlineData("duplicateField")]
    public async Task 后端切换严格拒绝缺失未知重复字段和非公开后端(string mutation)
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        string metadata = JsonSerializer.Serialize(fixture.Metadata(), Json);
        string generation = JsonSerializer.Serialize(preview.PreviewGeneration);
        string json = mutation switch
        {
            "missingGeneration" => $$"""{"renderBackend":"cpu","metadata":{{metadata}}}""",
            "missingBackend" => $$"""{"expectedPreviewGeneration":{{generation}},"metadata":{{metadata}}}""",
            "backendNull" => $$"""{"expectedPreviewGeneration":{{generation}},"renderBackend":null,"metadata":{{metadata}}}""",
            "nativeBackendName" => $$"""{"expectedPreviewGeneration":{{generation}},"renderBackend":"vulkan","metadata":{{metadata}}}""",
            "unknownField" => $$"""{"expectedPreviewGeneration":{{generation}},"renderBackend":"cpu","device":"fixture","metadata":{{metadata}}}""",
            _ => $$"""{"expectedPreviewGeneration":{{generation}},"renderBackend":"cpu","renderBackend":"gpu","metadata":{{metadata}}}""",
        };
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage rejected = await fixture.Http.PutAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/render-backend", body);
        BackendErrorResponse error = await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        if (mutation == "missingGeneration")
            Assert.Equal("expectedPreviewGeneration", error.Field);
        Assert.Equal(preview.PreviewGeneration, fixture.Previews.Read(fixture.Client.ClientId, preview.PreviewId).Status!.Session.PreviewGeneration);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task 规范但过期的零代次仍是冲突而不是格式错误()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage stale = await fixture.Http.PutAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/render-backend",
            new PreviewRenderBackendRequest("0", "cpu", fixture.Metadata()), Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.PreviewGenerationConflict);
        Assert.Equal(preview.PreviewGeneration, fixture.Previews.Read(fixture.Client.ClientId, preview.PreviewId).Status!.Session.PreviewGeneration);
    }

    [Fact]
    public async Task 后端列表必须有客户端授权并拒绝过期租约外站Origin及URL凭证()
    {
        await using Fixture fixture = await Fixture.Create(leaseSeconds: 2);
        using HttpResponseMessage valid = await fixture.Http.GetAsync("/api/v1/render-backends");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        RenderBackendsResponse backends = (await valid.Content.ReadFromJsonAsync<RenderBackendsResponse>(Json))!;
        Assert.Equal("cpu", backends.DefaultBackend);
        Assert.Collection(backends.Backends,
            backend => Assert.Equal("cpu", backend.RenderBackend),
            backend => Assert.Equal("gpu", backend.RenderBackend));
        Assert.All(backends.Backends, backend => Assert.False(backend.Available));
        Assert.Equal(0, fixture.Factory.Created);

        using HttpClient anonymous = new() { BaseAddress = fixture.Http.BaseAddress };
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync("/api/v1/render-backends");
        await Error(unauthenticated, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
        using HttpRequestMessage foreignOrigin = new(HttpMethod.Get, "/api/v1/render-backends");
        foreignOrigin.Headers.Add("Origin", "https://untrusted.example");
        using HttpResponseMessage blocked = await fixture.Http.SendAsync(foreignOrigin);
        await Error(blocked, HttpStatusCode.Forbidden, BackendErrorCode.UnauthorizedClient);
        using HttpResponseMessage urlCredential = await fixture.Http.GetAsync("/api/v1/render-backends?access_token=invalid");
        await Error(urlCredential, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        fixture.Clock.Advance(3);
        await fixture.Service.DrainExpiredClientsAsync();
        using HttpResponseMessage expired = await fixture.Http.GetAsync("/api/v1/render-backends");
        await Error(expired, HttpStatusCode.Unauthorized, BackendErrorCode.ClientTokenExpired);
    }
}
