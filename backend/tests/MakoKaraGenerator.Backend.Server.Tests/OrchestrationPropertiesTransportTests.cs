using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP基础编排属性完整读回撤销无变化重做保存重开与拒绝()
    {
        await using Fixture f = await Fixture.Create();
        var session = await f.OpenNoAudioProject();
        string path = $"/api/v1/project-sessions/{session.ProjectSessionId}";
        string revision = "0";
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            var metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(revision, command,
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            revision = result.ProjectRevision; return result;
        }
        async Task<ProjectOrchestrationResponse> Model() => (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=" + revision, Json))!;
        async Task<BackendErrorResponse> Reject(ProjectCommandDto command, HttpStatusCode status, string? expected = null)
        {
            var metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(expected ?? revision, command,
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.Equal(status, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        }
        async Task<JobResponse> Terminal(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            while (true)
            {
                var current = (await f.Http.GetFromJsonAsync<JobResponse>("/api/v1/jobs/" + accepted.JobId, Json, timeout.Token))!;
                if (current.State is "succeeded" or "failed" or "cancelled")
                { Assert.True(current.State == "succeeded", current.Error?.ToString()); return current; }
                await Task.Delay(10, timeout.Token);
            }
        }
        var initial = await Model(); var program = initial.Programs.Single(p => p.BranchId == "primary");
        var placement = new ProjectOrchestrationPlacementDto(.3, .7, "BottomRight", -12, .8);
        var rect = new ProjectOrchestrationReferenceRectDto(.75, .65);
        var geometry = await Send(new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, placement, rect));
        Assert.True(geometry.Changed); Assert.Equal("setNodeGeometry", geometry.PropertiesResult!.Operation);
        Assert.Equal(placement, (await Model()).Nodes.Single(n => n.Id == program.RootContainerId).Placement);
        await Send(new SetOrchestrationLayoutSettingsCommandDto(program.DefaultContainerId, new(.1, .2, .9, .85), .75));
        Assert.Equal(.75, (await Model()).Nodes.Single(n => n.Id == program.DefaultContainerId).Layout!.MaxWidthRatio);
        await Send(new SetOrchestrationProgramAnimationPolicyCommandDto("primary", "Static"));
        Assert.Equal("Static", (await Model()).Programs.Single(p => p.BranchId == "primary").AnimationPolicy);
        await Send(new SetOrchestrationProgramAnimationPolicyCommandDto("primary", null));
        await Send(new BasicLayoutCommandDto("primary", "KaraokeDouble", null, "HTTP two", ["left", "right"], "selected", "retained", false));
        var layoutModel = await Model(); string container = layoutModel.Programs.Single(p => p.BranchId == "primary").DefaultContainerId;
        var original = layoutModel.Nodes.Single(n => n.Id == container).Layout!;
        var channel = new ProjectOrchestrationSceneTransformDto(.5, 1.2, 12, -6, 18);
        var animation = new ProjectOrchestrationSlotAnimationDto("TimeAfterPreviousStart", .4, "10000", "20000", 2,
            "ReplacePrevious", "Immediate", "150000", "220000", channel, channel with { Opacity = 1 }, channel with { Opacity = 0 }, "EaseInOut", "Linear");
        var changed = await Send(new SetOrchestrationSlotAnimationCommandDto(container, animation));
        Assert.True(changed.Changed);
        Assert.Equal(animation, (await Model()).Nodes.Single(n => n.Id == container).Layout!.SlotAnimation);
        await Send(new ProjectCommandDto("history.undo"));
        var noop = await Send(new SetOrchestrationSlotAnimationCommandDto(container, original.SlotAnimation));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        await Send(new ProjectCommandDto("history.redo"));
        var final = await Model();
        Assert.Equal(animation, final.Nodes.Single(n => n.Id == container).Layout!.SlotAnimation);
        Assert.Equal(original.SlotIds, final.Nodes.Single(n => n.Id == container).Layout!.SlotIds);
        Assert.Equal("PresentationInvalid", (await Reject(new SetOrchestrationSlotAnimationCommandDto(container, null), HttpStatusCode.UnprocessableEntity)).FailureCode);
        Assert.Equal("PresentationInvalid", (await Reject(new SetOrchestrationProgramAnimationPolicyCommandDto("primary", "Static"), HttpStatusCode.UnprocessableEntity)).FailureCode);
        Assert.Equal("TargetStale", (await Reject(new SetOrchestrationNodeGeometryCommandDto("absent", placement, rect), HttpStatusCode.UnprocessableEntity)).FailureCode);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await Reject(new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, placement, rect), HttpStatusCode.Conflict, "0")).Code);
        using (var stale = await f.Http.GetAsync(path + "/orchestration?expectedRevision=0"))
            await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using var handleResponse = await f.Http.PostAsJsonAsync("/api/v1/file-handles", new FileHandleRequest("preview-test-root", "no-audio", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
        var handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
        using (var saved = await f.Http.PostAsJsonAsync(path + "/save-jobs", new SaveProjectRequest(revision, handle.HandleId, f.Metadata()), Json))
            await Terminal(saved);
        using (var closed = await f.Http.PostAsJsonAsync(path + "/close", new CloseProjectRequest(revision, ActiveJobs: "wait", Metadata: f.Metadata()), Json))
            await Terminal(closed);
        using var opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        var reopened = await Terminal(opened);
        var persisted = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>($"/api/v1/project-sessions/{reopened.ProjectSessionId}/orchestration?expectedRevision=0", Json))!;
        Assert.Equal(JsonSerializer.Serialize(final.Nodes, Json), JsonSerializer.Serialize(persisted.Nodes, Json));
        Assert.Equal(JsonSerializer.Serialize(final.Programs, Json), JsonSerializer.Serialize(persisted.Programs, Json));
        var capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string command in new[] { "setNodeGeometry", "setLayoutSettings", "setSlotAnimation", "setProgramAnimationPolicy" })
            Assert.Contains("project-sessions.commands.orchestration." + command, capabilities.Features);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.setNodeGeometry\",\"nodeId\":\"x\",\"placement\":{\"x\":0,\"y\":0,\"anchor\":\"Center\",\"rotationDegrees\":0,\"scale\":1e400},\"referenceRect\":{\"widthRatio\":1,\"heightRatio\":1}}")]
    [InlineData("{\"type\":\"orchestration.setLayoutSettings\",\"containerId\":\"x\",\"safeArea\":{\"left\":0,\"top\":0,\"right\":1,\"bottom\":1,\"right\":1},\"maxWidthRatio\":0.9}")]
    [InlineData("{\"type\":\"orchestration.setProgramAnimationPolicy\",\"branchId\":\"primary\",\"animationPolicy\":\"1\"}")]
    [InlineData("{\"type\":\"orchestration.setProgramAnimationPolicy\",\"branchId\":\"primary\"}")]
    [InlineData("{\"type\":\"orchestration.setSlotAnimation\",\"containerId\":\"x\",\"slotAnimation\":{}}")]
    [InlineData("{\"type\":\"orchestration.setNodeGeometry\",\"nodeId\":\"x\",\"placement\":null,\"referenceRect\":{\"widthRatio\":1,\"heightRatio\":1}}")]
    public async Task 真实HTTP基础编排属性严格形状拒绝且历史修订不变(string raw)
    {
        await using Fixture f = await Fixture.Create(); var metadata = f.Metadata();
        string body = "{\"expectedRevision\":\"0\",\"command\":" + raw + ",\"requestId\":" + JsonSerializer.Serialize(metadata.RequestId)
            + ",\"hostInstanceId\":\"host\",\"validUntilUtc\":" + JsonSerializer.Serialize(metadata.ValidUntilUtc) + "}";
        using var response = await f.Http.PostAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands", new StringContent(body, Encoding.UTF8, "application/json"));
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }
}
