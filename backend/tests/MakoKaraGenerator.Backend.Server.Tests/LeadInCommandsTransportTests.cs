using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP纯新建工程提示容器前奏编辑投影撤销重做与引用保护()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        string revision = "0";
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            var metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(revision, command,
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            revision = result.ProjectRevision;
            return result;
        }
        async Task<BackendErrorResponse> Reject(ProjectCommandDto command, string? expected = null)
        {
            var metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(expected ?? revision, command,
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.False(response.IsSuccessStatusCode);
            return (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        }
        async Task<ProjectOrchestrationResponse> Model() => (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=" + revision, Json))!;
        var initial = await Model();
        var program = Assert.Single(initial.Programs, p => p.BranchId == "primary");
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        var line = Assert.Single(lyrics.Lines);
        await Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line.Id), "2000000", "3000000"));
        // Clear imported element ranges so Core uses the explicit line anchor.
        foreach (var token in line.Tokens) await Send(new ClearTokenTimeCommandDto(new("primary", line.Id, token.Id)));
        var countdown = await Send(new CreateOrchestrationCountdownCommandDto(program.RootContainerId, "起唱提示", 3));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        Assert.Equal(3, countdown.LeadInResult.MarkNodeIds!.Length);
        var created = await Send(new CreateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, indicator, "2000000", true));
        Assert.True(created.Changed); Assert.Contains("orchestration", created.ChangeSet.Areas);
        var model = await Model();
        var cue = Assert.Single(model.Programs.Single(p => p.BranchId == "primary").LeadIns!);
        Assert.Equal("0", cue.StartUs); Assert.Equal("2000000", cue.EndUs); Assert.Equal("Manual", cue.Origin);
        Assert.Equal(countdown.LeadInResult.MarkNodeIds, model.Nodes.Single(n => n.Id == indicator).Content!.MarkNodeIds);
        var retryMetadata = f.Metadata();
        var canonicalUpdate = new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, indicator, "02000000", true);
        var retryRequest = new ProjectCommandRequest(revision, canonicalUpdate, retryMetadata.RequestId, retryMetadata.HostInstanceId, retryMetadata.ValidUntilUtc);
        using var firstRetry = await f.Http.PostAsJsonAsync(path + "/commands", retryRequest, Json);
        using var equivalentRetry = await f.Http.PostAsJsonAsync(path + "/commands", retryRequest with { Command = canonicalUpdate with { DurationUs = "+2000000" } }, Json);
        Assert.Equal(HttpStatusCode.OK, firstRetry.StatusCode); Assert.Equal(HttpStatusCode.OK, equivalentRetry.StatusCode);
        Assert.Equal(revision, (await equivalentRetry.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.ProjectRevision);

        Assert.Equal("IdentityConflict", (await Reject(new CreateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, indicator, "2000000", true))).FailureCode);
        Assert.Equal("PresentationInvalid", (await Reject(new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, indicator, "2", true))).FailureCode);
        Assert.Equal("TargetStale", (await Reject(new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", null, "missing-line", indicator, "2000000", true))).FailureCode);
        Assert.Equal(BackendErrorCode.CommandRejected, (await Reject(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line.Id), "1000000", "3000000"))).Code);
        await Send(new RemoveLineCommandDto(new("primary", line.Id)));
        Assert.Empty((await Model()).Programs.Single(p => p.BranchId == "primary").LeadIns!);
        await Send(new ProjectCommandDto("history.undo"));
        Assert.Single((await Model()).Programs.Single(p => p.BranchId == "primary").LeadIns!);
        Assert.Equal("ContainerInvalid", (await Reject(new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, program.RootContainerId, "2000000", true))).FailureCode);
        Assert.Equal("LeadInTimeConflict", (await Reject(new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, indicator, "2000001", true))).FailureCode);
        Assert.Equal("PresentationInvalid", (await Reject(new RemoveOrchestrationNodeCommandDto(indicator))).FailureCode);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await Reject(new DeleteOrchestrationLeadInCommandDto("primary", "cue/http"), "0")).Code);
        await Send(new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", "改名", line.Id, indicator, "2000000", false));
        await Send(new ProjectCommandDto("history.undo"));
        var noop = await Send(new UpdateOrchestrationLeadInCommandDto("primary", "cue/http", null, line.Id, indicator, "2000000", true));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        await Send(new ProjectCommandDto("history.redo"));
        Assert.False(Assert.Single((await Model()).Programs.Single(p => p.BranchId == "primary").LeadIns!).Enabled);
        await Send(new DeleteOrchestrationLeadInCommandDto("primary", "cue/http"));
        Assert.Empty((await Model()).Programs.Single(p => p.BranchId == "primary").LeadIns!);
        await Send(new ProjectCommandDto("history.undo"));
        Assert.Single((await Model()).Programs.Single(p => p.BranchId == "primary").LeadIns!);
        await Send(new ProjectCommandDto("history.redo"));
        await Send(new RemoveOrchestrationNodeCommandDto(indicator));
        Assert.DoesNotContain((await Model()).Nodes, n => n.Id == indicator);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.createCountdown\",\"parentId\":\"x\",\"name\":null,\"count\":65}")]
    [InlineData("{\"type\":\"orchestration.createCountdown\",\"parentId\":\"x\",\"name\":null,\"count\":{}}")]
    [InlineData("{\"type\":\"orchestration.createCountdown\",\"parentId\":\"x\",\"name\":null,\"count\":true}")]
    [InlineData("{\"type\":\"orchestration.createCountdown\",\"parentId\":\"x\",\"name\":null,\"count\":null}")]
    [InlineData("{\"type\":\"orchestration.createCountdown\",\"parentId\":\"x\",\"name\":null,\"count\":\"3\"}")]
    [InlineData("{\"type\":\"orchestration.createCountdown\",\"parentId\":\"x\",\"name\":null,\"count\":3,\"extra\":true}")]
    [InlineData("{\"type\":\"orchestration.deleteLeadIn\",\"branchId\":\"primary\",\"leadInId\":\"x\",\"leadInId\":\"x\"}")]
    [InlineData("{\"type\":\"orchestration.updateLeadIn\",\"branchId\":\"primary\",\"leadInId\":\"x\"}")]
    public async Task 真实HTTP前奏严格形状拒绝且修订不变(string command)
    {
        await using Fixture f = await Fixture.Create();
        var metadata = f.Metadata();
        string body = "{\"expectedRevision\":\"0\",\"command\":" + command + ",\"requestId\":" + JsonSerializer.Serialize(metadata.RequestId)
            + ",\"hostInstanceId\":\"host\",\"validUntilUtc\":" + JsonSerializer.Serialize(metadata.ValidUntilUtc) + "}";
        using var response = await f.Http.PostAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }
}
