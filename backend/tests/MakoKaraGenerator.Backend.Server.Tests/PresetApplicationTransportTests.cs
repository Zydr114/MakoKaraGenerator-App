using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP静态预设工程可创建公共预览并在实际PNG中反映分配与移除()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())), presetProfile: true);
        f.Projects.ConfigureTemplateLibrary();
        using (HttpResponseMessage created = await TemplatePost(f, "missing", "{\"type\":\"create\",\"definition\":"
            + TemplateLibraryWireTests.Definition("预览模板") + "}"))
        { Assert.Equal(HttpStatusCode.OK, created.StatusCode); }
        TemplateListResponse personal = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/personal/templates", Json))!;
        TemplateSummaryDto template = Assert.Single(personal.Templates);
        byte[] baseline = await CapturePreviewPng(f, "0");

        string command = JsonSerializer.Serialize(new
        {
            type = "presetApplications.apply", templateLibraryId = "personal", templateId = template.TemplateId,
            templateRevision = personal.Revision, targets = new[] { new { kind = "project" } }
        }, Json);
        using (HttpResponseMessage applied = await PostRawEditing(f, "0", command))
        { Assert.Equal(HttpStatusCode.OK, applied.StatusCode); }
        byte[] assigned = await CapturePreviewPng(f, "1");
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(baseline)), Convert.ToHexString(SHA256.HashData(assigned)));

        string applicationId = ReadApplicationId(f);
        using (HttpResponseMessage removed = await PostRawEditing(f, "1", JsonSerializer.Serialize(new
        { type = "presetApplications.remove", applicationIds = new[] { applicationId } }, Json)))
        { Assert.Equal(HttpStatusCode.OK, removed.StatusCode); }
        byte[] restored = await CapturePreviewPng(f, "2");
        Assert.Equal(SHA256.HashData(baseline), SHA256.HashData(restored));
    }

    private static async Task<byte[]> CapturePreviewPng(Fixture f, string revision)
    {
        PreviewSessionRequest request = new(new("project", f.Project.ProjectSessionId, revision), new(320, 180), Metadata: f.Metadata());
        using HttpResponseMessage createdResponse = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions", request, Json);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        PreviewSessionResponse created = (await createdResponse.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{created.PreviewId}/frame-requests", f.FrameRequest(created, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        PreviewSessionStateResponse ready = await f.WaitReady(created.PreviewId);
        using HttpResponseMessage content = await f.Http.GetAsync(ready.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        return await content.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task HTTP工程预设分配按模板修订固定快照并支持启停移除与撤销重做()
    {
        await using Fixture f = await Fixture.Create();
        f.Projects.ConfigureTemplateLibrary();
        TemplateListResponse builtin = (await f.Http.GetFromJsonAsync<TemplateListResponse>(
            "/api/v1/template-libraries/builtin/templates", Json))!;
        TemplateSummaryDto template = builtin.Templates[0];
        string session = f.Project.ProjectSessionId;
        string command = JsonSerializer.Serialize(new
        {
            type = "presetApplications.apply",
            templateLibraryId = "builtin",
            templateId = template.TemplateId,
            templateRevision = builtin.Revision,
            targets = new[] { new { kind = "project" } }
        }, Json);

        using HttpResponseMessage applied = await PostRawEditing(f, "0", command);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        ProjectCommandResponse appliedBody = (await applied.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(appliedBody.Changed);
        Assert.Equal("1", appliedBody.ProjectRevision);
        Assert.Contains("presetApplications", appliedBody.ChangeSet.Areas);
        Assert.NotNull(appliedBody.PresetApplicationResult);
        Assert.Single(appliedBody.PresetApplicationResult!.ApplicationIds);
        ProjectPresetApplicationsResponse applications = (await f.Http.GetFromJsonAsync<ProjectPresetApplicationsResponse>(
            $"/api/v1/project-sessions/{session}/preset-applications?expectedRevision=1", Json))!;
        ProjectPresetApplicationDto application = Assert.Single(applications.Applications);
        Assert.Equal(appliedBody.PresetApplicationResult.ApplicationIds[0], application.ApplicationId);
        Assert.Equal("project", application.Target.Kind);
        Assert.True(application.TargetValid);
        Assert.True(application.Source is { Comparable: false, LibraryId: null, Revision: null, NewVersionAvailable: null });
        using HttpResponseMessage stale = await f.Http.GetAsync(
            $"/api/v1/project-sessions/{session}/preset-applications?expectedRevision=0");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        ProjectStyleResponse styled = (await f.Http.GetFromJsonAsync<ProjectStyleResponse>(
            $"/api/v1/project-sessions/{session}/style?expectedRevision=1&kind=project", Json))!;
        Assert.True(styled.HasPresetApplications);

        string applicationId = ReadApplicationId(f);
        using HttpResponseMessage disabled = await PostRawEditing(f, "1", JsonSerializer.Serialize(new
        {
            type = "presetApplications.setEnabled", applicationId, enabled = false
        }, Json));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        string update = JsonSerializer.Serialize(new
        {
            type = "presetApplications.update",
            applicationId,
            templateLibraryId = "builtin",
            templateId = template.TemplateId,
            templateRevision = builtin.Revision
        }, Json);
        using HttpResponseMessage updated = await PostRawEditing(f, "2", update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        ProjectCommandResponse updatedBody = (await updated.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.False(updatedBody.Changed);
        Assert.Equal("2", updatedBody.ProjectRevision);

        using HttpResponseMessage removed = await PostRawEditing(f, "2", JsonSerializer.Serialize(new
        {
            type = "presetApplications.remove", applicationIds = new[] { applicationId }
        }, Json));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        using HttpResponseMessage undone = await PostRawEditing(f, "3", "{\"type\":\"history.undo\"}");
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.True((await f.Http.GetFromJsonAsync<ProjectStyleResponse>(
            $"/api/v1/project-sessions/{session}/style?expectedRevision=4&kind=project", Json))!.HasPresetApplications);
        using HttpResponseMessage redone = await PostRawEditing(f, "4", "{\"type\":\"history.redo\"}");
        Assert.Equal(HttpStatusCode.OK, redone.StatusCode);
        Assert.False((await f.Http.GetFromJsonAsync<ProjectStyleResponse>(
            $"/api/v1/project-sessions/{session}/style?expectedRevision=5&kind=project", Json))!.HasPresetApplications);

        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>(
            "/api/v1/capabilities", Json))!;
        Assert.Contains("project-sessions.preset-applications.read", capabilities.Features);
        foreach (string operation in new[] { "apply", "update", "setEnabled", "remove" })
            Assert.Contains("project-sessions.commands.presetApplications." + operation, capabilities.Features);
    }

    private static string ReadApplicationId(Fixture fixture)
    {
        object workspace = typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Project)!;
        Project project = ((ProjectWorkspace)workspace).Project;
        return Assert.Single(project.PresetApplications.Items).Id;
    }
}
