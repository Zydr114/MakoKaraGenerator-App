using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G20B真实HTTP创建三类节点复制闭包单步历史并保存关闭重开()
    {
        await using Fixture f = await Fixture.Create();
        ProjectSession session = await f.OpenNoAudioProject();
        NodeHttpHarness http = new(f, session.ProjectSessionId);
        ProjectOrchestrationResponse initial = await http.Read();
        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string operation in new[] { "createNode", "copySubtree" })
            Assert.Contains("project-sessions.commands.orchestration." + operation, capabilities.Features);

        OrchestrationStructureCommandResultDto root = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("group", null, "HTTP树", null)), "createNode", 1);
        OrchestrationStructureCommandResultDto group = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("group", root.NodeId, null, null)), "createNode", 1);
        OrchestrationStructureCommandResultDto auto = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("auto", group.NodeId, new string('字', 256), null)), "createNode", 1);
        OrchestrationStructureCommandResultDto sequence = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("slotSequence", root.NodeId, "三槽", 3)), "createNode", 4);
        ProjectOrchestrationResponse beforeCopy = await http.Read();
        ProjectOrchestrationNodeDto rootNode = beforeCopy.Nodes.Single(n => n.Id == root.NodeId);
        Assert.Null(rootNode.ParentId); Assert.Null(rootNode.Layout); Assert.Null(rootNode.Content);
        Assert.Equal(new ProjectOrchestrationPlacementDto(.5, .5, "Center", 0, 1), rootNode.Placement);
        Assert.Equal(new ProjectOrchestrationReferenceRectDto(1, 1), rootNode.ReferenceRect);
        ProjectOrchestrationLayoutDto autoLayout = beforeCopy.Nodes.Single(n => n.Id == auto.NodeId).Layout!;
        Assert.Equal("Auto", autoLayout.Kind); Assert.Null(autoLayout.DefaultPlacement); Assert.Null(autoLayout.SlotAnimation);
        Assert.Empty(autoLayout.SlotIds); Assert.Equal(.9, autoLayout.MaxWidthRatio);
        Assert.Equal(new ProjectOrchestrationSafeAreaDto(0, 0, 1, 1), autoLayout.SafeArea);
        ProjectOrchestrationLayoutDto slots = beforeCopy.Nodes.Single(n => n.Id == sequence.NodeId).Layout!;
        Assert.Equal("SlotSequence", slots.Kind); Assert.Equal(sequence.CreatedNodeIds!.Skip(1), slots.SlotIds);
        Assert.Equal(2, slots.SlotAnimation!.MaxVisibleLines);
        Assert.All(slots.SlotIds, id => Assert.Equal(sequence.NodeId, beforeCopy.Nodes.Single(n => n.Id == id).ParentId));

        // Give the source nondefault geometry so the copy is checked beyond identity and node count.
        ProjectOrchestrationPlacementDto placement = new(.2, .7, "BottomRight", 15, 1.25);
        await http.Send(new SetOrchestrationNodeGeometryCommandDto(auto.NodeId, placement, new(.6, .4)));
        beforeCopy = await http.Read();
        OrchestrationStructureCommandResultDto copied = NodeResult(await http.Send(new CopyOrchestrationSubtreeCommandDto(root.NodeId, null)), "copySubtree", 7);
        Assert.Null(copied.ParentId);
        ProjectOrchestrationResponse afterCopy = await http.Read();
        Assert.Equal(JsonSerializer.Serialize(beforeCopy.Programs, Json), JsonSerializer.Serialize(afterCopy.Programs, Json));
        Assert.Equal(JsonSerializer.Serialize(beforeCopy.Nodes, Json), JsonSerializer.Serialize(afterCopy.Nodes.Where(n => !copied.CreatedNodeIds!.Contains(n.Id)).ToArray(), Json));
        Assert.All(copied.CreatedNodeIds!, id => Assert.DoesNotContain(beforeCopy.Nodes, n => n.Id == id));
        Assert.Equal(copied.NodeId, afterCopy.Nodes.Last(n => n.ParentId is null).Id);
        ProjectOrchestrationNodeDto copyRoot = afterCopy.Nodes.Single(n => n.Id == copied.NodeId);
        Assert.Null(copyRoot.Name);
        ProjectOrchestrationNodeDto copyGroup = Assert.Single(afterCopy.Nodes, n => n.ParentId == copyRoot.Id && n.Layout is null);
        ProjectOrchestrationNodeDto copyAuto = Assert.Single(afterCopy.Nodes, n => n.ParentId == copyGroup.Id);
        Assert.Equal(new string('字', 256), copyAuto.Name); Assert.Equal(placement, copyAuto.Placement);
        Assert.Equal(new ProjectOrchestrationReferenceRectDto(.6, .4), copyAuto.ReferenceRect);
        ProjectOrchestrationNodeDto copySequence = Assert.Single(afterCopy.Nodes, n => n.ParentId == copyRoot.Id && n.Layout is not null);
        Assert.Equal("三槽", copySequence.Name);
        Assert.Equal(JsonSerializer.Serialize(slots.SlotAnimation, Json), JsonSerializer.Serialize(copySequence.Layout!.SlotAnimation, Json));
        Assert.All(copySequence.Layout.SlotIds, id =>
        {
            Assert.Contains(id, copied.CreatedNodeIds!);
            Assert.Equal(copySequence.Id, afterCopy.Nodes.Single(n => n.Id == id).ParentId);
            Assert.DoesNotContain(id, slots.SlotIds);
        });
        await http.Send(new ProjectCommandDto("history.undo"));
        Assert.Equal(JsonSerializer.Serialize(beforeCopy.Nodes, Json), JsonSerializer.Serialize((await http.Read()).Nodes, Json));
        await http.Send(new ProjectCommandDto("history.redo"));
        ProjectOrchestrationResponse final = await http.Read();
        Assert.Equal(JsonSerializer.Serialize(afterCopy.Nodes, Json), JsonSerializer.Serialize(final.Nodes, Json));

        using HttpResponseMessage handleResponse = await f.Http.PostAsJsonAsync("/api/v1/file-handles", new FileHandleRequest("preview-test-root", "no-audio", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
        FileHandleResponse handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
        using (HttpResponseMessage saved = await f.Http.PostAsJsonAsync(http.Path + "/save-jobs", new SaveProjectRequest(http.Revision, handle.HandleId, f.Metadata()), Json))
            await NodeTerminal(f, saved);
        using (HttpResponseMessage closed = await f.Http.PostAsJsonAsync(http.Path + "/close", new CloseProjectRequest(http.Revision, ActiveJobs: "wait", Metadata: f.Metadata()), Json))
            await NodeTerminal(f, closed);
        using HttpResponseMessage opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        JobResponse reopened = await NodeTerminal(f, opened);
        Assert.NotNull(reopened.ProjectSessionId);
        ProjectOrchestrationResponse persisted = await new NodeHttpHarness(f, reopened.ProjectSessionId).Read();
        Assert.Equal(JsonSerializer.Serialize(final.Nodes, Json), JsonSerializer.Serialize(persisted.Nodes, Json));
        Assert.Equal(JsonSerializer.Serialize(initial.Programs, Json), JsonSerializer.Serialize(persisted.Programs, Json));
    }

    [Fact]
    public async Task G20B真实HTTP复制槽位标记更新外部序列而完整树复制不复制歌词绑定()
    {
        await using Fixture f = await Fixture.Create();
        NodeHttpHarness http = new(f);
        ProjectOrchestrationResponse initial = await http.Read();
        ProjectOrchestrationProgramDto program = initial.Programs.Single(p => p.BranchId == "primary");
        OrchestrationStructureCommandResultDto sequence = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("slotSequence", program.RootContainerId, "有序槽", 1)), "createNode", 2);
        string firstSlot = sequence.CreatedNodeIds![1];
        OrchestrationStructureCommandResultDto slotCopy = NodeResult(await http.Send(new CopyOrchestrationSubtreeCommandDto(firstSlot, "复制槽")), "copySubtree", 1);
        ProjectOrchestrationResponse afterSlot = await http.Read();
        Assert.Equal(sequence.NodeId, slotCopy.ParentId);
        Assert.Equal(new[] { firstSlot, slotCopy.NodeId! }, afterSlot.Nodes.Single(n => n.Id == sequence.NodeId).Layout!.SlotIds);
        Assert.Equal(1, afterSlot.Nodes.Single(n => n.Id == sequence.NodeId).Layout!.SlotAnimation!.MaxVisibleLines);

        ProjectCommandResponse countdown = await http.Send(new CreateOrchestrationCountdownCommandDto(program.RootContainerId, "倒计时", 2));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        string[] marks = countdown.LeadInResult.MarkNodeIds!;
        OrchestrationStructureCommandResultDto markCopy = NodeResult(await http.Send(new CopyOrchestrationSubtreeCommandDto(marks[0], "复制标记")), "copySubtree", 1);
        Assert.Equal(indicator, markCopy.ParentId);
        Assert.Equal(marks.Append(markCopy.NodeId!), (await http.Read()).Nodes.Single(n => n.Id == indicator).Content!.MarkNodeIds);
        await http.Send(new ProjectCommandDto("history.undo"));
        Assert.Equal(marks, (await http.Read()).Nodes.Single(n => n.Id == indicator).Content!.MarkNodeIds);
        await http.Send(new ProjectCommandDto("history.redo"));

        ProjectLyricsWindowResponse lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;
        var line = Assert.Single(lyrics.Lines);
        await http.Send(new ProjectCommandDto("lyrics.setTimeRange", new("primary", line.Id), "2000000", "3000000"));
        foreach (var token in line.Tokens) await http.Send(new ClearTokenTimeCommandDto(new("primary", line.Id, token.Id)));
        await http.Send(new CreateOrchestrationLeadInCommandDto("primary", "cue/g20b", "保留原绑定", line.Id, indicator, "2000000", true));
        await http.Send(new CreateOrchestrationClipCommandDto("primary", "clip/g20b", "原片段", [line.Id], sequence.NodeId));
        await http.Send(new UpdateOrchestrationLeadInCommandDto("primary", "cue/g20b", "最短合法提示", line.Id, indicator, "3", true));
        ProjectOrchestrationResponse beforeRejectedCopy = await http.Read();
        // Copying a mark also extends its old sequence; its existing cue must remain valid.
        await http.Reject(new CopyOrchestrationSubtreeCommandDto(marks[0], null), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "PresentationInvalid");
        Assert.Equal(JsonSerializer.Serialize(beforeRejectedCopy, Json), JsonSerializer.Serialize(await http.Read(), Json));
        ProjectOrchestrationResponse beforeCopy = await http.Read();
        OrchestrationStructureCommandResultDto treeCopy = NodeResult(await http.Send(new CopyOrchestrationSubtreeCommandDto(program.RootContainerId, "无绑定副本")), "copySubtree", NodeSubtree(beforeCopy, program.RootContainerId).Count);
        ProjectOrchestrationResponse afterCopy = await http.Read();
        Assert.Equal(JsonSerializer.Serialize(beforeCopy.Programs, Json), JsonSerializer.Serialize(afterCopy.Programs, Json));
        Assert.All(afterCopy.Programs, p =>
        {
            Assert.DoesNotContain(p.RootContainerId, treeCopy.CreatedNodeIds!);
            Assert.DoesNotContain(p.DefaultContainerId, treeCopy.CreatedNodeIds!);
            Assert.All(p.Clips ?? [], clip => Assert.DoesNotContain(clip.ContainerId, treeCopy.CreatedNodeIds!));
            Assert.All(p.LeadIns ?? [], cue => Assert.DoesNotContain(cue.IndicatorContainerId, treeCopy.CreatedNodeIds!));
        });
        ProjectOrchestrationNodeDto copiedCountdown = Assert.Single(afterCopy.Nodes, n => treeCopy.CreatedNodeIds!.Contains(n.Id) && n.ContentKind == "countdownSequence");
        Assert.Equal(3, copiedCountdown.Content!.MarkNodeIds!.Length);
        Assert.All(copiedCountdown.Content.MarkNodeIds, id =>
        {
            Assert.Contains(id, treeCopy.CreatedNodeIds!);
            ProjectOrchestrationNodeDto mark = afterCopy.Nodes.Single(n => n.Id == id);
            Assert.Equal(copiedCountdown.Id, mark.ParentId); Assert.Equal("●", mark.Content!.Text);
        });
        ProjectLyricsWindowResponse afterLyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(http.Path + "/lyrics/window?branchId=primary&expectedRevision=" + http.Revision, Json))!;
        Assert.Equal(line.Id, Assert.Single(afterLyrics.Lines).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G20B真实HTTP生成身份回执同请求复放与旧修订冲突(bool copy)
    {
        await using Fixture f = await Fixture.Create();
        NodeHttpHarness http = new(f);
        ProjectOrchestrationResponse initial = await http.Read();
        string source = initial.Programs.Single(p => p.BranchId == "primary").RootContainerId;
        ProjectCommandDto command = copy ? new CopyOrchestrationSubtreeCommandDto(source, "稳定副本") : new CreateOrchestrationNodeCommandDto("slotSequence", null, "稳定创建", 64);
        RequestMetadataDto metadata = f.Metadata();
        using HttpResponseMessage first = await http.Post(command, "0", metadata);
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        ProjectCommandResponse accepted = (await first.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        NodeResult(accepted, copy ? "copySubtree" : "createNode", copy ? NodeSubtree(initial, source).Count : 65);
        using HttpResponseMessage retry = await http.Post(command, "00", metadata);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(accepted, Json), JsonSerializer.Serialize(await retry.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json), Json));
        ProjectCommandDto changed = copy ? new CopyOrchestrationSubtreeCommandDto(source, "改变名称") : new CreateOrchestrationNodeCommandDto("slotSequence", null, "改变名称", 64);
        using (HttpResponseMessage reuse = await http.Post(changed, "0", metadata))
            await Error(reuse, HttpStatusCode.Conflict, BackendErrorCode.RequestIdReuse);
        using (HttpResponseMessage stale = await http.Post(command, "0"))
            await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        http.Revision = "1";
        ProjectOrchestrationResponse beforeUndo = await http.Read();
        await http.Send(new ProjectCommandDto("history.undo"));
        ProjectOrchestrationResponse undone = await http.Read();
        Assert.All(accepted.StructureResult!.CreatedNodeIds!, id => Assert.DoesNotContain(undone.Nodes, n => n.Id == id));
        await http.Send(new ProjectCommandDto("history.redo"));
        Assert.Equal(JsonSerializer.Serialize(beforeUndo.Nodes, Json), JsonSerializer.Serialize((await http.Read()).Nodes, Json));
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"group\",\"name\":null,\"slotCount\":null}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"group\",\"parentId\":null,\"slotCount\":null}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"group\",\"parentId\":null,\"name\":null}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"Group\",\"parentId\":null,\"name\":null,\"slotCount\":null}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"group\",\"parentId\":null,\"name\":null,\"slotCount\":1}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"auto\",\"parentId\":null,\"name\":null,\"slotCount\":1}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"slotSequence\",\"parentId\":null,\"name\":null,\"slotCount\":null}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"slotSequence\",\"parentId\":null,\"name\":null,\"slotCount\":0}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"slotSequence\",\"parentId\":null,\"name\":null,\"slotCount\":65}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"slotSequence\",\"parentId\":null,\"name\":null,\"slotCount\":\"1\"}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"group\",\"parentId\":null,\"name\":null,\"slotCount\":null,\"extra\":1}")]
    [InlineData("{\"type\":\"orchestration.createNode\",\"kind\":\"group\",\"parentId\":null,\"name\":null,\"name\":null,\"slotCount\":null}")]
    [InlineData("{\"type\":\"orchestration.copySubtree\",\"nodeId\":\"x\"}")]
    [InlineData("{\"type\":\"orchestration.copySubtree\",\"nodeId\":\"x\",\"name\":null,\"parentId\":null}")]
    [InlineData("{\"type\":\"orchestration.copySubtree\",\"nodeId\":null,\"name\":null}")]
    public async Task G20B真实HTTP严格创建复制形状拒绝且不改历史(string raw)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage rejected = await PostRawEditing(f, "0", raw);
        await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo); Assert.False(summary.CanRedo);
    }

    [Fact]
    public async Task G20B真实HTTP失效目标非法父与名称预算拒绝保留重做()
    {
        await using Fixture f = await Fixture.Create();
        NodeHttpHarness http = new(f);
        string auto = (await http.Read()).Programs.Single(p => p.BranchId == "primary").DefaultContainerId;
        OrchestrationStructureCommandResultDto sequence = NodeResult(await http.Send(new CreateOrchestrationNodeCommandDto("slotSequence", null, "待撤销", 1)), "createNode", 2);
        await http.Send(new ProjectCommandDto("history.undo"));
        ProjectOrchestrationResponse initial = await http.Read();
        await http.Reject(new CreateOrchestrationNodeCommandDto("group", "missing", null, null), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale");
        await http.Reject(new CopyOrchestrationSubtreeCommandDto(sequence.NodeId, null), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "TargetStale");
        await http.Reject(new CreateOrchestrationNodeCommandDto("group", auto, null, null), HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected, "ContainerInvalid");
        await http.Reject(new CreateOrchestrationNodeCommandDto("group", null, new string('字', 257), null), HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        await http.Reject(new CopyOrchestrationSubtreeCommandDto(auto, new string('字', 257)), HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(JsonSerializer.Serialize(initial.Nodes, Json), JsonSerializer.Serialize((await http.Read()).Nodes, Json));
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        Assert.Equal(http.Revision, summary.ProjectRevision); Assert.True(summary.CanRedo);
        await http.Send(new ProjectCommandDto("history.redo"));
        Assert.Contains((await http.Read()).Nodes, n => n.Id == sequence.NodeId);
    }

    [Theory]
    [InlineData("candidateNodes")]
    [InlineData("sourceNodes")]
    [InlineData("copyNodes")]
    [InlineData("copyAtLimit")]
    [InlineData("sourceDepth")]
    [InlineData("candidateDepth")]
    public async Task G20B真实HTTP源候选与复制预算原子拒绝(string budget)
    {
        await using Fixture f = await Fixture.Create();
        // These preexisting project shapes are fixtures; the rejected mutation crosses the real HTTP boundary.
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        var tree = workspace.Project.LyricPresentation.WithDefaultPrograms(workspace.Project.Lyrics);
        List<SceneContainerDefinition> extra = [];
        if (budget is "candidateNodes" or "sourceNodes")
        {
            int targetCount = budget == "candidateNodes" ? 2000 : 2001;
            extra.AddRange(Enumerable.Range(0, targetCount - tree.Nodes.Length).Select(i => new SceneContainerDefinition { Id = "node/budget/" + i }));
        }
        else if (budget is "copyNodes" or "copyAtLimit")
        {
            // A valid stored node array need not put a parent before its children.
            extra.AddRange(Enumerable.Range(0, budget == "copyAtLimit" ? 127 : 128).Select(i => new SceneContainerDefinition { Id = "node/budget/" + i, ParentId = "node/budget/root" }));
            extra.Add(new() { Id = "node/budget/root" });
        }
        else
        {
            int depth = budget == "candidateDepth" ? 64 : 65;
            for (int i = 0; i < depth; i++) extra.Add(new() { Id = "node/budget/" + i, ParentId = i == 0 ? null : "node/budget/" + (i - 1) });
        }
        workspace.Replace(workspace.Project with { LyricPresentation = tree with { Nodes = tree.Nodes.AddRange(extra) } });
        NodeHttpHarness http = new(f);
        ProjectOrchestrationResponse before = await http.Read();
        ProjectCommandDto command = budget is "copyNodes" or "copyAtLimit" ? new CopyOrchestrationSubtreeCommandDto("node/budget/root", null)
            : new CreateOrchestrationNodeCommandDto("group", budget == "candidateDepth" ? "node/budget/63" : null, null, null);
        if (budget == "copyAtLimit")
        {
            OrchestrationStructureCommandResultDto copied = NodeResult(await http.Send(command), "copySubtree", 128);
            ProjectOrchestrationResponse model = await http.Read();
            Assert.Null(model.Nodes.Single(n => n.Id == copied.CreatedNodeIds![0]).ParentId);
            Assert.All(copied.CreatedNodeIds!.Skip(1), id => Assert.Equal(copied.NodeId, model.Nodes.Single(n => n.Id == id).ParentId));
            return;
        }
        await http.Reject(command, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        ProjectOrchestrationResponse after = await http.Read();
        Assert.Equal(JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json));
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(http.Path, Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo); Assert.False(summary.CanRedo);
    }

    private static HashSet<string> NodeSubtree(ProjectOrchestrationResponse model, string root)
    {
        HashSet<string> ids = [root];
        while (true)
        {
            int before = ids.Count;
            foreach (ProjectOrchestrationNodeDto node in model.Nodes)
                if (node.ParentId is not null && ids.Contains(node.ParentId)) ids.Add(node.Id);
            if (ids.Count == before) return ids;
        }
    }

    private static OrchestrationStructureCommandResultDto NodeResult(ProjectCommandResponse result, string operation, int count)
    {
        Assert.True(result.Changed); Assert.NotNull(result.StructureResult);
        OrchestrationStructureCommandResultDto structure = result.StructureResult;
        Assert.Equal(operation, structure.Operation); Assert.True(structure.Changed);
        Assert.NotNull(structure.NodeId); Assert.NotNull(structure.CreatedNodeIds);
        Assert.Equal(count, structure.CreatedNodeIds.Length);
        Assert.Equal(structure.NodeId, structure.CreatedNodeIds[0]);
        Assert.Equal(count, structure.CreatedNodeIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["orchestration", "renderInputs"], result.ChangeSet.Areas);
        return structure;
    }

    private static async Task<JobResponse> NodeTerminal(Fixture f, HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        JobResponse accepted = (await response.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        while (true)
        {
            JobResponse current = (await f.Http.GetFromJsonAsync<JobResponse>("/api/v1/jobs/" + accepted.JobId, Json, timeout.Token))!;
            if (current.State is "succeeded" or "failed" or "cancelled")
            { Assert.True(current.State == "succeeded", current.Error?.ToString()); return current; }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class NodeHttpHarness(Fixture fixture, string? sessionId = null)
    {
        internal string Path { get; } = "/api/v1/project-sessions/" + (sessionId ?? fixture.Project.ProjectSessionId);
        internal string Revision { get; set; } = "0";
        internal async Task<ProjectOrchestrationResponse> Read() => await ReadAt(Revision);
        internal async Task<ProjectOrchestrationResponse> ReadAt(string revision) => (await fixture.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(Path + "/orchestration?expectedRevision=" + revision, Json))!;
        internal Task<HttpResponseMessage> Post(ProjectCommandDto command, string revision, RequestMetadataDto? metadata = null)
        {
            metadata ??= fixture.Metadata();
            return fixture.Http.PostAsJsonAsync(Path + "/commands", new ProjectCommandRequest(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        }
        internal async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            using HttpResponseMessage response = await Post(command, Revision);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            ProjectCommandResponse result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.Equal(Revision, result.PreviousRevision);
            if (result.Changed) Assert.Equal((int.Parse(Revision, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture), result.ProjectRevision);
            Revision = result.ProjectRevision; return result;
        }
        internal async Task Reject(ProjectCommandDto command, HttpStatusCode status, BackendErrorCode code, string? failureCode = null)
        {
            using HttpResponseMessage response = await Post(command, Revision);
            Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
            BackendErrorResponse error = (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
            Assert.Equal(code, error.Code); Assert.Equal(failureCode, error.FailureCode);
        }
    }
}
