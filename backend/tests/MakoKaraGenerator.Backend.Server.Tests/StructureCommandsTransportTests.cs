using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G21真实HTTP内部布局默认位置读回修订与严格字段()
    {
        await using Fixture f = await Fixture.Create();
        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        Assert.Contains("project-sessions.commands.orchestration.setLayoutDefaultPlacement", capabilities.Features);
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        ProjectOrchestrationResponse initial = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=0", Json))!;
        string containerId = initial.Programs.Single(value => value.BranchId == "primary").DefaultContainerId;
        ProjectOrchestrationPlacementDto placement = new(.2, .8, "BottomRight", -15, 1.1);

        RequestMetadataDto metadata = f.Metadata();
        using HttpResponseMessage applied = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(
            "0", new SetOrchestrationLayoutDefaultPlacementCommandDto(containerId, placement), metadata.RequestId,
            metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        ProjectCommandResponse result = (await applied.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(result.Changed); Assert.Equal("1", result.ProjectRevision);
        Assert.Equal(new OrchestrationLayoutCommandResultDto(containerId, true), result.LayoutResult);
        ProjectOrchestrationResponse changed = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=1", Json))!;
        ProjectOrchestrationPlacementDto actual = changed.Nodes.Single(value => value.Id == containerId).Layout!.DefaultPlacement!;
        Assert.Equal(placement, actual);

        metadata = f.Metadata();
        using HttpResponseMessage noop = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(
            "1", new SetOrchestrationLayoutDefaultPlacementCommandDto(containerId, placement), metadata.RequestId,
            metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        ProjectCommandResponse noopResult = (await noop.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.False(noopResult.Changed); Assert.Equal("1", noopResult.ProjectRevision);
        Assert.Equal(new OrchestrationLayoutCommandResultDto(containerId, false), noopResult.LayoutResult);

        using HttpResponseMessage malformed = await PostRawEditing(f, "1",
            "{\"type\":\"orchestration.setLayoutDefaultPlacement\",\"containerId\":\"" + containerId
            + "\",\"placement\":{\"x\":0.2,\"y\":0.8,\"anchor\":\"BottomRight\",\"rotationDegrees\":-15}}");
        await Error(malformed, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task G20真实HTTP六结构命令精确修订回执投影与引用删除保护()
    {
        await using Fixture f = await Fixture.Create();
        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string operation in new[] { "setNode", "reparent", "reorderChildren", "addSlot", "removeNode", "reorderSlots" })
            Assert.Contains("project-sessions.commands.orchestration." + operation, capabilities.Features);

        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        int revision = 0;
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command, string? operation = null)
        {
            RequestMetadataDto metadata = f.Metadata();
            using HttpResponseMessage response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(
                revision.ToString(CultureInfo.InvariantCulture), command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            ProjectCommandResponse result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.Equal(revision.ToString(CultureInfo.InvariantCulture), result.PreviousRevision);
            Assert.True(result.Changed); revision++;
            Assert.Equal(revision.ToString(CultureInfo.InvariantCulture), result.ProjectRevision);
            if (operation is not null)
            {
                Assert.Equal(operation, result.StructureResult!.Operation); Assert.True(result.StructureResult.Changed);
                Assert.Equal(["orchestration", "renderInputs"], result.ChangeSet.Areas);
            }
            return result;
        }
        async Task<ProjectOrchestrationResponse> Read() => (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(
            path + "/orchestration?expectedRevision=" + revision.ToString(CultureInfo.InvariantCulture), Json))!;

        await Send(new BasicLayoutCommandDto("primary", "DoubleCentered", null, "双行", ["一", "二"], "已选", "保留", false));
        ProjectOrchestrationResponse model = await Read();
        ProjectOrchestrationProgramDto program = model.Programs.Single(p => p.BranchId == "primary");
        string container = program.DefaultContainerId;
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        string freeNode = "group/http-g20";
        workspace.Replace(workspace.Project with { LyricPresentation = workspace.Project.LyricPresentation with
        {
            Nodes = workspace.Project.LyricPresentation.Nodes.Add(new SceneContainerDefinition { Id = freeNode, ParentId = program.RootContainerId }),
        } });

        ProjectCommandResponse renamed = await Send(new SetOrchestrationNodeCommandDto(container, "HTTP新布局"), "setNode");
        Assert.Equal(container, renamed.StructureResult!.NodeId);
        Assert.Equal("HTTP新布局", (await Read()).Nodes.Single(n => n.Id == container).Name);
        ProjectCommandResponse moved = await Send(new ReparentOrchestrationNodeCommandDto(freeNode, null), "reparent");
        Assert.Equal(freeNode, moved.StructureResult!.NodeId); Assert.Null(moved.StructureResult.ParentId);
        Assert.Null((await Read()).Nodes.Single(n => n.Id == freeNode).ParentId);

        model = await Read();
        string[] children = model.Nodes.Where(n => n.ParentId == container).Select(n => n.Id).Reverse().ToArray();
        Assert.Equal(2, children.Length);
        ProjectCommandResponse childOrder = await Send(new ReorderOrchestrationChildrenCommandDto(container, children), "reorderChildren");
        Assert.Equal(children, childOrder.StructureResult!.OrderedIds);
        Assert.Equal(children, (await Read()).Nodes.Where(n => n.ParentId == container).Select(n => n.Id));

        string slot = "slot/http-g20";
        ProjectCommandResponse added = await Send(new AddOrchestrationSlotCommandDto(container, slot, "新槽"), "addSlot");
        Assert.Equal(slot, added.StructureResult!.NodeId); Assert.Equal(container, added.StructureResult.ContainerId);
        model = await Read();
        Assert.Equal("新槽", model.Nodes.Single(n => n.Id == slot).Name);
        string[] slots = model.Nodes.Single(n => n.Id == container).Layout!.SlotIds.Reverse().ToArray();
        ProjectCommandResponse slotOrder = await Send(new ReorderOrchestrationSlotsCommandDto(container, slots), "reorderSlots");
        Assert.Equal(slots, slotOrder.StructureResult!.OrderedIds);
        Assert.Equal(slots, (await Read()).Nodes.Single(n => n.Id == container).Layout!.SlotIds);

        RequestMetadataDto metadata = f.Metadata();
        using HttpResponseMessage referenced = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(
            "6", new RemoveOrchestrationNodeCommandDto(container), metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, referenced.StatusCode);
        BackendErrorResponse error = (await referenced.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(BackendErrorCode.CommandRejected, error.Code); Assert.Equal("PresentationInvalid", error.FailureCode);
        Assert.Equal("6", (await Read()).ProjectRevision);

        ProjectCommandResponse removed = await Send(new RemoveOrchestrationNodeCommandDto(slot), "removeNode");
        Assert.Equal(slot, removed.StructureResult!.NodeId);
        model = await Read();
        Assert.DoesNotContain(model.Nodes, n => n.Id == slot);
        Assert.Equal(slots.Where(id => id != slot), model.Nodes.Single(n => n.Id == container).Layout!.SlotIds);
        using HttpResponseMessage stale = await f.Http.GetAsync(path + "/orchestration?expectedRevision=6");
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.reparent\",\"nodeId\":\"node\"}")]
    [InlineData("{\"type\":\"orchestration.reparent\",\"nodeId\":\"node\",\"parentId\":null,\"extra\":1}")]
    public async Task G20真实HTTP结构命令拒绝缺失与未知字段且不改修订(string command)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage rejected = await PostRawEditing(f, "0", command);
        await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo);
    }
}
