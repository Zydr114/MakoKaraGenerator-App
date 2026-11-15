using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP自动前奏预览应用保护标记外观与撤销重做()
    {
        await using Fixture f = await Fixture.Create();
        // This test needs a 5s opening; the default audio fixture is only 3s.
        var project = await f.OpenNoAudioProject();
        string path = $"/api/v1/project-sessions/{project.ProjectSessionId}", revision = "0";
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            var metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(revision, command,
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            revision = result.ProjectRevision; return result;
        }
        async Task<ProjectOrchestrationResponse> Model() => (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=" + revision, Json))!;
        var initial = await Model(); var root = initial.Programs.Single(p => p.BranchId == "primary").RootContainerId;
        await Send(new SetOutputDurationCommandDto("6000000"));
        await Send(new AddLineCommandDto("primary", null));
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=" + revision, Json))!;
        var line = Assert.Single(lyrics.Lines);
        await Send(new SetLineTextCommandDto(new("primary", line.Id), "Lead-in"));
        await Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line.Id), "5000000", "6000000"));
        foreach (var token in line.Tokens) await Send(new ClearTokenTimeCommandDto(new("primary", line.Id, token.Id)));
        var countdown = await Send(new CreateOrchestrationCountdownCommandDto(root, "三点", 3));
        string group = countdown.LeadInResult!.IndicatorContainerId!; string mark = countdown.LeadInResult.MarkNodeIds![0];
        var settings = new LeadInGenerationSettingsDto(true, true, "8000000", "3000000", group, []);
        string before = revision;
        var preview = await Send(new PreviewLeadInGenerationCommandDto("primary", settings));
        Assert.False(preview.Changed); Assert.Equal(before, revision);
        var candidate = Assert.Single(preview.LeadInGenerationResult!.Candidates);
        Assert.Equal("New", candidate.Status); Assert.Equal("2000000", candidate.StartUs); Assert.Equal("5000000", candidate.EndUs);
        var applied = await Send(new ApplyLeadInGenerationCommandDto("primary", settings, [line.Id]));
        Assert.True(applied.Changed); string cue = Assert.Single((await Model()).Programs.Single(p => p.BranchId == "primary").LeadIns!).Id;
        Assert.False((await Send(new ApplyLeadInGenerationCommandDto("primary", settings, [line.Id]))).Changed);
        await Send(new SetOrchestrationMarkTextCommandDto(mark, "起"));
        var model = await Model(); var appearance = model.Nodes.Single(n => n.Id == group).Content!.Appearance!;
        await Send(new SetCountdownAppearanceCommandDto(group, appearance with { Opacity = .6f }));
        await Send(new SetMarkAppearanceCommandDto(mark, [new StyleOpacityChangeDto(.4f), new StyleUnsungStrokesChangeDto([])]));
        model = await Model();
        Assert.Equal(.6f, model.Nodes.Single(n => n.Id == group).Content!.Appearance!.Opacity);
        Assert.Equal(2, model.Nodes.Single(n => n.Id == mark).Content!.AppearanceOverride!.Length);
        await Send(new DeleteLeadInAndExcludeCommandDto("primary", cue));
        model = await Model(); Assert.Empty(model.Programs.Single(p => p.BranchId == "primary").LeadIns!);
        Assert.Contains(line.Id, model.Programs.Single(p => p.BranchId == "primary").LeadInGeneration!.ExcludedLineIds);
        await Send(new ProjectCommandDto("history.undo"));
        var noop = await Send(new SetMarkAppearanceCommandDto(mark, [new StyleOpacityChangeDto(.4f), new StyleUnsungStrokesChangeDto([])]));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        await Send(new ProjectCommandDto("history.redo"));
        await Send(new SetLeadInGenerationCommandDto("primary", null));
        Assert.Null((await Model()).Programs.Single(p => p.BranchId == "primary").LeadInGeneration);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.setLeadInGeneration\",\"branchId\":\"primary\"}")]
    [InlineData("{\"type\":\"orchestration.previewLeadInGeneration\",\"branchId\":\"primary\",\"settings\":null}")]
    [InlineData("{\"type\":\"orchestration.setLeadInGeneration\",\"branchId\":\"primary\",\"settings\":{\"includeOpening\":true}}")]
    [InlineData("{\"type\":\"orchestration.applyLeadInGeneration\",\"branchId\":\"primary\",\"settings\":null,\"selectedLineIds\":[]}")]
    [InlineData("{\"type\":\"orchestration.addMark\",\"containerId\":\"x\",\"text\":\"x\",\"text\":\"y\"}")]
    [InlineData("{\"type\":\"orchestration.reorderMarks\",\"containerId\":\"x\",\"orderedIds\":null}")]
    [InlineData("{\"type\":\"orchestration.spaceMarks\",\"containerId\":\"x\",\"mode\":\"auto\"}")]
    [InlineData("{\"type\":\"orchestration.setCountdownAppearance\",\"containerId\":\"x\",\"appearance\":null}")]
    [InlineData("{\"type\":\"orchestration.setCountdownPresentation\",\"containerId\":\"x\",\"shape\":null,\"animation\":null}")]
    [InlineData("{\"type\":\"orchestration.setCountdownPresentation\",\"containerId\":\"x\",\"animation\":null}")]
    [InlineData("{\"type\":\"orchestration.setCountdownPresentation\",\"containerId\":\"x\",\"shape\":{\"kind\":\"Circle\",\"barMinHeightRatio\":0.45,\"svgColorMode\":\"SilhouetteStyle\"},\"animation\":null}")]
    [InlineData("{\"type\":\"orchestration.setCountdownPresentation\",\"containerId\":\"x\",\"shape\":{\"kind\":\"Circle\",\"barMinHeightRatio\":0.45,\"svgXml\":null,\"svgColorMode\":\"SilhouetteStyle\"},\"animation\":null,\"extra\":1}")]
    [InlineData("{\"type\":\"orchestration.setCountdownPresentation\",\"containerId\":\"x\",\"shape\":{\"kind\":\"CustomSvg\",\"barMinHeightRatio\":0.45,\"svgXml\":\"<svg><script>alert(1)</script></svg>\",\"svgColorMode\":\"SilhouetteStyle\"},\"animation\":null}")]
    [InlineData("{\"type\":\"orchestration.setCountdownPresentation\",\"containerId\":\"x\",\"shape\":{\"kind\":\"CustomSvg\",\"barMinHeightRatio\":0.45,\"svgXml\":\"<svg><rect><animate attributeName=\\\"x\\\"/></rect></svg>\",\"svgColorMode\":\"SilhouetteStyle\"},\"animation\":null}")]
    [InlineData("{\"type\":\"orchestration.setMarkAppearance\",\"nodeId\":\"x\",\"changes\":[{\"property\":\"opacity\",\"value\":0.5,\"extra\":true}]}")]
    public async Task 真实HTTP前奏制作严格形状拒绝不产生历史(string command)
    {
        await using Fixture f = await Fixture.Create(); var metadata = f.Metadata();
        string body = "{\"expectedRevision\":\"0\",\"command\":" + command + ",\"requestId\":" + JsonSerializer.Serialize(metadata.RequestId)
            + ",\"hostInstanceId\":\"host\",\"validUntilUtc\":" + JsonSerializer.Serialize(metadata.ValidUntilUtc) + "}";
        using var response = await f.Http.PostAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task 真实HTTP能力声明与前奏图形SVG动画往返及413拒绝()
    {
        await using Fixture f = await Fixture.Create();
        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        Assert.Contains("project-sessions.commands.orchestration.setCountdownPresentation", capabilities.Features);

        ProjectSession project = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, project.ProjectSessionId);
        string root = (await http.Read()).Programs.Single(p => p.BranchId == "primary").RootContainerId;
        var created = await http.Send(new CreateOrchestrationCountdownCommandDto(root, "图形组", 3));
        string group = created.LeadInResult!.IndicatorContainerId!;

        CountdownShapeSpecDto stepped = new("SteppedBars", 0.35, null, "SilhouetteStyle");
        CountdownAnimationSpecDto wipe = new("BlinkThenWipeFill", 0.35, 2, 0.04);
        await http.Send(new SetCountdownPresentationCommandDto(group, stepped, wipe));
        var readBuiltIn = (await http.Read()).Nodes.Single(n => n.Id == group).Content!;
        Assert.Equal(stepped, readBuiltIn.Shape);
        Assert.Equal(wipe, readBuiltIn.Animation);

        string inlineSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"10\" fill=\"currentColor\"/></svg>";
        CountdownShapeSpecDto customSvg = new("CustomSvg", 0.45, inlineSvg, "SilhouetteStyle");
        await http.Send(new SetCountdownPresentationCommandDto(group, customSvg, null));
        var readSvg = (await http.Read()).Nodes.Single(n => n.Id == group).Content!;
        Assert.Equal(customSvg, readSvg.Shape);
        Assert.Equal(wipe, readSvg.Animation);

        string tooLargeSvg = "<svg viewBox=\"0 0 24 24\"><path d=\"M0 0h" + new string('1', 64 * 1024) + "\"/></svg>";
        using HttpResponseMessage oversized = await http.Post(
            new SetCountdownPresentationCommandDto(group, new("CustomSvg", 0.45, tooLargeSvg, "SilhouetteStyle"), null),
            http.Revision, f.Metadata());
        await Error(oversized, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        Assert.Equal(http.Revision, (await f.Projects.ReadSummaryAsync(f.Client.ClientId, project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task 真实Worker同帧测量HTTP切换前奏首字上方避让注音左侧与固定附着()
    {
        await using Fixture f = await Fixture.Create(
            injectedFactory: new WorkerRenderFrameSessionFactory(new(FindWorkerApphost())));
        f.Projects.ConfigureSceneMeasurements(new(FindWorkerApphost()));
        ProjectSession project = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, project.ProjectSessionId);
        await http.Send(new SetOutputDurationCommandDto("8000000"));
        await http.Send(new AddLineCommandDto("primary", null));
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;
        var line = Assert.Single(lyrics.Lines);
        await http.Send(new SetLineTextCommandDto(new("primary", line.Id), "踊れ"));
        lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;
        line = Assert.Single(lyrics.Lines);
        await http.Send(new SetRubyTextCommandDto(new("primary", line.Id, line.Tokens[0].Id), "おど"));
        await http.Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line.Id), "4000000", "6000000"));
        foreach (var token in line.Tokens) await http.Send(new ClearTokenTimeCommandDto(new("primary", line.Id, token.Id)));

        string root = (await http.Read()).Programs.Single(p => p.BranchId == "primary").RootContainerId;
        var countdown = await http.Send(new CreateOrchestrationCountdownCommandDto(root, "注音避让提示", 3));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        var leadIn = await http.Send(new CreateOrchestrationLeadInCommandDto("primary", "cue/above-ruby", null, line.Id, indicator, "2000000", true));
        string cueId = leadIn.LeadInResult!.LeadInId!;

        await http.Send(new SetLeadInAttachmentCommandDto("primary", cueId, indicator, "CueTargetAboveStart"));
        var aboveNode = (await http.Read()).Nodes.Single(n => n.Id == indicator);
        Assert.Equal("CueTargetAboveStart", aboveNode.Content!.AttachmentMode);

        await http.Send(new SetLeadInAttachmentCommandDto("primary", cueId, indicator, "CueTargetText"));
        var leftNode = (await http.Read()).Nodes.Single(n => n.Id == indicator);
        Assert.Equal("CueTargetText", leftNode.Content!.AttachmentMode);
        Assert.True(aboveNode.Placement.Y > leftNode.Placement.Y);

        await http.Send(new SetLeadInAttachmentCommandDto("primary", cueId, indicator, "FixedParent"));
        var fixedNode = (await http.Read()).Nodes.Single(n => n.Id == indicator);
        Assert.Equal("FixedParent", fixedNode.Content!.AttachmentMode);
    }
}
