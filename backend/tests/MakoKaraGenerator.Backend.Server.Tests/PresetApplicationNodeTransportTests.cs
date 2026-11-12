using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G23真实HTTP标记模板候选实际Worker像素历史及保存重开闭环()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())), presetProfile: true);
        ProjectSession project = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, project.ProjectSessionId);
        f.Projects.ConfigureTemplateLibrary();
        string definition = TemplateLibraryWireTests.Definition("标记透明度")
            .Replace("\"opacity\":1", "\"opacity\":0.2", StringComparison.Ordinal)
            .Replace("\"properties\":[\"typography.fontSizePx\"]", "\"properties\":[\"opacity\"]", StringComparison.Ordinal);
        using (HttpResponseMessage created = await TemplatePost(f, "missing", "{\"type\":\"create\",\"definition\":" + definition + "}"))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        TemplateListResponse personal = (await f.Http.GetFromJsonAsync<TemplateListResponse>("/api/v1/template-libraries/personal/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(personal.Templates);
        await http.Send(new AddLineCommandDto("primary"));
        ProjectLyricsWindowResponse lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;
        string line = Assert.Single(lyrics.Lines).Id;
        await http.Send(new SetLineTextCommandDto(new("primary", line), "起唱"));
        await http.Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line), "2000000", "3000000"));
        ProjectOrchestrationProgramDto program = (await http.Read()).Programs.Single(p => p.BranchId == "primary");
        var countdown = await http.Send(new CreateOrchestrationCountdownCommandDto(program.RootContainerId, "三点前奏", 3));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        string mark = countdown.LeadInResult.MarkNodeIds![0];
        await http.Send(new CreateOrchestrationLeadInCommandDto("primary", "cue/mark-preset", null, line, indicator, "2000000", true));
        byte[] baseline = await CaptureMarkPng(f, project.ProjectSessionId, http.Revision);
        ProjectSessionSummaryResponse before = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        PresetApplicationTargetDto[] targets = [new("node", NodeId: mark, IncludeDescendants: false)];
        PresetApplicationPreviewRequest request = new(http.Revision, "personal", personal.Revision, template.TemplateId,
            template.TemplateRevision, targets, null, new(320, 180), Metadata: f.Metadata());
        using HttpResponseMessage candidateResponse = await f.Http.PostAsJsonAsync(http.Path + "/preset-application-previews", request, Json);
        Assert.True(candidateResponse.StatusCode == HttpStatusCode.Created, await candidateResponse.Content.ReadAsStringAsync());
        PreviewSessionResponse candidate = (await candidateResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("presetCandidate", candidate.Source.Kind);
        Assert.Equal(project.ProjectSessionId, candidate.Source.SourceId);
        Assert.Equal(before.ProjectRevision, candidate.Source.SourceRevision);
        byte[] candidatePng = await RenderMarkPng(f, candidate);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(baseline)), Convert.ToHexString(SHA256.HashData(candidatePng)));
        ProjectSessionSummaryResponse after = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        Assert.Equal(before.ProjectRevision, after.ProjectRevision); Assert.Equal(before.IsDirty, after.IsDirty);
        Assert.Equal(before.CanUndo, after.CanUndo); Assert.Equal(before.CanRedo, after.CanRedo);
        await http.Send(new ApplyPresetApplicationCommandDto("personal", template.TemplateId, personal.Revision, targets, null));
        ProjectPresetApplicationsResponse applications = (await f.Http.GetFromJsonAsync<ProjectPresetApplicationsResponse>(http.Path + "/preset-applications?expectedRevision=" + http.Revision, Json))!;
        ProjectPresetApplicationDto application = Assert.Single(applications.Applications);
        Assert.True(application.TargetValid); Assert.Equal(mark, application.Target.NodeId);
        Assert.False(application.Target.IncludeDescendants);
        byte[] appliedPng = await CaptureMarkPng(f, project.ProjectSessionId, http.Revision);
        Assert.Equal(SHA256.HashData(candidatePng), SHA256.HashData(appliedPng));
        await http.Reject(new ApplyPresetApplicationCommandDto("personal", template.TemplateId, personal.Revision,
            [new("node", NodeId: "missing-mark", IncludeDescendants: false)], null), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        await http.Send(new SetPresetApplicationEnabledCommandDto(application.ApplicationId, false));
        await http.Send(new ProjectCommandDto("history.undo"));
        ProjectCommandResponse noop = await http.Send(new UpdatePresetApplicationCommandDto(application.ApplicationId,
            "personal", template.TemplateId, personal.Revision, null));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        await http.Send(new ProjectCommandDto("history.redo"));
        Assert.Equal(SHA256.HashData(baseline), SHA256.HashData(await CaptureMarkPng(f, project.ProjectSessionId, http.Revision)));
        await http.Send(new ProjectCommandDto("history.undo"));

        using HttpResponseMessage handleResponse = await f.Http.PostAsJsonAsync("/api/v1/file-handles", new FileHandleRequest("preview-test-root", "no-audio", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
        FileHandleResponse handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
        using (HttpResponseMessage saved = await f.Http.PostAsJsonAsync(http.Path + "/save-jobs", new SaveProjectRequest(http.Revision, handle.HandleId, f.Metadata()), Json))
            await NodeTerminal(f, saved);
        using (HttpResponseMessage closed = await f.Http.PostAsJsonAsync(http.Path + "/close", new CloseProjectRequest(http.Revision, ActiveJobs: "wait", Metadata: f.Metadata()), Json))
            await NodeTerminal(f, closed);
        using HttpResponseMessage opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        JobResponse reopened = await NodeTerminal(f, opened);
        NodeHttpHarness restored = new(f, reopened.ProjectSessionId);
        ProjectPresetApplicationDto persisted = Assert.Single((await f.Http.GetFromJsonAsync<ProjectPresetApplicationsResponse>(restored.Path + "/preset-applications?expectedRevision=0", Json))!.Applications);
        Assert.Equal(application.ApplicationId, persisted.ApplicationId); Assert.Equal(mark, persisted.Target.NodeId);
        Assert.True(persisted.TargetValid); Assert.True(persisted.Enabled);
        Assert.Equal(SHA256.HashData(appliedPng), SHA256.HashData(await CaptureMarkPng(f, reopened.ProjectSessionId!, "0")));
    }

    private static async Task<byte[]> CaptureMarkPng(Fixture f, string sessionId, string revision)
    {
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", sessionId, revision), new(320, 180), Metadata: f.Metadata()), Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await RenderMarkPng(f, (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!);
    }

    private static async Task<byte[]> RenderMarkPng(Fixture f, PreviewSessionResponse session)
    {
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{session.PreviewId}/frame-requests", f.FrameRequest(session, 1, 500000), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        PreviewSessionStateResponse ready = await f.WaitReady(session.PreviewId);
        using HttpResponseMessage content = await f.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        byte[] bytes = await content.Content.ReadAsByteArrayAsync();
        using HttpResponseMessage closed = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{session.PreviewId}/close",
            new ClosePreviewSessionRequest(session.PreviewGeneration, session.Source.SourceRevision, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        return bytes;
    }
}
