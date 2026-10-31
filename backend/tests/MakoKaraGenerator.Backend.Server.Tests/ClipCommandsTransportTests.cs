using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP片段命令链精确推进修订并投影撤销重做()
    {
        await using Fixture f = await Fixture.Create();
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        Project source = workspace.Project;
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        var program = tree.FindProgram("primary")!;
        string alternateId = "branch/primary/display/alternate";
        workspace.Replace(source with
        {
            LyricPresentation = tree with
            {
                Nodes = tree.Nodes.Add(new SceneContainerDefinition { Id = alternateId, ParentId = program.RootContainerId, Layout = new() }),
            },
        });

        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        ProjectLyricsWindowResponse lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        string lineId = Assert.Single(lyrics.Lines).Id;
        async Task<ProjectCommandResponse> Send(string revision, ProjectCommandDto command)
        {
            RequestMetadataDto metadata = f.Metadata();
            using HttpResponseMessage response = await f.Http.PostAsJsonAsync(path + "/commands",
                new ProjectCommandRequest(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        }

        ProjectCommandResponse created = await Send("0", new CreateOrchestrationClipCommandDto("primary", "clip/http", "副歌", [lineId], LyricSceneIds.Regular("primary")));
        Assert.True(created.Changed); Assert.Equal("0", created.PreviousRevision); Assert.Equal("1", created.ProjectRevision);
        Assert.Equal("primary", created.ClipResult!.BranchId); Assert.Equal("clip/http", created.ClipResult.ClipId);
        Assert.Equal("副歌", created.ClipResult.Name); Assert.Equal([lineId], created.ClipResult.LineIds);
        Assert.Equal(LyricSceneIds.Regular("primary"), created.ClipResult.ContainerId); Assert.True(created.ClipResult.Changed);

        ProjectOrchestrationResponse model = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=1", Json))!;
        Assert.Equal("副歌", Assert.Single(model.Programs.Single(p => p.BranchId == "primary").Clips!).Name);

        ProjectCommandResponse renamed = await Send("1", new RenameOrchestrationClipCommandDto("primary", "clip/http", null));
        Assert.Equal("2", renamed.ProjectRevision); Assert.True(renamed.Changed); Assert.Null(renamed.ClipResult!.Name);

        ProjectCommandResponse moved = await Send("2", new SetOrchestrationClipContainerCommandDto("primary", "clip/http", alternateId));
        Assert.Equal("3", moved.ProjectRevision); Assert.Equal(alternateId, moved.ClipResult!.ContainerId);

        ProjectCommandResponse deleted = await Send("3", new DeleteOrchestrationClipCommandDto("primary", "clip/http"));
        Assert.Equal("4", deleted.ProjectRevision); Assert.True(deleted.Changed); Assert.Equal("clip/http", deleted.ClipResult!.ClipId);
        model = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=4", Json))!;
        Assert.Empty(model.Programs.Single(p => p.BranchId == "primary").Clips!);

        ProjectCommandResponse undone = await Send("4", new("history.undo"));
        Assert.Equal("5", undone.ProjectRevision); Assert.True(undone.Changed);
        model = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=5", Json))!;
        Assert.Equal(alternateId, Assert.Single(model.Programs.Single(p => p.BranchId == "primary").Clips!).ContainerId);

        ProjectCommandResponse redone = await Send("5", new("history.redo"));
        Assert.Equal("6", redone.ProjectRevision); Assert.True(redone.Changed);
        model = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=6", Json))!;
        Assert.Empty(model.Programs.Single(p => p.BranchId == "primary").Clips!);
    }

    [Fact]
    public async Task 真实HTTP片段命令严格JSON与稳定失败编号()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands";
        RequestMetadataDto metadata = f.Metadata();
        string missingName = JsonSerializer.Serialize(new
        {
            expectedRevision = "0",
            command = new { type = "orchestration.renameClip", branchId = "primary", clipId = "missing" },
            requestId = metadata.RequestId, hostInstanceId = metadata.HostInstanceId, validUntilUtc = metadata.ValidUntilUtc,
        }, Json);
        using HttpResponseMessage malformed = await f.Http.PostAsync(path, new StringContent(missingName, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await malformed.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Code);

        metadata = f.Metadata();
        using HttpResponseMessage invalidContainer = await f.Http.PostAsJsonAsync(path,
            new ProjectCommandRequest("0", new SetOrchestrationClipContainerCommandDto("primary", "missing", "branch/primary/display/nope"),
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidContainer.StatusCode);
        BackendErrorResponse error = (await invalidContainer.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(BackendErrorCode.CommandRejected, error.Code); Assert.Equal("TargetStale", error.FailureCode);
    }
}
