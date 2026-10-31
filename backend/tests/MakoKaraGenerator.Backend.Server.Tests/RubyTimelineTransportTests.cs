using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Time;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Server.Tests;

/// <summary>Runs the published apphosts, including real Worker audio preparation. No Hosting seams are substituted.</summary>
[Trait("Category", "PublishedServer")]
public sealed class RubyTimelineTransportTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private const string Ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:mkg="urn:makokara:ttml" mkg:timing-profile="amll-absolute"><body><div><p begin="1s" end="3s"><span begin="1s" end="3s"><span tts:ruby="container"><span tts:ruby="base">千</span><span tts:ruby="textContainer" xml:lang="ja"><span tts:ruby="text" begin="1.1s" end="1.4s">せ</span><span tts:ruby="text" begin="1.5s" end="1.8s">ん</span></span></span></span></p></div></body></tt>
        """;

    [Fact]
    public async Task 已发布真实HTTP独立注音编辑历史保存重开及完整TTML认证下载回读()
    {
        await using PublishedFixture f = await PublishedFixture.Start(output);
        ProjectSessionSummaryResponse project = await f.CreateProject();
        string session = project.ProjectSessionId;
        string revision = "0";
        string PathFor() => "/api/v1/project-sessions/" + session;
        async Task<ProjectLyricsWindowResponse> Window(string branch = "primary") => await f.Get<ProjectLyricsWindowResponse>(
            PathFor() + "/lyrics/window?branchId=" + branch + "&expectedRevision=" + revision);
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            var result = await f.Post<ProjectCommandResponse>(PathFor() + "/commands", new ProjectCommandRequest(revision, command,
                f.Metadata().RequestId, f.Host, DateTimeOffset.UtcNow.AddMinutes(1)), HttpStatusCode.OK);
            Assert.Equal(revision, result.PreviousRevision); revision = result.ProjectRevision; return result;
        }
        var imported = await Window();
        var line = Assert.Single(imported.Lines); var ruby = Assert.Single(line.RubyAnnotations);
        Assert.Equal("せん", ruby.Text); Assert.Equal(2, ruby.Segments.Length);
        Assert.Equal("1100000", ruby.Segments[0].Time.StartUs); Assert.Equal("1800000", ruby.Segments[1].Time.EndUs);
        var baseTime = Assert.Single(line.Tokens).Time;
        var group = new RubyGroupTargetDto("primary", line.Id, ruby.BaseTokenId, ruby.Id);
        RubySegmentTargetDto Target(string id) => new(group.BranchId, group.LineId, group.TokenId, group.RubyId, id);
        var first = Target(ruby.Segments[0].Id); var second = Target(ruby.Segments[1].Id);
        Assert.True((await Send(new SetRubySegmentTimeCommandDto(first, "1150000", "1450000"))).Changed);
        Assert.True((await Send(new SetRubySegmentTextCommandDto(first, "セ"))).Changed);
        Assert.True((await Send(new ClearRubySegmentTimeCommandDto(second))).Changed);
        Assert.Equal(ProjectLyricTimingStatus.Missing, (await Window()).Lines[0].RubyAnnotations[0].Segments[1].TimingStatus);
        var added = await Send(new AddRubySegmentCommandDto(group, "追加", second.SegmentId));
        string extra = added.RubyResult!.CreatedSegmentId!; Assert.False(string.IsNullOrEmpty(extra));
        await Send(new ReorderRubySegmentsCommandDto(group, [extra, first.SegmentId, second.SegmentId]));
        Assert.Equal(extra, (await Window()).Lines[0].RubyAnnotations[0].Segments[0].Id);
        await Send(new RemoveRubySegmentCommandDto(Target(extra)));
        RubySegmentTimeChangeDto[] changes = [new(first, "1200000", "1500000"), new(second, "1600000", "1900000")];
        var batch = await Send(new SetRubySegmentTimesCommandDto(changes));
        Assert.True(batch.Changed); Assert.Equal(2, batch.RubyResult!.ChangedSegmentCount);
        var finalRuby = (await Window()).Lines[0].RubyAnnotations[0];
        Assert.Equal("セん", finalRuby.Text); Assert.Equal(baseTime, (await Window()).Lines[0].Tokens[0].Time);
        await Send(new ProjectCommandDto("history.undo"));
        var undone = (await Window()).Lines[0].RubyAnnotations[0];
        Assert.Equal("1150000", undone.Segments[0].Time.StartUs); Assert.Equal(ProjectLyricTimingStatus.Missing, undone.Segments[1].TimingStatus);
        string beforeNoOp = revision;
        var noOp = await Send(new SetRubySegmentTextCommandDto(first, "セ"));
        Assert.False(noOp.Changed); Assert.True(noOp.CanRedo); Assert.Equal(beforeNoOp, revision);
        await Send(new ProjectCommandDto("history.redo"));
        Assert.Equal(JsonSerializer.Serialize(finalRuby, Json), JsonSerializer.Serialize((await Window()).Lines[0].RubyAnnotations[0], Json));
        // Add another source: document scope must include it; branch scope must disclose its omission.
        using (var append = await f.RawCommand(PathFor(), revision,
            """{"type":"lyrics.appendText","text":"translation","destination":{"kind":"newTranslation","name":"English"}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, append.StatusCode);
            revision = (await append.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.ProjectRevision;
        }
        string expectedPrimary = JsonSerializer.Serialize((await Window()).Lines, Json);
        var branches = await f.Get<ProjectLyricBranchesResponse>(PathFor() + "/lyrics/branches?expectedRevision=" + revision);
        Assert.Equal(2, branches.Branches.Length);
        var handle = await f.Post<FileHandleResponse>("/api/v1/file-handles", new FileHandleRequest("ruby-root", "saved", Metadata: f.Metadata()), HttpStatusCode.Created);
        await f.Job(PathFor() + "/save-jobs", new SaveProjectRequest(revision, handle.HandleId, f.Metadata()));
        await f.Job(PathFor() + "/close", new CloseProjectRequest(revision, Metadata: f.Metadata()));
        JobResponse opened = await f.Job("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()));
        session = opened.ProjectSessionId!; revision = "0";
        Assert.Equal(expectedPrimary, JsonSerializer.Serialize((await Window()).Lines, Json));
        Assert.Equal(JsonSerializer.Serialize(branches.Branches, Json), JsonSerializer.Serialize((await f.Get<ProjectLyricBranchesResponse>(PathFor() + "/lyrics/branches?expectedRevision=0")).Branches, Json));
        var plan = await f.Post<LyricsExportPlanResponse>(PathFor() + "/lyrics-export-plans",
            new LyricsExportPlanRequest("0", null, "ttml.project", new(), f.Metadata(), "document"), HttpStatusCode.Created);
        Assert.Equal("document", plan.Scope); Assert.Null(plan.BranchId); Assert.Equal(0, plan.OmittedBranchCount);
        Assert.False(plan.RequiresLossAcceptance);
        var exported = await f.Post<LyricsExportResponse>(PathFor() + "/lyrics-exports", new LyricsExportRequest("0", plan.PlanId, false, f.Metadata()), HttpStatusCode.Created);
        Assert.Equal("document", exported.Scope); Assert.Null(exported.BranchId); Assert.Equal(2, exported.Report.SourceLineCount);
        Assert.Equal(2, exported.Report.WrittenRubySegmentCount); Assert.Equal(0, exported.Report.SkippedRubySegmentCount); Assert.Equal(0, exported.Report.DroppedRubySegmentTimeCount);
        using HttpResponseMessage download = await f.Http.GetAsync(exported.ContentUrl, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("application/ttml+xml", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".ttml", download.Content.Headers.ContentDisposition!.FileName, StringComparison.Ordinal);
        byte[] bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(exported.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using MemoryStream coreInput = new(bytes);
        var decoded = new TtmlImporter().Import(coreInput, new() { FileName = "download.ttml" });
        Assert.Equal(2, decoded.Document.Branches.Length);
        var decodedRuby = Assert.Single(decoded.Document.Lines[0].RubyAnnotations);
        Assert.Equal(finalRuby.Id, decodedRuby.Id); Assert.Equal(finalRuby.Text, decodedRuby.Text);
        Assert.Equal(finalRuby.Segments.Select(item => item.Id), decodedRuby.Segments.Select(item => item.Id));
        Assert.Equal(new TimeRange(1_200_000, 1_500_000), decodedRuby.Segments[0].Time);
        Assert.Equal(new TimeRange(1_600_000, 1_900_000), decodedRuby.Segments[1].Time);
        using (HttpRequestMessage headRequest = new(HttpMethod.Head, exported.ContentUrl))
        using (HttpResponseMessage head = await f.Http.SendAsync(headRequest))
        { Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(bytes.Length, head.Content.Headers.ContentLength); Assert.Empty(await head.Content.ReadAsByteArrayAsync()); }
        using (HttpRequestMessage rangeRequest = new(HttpMethod.Get, exported.ContentUrl))
        {
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 31);
            using HttpResponseMessage range = await f.Http.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode); Assert.Equal(bytes[..32], await range.Content.ReadAsByteArrayAsync());
        }
        using (HttpRequestMessage cachedRequest = new(HttpMethod.Get, exported.ContentUrl))
        {
            cachedRequest.Headers.IfNoneMatch.Add(download.Headers.ETag!);
            using HttpResponseMessage cached = await f.Http.SendAsync(cachedRequest); Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        }
        var branchPlan = await f.Post<LyricsExportPlanResponse>(PathFor() + "/lyrics-export-plans",
            new LyricsExportPlanRequest("0", "primary", "ttml.project", new(), f.Metadata()), HttpStatusCode.Created);
        Assert.True(branchPlan.RequiresLossAcceptance); Assert.Contains(branchPlan.AdditionalLosses, item => item.Feature == "omittedBranches");
        using (var denied = await f.Http.PostAsJsonAsync(PathFor() + "/lyrics-exports", new LyricsExportRequest("0", branchPlan.PlanId, false, f.Metadata()), Json))
            await Error(denied, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        // Rejection leaves the exact immutable plan usable after explicit loss acceptance.
        await f.Post<LyricsExportResponse>(PathFor() + "/lyrics-exports", new LyricsExportRequest("0", branchPlan.PlanId, true, f.Metadata()), HttpStatusCode.Created);
        // The response has acquired immutable content before close; the caller can finish reading afterwards.
        using HttpResponseMessage inFlight = await f.Http.GetAsync(exported.ContentUrl, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, inFlight.StatusCode);
        await f.Job(PathFor() + "/close", new CloseProjectRequest("0", Metadata: f.Metadata()));
        Assert.Equal(bytes, await inFlight.Content.ReadAsByteArrayAsync());
        using HttpResponseMessage closed = await f.Http.GetAsync(exported.ContentUrl);
        await Error(closed, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task 已发布真实HTTP严格形状修订目标预算和原子失败均无局部变更()
    {
        await using PublishedFixture f = await PublishedFixture.Start(output);
        var created = await f.CreateProject(); string path = "/api/v1/project-sessions/" + created.ProjectSessionId;
        string readRoute = path + "/lyrics/window?branchId=primary&expectedRevision=0";
        var window = await f.Get<ProjectLyricsWindowResponse>(readRoute); var line = window.Lines[0]; var ruby = line.RubyAnnotations[0];
        var target = new RubySegmentTargetDto("primary", line.Id, ruby.BaseTokenId, ruby.Id, ruby.Segments[0].Id);
        string before = await f.Http.GetStringAsync(path); string original = await f.Http.GetStringAsync(readRoute);
        async Task Reject(ProjectCommandDto command, string revision, HttpStatusCode status, BackendErrorCode code, string? failure = null)
        {
            var metadata = f.Metadata(); using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            await Error(response, status, code, failure);
            Assert.Equal(before, await f.Http.GetStringAsync(path)); Assert.Equal(original, await f.Http.GetStringAsync(readRoute));
        }
        await Reject(new SetRubyTextCommandDto(new("primary", line.Id, ruby.BaseTokenId), "replacement"), "0", HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "RubySegmentsRequireExplicitEdit");
        await Reject(new SetRubySegmentTimeCommandDto(target, "1", "2"), "1", HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        await Reject(new SetRubySegmentTimeCommandDto(target with { SegmentId = "missing" }, "1", "2"), "0", HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale");
        await Reject(new SetRubySegmentTimesCommandDto([new(target, "123", "456"), new(target with { SegmentId = "missing" }, "500", "600")]), "0", HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale");
        await Reject(new SetRubySegmentTextCommandDto(target, new string('x', RubySegmentEditingLimits.MaxIntentBytes + 1)), "0", HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        string t = JsonSerializer.Serialize(target, Json);
        foreach (string command in new[] {
            "{\"type\":\"lyrics.setRubySegmentTime\",\"target\":" + t + ",\"startUs\":null,\"endUs\":\"2\"}",
            "{\"type\":\"lyrics.setRubySegmentTime\",\"target\":" + t + ",\"startUs\":\"1\",\"endUs\":\"2\",\"extra\":1}",
            "{\"type\":\"lyrics.setRubySegmentText\",\"target\":" + t + ",\"text\":\"x\",\"text\":\"y\"}",
            "{\"type\":\"lyrics.setRubySegmentTimes\",\"changes\":[]}" })
        {
            using var invalid = await f.RawCommand(path, "0", command); await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
            Assert.Equal(before, await f.Http.GetStringAsync(path)); Assert.Equal(original, await f.Http.GetStringAsync(readRoute));
        }
        foreach (string fields in new[] { "\"scope\":null,\"branchId\":\"primary\"", "\"scope\":\"unknown\",\"branchId\":\"primary\"", "\"scope\":\"branch\"", "\"scope\":\"document\",\"branchId\":null", "\"scope\":\"document\",\"branchId\":\"primary\"", "\"scope\":\"document\",\"extra\":1" })
        {
            string raw = "{\"expectedRevision\":\"0\",\"format\":\"ttml.project\",\"options\":{},\"metadata\":" + JsonSerializer.Serialize(f.Metadata(), Json) + "," + fields + "}";
            using var invalid = await f.Http.PostAsync(path + "/lyrics-export-plans", new StringContent(raw, Encoding.UTF8, "application/json"));
            await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        Assert.Equal(before, await f.Http.GetStringAsync(path)); Assert.Equal(original, await f.Http.GetStringAsync(readRoute));
        var fixedPlan = await f.Post<LyricsExportPlanResponse>(path + "/lyrics-export-plans",
            new LyricsExportPlanRequest("0", null, "ttml.project", new(), f.Metadata(), "document"), HttpStatusCode.Created);
        var metadata = f.Metadata();
        await f.Post<ProjectCommandResponse>(path + "/commands", new ProjectCommandRequest("0", new SetRubySegmentTextCommandDto(target, "編集"), metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), HttpStatusCode.OK);
        foreach (string expected in new[] { "0", "1" })
        {
            using var stalePlan = await f.Http.PostAsJsonAsync(path + "/lyrics-exports", new LyricsExportRequest(expected, fixedPlan.PlanId, true, f.Metadata()), Json);
            await Error(stalePlan, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        }
        await f.Job(path + "/close", new CloseProjectRequest("1", "discard", Metadata: f.Metadata()));
    }

    [Fact]
    public async Task 已发布真实HTTP精确Int64时间和多来源TTML再上传显式导入无静默扁平化()
    {
        await using PublishedFixture f = await PublishedFixture.Start(output);
        var created = await f.CreateProject(); string session = created.ProjectSessionId; string revision = "0";
        string PathFor() => "/api/v1/project-sessions/" + session;
        async Task<ProjectLyricsWindowResponse> Window(string branch = "primary") => await f.Get<ProjectLyricsWindowResponse>(PathFor() + "/lyrics/window?branchId=" + branch + "&expectedRevision=" + revision);
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            var metadata = f.Metadata();
            var result = await f.Post<ProjectCommandResponse>(PathFor() + "/commands", new ProjectCommandRequest(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), HttpStatusCode.OK);
            revision = result.ProjectRevision; return result;
        }
        var original = (await Window()).Lines[0]; var originalRuby = original.RubyAnnotations[0];
        var target = new RubySegmentTargetDto("primary", original.Id, originalRuby.BaseTokenId, originalRuby.Id, originalRuby.Segments[0].Id);
        const string exactStart = "9007199254740993"; const string exactEnd = "9007199254741999";
        await Send(new SetRubySegmentTimeCommandDto(target, exactStart, exactEnd));
        Assert.Equal(exactStart, (await Window()).Lines[0].RubyAnnotations[0].Segments[0].Time.StartUs);
        Assert.Equal(original.Time, (await Window()).Lines[0].Time); Assert.Equal(original.Tokens[0].Time, (await Window()).Lines[0].Tokens[0].Time);
        await Send(new ProjectCommandDto("history.undo"));
        Assert.Equal("1100000", (await Window()).Lines[0].RubyAnnotations[0].Segments[0].Time.StartUs);
        await Send(new ProjectCommandDto("history.redo"));
        Assert.Equal(exactEnd, (await Window()).Lines[0].RubyAnnotations[0].Segments[0].Time.EndUs);
        var handle = await f.Post<FileHandleResponse>("/api/v1/file-handles", new FileHandleRequest("ruby-root", "saved", Metadata: f.Metadata()), HttpStatusCode.Created);
        await f.Job(PathFor() + "/save-jobs", new SaveProjectRequest(revision, handle.HandleId, f.Metadata()));
        await f.Job(PathFor() + "/close", new CloseProjectRequest(revision, Metadata: f.Metadata()));
        session = (await f.Job("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()))).ProjectSessionId!; revision = "0";
        var reopened = (await Window()).Lines[0];
        Assert.Equal(exactStart, reopened.RubyAnnotations[0].Segments[0].Time.StartUs); Assert.Equal(exactEnd, reopened.RubyAnnotations[0].Segments[0].Time.EndUs);
        Assert.Equal(original.Time, reopened.Time); Assert.Equal(original.Tokens[0].Time, reopened.Tokens[0].Time);
        var singlePlan = await f.Post<LyricsExportPlanResponse>(PathFor() + "/lyrics-export-plans", new LyricsExportPlanRequest(revision, null, "ttml.project", new(), f.Metadata(), "document"), HttpStatusCode.Created);
        Assert.True(singlePlan.RequiresLossAcceptance); // Out-of-line raw times are retained and their standard TTML projection is disclosed.
        var singleExport = await f.Post<LyricsExportResponse>(PathFor() + "/lyrics-exports", new LyricsExportRequest(revision, singlePlan.PlanId, true, f.Metadata()), HttpStatusCode.Created);
        byte[] singleBytes = await f.Http.GetByteArrayAsync(singleExport.ContentUrl);
        using (MemoryStream rawInput = new(singleBytes))
        {
            var raw = new TtmlImporter().Import(rawInput, new() { FileName = "int64.ttml" }).Document.Lines[0];
            Assert.Equal(new TimeRange(9_007_199_254_740_993, 9_007_199_254_741_999), raw.RubyAnnotations[0].Segments[0].Time);
            Assert.Equal(original.Time.StartUs, raw.Time!.Value.StartUs.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(original.Time.EndUs, raw.Time.Value.EndUs.ToString(CultureInfo.InvariantCulture));
        }
        // Reimport one source using the old options shape, then link it to a real primary line.
        var singleAsset = await f.Upload("single.ttml", singleBytes, "application/ttml+xml");
        var secondary = await Send(new ImportAssetCommandDto(singleAsset.AssetId, new NewBranchLyricsDestinationDto("Linked source", ProjectLyricBranchRole.Custom), new()));
        string sourceBranch = secondary.ImportResult!.SelectedBranchId;
        var sourceLine = (await Window(sourceBranch)).Lines[0];
        await Send(new LinkPrimaryLineCommandDto(sourceBranch, sourceLine.Id, original.Id));
        var multiPlan = await f.Post<LyricsExportPlanResponse>(PathFor() + "/lyrics-export-plans", new LyricsExportPlanRequest(revision, null, "ttml.project", new(), f.Metadata(), "document"), HttpStatusCode.Created);
        var multiExport = await f.Post<LyricsExportResponse>(PathFor() + "/lyrics-exports", new LyricsExportRequest(revision, multiPlan.PlanId, true, f.Metadata()), HttpStatusCode.Created);
        byte[] multiBytes = await f.Http.GetByteArrayAsync(multiExport.ContentUrl);
        var multiAsset = await f.Upload("multiple.ttml", multiBytes, "application/ttml+xml");
        // Remove the target primary line after export so the selected source's external link becomes unavailable.
        await Send(new RemoveLineCommandDto(new("primary", original.Id)));
        string before = await f.Http.GetStringAsync(PathFor());
        string priorSources = JsonSerializer.Serialize((await Window(sourceBranch)).Lines, Json);
        async Task RejectImport(LyricsImportOptionsDto options, string failure)
        {
            var metadata = f.Metadata();
            using var rejected = await f.Http.PostAsJsonAsync(PathFor() + "/commands", new ProjectCommandRequest(revision,
                new ImportAssetCommandDto(multiAsset.AssetId, new NewBranchLyricsDestinationDto("Rejected", ProjectLyricBranchRole.Custom), options), metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            await Error(rejected, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, failure);
            Assert.Equal(before, await f.Http.GetStringAsync(PathFor())); Assert.Equal(priorSources, JsonSerializer.Serialize((await Window(sourceBranch)).Lines, Json));
        }
        await RejectImport(new(), "SourceBranchRequired");
        await RejectImport(new(SourceBranchId: "unknown-source"), "SourceBranchNotFound");
        string explicitNull = JsonSerializer.Serialize(new { type = "lyrics.importAsset", assetId = multiAsset.AssetId,
            destination = new { kind = "newBranch", name = "Rejected null", role = "custom" }, options = new { sourceBranchId = (string?)null } }, Json);
        using (var rejectedNull = await f.RawCommand(PathFor(), revision, explicitNull)) await Error(rejectedNull, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(before, await f.Http.GetStringAsync(PathFor())); Assert.Equal(priorSources, JsonSerializer.Serialize((await Window(sourceBranch)).Lines, Json));
        var selected = await Send(new ImportAssetCommandDto(multiAsset.AssetId, new NewBranchLyricsDestinationDto("Selected", ProjectLyricBranchRole.Custom), new(SourceBranchId: sourceBranch)));
        Assert.Contains(selected.ImportResult!.Report!.Issues, issue => issue.Severity == LyricsImportIssueSeverity.Warning && issue.Message.Contains("SourceBranchLinkUnavailable", StringComparison.Ordinal));
        var selectedLine = (await Window(selected.ImportResult.SelectedBranchId)).Lines[0];
        var selectedRuby = Assert.Single(selectedLine.RubyAnnotations); var oldRuby = sourceLine.RubyAnnotations[0];
        Assert.Equal(original.Id, selectedLine.PrimaryLineId);
        Assert.NotEqual(sourceLine.Id, selectedLine.Id); Assert.NotEqual(oldRuby.Id, selectedRuby.Id);
        Assert.Equal(Assert.Single(selectedLine.Tokens).Id, selectedRuby.BaseTokenId);
        Assert.DoesNotContain(selectedRuby.Segments, segment => oldRuby.Segments.Any(old => old.Id == segment.Id));
        Assert.Equal(oldRuby.Segments.Select(segment => segment.Text), selectedRuby.Segments.Select(segment => segment.Text));
        Assert.Equal(exactStart, selectedRuby.Segments[0].Time.StartUs); Assert.Equal(exactEnd, selectedRuby.Segments[0].Time.EndUs);
        var allBranches = await f.Get<ProjectLyricBranchesResponse>(PathFor() + "/lyrics/branches?expectedRevision=" + revision);
        List<string> ids = [.. allBranches.Branches.Select(branch => branch.Id)];
        foreach (var branch in allBranches.Branches)
            foreach (var row in (await Window(branch.Id)).Lines)
            {
                ids.Add(row.Id); ids.AddRange(row.Tokens.Select(token => token.Id));
                foreach (var annotation in row.RubyAnnotations) { ids.Add(annotation.Id); ids.AddRange(annotation.Segments.Select(segment => segment.Id)); }
            }
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        await f.Job(PathFor() + "/close", new CloseProjectRequest(revision, "discard", Metadata: f.Metadata()));
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, BackendErrorCode code, string? failure = null)
    {
        string body = await response.Content.ReadAsStringAsync(); Assert.True(response.StatusCode == status, body);
        var error = JsonSerializer.Deserialize<BackendErrorResponse>(body, Json)!; Assert.Equal(code, error.Code);
        if (failure is not null) Assert.Equal(failure, error.FailureCode);
    }

    private sealed class PublishedFixture : IAsyncDisposable
    {
        private readonly Process _server;
        private readonly string _root;
        private readonly ITestOutputHelper _output;
        private readonly Task _stdout;
        private readonly Task _stderr;
        private readonly ObservedProcessTree _processes;
        internal HttpClient Http { get; }
        internal string Host { get; }
        private PublishedFixture(Process server, string root, ITestOutputHelper output, HttpClient http, string host, Task stdout, Task stderr, ObservedProcessTree processes)
        { _server = server; _root = root; _output = output; Http = http; Host = host; _stdout = stdout; _stderr = stderr; _processes = processes; }
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Host, DateTimeOffset.UtcNow.AddMinutes(1));
        internal static async Task<PublishedFixture> Start(ITestOutputHelper output)
        {
            string repo = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repo, "MakoKaraGenerator.slnx")) || !Directory.Exists(Path.Combine(repo, "src")))
                repo = Directory.GetParent(repo)?.FullName ?? throw new InvalidOperationException("Cannot locate repository.");
            string server = Environment.GetEnvironmentVariable("MAKO_RUBY_HTTP_SERVER_PATH") ?? Path.Combine(repo, "artifacts/ttml-ruby/publish/server/MakoKaraGenerator.Backend.Server");
            string worker = Environment.GetEnvironmentVariable("MAKO_RUBY_HTTP_WORKER_PATH") ?? Path.Combine(repo, "artifacts/ttml-ruby/publish/worker/MakoKaraGenerator.Backend.Worker");
            Assert.True(File.Exists(server), "Publish the matching self-contained Server first: " + server);
            Assert.True(File.Exists(worker), "Publish the matching complete Worker first: " + worker);
            string root = Path.Combine(repo, "artifacts/ttml-ruby/http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string projects = Path.Combine(root, "projects"); Directory.CreateDirectory(Path.Combine(projects, "saved"));
            string bootstrap = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            ProcessStartInfo start = new(server) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("MAKO_BACKEND_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
            start.Environment["MAKO_BACKEND_RESOURCE_ROOT"] = Path.Combine(root, "resources");
            start.Environment["MAKO_BACKEND_STATE_ROOT"] = Path.Combine(root, "state");
            start.Environment["MAKO_BACKEND_ALLOWED_ROOTS"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["ruby-root"] = projects });
            start.Environment["MAKO_BACKEND_BOOTSTRAP_TOKEN"] = bootstrap;
            start.Environment["MAKO_BACKEND_WORKER_PATH"] = worker;
            start.Environment["MAKO_BACKEND_FFMPEG_PATH"] = "/usr/bin/ffmpeg"; start.Environment["MAKO_BACKEND_FFPROBE_PATH"] = "/usr/bin/ffprobe";
            Process process = Process.Start(start)!;
            ObservedProcessTree processes = new(process.Id, Path.GetFileName(worker), root);
            TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Capture(StreamReader reader, string path, bool watchReady)
            {
                using StreamWriter log = new(path);
                while (await reader.ReadLineAsync() is { } line)
                {
                    await log.WriteLineAsync(line);
                    if (watchReady && line.StartsWith("BACKEND_READY ", StringComparison.Ordinal)) ready.TrySetResult(line);
                }
                if (watchReady) ready.TrySetException(new InvalidOperationException("Server exited before ready; see " + path));
            }
            Task stdout = Capture(process.StandardOutput, Path.Combine(root, "server.stdout.log"), true);
            Task stderr = Capture(process.StandardError, Path.Combine(root, "server.stderr.log"), false);
            try
            {
                string[] address = (await ready.Task.WaitAsync(TimeSpan.FromSeconds(45))).Split(' ');
                HttpClient http = new(new AuditHandler(Path.Combine(root, "http.ndjson"))) { BaseAddress = new(address[1]), Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Authorization = new("Bearer", bootstrap);
                using HttpResponseMessage registration = await http.PostAsJsonAsync("/api/v1/clients", new ClientRegistrationRequest(), Json);
                Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
                var client = (await registration.Content.ReadFromJsonAsync<ClientRegistrationResponse>(Json))!;
                http.DefaultRequestHeaders.Authorization = new("Bearer", client.AccessToken); http.DefaultRequestHeaders.Add("X-Mako-Client-Id", client.ClientId);
                var capabilities = (await http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
                Assert.True(capabilities.AudioPreparation!.Enabled, capabilities.AudioPreparation.DisabledReason);
                output.WriteLine("Published Server PID={0}; evidence={1}", process.Id, root);
                return new(process, root, output, http, address[2], stdout, stderr, processes);
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); await processes.Finish(requireWorker: false); process.Dispose(); throw; }
        }
        internal async Task<T> Get<T>(string route) => (await Http.GetFromJsonAsync<T>(route, Json))!;
        internal async Task<T> Post<T>(string route, object input, HttpStatusCode status)
        {
            using var response = await Http.PostAsJsonAsync(route, input, Json);
            Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>(Json))!;
        }
        internal Task<HttpResponseMessage> RawCommand(string path, string revision, string command) => Http.PostAsync(path + "/commands", new StringContent(
            "{\"expectedRevision\":\"" + revision + "\",\"command\":" + command + ",\"requestId\":" + JsonSerializer.Serialize(Metadata().RequestId) + ",\"hostInstanceId\":" + JsonSerializer.Serialize(Host) + ",\"validUntilUtc\":" + JsonSerializer.Serialize(DateTimeOffset.UtcNow.AddMinutes(1)) + "}", Encoding.UTF8, "application/json"));
        internal async Task<JobResponse> Job(string route, object request)
        {
            JobResponse accepted = await Post<JobResponse>(route, request, HttpStatusCode.Accepted);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            while (true)
            {
                JobResponse state = (await Http.GetFromJsonAsync<JobResponse>("/api/v1/jobs/" + accepted.JobId, Json, timeout.Token))!;
                if (state.State is "succeeded" or "failed" or "cancelled") { Assert.True(state.State == "succeeded", state.Error?.ToString()); return state; }
                await Task.Delay(20, timeout.Token);
            }
        }
        internal async Task<AssetResponse> Upload(string name, byte[] bytes, string mediaType)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/assets?fileName=" + name) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new(mediaType); var metadata = Metadata();
            request.Headers.Add("X-Mako-Request-Id", metadata.RequestId); request.Headers.Add("X-Mako-Host-Instance-Id", Host);
            request.Headers.Add("X-Mako-Valid-Until-Utc", metadata.ValidUntilUtc!.Value.ToString("O", CultureInfo.InvariantCulture));
            using var response = await Http.SendAsync(request); Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<AssetResponse>(Json))!;
        }
        internal async Task<ProjectSessionSummaryResponse> CreateProject()
        {
            byte[] wave = new byte[44 + 8_000 * 2 * 4];
            using (var writer = new BinaryWriter(new MemoryStream(wave), Encoding.ASCII, leaveOpen: false))
            { writer.Write("RIFF"u8); writer.Write(wave.Length - 8); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(wave.Length - 44); }
            var audio = await Upload("ruby.wav", wave, "audio/wav"); var lyrics = await Upload("ruby.ttml", Encoding.UTF8.GetBytes(Ttml), "application/ttml+xml");
            return await Post<ProjectSessionSummaryResponse>("/api/v1/project-sessions", new CreateProjectSessionRequest("ruby-http", audio.AssetId, lyrics.AssetId,
                new(640, 360, 30, 1, OutputDurationUs: 4_000_000), ImporterId: "ttml", Metadata: Metadata()), HttpStatusCode.Created);
        }
        public async ValueTask DisposeAsync()
        {
            int exitCode = -1;
            try
            {
                Http.Dispose();
                if (!_server.HasExited)
                {
                    ProcessStartInfo kill = new("/bin/kill") { UseShellExecute = false }; kill.ArgumentList.Add("-TERM"); kill.ArgumentList.Add(_server.Id.ToString(CultureInfo.InvariantCulture));
                    using Process signal = Process.Start(kill)!; await signal.WaitForExitAsync();
                    try { await _server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (TimeoutException) { _server.Kill(entireProcessTree: true); await _server.WaitForExitAsync(); throw; }
                }
                await Task.WhenAll(_stdout, _stderr); exitCode = _server.ExitCode;
                _output.WriteLine("Server exit={0}; evidence={1}", exitCode, _root);
            }
            finally { try { await _processes.Finish(requireWorker: true); } finally { _server.Dispose(); } }
            Assert.Equal(0, exitCode);
        }
    }
    /// <summary>Observes only the owned process tree, including already observed descendants after reparenting.</summary>
    private sealed class ObservedProcessTree : IAsyncDisposable
    {
        private sealed record Identity(int Pid, long StartTime, int ParentPid, string Image);
        private readonly ConcurrentDictionary<(int Pid, long StartTime), Identity> _observed = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _watch;
        private readonly string _workerImage;
        private readonly string _root;
        private readonly int _serverPid;
        private readonly long _serverStart;
        internal ObservedProcessTree(int serverPid, string workerImage, string root)
        {
            _serverPid = serverPid; _workerImage = workerImage; _root = root;
            Identity server = Read(serverPid) ?? throw new InvalidOperationException("Cannot observe owned Server identity.");
            _serverStart = server.StartTime; _observed.TryAdd((server.Pid, server.StartTime), server);
            _watch = Watch();
        }
        private async Task Watch()
        {
            while (!_stop.IsCancellationRequested)
            {
                Sample();
                try { await Task.Delay(5, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        private void Sample()
        {
            HashSet<int> seen = [];
            Queue<int> pending = new();
            if (Read(_serverPid) is { } server && server.StartTime == _serverStart) pending.Enqueue(_serverPid);
            foreach (Identity identity in _observed.Values)
                if (Read(identity.Pid) is { } current && current.StartTime == identity.StartTime) pending.Enqueue(identity.Pid);
            while (pending.TryDequeue(out int pid))
            {
                if (!seen.Add(pid) || Read(pid) is not { } identity) continue;
                _observed.AddOrUpdate((identity.Pid, identity.StartTime), identity, (_, previous) => identity.Image.Length > 0 ? identity : previous);
                try
                {
                    foreach (string child in File.ReadAllText($"/proc/{pid}/task/{pid}/children").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(child, CultureInfo.InvariantCulture, out int childPid)) pending.Enqueue(childPid);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        private static Identity? Read(int pid)
        {
            try
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                int end = stat.LastIndexOf(')'); if (end < 0) return null;
                string[] values = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // stat fields 4 (parent pid) and 22 (start clock ticks); comm can contain spaces and parentheses.
                int parent = int.Parse(values[1], CultureInfo.InvariantCulture);
                long start = long.Parse(values[19], CultureInfo.InvariantCulture);
                string? executable = new FileInfo($"/proc/{pid}/exe").LinkTarget;
                return new(pid, start, parent, Path.GetFileName(executable ?? string.Empty));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return null; }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); await _watch; _stop.Dispose(); }
        internal async Task Finish(bool requireWorker)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                while (_observed.Values.Any(identity => Read(identity.Pid) is { } current && current.StartTime == identity.StartTime))
                { Sample(); await Task.Delay(20, timeout.Token); }
            }
            catch (OperationCanceledException) { /* Preserve identities below before failing the exit assertion. */ }
            finally { await DisposeAsync(); }
            var evidence = _observed.Values.OrderBy(identity => identity.Pid).ThenBy(identity => identity.StartTime).Select(identity => new
            {
                identity.Pid, identity.StartTime, identity.ParentPid, identity.Image,
                Gone = Read(identity.Pid) is not { } current || current.StartTime != identity.StartTime,
            }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(_root, "observed-processes.json"), JsonSerializer.Serialize(evidence, Json));
            Assert.Contains(evidence, identity => identity.Pid == _serverPid);
            if (requireWorker) Assert.Contains(evidence, identity => identity.Image == _workerImage);
            Assert.All(evidence, identity => Assert.True(identity.Gone, "Observed process identity remains: " + identity.Pid + "/" + identity.StartTime));
        }
    }

    private sealed class AuditHandler(string path) : DelegatingHandler(new HttpClientHandler())
    {
        private readonly StreamWriter _log = new(path) { AutoFlush = true };
        private int _sequence;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage result = await base.SendAsync(request, cancellationToken);
            string route = request.RequestUri!.PathAndQuery;
            await _log.WriteLineAsync(JsonSerializer.Serialize(new { method = request.Method.Method, path = route, status = (int)result.StatusCode }));
            // Registration responses contain credentials. Only content-type is retained from ordinary response headers.
            if (request.RequestUri.AbsolutePath != "/api/v1/clients")
            {
                string directory = Path.Combine(Path.GetDirectoryName(path)!, "http-exchanges"); Directory.CreateDirectory(directory);
                string stem = Interlocked.Increment(ref _sequence).ToString("D4", CultureInfo.InvariantCulture);
                JsonElement? input = await ReadJson(request.Content); JsonElement? body = await ReadJson(result.Content);
                var headers = new Dictionary<string, string>();
                if (result.Content.Headers.ContentType is { } contentType) headers.Add("Content-Type", contentType.ToString());
                await File.WriteAllTextAsync(Path.Combine(directory, stem + ".request.json"), JsonSerializer.Serialize(new { method = request.Method.Method, path = route, json = input }, Json), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(directory, stem + ".response.json"), JsonSerializer.Serialize(new { status = (int)result.StatusCode, headers, json = body }, Json), cancellationToken);
            }
            return result;
        }
        private static async Task<JsonElement?> ReadJson(HttpContent? content)
        {
            string? mime = content?.Headers.ContentType?.MediaType;
            if (mime is null || !(mime == "application/json" || mime.EndsWith("+json", StringComparison.Ordinal))) return null;
            try { using JsonDocument document = JsonDocument.Parse(await content!.ReadAsStringAsync()); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        protected override void Dispose(bool disposing) { if (disposing) _log.Dispose(); base.Dispose(disposing); }
    }
}
