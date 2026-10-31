using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G21C与G24真实HTTP布局切换进入动画继承清除撤销并保存重开()
    {
        await using Fixture f = await Fixture.Create();
        var session = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, session.ProjectSessionId);
        ProjectCommandResponse added = await http.Send(new AddLineCommandDto("primary", null));
        ProjectLyricsWindowResponse lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;
        string lineId = lyrics.Lines.Last().Id;
        Assert.True(added.Changed);
        var capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string operation in new[] { "setLayoutKind", "setLineEnterAnimation", "setProjectEnterAnimation" })
            Assert.Contains("project-sessions.commands.orchestration." + operation, capabilities.Features);
        var model = await http.Read();
        string container = model.Programs.Single(p => p.BranchId == "primary").DefaultContainerId;
        await http.Send(new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [], 3));
        var sequence = (await http.Read()).Nodes.Single(n => n.Id == container).Layout!;
        Assert.Equal(3, sequence.SlotIds.Length); Assert.Equal(2, sequence.SlotAnimation!.MaxVisibleLines);
        string[] slots = sequence.SlotIds;
        ProjectOrchestrationPlacementDto placement = new(.25, .75, "BottomRight", -15, .8);
        await http.Send(new SetOrchestrationNodeGeometryCommandDto(slots[0], placement, new(.5, .7)));
        var animationSettings = sequence.SlotAnimation with { RevealLeadUs = "12345", EnterDurationUs = "23456" };
        await http.Send(new SetOrchestrationSlotAnimationCommandDto(container, animationSettings));
        await http.Send(new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", slots.Reverse().ToArray(), 0));
        Assert.Equal(animationSettings, (await http.Read()).Nodes.Single(n => n.Id == container).Layout!.SlotAnimation);
        var beforeAuto = await http.Read();
        await http.Send(new SetOrchestrationLayoutKindCommandDto(container, "Auto", [], 0));
        var automatic = await http.Read();
        Assert.Equal(beforeAuto.Nodes.Length, automatic.Nodes.Length);
        Assert.Empty(automatic.Nodes.Single(n => n.Id == container).Layout!.SlotIds);
        Assert.Null(automatic.Nodes.Single(n => n.Id == container).Layout!.SlotAnimation);
        Assert.All(slots, id => Assert.Contains(automatic.Nodes, node => node.Id == id));
        Assert.Equal(placement, automatic.Nodes.Single(n => n.Id == slots[0]).Placement);
        await http.Send(new ProjectCommandDto("history.undo"));
        Assert.Equal(JsonSerializer.Serialize(beforeAuto.Nodes, Json), JsonSerializer.Serialize((await http.Read()).Nodes, Json));
        var noop = await http.Send(new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", slots.Reverse().ToArray(), 0));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        await http.Send(new ProjectCommandDto("history.redo"));

        ProjectLineEnterAnimationDto projectAnimation = new("400000", "EaseOut", .2f, .8f, 12);
        await http.Send(new SetProjectEnterAnimationCommandDto(projectAnimation));
        Assert.Equal(projectAnimation, (await http.Read()).ProjectEnterAnimation);
        async Task<ProjectLinePresentationResponse> ReadLine() => (await f.Http.GetFromJsonAsync<ProjectLinePresentationResponse>(http.Path
            + "/orchestration/line-presentation?branchId=primary&lineId=" + lineId + "&expectedRevision=" + http.Revision, Json))!;
        var inherited = await ReadLine();
        Assert.Equal("project", inherited.AnimationSource); Assert.Null(inherited.LocalEnterAnimation);
        Assert.Equal(projectAnimation, inherited.EffectiveEnterAnimation);
        ProjectLineLocalTransformDto transform = new(.1, -.2, 20, 1.2);
        await http.Send(new SetLineLocalTransformCommandDto("primary", lineId, transform));
        ProjectLineEnterAnimationDto neutral = new("0", "Linear", 1, 1, 0);
        await http.Send(new SetLineEnterAnimationCommandDto("primary", lineId, neutral));
        var local = await ReadLine();
        Assert.Equal("line", local.AnimationSource); Assert.Equal(neutral, local.LocalEnterAnimation);
        Assert.Equal(neutral, local.EffectiveEnterAnimation); Assert.Equal(transform, local.LocalTransform);
        await http.Send(new SetLineEnterAnimationCommandDto("primary", lineId, null));
        var cleared = await ReadLine();
        Assert.Equal("project", cleared.AnimationSource); Assert.Equal(transform, cleared.LocalTransform);
        await http.Send(new SetProjectEnterAnimationCommandDto(null));
        Assert.Equal("none", (await ReadLine()).AnimationSource); Assert.Null((await http.Read()).ProjectEnterAnimation);
        await http.Send(new ProjectCommandDto("history.undo"));
        var final = await http.Read(); var finalLine = await ReadLine();
        using (HttpResponseMessage stale = await f.Http.GetAsync(http.Path + "/orchestration/line-presentation?branchId=primary&lineId=" + lineId + "&expectedRevision=0"))
            await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using HttpResponseMessage handleResponse = await f.Http.PostAsJsonAsync("/api/v1/file-handles", new FileHandleRequest("preview-test-root", "no-audio", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
        FileHandleResponse handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
        using (HttpResponseMessage saved = await f.Http.PostAsJsonAsync(http.Path + "/save-jobs", new SaveProjectRequest(http.Revision, handle.HandleId, f.Metadata()), Json))
            await NodeTerminal(f, saved);
        using (HttpResponseMessage closed = await f.Http.PostAsJsonAsync(http.Path + "/close", new CloseProjectRequest(http.Revision, ActiveJobs: "wait", Metadata: f.Metadata()), Json))
            await NodeTerminal(f, closed);
        using HttpResponseMessage opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        JobResponse reopened = await NodeTerminal(f, opened);
        var persisted = await new NodeHttpHarness(f, reopened.ProjectSessionId).Read();
        Assert.Equal(JsonSerializer.Serialize(final.Nodes, Json), JsonSerializer.Serialize(persisted.Nodes, Json));
        Assert.Equal(final.ProjectEnterAnimation, persisted.ProjectEnterAnimation);
        var persistedLine = (await f.Http.GetFromJsonAsync<ProjectLinePresentationResponse>($"/api/v1/project-sessions/{reopened.ProjectSessionId}/orchestration/line-presentation?branchId=primary&lineId={lineId}&expectedRevision=0", Json))!;
        Assert.Equal(finalLine.LocalTransform, persistedLine.LocalTransform); Assert.Equal(finalLine.EffectiveEnterAnimation, persistedLine.EffectiveEnterAnimation);
        Assert.Equal(finalLine.AnimationSource, persistedLine.AnimationSource);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.setLayoutKind\",\"containerId\":\"x\",\"kind\":\"Auto\",\"slotIds\":[]}")]
    [InlineData("{\"type\":\"orchestration.setLayoutKind\",\"containerId\":\"x\",\"kind\":\"auto\",\"slotIds\":[],\"newSlotCount\":0}")]
    [InlineData("{\"type\":\"orchestration.setLayoutKind\",\"containerId\":\"x\",\"kind\":\"Auto\",\"slotIds\":[\"s\"],\"newSlotCount\":0}")]
    [InlineData("{\"type\":\"orchestration.setLayoutKind\",\"containerId\":\"x\",\"kind\":\"SlotSequence\",\"slotIds\":[],\"newSlotCount\":0}")]
    [InlineData("{\"type\":\"orchestration.setLayoutKind\",\"containerId\":\"x\",\"kind\":\"SlotSequence\",\"slotIds\":[\"s\",\"s\"],\"newSlotCount\":0}")]
    [InlineData("{\"type\":\"orchestration.setLayoutKind\",\"containerId\":\"x\",\"kind\":\"SlotSequence\",\"slotIds\":[],\"newSlotCount\":65}")]
    [InlineData("{\"type\":\"orchestration.setLineEnterAnimation\",\"branchId\":\"primary\",\"lineId\":\"x\"}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":null,\"extra\":1}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":null,\"animation\":null}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":\"1\",\"easing\":\"Linear\",\"startOpacity\":0,\"startScale\":1}}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":1,\"easing\":\"Linear\",\"startOpacity\":0,\"startScale\":1,\"startOffsetPx\":0}}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":\"10000001\",\"easing\":\"Linear\",\"startOpacity\":0,\"startScale\":1,\"startOffsetPx\":0}}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":\"1\",\"easing\":\"linear\",\"startOpacity\":0,\"startScale\":1,\"startOffsetPx\":0}}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":\"1\",\"easing\":\"Linear\",\"startOpacity\":1.1,\"startScale\":1,\"startOffsetPx\":0}}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":\"1\",\"easing\":\"Linear\",\"startOpacity\":0,\"startScale\":0,\"startOffsetPx\":0}}")]
    [InlineData("{\"type\":\"orchestration.setProjectEnterAnimation\",\"animation\":{\"durationUs\":\"1\",\"easing\":\"Linear\",\"startOpacity\":0,\"startScale\":1e400,\"startOffsetPx\":0}}")]
    public async Task G21C与G24真实HTTP完整严格字段拒绝不改变工程(string raw)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage rejected = await PostRawEditing(f, "0", raw);
        await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        var summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo);
    }

    [Fact]
    public async Task G21C与G24真实HTTP领域拒绝唯一查询和全部G20结构操作可达()
    {
        await using Fixture f = await Fixture.Create();
        NodeHttpHarness http = new(f);
        var initial = await http.Read();
        var program = initial.Programs.Single(p => p.BranchId == "primary");
        var group = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("group", program.RootContainerId, "组", null)), "createNode", 1);
        await http.Reject(new SetOrchestrationLayoutKindCommandDto(group.NodeId, "Auto", [], 0), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "ContainerInvalid");
        await http.Reject(new SetOrchestrationLayoutKindCommandDto("missing", "Auto", [], 0), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale");
        await http.Reject(new SetLineEnterAnimationCommandDto("primary", "missing", null), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale");
        var renamed = await http.Send(new SetOrchestrationNodeCommandDto(group.NodeId, "已改名")); Assert.True(renamed.Changed);
        await http.Send(new ReparentOrchestrationNodeCommandDto(group.NodeId, null));
        var roots = (await http.Read()).Nodes.Where(node => node.ParentId is null).Select(node => node.Id).Reverse().ToArray();
        await http.Send(new ReorderOrchestrationChildrenCommandDto(null, roots));
        await http.Send(new SetOrchestrationLayoutKindCommandDto(program.DefaultContainerId, "SlotSequence", [], 1));
        await http.Send(new AddOrchestrationSlotCommandDto(program.DefaultContainerId, "slot/g21c", "追加槽"));
        var slots = (await http.Read()).Nodes.Single(node => node.Id == program.DefaultContainerId).Layout!.SlotIds.Reverse().ToArray();
        await http.Send(new ReorderOrchestrationSlotsCommandDto(program.DefaultContainerId, slots));
        await http.Send(new RemoveOrchestrationNodeCommandDto("slot/g21c"));
        await http.Send(new RemoveOrchestrationNodeCommandDto(group.NodeId));
        Assert.DoesNotContain((await http.Read()).Nodes, node => node.Id == group.NodeId || node.Id == "slot/g21c");
        foreach (string query in new[] { "expectedRevision=0&branchId=primary", "expectedRevision=0&branchId=primary&lineId=x&extra=1", "expectedRevision=0&branchId=primary&lineId=x&lineId=x" })
        {
            using HttpResponseMessage malformed = await f.Http.GetAsync(http.Path + "/orchestration/line-presentation?" + query);
            await Error(malformed, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
    }
}
