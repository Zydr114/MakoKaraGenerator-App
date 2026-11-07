using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering.Skia;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public void 组合租约失败时保留主错误且只重试失败项()
    {
        var first = new ControlledRetention(true);
        var second = new ControlledRetention(false);
        Type type = typeof(PreviewBackendService).GetNestedType("CombinedRetention", System.Reflection.BindingFlags.NonPublic)!;
        IDisposable combined = (IDisposable)Activator.CreateInstance(type, first, second)!;
        Assert.Equal("first failed", Assert.Throws<IOException>(() => combined.Dispose()).Message);
        Assert.Equal(1, first.Count);
        Assert.Equal(1, second.Count);
        combined.Dispose();
        Assert.Equal(2, first.Count);
        Assert.Equal(1, second.Count);
    }

    private sealed class ControlledRetention(bool failFirst) : IDisposable
    {
        internal int Count;
        public void Dispose()
        {
            Count++;
            if (failFirst && Count == 1) throw new IOException("first failed");
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Fact]
    public async Task 导出HTTP接受后断连不取消并流式读取RangeHEAD及ETag且跨client拒绝()
    {
        await using Fixture fixture = await Fixture.Create(exportFixture: true);
        ExportJobRequest input = new("0", IncludeAudio: false, Metadata: fixture.Metadata());
        string route = $"/api/v1/project-sessions/{fixture.Project.ProjectSessionId}/export-jobs";
        using CancellationTokenSource disconnected = new();
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync(route, input, Json, disconnected.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        disconnected.Cancel(); await fixture.Exporter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(fixture.Exporter.Token.IsCancellationRequested);
        using HttpResponseMessage retry = await fixture.Http.PostAsJsonAsync(route, input, Json);
        Assert.Equal(job.JobId, (await retry.Content.ReadFromJsonAsync<JobResponse>(Json))!.JobId);
        fixture.Exporter.Release.TrySetResult();
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!.State == "succeeded");
        string content = $"/api/v1/jobs/{job.JobId}/export-content";
        using HttpResponseMessage report = await fixture.Http.GetAsync($"/api/v1/jobs/{job.JobId}/export-result"); Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using HttpResponseMessage full = await fixture.Http.GetAsync(content); Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(ExportExecutor.Bytes, await full.Content.ReadAsByteArrayAsync()); Assert.Equal("video/mp4", full.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(full.Content.Headers.ContentDisposition); string etag = full.Headers.ETag!.ToString();
        using HttpRequestMessage headRequest = new(HttpMethod.Head, content); using HttpResponseMessage head = await fixture.Http.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(ExportExecutor.Bytes.Length, head.Content.Headers.ContentLength); Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage rangeRequest = new(HttpMethod.Get, content); rangeRequest.Headers.Range = new RangeHeaderValue(1, 4);
        using HttpResponseMessage range = await fixture.Http.SendAsync(rangeRequest); Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(ExportExecutor.Bytes[1..5], await range.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage cachedRequest = new(HttpMethod.Get, content); cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using HttpResponseMessage cached = await fixture.Http.SendAsync(cachedRequest); Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        ClientRegistrationResponse other = fixture.Clients.Register(fixture._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreign = new(HttpMethod.Get, content); foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreign.Headers.Add("X-Mako-Client-Id", other.ClientId); using HttpResponseMessage refused = await fixture.Http.SendAsync(foreign);
        await Error(refused, HttpStatusCode.NotFound, BackendErrorCode.ExportArtifactNotFound);
        Assert.Equal(1, fixture.Exporter.Calls);
    }

    [Fact]
    public async Task 实际导出失败HTTP报告分型可读且没有媒体下载入口()
    {
        await using Fixture fixture = await Fixture.Create(exportFixture: true);
        Guid epoch = Guid.NewGuid();
        var actual = new MakoKaraGenerator.Scripting.RuntimeDiagnostic(new("actual"), "artifact", "render", 0,
            new(MakoKaraGenerator.Scripting.DiagnosticSeverity.Error, "ActualError", "actual failure"));
        fixture.Exporter.Failure = new("ActualFailure", "controlled", RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 1, actual)]));
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/project-sessions/{fixture.Project.ProjectSessionId}/export-jobs",
            new ExportJobRequest("0", IncludeAudio: false, Metadata: fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        await fixture.Exporter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); fixture.Exporter.Release.TrySetResult();
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!.State == "failed");
        JobResponse failed = fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!;
        Assert.Null(failed.ExportResult); Assert.NotNull(failed.FailureReportRef); Assert.Equal("ActualFailure", failed.FailureCode);
        ExportFailureReportDto report = (await fixture.Http.GetFromJsonAsync<ExportFailureReportDto>($"/api/v1/jobs/{job.JobId}/export-result", Json))!;
        Assert.Equal("exportFailure", report.Kind); Assert.Equal(epoch, Assert.Single(report.Diagnostics).Occurrence!.Epoch);
        ExportDiagnosticsPageDto diagnostics = (await fixture.Http.GetFromJsonAsync<ExportDiagnosticsPageDto>($"/api/v1/jobs/{job.JobId}/export-diagnostics", Json))!;
        Assert.Equal("ActualError", Assert.Single(diagnostics.Diagnostics).Code);
        using HttpResponseMessage content = await fixture.Http.GetAsync($"/api/v1/jobs/{job.JobId}/export-content");
        await Error(content, HttpStatusCode.NotFound, BackendErrorCode.ExportArtifactNotFound);
    }

    [Fact]
    public async Task 导出拒绝未部署与未知路径并明确拒绝未授权格式音频()
    {
        await using Fixture disabled = await Fixture.Create(enabled: false, exportFixture: true);
        ExportTaskCapabilitiesDto caps = (await disabled.Http.GetFromJsonAsync<ExportTaskCapabilitiesDto>("/api/v1/export-capabilities", Json))!;
        Assert.False(caps.Enabled); Assert.Null(caps.OperationTimeoutSeconds);
        using HttpResponseMessage unavailable = await disabled.Http.PostAsJsonAsync($"/api/v1/project-sessions/{disabled.Project.ProjectSessionId}/export-jobs",
            new ExportJobRequest("0", IncludeAudio: false, Metadata: disabled.Metadata()), Json);
        await Error(unavailable, HttpStatusCode.ServiceUnavailable, BackendErrorCode.TaskUnavailable);
        await using Fixture enabled = await Fixture.Create(exportFixture: true);
        string path = $"/api/v1/project-sessions/{enabled.Project.ProjectSessionId}/export-jobs";
        using StringContent raw = new("{\"expectedRevision\":\"0\",\"includeAudio\":false,\"outputPath\":\"/etc/passwd\"}", Encoding.UTF8, "application/json");
        using HttpResponseMessage unsafeInput = await enabled.Http.PostAsync(path, raw); await Error(unsafeInput, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        using HttpResponseMessage audio = await enabled.Http.PostAsJsonAsync(path, new ExportJobRequest("0", Metadata: enabled.Metadata()), Json);
        await Error(audio, HttpStatusCode.UnprocessableEntity, BackendErrorCode.ExportAudioUnsupported);
        using HttpResponseMessage format = await enabled.Http.PostAsJsonAsync(path, new ExportJobRequest("0", "webm", false, enabled.Metadata()), Json);
        await Error(format, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        Assert.Equal(0, enabled.Exporter.Calls);
    }

    [Fact]
    public async Task 正式验证HTTP接受后断连不取消且唯一回执及报告读取遵守客户端授权()
    {
        await using Fixture fixture = await Fixture.Create();
        ValidationJobRequest input = new("0", 3, fixture.Metadata());
        using CancellationTokenSource disconnected = new();
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/project-sessions/{fixture.Project.ProjectSessionId}/validation-jobs", input, Json, disconnected.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        disconnected.Cancel();
        await fixture.Validation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(fixture.Validation.Token.IsCancellationRequested);
        using HttpResponseMessage duplicate = await fixture.Http.PostAsJsonAsync($"/api/v1/project-sessions/{fixture.Project.ProjectSessionId}/validation-jobs", input, Json);
        Assert.Equal(job.JobId, (await duplicate.Content.ReadFromJsonAsync<JobResponse>(Json))!.JobId);
        fixture.Validation.Release.TrySetResult();
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!.State == "succeeded");
        using HttpResponseMessage report = await fixture.Http.GetAsync($"/api/v1/jobs/{job.JobId}/validation-result");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        byte[] bytes = await report.Content.ReadAsByteArrayAsync();
        Assert.Equal(fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!.ValidationResult!.Report.Sha256, WorkerProtocol.ComputeSha256(bytes));
        Assert.Equal(1, fixture.Validation.Calls);
        using HttpResponseMessage invalid = await fixture.Http.GetAsync($"/api/v1/jobs/{job.JobId}/validation-diagnostics?limit=0");
        await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.DiagnosticLimitExceeded);
        using HttpResponseMessage repeated = await fixture.Http.GetAsync($"/api/v1/jobs/{job.JobId}/validation-diagnostics?limit=1&limit=2");
        await Error(repeated, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        ClientRegistrationResponse other = fixture.Clients.Register(fixture._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreign = new(HttpMethod.Get, $"/api/v1/jobs/{job.JobId}/validation-result");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreign.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage refused = await fixture.Http.SendAsync(foreign);
        await Error(refused, HttpStatusCode.NotFound, BackendErrorCode.JobNotFound);
    }

    [Fact]
    public async Task 正式验证未部署时公布disabled且拒绝裸path及错误修订计划()
    {
        await using Fixture disabled = await Fixture.Create(enabled: false);
        ValidationTaskCapabilitiesDto caps = (await disabled.Http.GetFromJsonAsync<ValidationTaskCapabilitiesDto>("/api/v1/validation-capabilities", Json))!;
        Assert.False(caps.Enabled); Assert.Equal(60, caps.OperationTimeoutSeconds);
        using HttpResponseMessage unavailable = await disabled.Http.PostAsJsonAsync($"/api/v1/project-sessions/{disabled.Project.ProjectSessionId}/validation-jobs", new ValidationJobRequest("0", 3, disabled.Metadata()), Json);
        await Error(unavailable, HttpStatusCode.ServiceUnavailable, BackendErrorCode.TaskUnavailable);
        await using Fixture enabled = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{enabled.Project.ProjectSessionId}/validation-jobs";
        using StringContent raw = new("{\"expectedRevision\":\"0\",\"framesPerOutput\":3,\"path\":\"/etc/passwd\"}", Encoding.UTF8, "application/json");
        using HttpResponseMessage rejected = await enabled.Http.PostAsync(path, raw);
        await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        using HttpResponseMessage stale = await enabled.Http.PostAsJsonAsync(path, new ValidationJobRequest("1", 3, enabled.Metadata()), Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using HttpResponseMessage budget = await enabled.Http.PostAsJsonAsync(path, new ValidationJobRequest("0", 601, enabled.Metadata()), Json);
        await Error(budget, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(0, enabled.Validation.Calls);
    }

    [Fact]
    public async Task 真实HTTP创建统一回执并拒绝跨方法重用及非项目源和未知snapshot字段()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionRequest request = fixture.CreateRequest();
        using HttpResponseMessage first = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions", request, Json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        PreviewSessionResponse created = (await first.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        using HttpResponseMessage retry = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions", request, Json);
        Assert.Equal(created.PreviewId, (await retry.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!.PreviewId);
        Assert.Equal(1, fixture.Previews.Count);
        using HttpResponseMessage reuse = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{created.PreviewId}/frame-requests",
            new PreviewFrameCommandRequest("0", created.PreviewGeneration, "1", "0", "playback", request.Metadata), Json);
        await Error(reuse, HttpStatusCode.Conflict, BackendErrorCode.RequestIdReuse);
        using HttpResponseMessage invalidSource = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            fixture.CreateRequest() with { Source = request.Source with { Kind = "snapshot" } }, Json);
        await Error(invalidSource, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        using HttpResponseMessage purpose = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            fixture.CreateRequest() with { Purpose = "export" }, Json);
        await Error(purpose, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        using StringContent unsafeBody = new("{\"source\":{\"kind\":\"project\",\"sourceId\":\"x\",\"sourceRevision\":\"0\"},\"size\":{\"width\":1,\"height\":1},\"snapshot\":{\"audioPath\":\"/etc/passwd\"}}", Encoding.UTF8, "application/json");
        using HttpResponseMessage unknown = await fixture.Http.PostAsync("/api/v1/preview-sessions", unsafeBody);
        await Error(unknown, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task 未部署Worker报告真实disabled并返回503且不创建会话()
    {
        await using Fixture fixture = await Fixture.Create(enabled: false);
        using HttpResponseMessage info = await fixture.Http.GetAsync("/api/v1/runtime-info");
        PreviewRuntimeInfoResponse runtime = (await info.Content.ReadFromJsonAsync<PreviewRuntimeInfoResponse>(Json))!;
        Assert.False(runtime.Enabled);
        Assert.Equal("disabled", runtime.Status);
        using HttpResponseMessage create = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions", fixture.CreateRequest(), Json);
        await Error(create, HttpStatusCode.ServiceUnavailable, BackendErrorCode.PreviewUnavailable);
        Assert.Equal(0, fixture.Previews.Count);
        using HttpResponseMessage capabilities = await fixture.Http.GetAsync("/api/v1/capabilities");
        string body = await capabilities.Content.ReadAsStringAsync();
        Assert.DoesNotContain("preview.create.static-project", body, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task HTTP换代取消通过引用包装复用可恢复会话()
    {
        await using Fixture fixture = await Fixture.Create();
        fixture.Factory.Recoverable = true;
        PreviewSessionResponse preview = await fixture.CreatePreview();
        string route = $"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests";
        using HttpResponseMessage first = await fixture.Http.PostAsJsonAsync(route, fixture.FrameRequest(preview, 1, 10), Json);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        RenderCall old = await fixture.Factory.NextRender();
        using HttpResponseMessage seek = await fixture.Http.PostAsJsonAsync(route,
            fixture.FrameRequest(preview, 2, 0) with { Intent = "seek" }, Json);
        Assert.Equal(HttpStatusCode.Accepted, seek.StatusCode);
        RenderCall current = await fixture.Factory.NextRender();
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(1, fixture.Factory.Created);
        Assert.Equal(0, fixture.Factory.Disposed);
        current.Release.SetResult();
        await fixture.WaitReady(preview.PreviewId);
    }

    [Fact]
    public async Task 帧接受响应后HTTP令牌取消不影响渲染且SSE事件含真实帧句柄()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse session = await fixture.CreatePreview();
        using CancellationTokenSource aborted = new();
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/frame-requests",
            fixture.FrameRequest(session, 1, 0), Json, aborted.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        aborted.Cancel();
        RenderCall render = await fixture.Factory.NextRender();
        Assert.False(render.Token.IsCancellationRequested);
        render.Release.SetResult();
        PreviewSessionStateResponse ready = await fixture.WaitReady(session.PreviewId);
        Assert.Null(ready.Failure);
        using HttpResponseMessage content = await fixture.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.True((await content.Content.ReadAsByteArrayAsync()).Length > 40);
        await WaitUntil(() => fixture.Projects.ReadEvents(fixture.Client.ClientId, null).Events.Any(e => e.Type == "preview.frameReady"));
        BackendEventResponse notification = Assert.Single(fixture.Projects.ReadEvents(fixture.Client.ClientId, null).Events, e => e.Type == "preview.frameReady");
        Assert.Equal(ready.Session.LatestFrame.FrameId, notification.Preview!.LatestFrame!.FrameId);
        Assert.Null(notification.Summary);
        Assert.Null(notification.Job);
    }

    [Fact]
    public async Task 无音轨工程可创建预览读取PNG和SSE并关闭且不申请空音频租约()
    {
        await using Fixture fixture = await Fixture.Create();
        ProjectSession opened = await fixture.OpenNoAudioProject();
        PreviewSourceDto source = new("project", opened.ProjectSessionId, "0");
        ProjectSessionSummaryResponse summary = (await fixture.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(
            $"/api/v1/project-sessions/{opened.ProjectSessionId}", Json))!;
        Assert.Equal(string.Empty, summary.AudioAssetId);

        using HttpResponseMessage create = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(source, new(1, 1), Metadata: fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        PreviewSessionResponse preview = (await create.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests",
            new PreviewFrameCommandRequest("0", preview.PreviewGeneration, "1", "0", "playback", fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        RenderCall render = await fixture.Factory.NextRender();
        Assert.False(render.Token.IsCancellationRequested);
        render.Release.SetResult();

        PreviewSessionStateResponse ready = await fixture.WaitReady(preview.PreviewId);
        Assert.NotNull(ready.Session.LatestFrame);
        using HttpResponseMessage png = await fixture.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        Assert.True((await png.Content.ReadAsByteArrayAsync()).Length > 40);

        using CancellationTokenSource sseTimeout = new(TimeSpan.FromSeconds(2));
        using HttpResponseMessage sse = await fixture.Http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead, sseTimeout.Token);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType!.MediaType);
        using StreamReader reader = new(await sse.Content.ReadAsStreamAsync(sseTimeout.Token));
        bool frameEvent = false;
        while (!frameEvent)
        {
            string? line = await reader.ReadLineAsync(sseTimeout.Token);
            Assert.NotNull(line);
            frameEvent = line == "event: preview.frameReady";
        }

        using HttpResponseMessage close = await fixture.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/close",
            new ClosePreviewSessionRequest(preview.PreviewGeneration, "0", fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        PreviewSessionStateResponse closed = (await close.Content.ReadFromJsonAsync<PreviewSessionStateResponse>(Json))!;
        Assert.Equal("closed", closed.Session.State);
        using HttpResponseMessage afterClose = await fixture.Http.GetAsync($"/api/v1/preview-sessions/{preview.PreviewId}");
        await Error(afterClose, HttpStatusCode.NotFound, BackendErrorCode.PreviewSessionNotFound);
    }

    [Fact]
    public async Task 精确来源修订及seek新generation和重复请求由统一回执固定()
    {
        await using Fixture fixture = await Fixture.Create();
        using HttpResponseMessage stale = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            fixture.CreateRequest() with { Source = fixture.Source with { SourceRevision = "1" } }, Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        PreviewSessionResponse session = await fixture.CreatePreview();
        PreviewFrameCommandRequest request = fixture.FrameRequest(session, 1, 0) with { Intent = "seek" };
        using HttpResponseMessage seek = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/frame-requests", request, Json);
        PreviewFrameAcceptedResponse response = (await seek.Content.ReadFromJsonAsync<PreviewFrameAcceptedResponse>(Json))!;
        Assert.NotEqual(session.PreviewGeneration, response.PreviewGeneration);
        using HttpResponseMessage retry = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/frame-requests", request, Json);
        Assert.Equal(response, await retry.Content.ReadFromJsonAsync<PreviewFrameAcceptedResponse>(Json));
        using HttpResponseMessage old = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/frame-requests",
            fixture.FrameRequest(session, 2, 0), Json);
        await Error(old, HttpStatusCode.Conflict, BackendErrorCode.PreviewGenerationConflict);
        RenderCall rendering = await fixture.Factory.NextRender();
        rendering.Release.SetResult();
        _ = await fixture.WaitReady(session.PreviewId);
    }

    [Fact]
    public async Task PUT输入使用旧预览基线及新工程精确修订且允许CORS预检()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage missingSource = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0", null!, new(1, 1), Metadata: fixture.Metadata()), Json);
        await Error(missingSource, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        LyricLineDto line = Assert.Single((await fixture.Projects.ReadLyricsAsync(fixture.Client.ClientId, fixture.Project.ProjectSessionId, "primary"))!.Lines);
        Assert.True((await fixture.Projects.ExecuteCommandAsync(fixture.Client.ClientId, fixture.Project.ProjectSessionId,
            new("0", new("lyrics.setTimeRange", new("primary", line.Id), "1000", "2000"), "edit", "host", fixture.Clock.GetUtcNow().AddMinutes(1)))).Accepted);
        PreviewProjectInputRequest update = new(preview.PreviewGeneration, "0", fixture.Source with { SourceRevision = "1" },
            new(1, 1), Metadata: fixture.Metadata());
        using HttpResponseMessage changed = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input", update, Json);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        PreviewSessionResponse current = (await changed.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("1", current.Source.SourceRevision);
        Assert.NotEqual(preview.PreviewGeneration, current.PreviewGeneration);
        using HttpResponseMessage stale = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input", update with { Metadata = fixture.Metadata() }, Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.PreviewGenerationConflict);
        using HttpRequestMessage preflight = new(HttpMethod.Options, $"/api/v1/preview-sessions/{preview.PreviewId}/input");
        preflight.Headers.Add("Origin", fixture.Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using HttpResponseMessage cors = await fixture.Http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, cors.StatusCode);
        Assert.Contains("PUT", string.Join(',', cors.Headers.GetValues("Access-Control-Allow-Methods")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没开始渲染也固定音频且关闭工程后预览仍可运行并在关闭后释放()
    {
        await using Fixture fixture = await Fixture.Create();
        PreviewSessionResponse preview = await fixture.CreatePreview();
        BackendOperationResult<JobResponse> closing = await fixture.Projects.StartCloseAsync(fixture.Client.ClientId,
            fixture.Project.ProjectSessionId, new("0", "discard", Metadata: fixture.Metadata()));
        Assert.True(closing.Accepted);
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, closing.Response!.JobId).Response!.State == "succeeded");
        fixture.Clock.Advance(10);
        _ = fixture.Resources.CollectExpired();
        Assert.True(fixture.Resources.TryGetAuthorized(fixture.Client.ClientId, fixture.Project.AudioAssetId, out _));
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests", fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        (await fixture.Factory.NextRender()).Release.SetResult();
        _ = await fixture.WaitReady(preview.PreviewId);
        using HttpResponseMessage close = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/close",
            new ClosePreviewSessionRequest(preview.PreviewGeneration, "0", fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        fixture.Clock.Advance(10);
        _ = fixture.Resources.CollectExpired();
        Assert.False(fixture.Resources.TryGetAuthorized(fixture.Client.ClientId, fixture.Project.AudioAssetId, out _));
        Assert.Equal(1, fixture.Factory.Disposed);
    }

    [Fact]
    public async Task 旧FactoryCreate与更新关闭竞争时保留运行引用并精确释放迟到会话()
    {
        await using Fixture fixture = await Fixture.Create();
        fixture.Factory.PauseCreate = true;
        fixture.Factory.IgnoreCreateCancellation = true;
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests", fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        CreateCall old = await fixture.Factory.NextCreate();
        using HttpResponseMessage update = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0", fixture.Source, new(1, 1), Metadata: fixture.Metadata()), Json);
        PreviewSessionResponse current = (await update.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.True(old.Token.IsCancellationRequested);
        Task<BackendOperationResult<PreviewSessionStateResponse>> close = fixture.Service.CloseAsync(fixture.Client.ClientId,
            current.PreviewId, new(current.PreviewGeneration, "0", fixture.Metadata()));
        Assert.False(close.IsCompleted);
        old.Release.SetResult();
        Assert.Equal("closed", (await close).Response!.Session.State);
        Assert.Equal(1, fixture.Factory.Disposed);
        Assert.Equal(0, fixture.Previews.Count);
        Assert.DoesNotContain(fixture.Projects.ReadEvents(fixture.Client.ClientId, null).Events, e => e.Type == "preview.failed");
        Assert.False(fixture.Factory.Calls.Reader.TryRead(out _));
    }

    [Fact]
    public async Task 客户端租约到期排水预览但保留工程且宿主关闭也等待排水()
    {
        await using Fixture fixture = await Fixture.Create(leaseSeconds: 2);
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests", fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        RenderCall old = await fixture.Factory.NextRender();
        fixture.Clock.Advance(3);
        await fixture.Service.DrainExpiredClientsAsync();
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(0, fixture.Previews.Count);
        Assert.NotNull(await fixture.Projects.ReadSummaryAsync(fixture.Client.ClientId, fixture.Project.ProjectSessionId));
        using HttpResponseMessage expired = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions", fixture.CreateRequest(), Json);
        await Error(expired, HttpStatusCode.Unauthorized, BackendErrorCode.ClientTokenExpired);
        fixture.Resume();
        PreviewSessionResponse resumed = await fixture.CreatePreview();
        using HttpResponseMessage acceptedNew = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{resumed.PreviewId}/frame-requests", fixture.FrameRequest(resumed, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, acceptedNew.StatusCode);
        RenderCall current = await fixture.Factory.NextRender();
        await fixture.Service.DisposeAsync();
        Assert.True(current.Token.IsCancellationRequested);
        Assert.Equal(2, fixture.Factory.Disposed);
        Assert.Equal(0, fixture.Previews.Count);
    }

    [Fact]
    public async Task 过期预览排水失败不阻止其他客户端且下一轮重试()
    {
        await using Fixture fixture = await Fixture.Create(leaseSeconds: 2);
        ClientRegistrationResponse other = fixture.Clients.Register(fixture._security.BootstrapToken, new()).Response!;
        PreviewSourceDto source = new("project", "cleanup-source", "0");
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(ProjectWorkspace.CreateNew("cleanup").Project);
        ControlledRetention failedRetention = new(true);
        ControlledRetention otherRetention = new(false);
        BackendPreviewSessionResult failedOwner = fixture.Previews.Create(fixture.Client.ClientId, new(source, new(1, 1)),
            new(source, snapshot, fixture.Factory, RetentionLease: failedRetention));
        BackendPreviewSessionResult otherOwner = fixture.Previews.Create(other.ClientId, new(source, new(1, 1)),
            new(source, snapshot, fixture.Factory, RetentionLease: otherRetention));
        Assert.True(failedOwner.Accepted);
        Assert.True(otherOwner.Accepted);
        Assert.Equal(new[] { fixture.Client.ClientId, other.ClientId }, fixture.Previews.ActiveClientIds);

        fixture.Clock.Advance(3);
        await fixture.Service.DrainExpiredClientsAsync();

        Assert.Equal(1, failedRetention.Count);
        Assert.Equal(1, otherRetention.Count);
        Assert.Equal(1, fixture.Previews.Count);
        Assert.Equal("closing", fixture.Previews.Read(fixture.Client.ClientId, failedOwner.Status!.Session.PreviewId).Status!.Session.State);
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound,
            fixture.Previews.Read(other.ClientId, otherOwner.Status!.Session.PreviewId).Error!.Code);

        await fixture.Service.DrainExpiredClientsAsync();
        Assert.Equal(2, failedRetention.Count);
        Assert.Equal(1, otherRetention.Count);
        Assert.Equal(0, fixture.Previews.Count);
    }

    [Fact]
    public async Task 裸像素预算在接受前验证且失败不占预览或创建worker()
    {
        await using Fixture fixture = await Fixture.Create(rawBudget: 16);
        using HttpResponseMessage rejected = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            fixture.CreateRequest() with { Size = new(3, 3) }, Json);
        await Error(rejected, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        Assert.Equal(0, fixture.Previews.Count);
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage update = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0", fixture.Source, new(3, 3), Metadata: fixture.Metadata()), Json);
        await Error(update, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        Assert.Equal(preview.PreviewGeneration, fixture.Service.Read(fixture.Client.ClientId, preview.PreviewId).Response!.Session.PreviewGeneration);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task runtime创建失败或取消均释放独立资源引用(bool cancel)
    {
        await using Fixture fixture = await Fixture.Create();
        fixture.Factory.ThrowCreate = !cancel;
        fixture.Factory.PauseCreate = cancel;
        PreviewSessionResponse preview = await fixture.CreatePreview();
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests", fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        if (cancel) _ = await fixture.Factory.NextCreate();
        else await WaitUntil(() => fixture.Service.Read(fixture.Client.ClientId, preview.PreviewId).Response?.Session.State == "failed");
        BackendOperationResult<JobResponse> closingProject = await fixture.Projects.StartCloseAsync(fixture.Client.ClientId,
            fixture.Project.ProjectSessionId, new("0", "discard", Metadata: fixture.Metadata()));
        Assert.True(closingProject.Accepted);
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, closingProject.Response!.JobId).Response!.State == "succeeded");
        BackendOperationResult<PreviewSessionStateResponse> close = await fixture.Service.CloseAsync(fixture.Client.ClientId,
            preview.PreviewId, new(preview.PreviewGeneration, "0", fixture.Metadata()));
        Assert.True(close.Accepted);
        fixture.Clock.Advance(10);
        _ = fixture.Resources.CollectExpired();
        Assert.False(fixture.Resources.TryGetAuthorized(fixture.Client.ClientId, fixture.Project.AudioAssetId, out _));
        Assert.Equal(1, fixture.Factory.Created);
        Assert.Equal(0, fixture.Factory.Disposed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("nonexecutable")]
    public async Task 已验证能力后真实WorkerFactory的open不可用在GET和SSE保留稳定码并释放音频引用(string mode)
    {
        using TemporaryDirectory workerFiles = new();
        string path = workerFiles.FilePath("worker-apphost");
        if (mode != "missing") File.Copy(FindWorkerApphost(), path);
        var worker = new PreviouslyVerifiedProfileFactory(new WorkerRenderFrameSessionFactory(new(path)));
        await using Fixture fixture = await Fixture.Create(injectedFactory: worker);
        PreviewSessionResponse preview = await fixture.CreatePreview();
        Assert.Single(worker.Profiles);
        Assert.Equal(0, worker.OpenAttempts);
        // Simulate losing a previously configured apphost before the first lazy open.
        if (mode == "deleted") File.Delete(path);
        if (mode == "nonexecutable")
        {
            Assert.True(OperatingSystem.IsLinux(), "Executable permission regression is the bounded Linux exit.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests",
            fixture.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        PreviewSessionStateResponse failed;
        do
        {
            failed = (await fixture.Http.GetFromJsonAsync<PreviewSessionStateResponse>($"/api/v1/preview-sessions/{preview.PreviewId}", Json, timeout.Token))!;
            if (failed.Session.State != "failed") await Task.Delay(10, timeout.Token);
        } while (failed.Session.State != "failed");
        Assert.Equal("WorkerUnavailable", failed.RenderFailureCode);
        Assert.Equal(1, worker.OpenAttempts);
        Assert.NotNull(failed.Failure);
        Assert.Null(failed.Session.LatestFrame);
        Assert.DoesNotContain(path, JsonSerializer.Serialize(failed, Json), StringComparison.Ordinal);

        using HttpResponseMessage sse = await fixture.Http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType!.MediaType);
        using (StreamReader reader = new(await sse.Content.ReadAsStreamAsync(timeout.Token)))
        {
            BackendEventResponse? failureEvent = null;
            while (failureEvent is null)
            {
                string? line = await reader.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                BackendEventResponse notification = JsonSerializer.Deserialize<BackendEventResponse>(line[6..], Json)!;
                if (notification.Type == "preview.failed") failureEvent = notification;
            }
            Assert.Equal(preview.PreviewId, failureEvent.Preview!.PreviewId);
            Assert.Equal("WorkerUnavailable", failureEvent.Preview.RenderFailureCode);
            Assert.Null(failureEvent.Preview.LatestFrame);
            Assert.DoesNotContain(path, JsonSerializer.Serialize(failureEvent, Json), StringComparison.Ordinal);
        }

        BackendOperationResult<JobResponse> closingProject = await fixture.Projects.StartCloseAsync(fixture.Client.ClientId,
            fixture.Project.ProjectSessionId, new("0", "discard", Metadata: fixture.Metadata()));
        Assert.True(closingProject.Accepted);
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, closingProject.Response!.JobId).Response!.State == "succeeded");
        using HttpResponseMessage close = await fixture.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/close",
            new ClosePreviewSessionRequest(preview.PreviewGeneration, "0", fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        fixture.Clock.Advance(10);
        _ = fixture.Resources.CollectExpired();
        Assert.False(fixture.Resources.TryGetAuthorized(fixture.Client.ClientId, fixture.Project.AudioAssetId, out _));
        Assert.Equal(0, fixture.Previews.Count);
        Assert.Equal(0, fixture.Factory.Created); // The controllable stand-in was not used.
    }

    [Fact]
    public async Task 真实WorkerFactory缺失部署在创建前拒绝且不登记预览或保留音频引用()
    {
        using TemporaryDirectory workerFiles = new();
        string path = workerFiles.FilePath("missing-worker-apphost");
        var worker = new WorkerRenderFrameSessionFactory(new(path));
        await using Fixture fixture = await Fixture.Create(injectedFactory: worker);
        using HttpResponseMessage response = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(fixture.Source, new(1, 1), Metadata: fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        BackendErrorResponse error = JsonSerializer.Deserialize<BackendErrorResponse>(body, Json)!;
        Assert.Equal(BackendErrorCode.PreviewUnavailable, error.Code);
        Assert.Equal("WorkerUnavailable", error.FailureCode);
        Assert.Equal("worker", error.Field);
        Assert.DoesNotContain(path, body, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Previews.Count);
        Assert.Equal(0, fixture.Factory.Created);
        BackendOperationResult<JobResponse> close = await fixture.Projects.StartCloseAsync(fixture.Client.ClientId,
            fixture.Project.ProjectSessionId, new("0", "discard", Metadata: fixture.Metadata()));
        Assert.True(close.Accepted);
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, close.Response!.JobId).Response!.State == "succeeded");
        fixture.Clock.Advance(10);
        _ = fixture.Resources.CollectExpired();
        Assert.False(fixture.Resources.TryGetAuthorized(fixture.Client.ClientId, fixture.Project.AudioAssetId, out _));
    }

    // Models a successful earlier handshake only; native open still executes the real Worker factory.
    private sealed class PreviouslyVerifiedProfileFactory(IRenderFrameSessionFactory inner)
        : IRenderFrameSessionFactory, IBackendPreviewProfileCapability
    {
        internal List<string> Profiles { get; } = [];
        internal int OpenAttempts;
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        {
            Assert.Contains(profile, new[] { BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis });
            Profiles.Add(profile);
            return ValueTask.FromResult<BackendErrorResponse?>(null);
        }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenAttempts);
            return inner.CreateAsync(snapshot, cancellationToken);
        }
    }

    // Deliberately exposes the historical interfaces without claiming fixed runtime input support.
    private sealed class LegacyPreviewFactory(IRenderFrameSessionFactory inner)
        : IRenderFrameSessionFactory, IBackendPreviewProfileCapability, IBackendImagePreviewFactory
    {
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default) =>
            ((IBackendPreviewProfileCapability)inner).EnsureProfileAsync(profile, cancellationToken);
        public IRenderFrameSessionFactory Bind(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes) =>
            ((IBackendImagePreviewFactory)inner).Bind(snapshot, imageBytes);
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(snapshot, cancellationToken);
    }

    private static string FindWorkerApphost()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string path = Path.Combine(root!.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(path)); return path;
    }

    private static async Task<BackendErrorResponse> Error(HttpResponseMessage response, HttpStatusCode status, BackendErrorCode code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        BackendErrorResponse error = (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(code, error.Code);
        return error;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TemporaryDirectory _directory = new();
        internal readonly Clock Clock = new();
        internal readonly Factory Factory = new();
        internal readonly ValidationExecutor Validation = new();
        internal readonly ExportExecutor Exporter = new();
        internal readonly AnalysisExecutor Analyzer = new();
        internal ResourceStore Resources = null!;
        internal ProjectSessionRegistry Projects = null!;
        internal ProjectSession Project = null!;
        internal BackendPreviewSessionRegistry Previews = null!;
        internal PreviewBackendService Service = null!;
        internal ClientRegistry Clients = null!;
        internal ClientRegistrationResponse Client = null!;
        internal HttpClient Http = null!;
        private WebApplication _app = null!;
        internal LoopbackSecurity _security = null!;
        internal PreviewSourceDto Source => new("project", Project.ProjectSessionId, "0");
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), "host", Clock.GetUtcNow().AddMinutes(1));
        internal PreviewSessionRequest CreateRequest() => new(Source, new(1, 1), Metadata: Metadata());
        internal PreviewFrameCommandRequest FrameRequest(PreviewSessionResponse session, long sequence, long time) =>
            new(session.Source.SourceRevision, session.PreviewGeneration, sequence.ToString(CultureInfo.InvariantCulture), time.ToString(CultureInfo.InvariantCulture), "playback", Metadata());

        internal static async Task<Fixture> Create(bool enabled = true, int leaseSeconds = 300, long rawBudget = 4 * 1024 * 1024,
            IRenderFrameSessionFactory? injectedFactory = null, bool exportFixture = false, bool analysisFixture = false,
            byte[]? analysisBytes = null, bool compressedAnalysis = false, bool compressedExport = false, bool emptyClient = false,
            LoadedWaveform? waveformOverride = null, WaveformCache? waveformCache = null, bool presetProfile = false,
            IBackendExportExecutor? injectedExportExecutor = null, BackendExportCapabilities? exportCapabilities = null,
            bool packageProfile = false, bool imageProfile = false,
            long? maxAssetBytes = null, long? maxAssetQuotaBytes = null, int maxAssets = 4096,
            BackendRenderBackendConfiguration? renderBackends = null)
        {
            Fixture fixture = new();
            BackendHostOptions options = new() { ResourceRoot = Path.Combine(fixture._directory.Path, "resources"), UnreferencedAssetLifetimeSeconds = 1,
                MaxAssetBytes = maxAssetBytes, MaxAssetQuotaBytes = maxAssetQuotaBytes, MaxAssets = maxAssets };
            fixture.Resources = new(options, fixture.Clock);
            fixture._security = new(options.ResourceRoot);
            fixture.Clients = new("host", fixture._security.BootstrapToken,
                new() { TokenLifetimeSeconds = 120, LeaseLifetimeSeconds = leaseSeconds, MaxClients = emptyClient ? 1 : 16 }, fixture.Clock);
            fixture.Client = fixture.Clients.Register(fixture._security.BootstrapToken, new()).Response!;
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24_000]);
            LoadedWaveform waveform = waveformOverride ?? new(new AudioStreamInfo(3_000_000, 8_000, 1, "pcm", "wav"), peaks.Build(8_000));
            fixture.Projects = new("host", fixture.Resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), fixture.Clock, clients: fixture.Clients,
                waveformCache: waveformCache);
            fixture.Projects.ConfigurePersistence(new()
            {
                StateRoot = Path.Combine(fixture._directory.Path, "state"),
                AllowedProjectRoots = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["preview-test-root"] = Path.Combine(fixture._directory.Path, "projects"),
                },
            });
            fixture.Projects.ConfigureValidationTasks(enabled ? fixture.Validation : null);
            fixture.Projects.ConfigureExportTasks(enabled ? injectedExportExecutor ?? fixture.Exporter : null, exportCapabilities ?? new(enabled, enabled, enabled,
                AudioProfiles: compressedExport ? [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                    BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg] : null,
                ExecutionProfiles: compressedExport ? [WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile] : null));
            fixture.Projects.ConfigureAnalysisTasks(enabled && analysisFixture ? fixture.Analyzer : null,
                new(enabled && analysisFixture, enabled && analysisFixture,
                    AudioProfiles: compressedAnalysis ? [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                        BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg] : null,
                    SourceProfiles: compressedAnalysis ? [BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource] : null));
            StoredAsset audio = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "song.wav", "audio/wav", new MemoryStream(analysisBytes ?? (analysisFixture ? AnalysisWave() : [1, 2, 3])));
            StoredAsset lyrics = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "song.lrc", "text/plain", new MemoryStream(exportFixture ? "[00:00.00]<00:00.00>hello<00:03.00>\n"u8.ToArray() : "[00:00.00]hello\n"u8.ToArray()));
            if (!emptyClient)
                fixture.Project = (await fixture.Projects.CreateAsync(fixture.Client.ClientId,
                    new("preview", audio.AssetId, lyrics.AssetId, new(640, 360, 30, 1, OutputDurationUs: 3000000), Metadata: fixture.Metadata()))).Session!;
            PreviewFrameHandleStore frames = new(); PreviewDiagnosticsStore diagnostics = new();
            fixture.Previews = new(frames, diagnostics, new SkiaBackendPreviewPngEncoder());
            string[] previewProfiles = presetProfile
                ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets]
                : [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis];
            if (packageProfile) previewProfiles = [.. previewProfiles, MakoKaraGenerator.Backend.Rendering.PackageProjectSnapshotCodec.Profile];
            if (packageProfile && presetProfile) previewProfiles = [.. previewProfiles, BackendSnapshotProfiles.PackageLyricsScenePresets];
            if (imageProfile) previewProfiles = [.. previewProfiles, BackendSnapshotProfiles.ProjectImagePreview];
            if (injectedFactory is IBackendRuntimePreviewFactory)
                previewProfiles = [.. previewProfiles, ProjectRuntimeInputWorkerContract.Profile];
            PreviewRuntimeInfoResponse runtime = new(enabled, enabled ? "configured-unverified" : "disabled", "static-lyrics-scene-v1", "worker-process", 1280, 720, rawBudget,
                enabled ? null : new(BackendErrorCode.PreviewUnavailable, "Worker未部署。"),
                SourceProfiles: previewProfiles);
            fixture.Service = new(fixture.Projects, fixture.Previews, fixture.Resources, fixture.Clients,
                enabled ? injectedFactory ?? fixture.Factory : null, runtime, renderBackends);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(configure =>
            {
                configure.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                configure.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });
            builder.Services.AddSingleton(fixture.Resources); builder.Services.AddSingleton(fixture.Projects);
            builder.Services.AddSingleton(fixture.Clients); builder.Services.AddSingleton(fixture._security);
            builder.Services.AddSingleton(fixture.Service); builder.Services.AddSingleton(new AcceptedOperationTracker());
            builder.Services.AddSingleton(new UploadPreparationLimits()); builder.Services.AddSingleton(frames); builder.Services.AddSingleton(diagnostics);
            fixture._app = builder.Build(); ServerEndpoints.Configure(fixture._app); await fixture._app.StartAsync();
            fixture._security.Started(fixture._app.Services, "host");
            string address = fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture.Http = new() { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) };
            fixture.SetHeaders();
            return fixture;
        }

        private void SetHeaders()
        {
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Client.AccessToken);
            Http.DefaultRequestHeaders.Remove("X-Mako-Client-Id"); Http.DefaultRequestHeaders.Add("X-Mako-Client-Id", Client.ClientId);
        }
        internal void Resume() { Client = Clients.Register(_security.BootstrapToken, new(ResumeClientId: Client.ClientId, ResumeToken: Client.ResumeToken)).Response!; SetHeaders(); }
        internal async Task<PreviewSessionResponse> CreatePreview()
        {
            using HttpResponseMessage response = await Http.PostAsJsonAsync("/api/v1/preview-sessions", CreateRequest(), Json);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        }
        internal async Task<ProjectSession> OpenNoAudioProject(Project? project = null, string directoryName = "no-audio")
        {
            string directory = Path.Combine(_directory.Path, "projects", directoryName);
            ProjectStore.Save(project ?? ProjectWorkspace.CreateNew("no-audio").Project with
            {
                AudioPath = null,
                AudioMedia = null,
                AudioAnalysis = null,
            }, directory);
            using HttpResponseMessage handleResponse = await Http.PostAsJsonAsync("/api/v1/file-handles",
                new FileHandleRequest("preview-test-root", directoryName, Metadata: Metadata()), Json);
            Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
            FileHandleResponse handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
            using HttpResponseMessage openResponse = await Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs",
                new OpenProjectSessionRequest(handle.HandleId, Metadata: Metadata()), Json);
            Assert.Equal(HttpStatusCode.Accepted, openResponse.StatusCode);
            JobResponse accepted = (await openResponse.Content.ReadFromJsonAsync<JobResponse>(Json))!;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                JobResponse current = (await Http.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{accepted.JobId}", Json, timeout.Token))!;
                if (current.State is "succeeded" or "failed" or "cancelled")
                {
                    Assert.Equal("succeeded", current.State);
                    Assert.NotNull(current.ProjectSessionId);
                    Assert.True(Projects.TryGet(Client.ClientId, current.ProjectSessionId!, out ProjectSession? session));
                    return session!;
                }
                await Task.Delay(10, timeout.Token);
            }
        }
        internal async Task<PreviewSessionStateResponse> WaitReady(string id)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                PreviewSessionStateResponse current = (await Http.GetFromJsonAsync<PreviewSessionStateResponse>($"/api/v1/preview-sessions/{id}", Json, timeout.Token))!;
                if (current.Session.State == "failed") Assert.Null(current.Failure);
                if (current.Session.State == "ready") return current;
                await Task.Delay(10, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose(); await _app.StopAsync(); await Service.DisposeAsync(); await Projects.DisposeAsync();
            await _app.DisposeAsync(); _security.Dispose(); _directory.Dispose();
        }
    }

    private sealed class ExportExecutor : IBackendExportExecutor
    {
        internal static readonly byte[] Bytes = "transport-export-seam"u8.ToArray();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token; internal int Calls; internal RenderSessionFailureException? Failure;
        internal BackendExportExecutionInput? Input;
        public Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackendExportCapabilities(true, true, true));
        public void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input) { }
        public async Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); Token = cancellationToken; Input = input; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            await File.WriteAllBytesAsync(input.CandidatePath, Bytes, cancellationToken);
            if (Failure is not null) throw Failure;
            var c = snapshot.Composition; var plan = MakoKaraGenerator.Core.Time.CompositionFramePlan.Create(c.OutputDurationUs!.Value, c.FrameRate);
            string duration = plan.VideoDurationUs.ToString(CultureInfo.InvariantCulture);
            return new(input.Format, input.IncludeAudio, plan.TotalFrames, plan.TotalFrames, plan.TotalFrames, 0, 0, c.CanvasWidth, c.CanvasHeight,
                c.FrameRate.Numerator, c.FrameRate.Denominator, duration, duration, input.IncludeAudio ? duration : null, "h264", input.IncludeAudio ? "aac" : null, "yuv420p", false,
                Bytes.Length, WorkerProtocol.ComputeSha256(Bytes), [], [], true, input.IncludeAudio ? duration : null, true);
        }
    }

    private sealed class ValidationExecutor : IBackendValidationExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int Calls;
        public async Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); Token = cancellationToken; Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(true, framesPerOutput, framesPerOutput, [], []);
        }
    }

    private sealed record CreateCall(TaskCompletionSource Release, CancellationToken Token);
    private sealed record RenderCall(TaskCompletionSource Release, CancellationToken Token);
    private sealed class Factory : IRenderFrameSessionFactory, IBackendPreviewProfileCapability
    {
        internal readonly Channel<RenderCall> Calls = Channel.CreateUnbounded<RenderCall>();
        internal readonly Channel<CreateCall> Creates = Channel.CreateUnbounded<CreateCall>();
        internal bool PauseCreate;
        internal bool IgnoreCreateCancellation;
        internal bool ThrowCreate;
        internal bool Recoverable;
        internal BackendErrorResponse? ProfileError;
        internal int ProfileChecks;
        internal int Created;
        internal int Disposed;
        internal Task<RenderCall> NextRender() => Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        internal Task<CreateCall> NextCreate() => Creates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ProfileChecks);
            return ValueTask.FromResult(ProfileError);
        }
        public async ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Created);
            if (ThrowCreate) throw new InvalidOperationException("controlled runtime create failure");
            if (PauseCreate)
            {
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Creates.Writer.TryWrite(new(release, cancellationToken));
                if (IgnoreCreateCancellation) await release.Task.ConfigureAwait(false);
                else await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return new Session(this);
        }
        private sealed class Session(Factory owner) : IRenderFrameSession, IBackendPreviewCancellationRecoverable
        {
            public bool CanReuseAfterCancellation => owner.Recoverable;
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Calls.Writer.TryWrite(new(release, cancellationToken));
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                RenderFrame frame = new([0, 0, 255, 255], request.ResolveTarget(), PixelFormat.Bgra8888Premultiplied, request.TimeUs);
                if (request.Inspect) frame.AttachInspection(InspectionSample());
                return frame;
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposed); return ValueTask.CompletedTask; }
        }
    }
}
