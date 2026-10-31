using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP附着失效来源前奏及错误容器稳定拒绝重放且保持历史并释放名额()
    {
        await using Fixture f = await Fixture.Create();
        NodeHttpHarness http = new(f);
        var program = (await http.Read()).Programs.Single(p => p.BranchId == "primary");
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(http.Path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        var line = Assert.Single(lyrics.Lines);
        await http.Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line.Id), "2000000", "3000000"));
        foreach (var token in line.Tokens) await http.Send(new ClearTokenTimeCommandDto(new("primary", line.Id, token.Id)));
        var created = await http.Send(new CreateOrchestrationCountdownCommandDto(program.RootContainerId, "提示", 3));
        string indicator = created.LeadInResult!.IndicatorContainerId!;
        var other = await http.Send(new CreateOrchestrationCountdownCommandDto(program.RootContainerId, "另一提示", 1));
        string unrelated = other.LeadInResult!.IndicatorContainerId!;
        await http.Send(new CreateOrchestrationLeadInCommandDto("primary", "cue/attachment-errors", null, line.Id, indicator, "2000000", true));
        await http.Send(new SetOrchestrationNodeCommandDto(indicator, "待重做名称"));
        await http.Send(new ProjectCommandDto("history.undo"));
        var baseline = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        Assert.True(baseline.CanUndo); Assert.True(baseline.CanRedo);
        var baselineModel = await http.Read();
        string mode = baselineModel.Nodes.Single(node => node.Id == indicator).Content!.AttachmentMode!;
        Assert.Equal("CueTargetText", mode);
        string model = JsonSerializer.Serialize(baselineModel, Json);
        string summary = JsonSerializer.Serialize(baseline, Json);
        SetLeadInAttachmentCommandDto valid = new("primary", "cue/attachment-errors", indicator, mode);

        async Task Unchanged()
        {
            Assert.Equal(summary, JsonSerializer.Serialize(await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json), Json));
            Assert.Equal(model, JsonSerializer.Serialize(await http.Read(), Json));
        }

        async Task Reject(SetLeadInAttachmentCommandDto command, string expected, HttpStatusCode status,
            BackendErrorCode code, string? failureCode, string field)
        {
            var metadata = f.Metadata();
            using var rejected = await http.Post(command, expected, metadata);
            Assert.Equal(status, rejected.StatusCode);
            string body = await rejected.Content.ReadAsStringAsync();
            var error = (await rejected.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
            Assert.Equal(code, error.Code); Assert.Equal(failureCode, error.FailureCode); Assert.Equal(field, error.Field);
            using var replay = await http.Post(command, expected, metadata);
            Assert.Equal(status, replay.StatusCode); Assert.Equal(body, await replay.Content.ReadAsStringAsync());
            await Unchanged();
            // A successful attachment no-op after each failure proves the same client's measurement reservation was released.
            var noopMetadata = f.Metadata();
            using var noop = await http.Post(valid, http.Revision, noopMetadata);
            Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
            var result = (await noop.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.False(result.Changed); Assert.Equal(http.Revision, result.ProjectRevision); Assert.True(result.CanRedo);
            using var noopReplay = await http.Post(valid, http.Revision, noopMetadata);
            Assert.Equal(HttpStatusCode.OK, noopReplay.StatusCode);
            Assert.Equal(await noop.Content.ReadAsStringAsync(), await noopReplay.Content.ReadAsStringAsync());
            await Unchanged();
        }

        await Reject(valid with { BranchId = "missing-branch" }, http.Revision,
            HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale", "command.branchId");
        await Reject(valid with { LeadInId = "missing-cue" }, http.Revision,
            HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale", "command.leadInId");
        foreach (string container in new[] { "missing-container", program.DefaultContainerId, unrelated })
            await Reject(valid with { ContainerId = container, Mode = "CueTargetText" }, http.Revision,
                HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "ContainerInvalid", "command.containerId");
        // Revision conflict retains precedence over target validation and also releases its reservation.
        await Reject(valid with { BranchId = "missing-branch" }, "0",
            HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict, null, "expectedRevision");
        await http.Send(new ProjectCommandDto("history.redo"));
        Assert.Equal("待重做名称", (await http.Read()).Nodes.Single(node => node.Id == indicator).Name);
    }
}
