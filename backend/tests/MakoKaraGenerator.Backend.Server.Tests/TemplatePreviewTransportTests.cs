using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Backend.Server;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(StyleTemplateKind.Line)]
    [InlineData(StyleTemplateKind.Token)]
    public async Task 已导入高级整行元素模板支持完整候选试样与正式分配且不冒充静态定义(StyleTemplateKind kind)
    {
        var plain = new WorkerRenderFrameSessionFactory(new(FindWorkerApphost()));
        var packageFactory = new WorkerPackageRenderFrameSessionFactory(new(FindWorkerApphost()));
        using BackendRenderBackendConfiguration devices = SdkV2GpuDevices();
        await using Fixture f = await Fixture.Create(injectedFactory: new ProjectPreviewFrameSessionFactory(plain, packageFactory),
            presetProfile: true, packageProfile: true, exportFixture: true, renderBackends: devices);
        f.Projects.ConfigureTemplateLibrary();
        var package = EffectPackageFixture.Contents();
        var template = StyleTemplate.Create("advanced", "完整高级", kind, LyricStyle.Default,
            [LyricStyleProperty.Opacity], [new("scale", "缩放", TemplateEffectTarget.TokenFamily,
                package.PackageId, package.Revision, "scale", readScope: ScriptReadScope.BranchInput,
                branchInput: "vocals", fixedSeed: ulong.MaxValue)], [package]);
        var preflight = await TransferPreflight(f, await TransferBytes([template]));
        Assert.Equal("advancedRuntimeAvailable", Assert.Single(preflight.Templates).DefinitionAvailability);
        using var importedResponse = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports",
            new TemplateImportRequest("missing", preflight.PreflightId, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        var library = await TemplateList(f);
        var summary = Assert.Single(library.Templates);
        var detail = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(TemplateRoot
            + $"/personal/templates/detail?expectedRevision={library.Revision}&templateId={summary.TemplateId}", Json))!;
        Assert.Equal("advancedRuntimeAvailable", detail.DefinitionAvailability);
        Assert.Null(detail.Definition);
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        var line = lyrics.Lines[0];
        PresetApplicationTargetDto target = kind == StyleTemplateKind.Line ? new("line", BranchId: "primary", LineId: line.Id)
            : new("token", BranchId: "primary", LineId: line.Id, TokenId: line.Tokens[0].Id);
        var inputs = new Dictionary<string, string> { ["vocals"] = "primary" };
        PresetApplicationPreviewRequest candidateRequest = new("0", "personal", library.Revision, summary.TemplateId,
            summary.TemplateRevision, [target], inputs, new(320, 180), Metadata: f.Metadata());
        using var candidateResponse = await f.Http.PostAsJsonAsync(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/preset-application-previews", candidateRequest, Json);
        Assert.True(candidateResponse.StatusCode == HttpStatusCode.Created, await candidateResponse.Content.ReadAsStringAsync());
        var candidate = (await candidateResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        byte[] candidatePng = await FramePng(candidate);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
        using var sampleResponse = await f.Http.PostAsJsonAsync("/api/v1/template-preview-sessions",
            new TemplatePreviewSessionRequest(new("savedTemplate", "personal", library.Revision, summary.TemplateId,
                summary.TemplateRevision), TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, sampleResponse.StatusCode);
        var sample = (await sampleResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("template", sample.PreviewKind);
        Assert.True((await FramePng(sample)).Length > 40);
        using var fullResponse = await f.Http.GetAsync(TemplateRoot
            + $"/personal/templates/detail?expectedRevision={library.Revision}&templateId={summary.TemplateId}&definitionFormat=full");
        Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
        using var fullDocument = JsonDocument.Parse(await fullResponse.Content.ReadAsStringAsync());
        var full = fullDocument.RootElement.GetProperty("definition").Deserialize<FullTemplateDefinitionDto>(Json)!;
        TemplatePreviewSourceDto draft = new("draft", DraftRevision: "complete-draft-v1", Definition: full, DefinitionFormat: "full");
        using var optionsResponse = await f.Http.PostAsJsonAsync("/api/v1/template-preview-options", new TemplatePreviewOptionsRequest(draft), Json);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var options = (await optionsResponse.Content.ReadFromJsonAsync<TemplatePreviewOptionsResponse>(Json))!;
        var multi = Assert.Single(options.Samples, item => item.SampleId == TemplatePreviewLimits.MultiLineSampleId);
        Assert.Equal("12000000", multi.DurationUs);
        Assert.NotEmpty(multi.Targets);
        using var workshopResponse = await f.Http.PostAsJsonAsync("/api/v1/template-preview-sessions",
            new TemplatePreviewSessionRequest(draft, multi.SampleId, new(320, 180), Metadata: f.Metadata(),
                TargetKey: multi.Targets[0].Key, MaterialSlot: "sungFill"), Json);
        Assert.True(workshopResponse.StatusCode == HttpStatusCode.Created, await workshopResponse.Content.ReadAsStringAsync());
        var workshop = (await workshopResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal(new(multi.SampleId, multi.Targets[0].Key, "sungFill"), workshop.Source.TemplateSample);
        Assert.True((await FramePng(workshop)).Length > 40);
        PresetApplicationPreviewRequest draftCandidate = candidateRequest with
        {
            TemplateLibraryId = null, TemplateLibraryRevision = null, TemplateId = null, TemplateRevision = null,
            Source = draft, Metadata = f.Metadata(),
        };
        using var draftCandidateResponse = await f.Http.PostAsJsonAsync(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/preset-application-previews", draftCandidate, Json);
        Assert.True(draftCandidateResponse.StatusCode == HttpStatusCode.Created, await draftCandidateResponse.Content.ReadAsStringAsync());
        var draftPreview = (await draftCandidateResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal(SHA256.HashData(candidatePng), SHA256.HashData(await FramePng(draftPreview)));
        Assert.Equal(library.Revision, (await TemplateList(f)).Revision);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
        foreach (var finished in new[] { candidate, sample, workshop, draftPreview })
        {
            using var close = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{finished.PreviewId}/close",
                new ClosePreviewSessionRequest(finished.PreviewGeneration, finished.Source.SourceRevision, f.Metadata()), Json);
            Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        }
        using var applied = await PostRawEditing(f, "0", System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "presetApplications.apply", templateLibraryId = "personal", templateId = summary.TemplateId,
            templateRevision = library.Revision, targets = new[] { target }, branchInputs = inputs
        }, Json));
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(SHA256.HashData(candidatePng), SHA256.HashData(await CapturePreviewPng(f, "1")));

        async Task<byte[]> FramePng(PreviewSessionResponse preview)
        {
            using var accepted = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests",
                f.FrameRequest(preview, 1, 0), Json);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var ready = await f.WaitReady(preview.PreviewId);
            return await f.Http.GetByteArrayAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        }
    }

    [Fact]
    public async Task G17_savedTemplate试样创建真实会话并下载PNG()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())));
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(library.Templates, item => item.TemplateId == "builtin:line-classic");
        TemplatePreviewSessionRequest request = new(new("savedTemplate", "builtin", library.Revision,
            template.TemplateId, template.TemplateRevision), TemplatePreviewLimits.DefaultSampleId, new(320, 180),
            Metadata: f.Metadata());

        using HttpResponseMessage createdResponse = await f.Http.PostAsJsonAsync(
            "/api/v1/template-preview-sessions", request, Json);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        PreviewSessionResponse created = (await createdResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("template", created.PreviewKind);
        Assert.False(created.Committable);
        Assert.Equal("template", created.Source.Kind);
        Assert.Equal("builtin:builtin:line-classic", created.Source.SourceId);
        Assert.Equal($"{library.Revision}:{template.TemplateRevision}", created.Source.SourceRevision);

        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{created.PreviewId}/frame-requests",
            f.FrameRequest(created, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        PreviewSessionStateResponse ready = await f.WaitReady(created.PreviewId);
        using HttpResponseMessage png = await f.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        byte[] bytes = await png.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 40);
        Assert.Equal("89504e470d0a1a0a", Convert.ToHexString(bytes[..8]).ToLowerInvariant());
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(bytes)), string.Empty);
        Assert.NotNull(png.Headers.ETag);
        string etag = png.Headers.ETag!.ToString();
        using HttpRequestMessage cachedRequest = new(HttpMethod.Get, ready.Session.LatestFrame.Content.ContentUrl);
        cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using HttpResponseMessage cached = await f.Http.SendAsync(cachedRequest);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task G17_draft试样创建真实会话并下载PNG且不写模板库()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())));
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = library.Templates[0];
        TemplateDetailResponse detail = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(
            $"/api/v1/template-libraries/builtin/templates/detail?expectedRevision={library.Revision}&templateId={Uri.EscapeDataString(template.TemplateId)}", Json))!;
        Assert.NotNull(detail.Definition);
        TemplatePreviewSessionRequest request = new(new("draft", DraftRevision: "draft-test-1", Definition: detail.Definition),
            TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata());

        using HttpResponseMessage createdResponse = await f.Http.PostAsJsonAsync(
            "/api/v1/template-preview-sessions", request, Json);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        PreviewSessionResponse created = (await createdResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{created.PreviewId}/frame-requests",
            f.FrameRequest(created, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        PreviewSessionStateResponse ready = await f.WaitReady(created.PreviewId);
        using HttpResponseMessage png = await f.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        Assert.True((await png.Content.ReadAsByteArrayAsync()).Length > 40);

        TemplateListResponse after = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/personal/templates", Json))!;
        Assert.Equal("missing", after.Revision);
        Assert.Empty(after.Templates);
    }

    [Fact]
    public async Task G17_template试样拒绝更新输入()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())));
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(library.Templates, item => item.TemplateId == "builtin:line-classic");
        TemplatePreviewSessionRequest request = new(new("savedTemplate", "builtin", library.Revision,
            template.TemplateId, template.TemplateRevision), TemplatePreviewLimits.DefaultSampleId, new(320, 180),
            Metadata: f.Metadata());
        using HttpResponseMessage createdResponse = await f.Http.PostAsJsonAsync(
            "/api/v1/template-preview-sessions", request, Json);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        PreviewSessionResponse created = (await createdResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;

        using HttpResponseMessage update = await f.Http.PutAsJsonAsync(
            $"/api/v1/preview-sessions/{created.PreviewId}/input",
            new PreviewProjectInputRequest(created.PreviewGeneration, created.Source.SourceRevision,
                f.Source, new(320, 180), Metadata: f.Metadata()), Json);
        await Error(update, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        PreviewSessionStateResponse current = (await f.Http.GetFromJsonAsync<PreviewSessionStateResponse>(
            $"/api/v1/preview-sessions/{created.PreviewId}", Json))!;
        Assert.Equal("template", current.Session.PreviewKind);
        Assert.Equal(created.Source, current.Session.Source);
    }

    [Fact]
    public async Task G17_template来源union拒绝混用字段且不创建会话()
    {
        await using Fixture f = await Fixture.Create();
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(library.Templates, item => item.TemplateId == "builtin:line-classic");
        TemplateDetailResponse detail = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(
            $"/api/v1/template-libraries/builtin/templates/detail?expectedRevision={library.Revision}&templateId={Uri.EscapeDataString(template.TemplateId)}", Json))!;
        TemplatePreviewSessionRequest[] invalid =
        [
            new(new("savedTemplate", "builtin", library.Revision, template.TemplateId, template.TemplateRevision,
                DraftRevision: "mixed", Definition: detail.Definition), TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata()),
            new(new("draft", LibraryId: "builtin", DraftRevision: "draft-mixed", Definition: detail.Definition),
                TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata()),
        ];

        foreach (TemplatePreviewSessionRequest request in invalid)
        {
            using HttpResponseMessage response = await f.Http.PostAsJsonAsync(
                "/api/v1/template-preview-sessions", request, Json);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity });
            Assert.Equal(0, f.Previews.Count);
        }
    }

    [Fact]
    public async Task G17_worker能力不匹配时在创建会话前拒绝()
    {
        Factory factory = new()
        {
            ProfileError = new(BackendErrorCode.PreviewUnavailable, "profile mismatch", "worker",
                FailureCode: "WorkerStaticProfileMismatch")
        };
        await using Fixture f = await Fixture.Create(injectedFactory: factory);
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(library.Templates, item => item.TemplateId == "builtin:line-classic");
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync(
            "/api/v1/template-preview-sessions",
            new TemplatePreviewSessionRequest(new("savedTemplate", "builtin", library.Revision,
                template.TemplateId, template.TemplateRevision), TemplatePreviewLimits.DefaultSampleId, new(320, 180),
                Metadata: f.Metadata()), Json);
        await Error(response, HttpStatusCode.ServiceUnavailable, BackendErrorCode.PreviewUnavailable);
        Assert.Equal(1, factory.ProfileChecks);
        Assert.Equal(0, f.Previews.Count);
    }

    [Fact]
    public async Task G17非法来源在创建会话前拒绝()
    {
        await using Fixture f = await Fixture.Create();
        f.Projects.ConfigureTemplateLibrary();
        TemplatePreviewSessionRequest[] invalid =
        [
            new(new("unknown"), TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata()),
            new(new("savedTemplate", LibraryId: "builtin", LibraryRevision: "missing", TemplateId: "builtin:line-classic", TemplateRevision: "missing"), TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata()),
            new(new("draft", DraftRevision: "draft-without-definition"), TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata()),
        ];

        foreach (TemplatePreviewSessionRequest request in invalid)
        {
            using HttpResponseMessage response = await f.Http.PostAsJsonAsync(
                "/api/v1/template-preview-sessions", request, Json);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity });
            Assert.Equal(0, f.Previews.Count);
        }
    }
}
