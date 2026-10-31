using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G16候选试看严格拒绝过期工程修订和重复目标且不创建会话()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new MakoKaraGenerator.Backend.Hosting.WorkerRenderFrameSessionFactory(new(FindWorkerApphost())),
            presetProfile: true);
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse builtin = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = builtin.Templates[0];
        PresetApplicationPreviewRequest stale = new("1", "builtin", builtin.Revision, template.TemplateId,
            template.TemplateRevision, [new("project")], null, new(320, 180), Metadata: f.Metadata());
        using HttpResponseMessage staleResponse = await f.Http.PostAsJsonAsync(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/preset-application-previews", stale, Json);
        await Error(staleResponse, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        Assert.Equal(0, f.Previews.Count);

        PresetApplicationPreviewRequest duplicate = stale with { ExpectedProjectRevision = "0", Targets = [new("project"), new("project")], Metadata = f.Metadata() };
        using HttpResponseMessage duplicateResponse = await f.Http.PostAsJsonAsync(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/preset-application-previews", duplicate, Json);
        await Error(duplicateResponse, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        Assert.Equal(0, f.Previews.Count);
    }

    [Fact]
    public async Task G16候选试看只读且与随后正式分配像素一致()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new MakoKaraGenerator.Backend.Hosting.WorkerRenderFrameSessionFactory(new(FindWorkerApphost())),
            presetProfile: true);
        f.Projects.ConfigureTemplateLibrary();
        using (HttpResponseMessage created = await TemplatePost(f, "missing", "{\"type\":\"create\",\"definition\":"
            + TemplateLibraryWireTests.Definition("G16模板") + "}"))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        TemplateListResponse personal = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/personal/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(personal.Templates);
        ProjectSessionSummaryResponse before = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;

        PresetApplicationPreviewRequest request = new("0", "personal", personal.Revision, template.TemplateId,
            template.TemplateRevision, [new("project")], null, new(320, 180), Metadata: f.Metadata());
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/preset-application-previews", request, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        PreviewSessionResponse candidate = (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("presetCandidate", candidate.PreviewKind);
        Assert.False(candidate.Committable);
        Assert.Equal("presetCandidate", candidate.Source.Kind);
        using HttpResponseMessage replaced = await f.Http.PutAsJsonAsync(
            $"/api/v1/preview-sessions/{candidate.PreviewId}/input",
            new PreviewProjectInputRequest(candidate.PreviewGeneration, candidate.Source.SourceRevision,
                candidate.Source, candidate.Size, candidate.AlphaMode, f.Metadata()), Json);
        await Error(replaced, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{candidate.PreviewId}/frame-requests",
            f.FrameRequest(candidate, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        PreviewSessionStateResponse ready = await f.WaitReady(candidate.PreviewId);
        byte[] candidatePng = await (await f.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl)).Content.ReadAsByteArrayAsync();

        ProjectSessionSummaryResponse after = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal(before.ProjectRevision, after.ProjectRevision);
        Assert.Equal(before.IsDirty, after.IsDirty);
        Assert.Equal(before.CanUndo, after.CanUndo);
        Assert.Equal(before.CanRedo, after.CanRedo);

        string apply = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "presetApplications.apply", templateLibraryId = "personal",
            templateId = template.TemplateId, templateRevision = personal.Revision,
            targets = new[] { new { kind = "project" } }
        }, Json);
        using HttpResponseMessage applied = await PostRawEditing(f, "0", apply);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        byte[] committedPng = await CapturePreviewPng(f, "1");
        Assert.Equal(SHA256.HashData(committedPng), SHA256.HashData(candidatePng));
    }
}
