using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP默认容器一次历史并可撤销重做()
    {
        await using Fixture f = await Fixture.Create();
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        Project source = workspace.Project;
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        var program = tree.FindProgram("primary")!;
        string alternateId = "http/primary/display/alternate";
        var alternate = new SceneContainerDefinition
        {
            Id = alternateId,
            ParentId = program.RootContainerId,
            Layout = new(),
        };
        workspace.Replace(source with
        {
            LyricPresentation = tree with { Nodes = tree.Nodes.Add(alternate) },
        });
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";

        async Task<ProjectCommandResponse> Send(string revision, ProjectCommandDto command)
        {
            RequestMetadataDto metadata = f.Metadata();
            using HttpResponseMessage response = await f.Http.PostAsJsonAsync(path + "/commands",
                new ProjectCommandRequest(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        }

        ProjectCommandResponse changed = await Send("0", new SetOrchestrationDefaultContainerCommandDto("primary", alternateId));
        Assert.True(changed.Changed);
        Assert.Equal("1", changed.ProjectRevision);
        Assert.True(changed.CanUndo);
        Assert.Equal(alternateId, (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(
            path + "/orchestration?expectedRevision=1", Json))!.Programs.Single(item => item.BranchId == "primary").DefaultContainerId);

        ProjectCommandResponse undone = await Send("1", new("history.undo"));
        Assert.Equal("2", undone.ProjectRevision);
        Assert.True(undone.CanRedo);
        Assert.NotEqual(alternateId, (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(
            path + "/orchestration?expectedRevision=2", Json))!.Programs.Single(item => item.BranchId == "primary").DefaultContainerId);

        ProjectCommandResponse redone = await Send("2", new("history.redo"));
        Assert.Equal("3", redone.ProjectRevision);
        Assert.Equal(alternateId, (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(
            path + "/orchestration?expectedRevision=3", Json))!.Programs.Single(item => item.BranchId == "primary").DefaultContainerId);
    }

    [Fact]
    public async Task 真实HTTP来源歌词窗口和设置带精确修订与字符串数值()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        using HttpResponseMessage branches = await f.Http.GetAsync(path + "/lyrics/branches?expectedRevision=0");
        Assert.Equal(HttpStatusCode.OK, branches.StatusCode);
        var sources = (await branches.Content.ReadFromJsonAsync<ProjectLyricBranchesResponse>(Json))!;
        Assert.Equal("primary", sources.PrimaryBranchId); Assert.Equal("0", sources.ProjectRevision);
        Assert.Equal(ProjectLyricBranchRole.Primary, Assert.Single(sources.Branches).Role);
        using HttpResponseMessage window = await f.Http.GetAsync(path + "/lyrics/window?branchId=primary&expectedRevision=0&offset=0&limit=1");
        Assert.Equal(HttpStatusCode.OK, window.StatusCode);
        var lyrics = (await window.Content.ReadFromJsonAsync<ProjectLyricsWindowResponse>(Json))!;
        Assert.Equal("hello", Assert.Single(lyrics.Lines).Text); Assert.Equal("0", lyrics.Lines[0].Time.StartUs);
        Assert.Equal(ProjectLyricTimingStatus.Imported, lyrics.Lines[0].TimingStatus);
        using HttpResponseMessage settings = await f.Http.GetAsync(path + "/settings?expectedRevision=0");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await settings.Content.ReadAsStringAsync());
        Assert.Equal("3000000", json.RootElement.GetProperty("outputDurationUs").GetString());
        Assert.Equal("3000000", json.RootElement.GetProperty("audioDurationUs").GetString());
        Assert.Equal(640, json.RootElement.GetProperty("canvasWidth").GetInt32());
        Assert.Equal("1", json.RootElement.GetProperty("protocolVersion").GetString());
        using HttpResponseMessage capabilities = await f.Http.GetAsync("/api/v1/capabilities");
        string capabilityJson = await capabilities.Content.ReadAsStringAsync();
        Assert.Contains("project-sessions.lyrics.branches.read", capabilityJson, StringComparison.Ordinal);
        Assert.Contains("project-sessions.lyrics.window.read", capabilityJson, StringComparison.Ordinal);
        Assert.Contains("project-sessions.settings.read", capabilityJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/lyrics/branches")]
    [InlineData("/lyrics/branches?expectedRevision=0&expectedRevision=0")]
    [InlineData("/lyrics/branches?expectedRevision=-1")]
    [InlineData("/lyrics/branches?expectedRevision=9223372036854775808")]
    [InlineData("/lyrics/branches?expectedRevision=0&limit=0")]
    [InlineData("/lyrics/branches?expectedRevision=0&limit=101")]
    [InlineData("/lyrics/branches?expectedRevision=0&offset=2")]
    [InlineData("/lyrics/branches?expectedRevision=0&offset=-1")]
    [InlineData("/lyrics/branches?expectedRevision=0&limit=1&limit=1")]
    [InlineData("/lyrics/branches?expectedRevision=0&offset=2147483648")]
    [InlineData("/lyrics/window?expectedRevision=0")]
    [InlineData("/lyrics/window?expectedRevision=0&branchId=primary&branchId=primary")]
    [InlineData("/settings")]
    [InlineData("/settings?expectedRevision=0&expectedRevision=0")]
    public async Task 真实HTTP新读模型拒绝缺失重复和非法查询(string suffix)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage response = await f.Http.GetAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}" + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Code);
    }

    [Fact]
    public async Task 真实HTTP旧歌词窗口修订在修改和撤销后均冲突()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var original = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        RequestMetadataDto metadata = f.Metadata();
        using HttpResponseMessage command = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest("0",
            new("lyrics.setTimeRange", new("primary", original.Lines[0].Id), "1", "1000000"), metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        foreach (string suffix in new[] { "/lyrics/window?branchId=primary", "/lyrics/branches?offset=0", "/settings?" })
        {
            string separator = suffix.EndsWith('?') ? "" : "&";
            using HttpResponseMessage stale = await f.Http.GetAsync(path + suffix + separator + "expectedRevision=0");
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(BackendErrorCode.RevisionConflict, (await stale.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Code);
        }
        metadata = f.Metadata();
        using HttpResponseMessage undo = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest("1", new("history.undo"),
            metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        using HttpResponseMessage staleAgain = await f.Http.GetAsync(path + "/lyrics/window?branchId=primary&expectedRevision=0");
        Assert.Equal(HttpStatusCode.Conflict, staleAgain.StatusCode);
        var restored = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=2", Json))!;
        Assert.Equal(original.Lines[0].Time, restored.Lines[0].Time);
    }

    [Fact]
    public async Task 真实HTTP会话隔离认证和缺失分支有稳定错误()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        using HttpResponseMessage branch = await f.Http.GetAsync(path + "/lyrics/window?branchId=missing&expectedRevision=0");
        Assert.Equal(HttpStatusCode.NotFound, branch.StatusCode);
        Assert.Equal(BackendErrorCode.CommandRejected, (await branch.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Code);
        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage request = new(HttpMethod.Get, path + "/settings?expectedRevision=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        request.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage unauthorizedSession = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, unauthorizedSession.StatusCode);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await unauthorizedSession.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Code);
        f.Http.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage unauthorized = await f.Http.GetAsync(path + "/settings?expectedRevision=0");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task 真实HTTP超大单行明确413并保持工程修订与历史()
    {
        await using Fixture f = await Fixture.Create();
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        Project project = workspace.Project;
        typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(workspace, project with
        {
            Lyrics = project.Lyrics.ReplaceBranch(project.Lyrics.Branches[0] with { Lines = [LyricLine.Create("big", new string('汉', 180000))] }),
        });
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        using HttpResponseMessage response = await f.Http.GetAsync(path + "/lyrics/window?branchId=primary&expectedRevision=0&limit=1");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Code);
        var summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(path, Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo);
    }
}

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP编排读模型返回结构树并拒绝旧修订()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/orchestration";
        using HttpResponseMessage response = await f.Http.GetAsync(path + "?expectedRevision=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = (await response.Content.ReadFromJsonAsync<ProjectOrchestrationResponse>(Json))!;
        Assert.NotEmpty(model.Nodes); Assert.NotEmpty(model.Programs);
        using HttpResponseMessage stale = await f.Http.GetAsync(path + "?expectedRevision=1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }
}

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP编排默认容器命令返回结果并支持无变化()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var metadata = f.Metadata();
        string container = "branch/primary/display/regular";
        using HttpResponseMessage command = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest("0",
            new SetOrchestrationDefaultContainerCommandDto("primary", container), metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        var result = (await command.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.Empty(result.ChangeSet.Areas);
        Assert.False(result.OrchestrationResult!.Changed);
    }

    [Fact]
    public async Task 真实HTTP整来源快速布局预演与应用更新编排投影()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        BasicLayoutCommandDto input = new("primary", "SingleCentered", null, "整来源单行", ["单槽"], "已选", "保留", true);
        using HttpResponseMessage preview = await f.Http.PostAsJsonAsync(path + "/commands",
            new ProjectCommandRequest("0", input, f.Metadata().RequestId, f.Metadata().HostInstanceId, f.Metadata().ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        ProjectCommandResponse candidate = (await preview.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.False(candidate.Changed); Assert.Equal("source", candidate.BasicLayoutResult!.Scope);
        input = input with { Preview = false, Type = "orchestration.applyBasicLayout" };
        RequestMetadataDto metadata = f.Metadata();
        using HttpResponseMessage apply = await f.Http.PostAsJsonAsync(path + "/commands",
            new ProjectCommandRequest("0", input, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        ProjectCommandResponse changed = (await apply.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(changed.Changed); Assert.Equal(["orchestration", "renderInputs"], changed.ChangeSet.Areas);
        ProjectOrchestrationResponse model = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=1", Json))!;
        Assert.Equal("SingleCentered", changed.BasicLayoutResult!.RecognizedPreset);
        var program = Assert.Single(model.Programs, value => value.BranchId == "primary");
        Assert.Equal("SingleCentered", program.RecognizedLayoutPreset);
        Assert.Contains(model.Nodes, item => item.Id == program.DefaultContainerId);

        BasicLayoutCommandDto mismatch = new("primary", "SingleCentered", null, "错误槽位", ["一", "二"], "已选", "保留", false);
        metadata = f.Metadata();
        using HttpResponseMessage rejected = await f.Http.PostAsJsonAsync(path + "/commands",
            new ProjectCommandRequest("1", mismatch, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        BackendErrorResponse mismatchError = (await rejected.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(BackendErrorCode.InvalidRequest, mismatchError.Code);
        Assert.Equal("InvalidRange", mismatchError.FailureCode);

        ProjectLyricsWindowResponse currentLyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            path + "/lyrics/window?branchId=primary&expectedRevision=1&limit=1", Json))!;
        string selectedLineId = Assert.Single(currentLyrics.Lines).Id;
        BasicLayoutCommandDto range = new("primary", "SingleCentered", [selectedLineId], "范围", ["槽"], "已选", "保留", true);
        metadata = f.Metadata();
        using HttpResponseMessage rangePreview = await f.Http.PostAsJsonAsync(path + "/commands",
            new ProjectCommandRequest("1", range, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, rangePreview.StatusCode);
        ProjectCommandResponse rangeResult = (await rangePreview.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.False(rangeResult.Changed); Assert.Equal("range", rangeResult.BasicLayoutResult!.Scope);
        Assert.NotNull(rangeResult.BasicLayoutResult.ClipIds);
        ProjectOrchestrationResponse rangeModel = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=1", Json))!;
        Assert.NotNull(Assert.Single(rangeModel.Programs, value => value.BranchId == "primary").Clips);

        BasicLayoutCommandDto rangeApply = range with { Preset = "KaraokeDouble", SlotNames = ["左", "右"], Preview = false, Type = "orchestration.applyBasicLayout" };
        metadata = f.Metadata();
        using HttpResponseMessage appliedRange = await f.Http.PostAsJsonAsync(path + "/commands",
            new ProjectCommandRequest("1", rangeApply, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, appliedRange.StatusCode);
        ProjectCommandResponse appliedRangeResult = (await appliedRange.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(appliedRangeResult.Changed); Assert.NotEmpty(appliedRangeResult.BasicLayoutResult!.ClipIds!);
        ProjectOrchestrationResponse appliedModel = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=2", Json))!;
        Assert.Contains(appliedModel.Programs.Single(value => value.BranchId == "primary").Clips!, clip => appliedRangeResult.BasicLayoutResult.ClipIds!.Contains(clip.Id));
    }
}
