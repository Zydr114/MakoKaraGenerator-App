using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData("lrc.plain", false)]
    [InlineData("lrc.enhanced", true)]
    public async Task LRC真实HTTP固定修订计划确认认证下载原导出器一致及HEADRangeETag(string format, bool bom)
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var before = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(path, Json))!;
        LyricsExportOptionsDto options = new("曲名", "艺术家", "专辑", "\r\n", bom, true);
        LyricsExportPlanRequest input = new("0", "primary", format, options, f.Metadata());
        using HttpResponseMessage planned = await f.Http.PostAsJsonAsync(path + "/lyrics-export-plans", input, Json);
        Assert.Equal(HttpStatusCode.Created, planned.StatusCode);
        LyricsExportPlanResponse plan = (await planned.Content.ReadFromJsonAsync<LyricsExportPlanResponse>(Json))!;
        Assert.Equal("branch", plan.Scope); Assert.True(plan.RequiresLossAcceptance);
        Assert.Contains(plan.AdditionalLosses, loss => loss.Feature == "lineEndTimes");
        using (HttpResponseMessage retry = await f.Http.PostAsJsonAsync(path + "/lyrics-export-plans", input, Json))
        { Assert.Equal(HttpStatusCode.Created, retry.StatusCode); Assert.Equal(plan.PlanId, (await retry.Content.ReadFromJsonAsync<LyricsExportPlanResponse>(Json))!.PlanId); }
        using (HttpResponseMessage denied = await f.Http.PostAsJsonAsync(path + "/lyrics-exports", new LyricsExportRequest("0", plan.PlanId, false, f.Metadata()), Json))
            await Error(denied, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        LyricsExportRequest exporting = new("0", plan.PlanId, true, f.Metadata());
        using HttpResponseMessage generated = await f.Http.PostAsJsonAsync(path + "/lyrics-exports", exporting, Json);
        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        LyricsExportResponse result = (await generated.Content.ReadFromJsonAsync<LyricsExportResponse>(Json))!;
        using (HttpResponseMessage retry = await f.Http.PostAsJsonAsync(path + "/lyrics-exports", exporting, Json))
        { Assert.Equal(HttpStatusCode.Created, retry.StatusCode); Assert.Equal(result.ExportId, (await retry.Content.ReadFromJsonAsync<LyricsExportResponse>(Json))!.ExportId); }
        ILyricsExporter exporter = format == "lrc.plain" ? new PlainLrcExporter() : new EnhancedLrcExporter();
        using MemoryStream expected = new();
        exporter.Export(LyricsDocument.Create([LyricLine.Create("line", "hello", new TimeRange(0, 3000000), TimingStatus.Imported)]), expected,
            new() { Title = options.Title, Artist = options.Artist, Album = options.Album, NewLine = "\r\n", WriteByteOrderMark = bom, MillisecondPrecision = true });
        using HttpResponseMessage downloaded = await f.Http.GetAsync(result.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("text/plain", downloaded.Content.Headers.ContentType!.MediaType);
        Assert.Equal(expected.ToArray(), await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Equal(result.SizeBytes, downloaded.Content.Headers.ContentLength);
        Assert.Equal("\"" + result.Sha256 + "\"", downloaded.Headers.ETag!.Tag);
        using HttpRequestMessage headRequest = new(HttpMethod.Head, result.ContentUrl);
        using HttpResponseMessage head = await f.Http.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(result.SizeBytes, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage rangeRequest = new(HttpMethod.Get, result.ContentUrl);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        using HttpResponseMessage range = await f.Http.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(expected.ToArray()[..4], await range.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage cachedRequest = new(HttpMethod.Get, result.ContentUrl);
        cachedRequest.Headers.IfNoneMatch.Add(downloaded.Headers.ETag);
        using HttpResponseMessage cached = await f.Http.SendAsync(cachedRequest);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        using HttpRequestMessage badRangeRequest = new(HttpMethod.Get, result.ContentUrl);
        badRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=999999-");
        using HttpResponseMessage badRange = await f.Http.SendAsync(badRangeRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, badRange.StatusCode);
        Assert.Equal(before, await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(path, Json));

        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreign = new(HttpMethod.Get, result.ContentUrl);
        foreign.Headers.Authorization = new("Bearer", other.AccessToken); foreign.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage foreignResponse = await f.Http.SendAsync(foreign);
        await Error(foreignResponse, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        using HttpRequestMessage anonymous = new(HttpMethod.Get, result.ContentUrl);
        anonymous.Headers.Authorization = new("Bearer", "invalid");
        using HttpResponseMessage anonymousResponse = await f.Http.SendAsync(anonymous);
        await Error(anonymousResponse, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
        f.Clock.Advance(301); f.Resume();
        using HttpResponseMessage expired = await f.Http.GetAsync(result.ContentUrl);
        await Error(expired, HttpStatusCode.Gone, BackendErrorCode.ResourceExpired);
    }

    [Theory]
    [InlineData("ttml.project")]
    [InlineData("ttml.amll")]
    public async Task TTML真实HTTP导出返回可解析的Ruby兼容内容(string format)
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        LyricsExportPlanRequest input = new("0", "primary", format, new(), f.Metadata());
        using HttpResponseMessage planned = await f.Http.PostAsJsonAsync(path + "/lyrics-export-plans", input, Json);
        Assert.Equal(HttpStatusCode.Created, planned.StatusCode);
        LyricsExportPlanResponse plan = (await planned.Content.ReadFromJsonAsync<LyricsExportPlanResponse>(Json))!;
        Assert.Equal(format == "ttml.amll", plan.RequiresLossAcceptance);
        Assert.Empty(plan.AdditionalLosses);
        if (format == "ttml.amll") Assert.Contains(plan.Plan.Losses, loss => loss.Feature == "项目身份和时间状态");

        using HttpResponseMessage generated = await f.Http.PostAsJsonAsync(path + "/lyrics-exports",
            new LyricsExportRequest("0", plan.PlanId, true, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        LyricsExportResponse result = (await generated.Content.ReadFromJsonAsync<LyricsExportResponse>(Json))!;
        using HttpResponseMessage content = await f.Http.GetAsync(result.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("application/ttml+xml", content.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".ttml", content.Content.Headers.ContentDisposition!.FileName, StringComparison.Ordinal);
        XDocument xml = XDocument.Parse(await content.Content.ReadAsStringAsync());
        XNamespace ttml = "http://www.w3.org/ns/ttml";
        Assert.Equal(ttml, xml.Root!.Name.Namespace);
        Assert.NotEmpty(xml.Descendants(ttml + "p"));
    }

    [Fact]
    public async Task LRC真实HTTP旧计划修订不接受重新指定当前修订以及关闭回收()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        async Task<LyricsExportPlanResponse> Plan(string revision)
        {
            using var response = await f.Http.PostAsJsonAsync(path + "/lyrics-export-plans", new LyricsExportPlanRequest(revision, "primary", "lrc.plain", new(), f.Metadata()), Json);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<LyricsExportPlanResponse>(Json))!;
        }
        var plan = await Plan("0");
        NodeHttpHarness http = new(f); await http.Send(new AddLineCommandDto("primary", null));
        foreach (string revision in new[] { "0", "1" })
        {
            using HttpResponseMessage stale = await f.Http.PostAsJsonAsync(path + "/lyrics-exports", new LyricsExportRequest(revision, plan.PlanId, true, f.Metadata()), Json);
            await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        }
        var fresh = await Plan("1");
        using HttpResponseMessage generated = await f.Http.PostAsJsonAsync(path + "/lyrics-exports", new LyricsExportRequest("1", fresh.PlanId, true, f.Metadata()), Json);
        var exported = (await generated.Content.ReadFromJsonAsync<LyricsExportResponse>(Json))!;
        using HttpResponseMessage closing = await f.Http.PostAsJsonAsync(path + "/close", new CloseProjectRequest("1", "discard", Metadata: f.Metadata()), Json);
        await NodeTerminal(f, closing);
        using HttpResponseMessage closed = await f.Http.GetAsync(exported.ContentUrl);
        await Error(closed, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":\"0\",\"branchId\":\"primary\",\"format\":\"lrc.plain\",\"options\":null}")]
    [InlineData("{\"expectedRevision\":\"0\",\"expectedRevision\":\"0\",\"branchId\":\"primary\",\"format\":\"lrc.plain\",\"options\":{}}")]
    [InlineData("{\"expectedRevision\":\"0\",\"branchId\":\"primary\",\"format\":\"lrc.plain\",\"options\":{\"extra\":1}}")]
    [InlineData("{\"expectedRevision\":\"0\",\"branchId\":\"primary\",\"format\":\"lrc.plain\",\"options\":{\"newLine\":\"\\r\"}}")]
    public async Task LRC真实HTTP严格字段拒绝(string raw)
    {
        await using Fixture f = await Fixture.Create();
        if (raw.Length > 2) raw = "{\"metadata\":" + JsonSerializer.Serialize(f.Metadata(), Json) + "," + raw[1..];
        using HttpResponseMessage response = await f.Http.PostAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics-export-plans", new StringContent(raw, Encoding.UTF8, "application/json"));
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task 目录真实HTTP有界查询工程标记相对路径无凭证拒绝与capability()
    {
        await using Fixture f = await Fixture.Create();
        _ = await f.OpenNoAudioProject();
        const string route = "/api/v1/file-roots/preview-test-root/entries";
        var page = (await f.Http.GetFromJsonAsync<ProjectDirectoryEntriesResponse>(route + "?limit=1", Json))!;
        Assert.Equal(1, page.TotalCount); Assert.Null(page.NextOffset);
        Assert.Equal(new ProjectDirectoryEntryDto("no-audio", "no-audio", true), Assert.Single(page.Entries));
        Assert.DoesNotContain("/tmp/", await f.Http.GetStringAsync(route), StringComparison.Ordinal);
        foreach (string query in new[] { "?extra=1", "?limit=1&limit=2", "?offset=-1", "?limit=101", "?offset=2" })
        {
            using HttpResponseMessage invalid = await f.Http.GetAsync(route + query);
            await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        using (HttpResponseMessage traversal = await f.Http.GetAsync(route + "?relativePath=..%2Foutside"))
            await Error(traversal, HttpStatusCode.BadRequest, BackendErrorCode.PathNotAllowed);
        using (HttpResponseMessage unknownRoot = await f.Http.GetAsync("/api/v1/file-roots/unknown/entries"))
            await Error(unknownRoot, HttpStatusCode.BadRequest, BackendErrorCode.PathNotAllowed);
        var capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string feature in new[] { "file-roots.entries", "project-sessions.lyrics-export-plans", "project-sessions.lyrics-exports", "lyrics-exports.content.range" })
            Assert.Contains(feature, capabilities.Features);
    }
}
