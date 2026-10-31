using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private static bool TryExecuteStructureCommand(Project before, ProjectCommandDto command,
        out Project after, out ProjectChangeSetDto? changeSet,
        out OrchestrationStructureCommandResultDto? structureResult, out BackendErrorResponse? error)
    {
        after = before;
        changeSet = null;
        structureResult = null;
        error = null;
        if (command.Type is not ("orchestration.setNode" or "orchestration.reparent" or "orchestration.reorderChildren"
            or "orchestration.addSlot" or "orchestration.removeNode" or "orchestration.reorderSlots")) return false;

        var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
        string? nodeId = command switch
        {
            SetOrchestrationNodeCommandDto value => value.NodeId,
            ReparentOrchestrationNodeCommandDto value => value.NodeId,
            RemoveOrchestrationNodeCommandDto value => value.NodeId,
            _ => null,
        };
        string? containerId = command switch
        {
            AddOrchestrationSlotCommandDto value => value.ContainerId,
            ReorderOrchestrationSlotsCommandDto value => value.ContainerId,
            _ => null,
        };
        if (nodeId is not null && !IsStableId(nodeId))
        { error = InvalidStructureCommand("节点身份无效。", "command.nodeId"); return true; }
        if (containerId is not null && !IsStableId(containerId))
        { error = InvalidStructureCommand("容器身份无效。", "command.containerId"); return true; }
        if (command is ReorderOrchestrationChildrenCommandDto { ParentId: not null } childrenCommand
            && !IsStableId(childrenCommand.ParentId))
        { error = InvalidStructureCommand("父节点身份无效。", "command.parentId"); return true; }

        bool accepted;
        switch (command)
        {
            case SetOrchestrationNodeCommandDto set:
                if (tree.FindNode(set.NodeId!) is not { } node)
                { error = StructureRejected("节点不存在。", "command.nodeId", "TargetStale"); return true; }
                accepted = SceneTreeEditor.TrySetNode(before, node with { Name = set.Name }, out after, out var setReport);
                if (!accepted) { error = StructureValidationError(setReport, "command.nodeId"); return true; }
                structureResult = new("setNode", node.Id, node.ParentId, null, null, !ReferenceEquals(before, after));
                break;
            case ReparentOrchestrationNodeCommandDto reparent:
                if (tree.FindNode(reparent.NodeId!) is null)
                { error = StructureRejected("节点不存在。", "command.nodeId", "TargetStale"); return true; }
                if (reparent.ParentId is not null && !IsStableId(reparent.ParentId))
                { error = InvalidStructureCommand("父节点身份无效。", "command.parentId"); return true; }
                accepted = SceneTreeEditor.TryReparent(before, reparent.NodeId!, reparent.ParentId, out after, out var reparentReport);
                if (!accepted) { error = StructureValidationError(reparentReport, "command.parentId"); return true; }
                structureResult = new("reparent", reparent.NodeId, reparent.ParentId, null, null, !ReferenceEquals(before, after));
                break;
            case ReorderOrchestrationChildrenCommandDto reorder:
                if (!ValidIds(reorder.OrderedIds)) { error = InvalidStructureCommand("orderedIds 必须包含不重复的稳定节点身份。", "command.orderedIds"); return true; }
                accepted = SceneTreeEditor.TryReorderChildren(before, reorder.ParentId, reorder.OrderedIds!, out after, out var reorderReport);
                if (!accepted) { error = StructureValidationError(reorderReport, "command.orderedIds"); return true; }
                structureResult = new("reorderChildren", null, reorder.ParentId, null, reorder.OrderedIds, !ReferenceEquals(before, after));
                break;
            case AddOrchestrationSlotCommandDto add:
                if (!IsStableId(add.SlotId)) { error = InvalidStructureCommand("槽位身份无效。", "command.slotId"); return true; }
                if (tree.FindNode(add.SlotId!) is not null)
                { error = StructureRejected("槽位身份已存在。", "command.slotId", "IdentityConflict"); return true; }
                SceneContainerDefinition slot = new() { Id = add.SlotId!, Name = add.Name, ParentId = add.ContainerId };
                accepted = SceneTreeEditor.TryAddSlot(before, add.ContainerId!, slot, out after, out var addReport);
                if (!accepted) { error = StructureValidationError(addReport, "command.containerId"); return true; }
                structureResult = new("addSlot", slot.Id, slot.ParentId, add.ContainerId, null, true);
                break;
            case RemoveOrchestrationNodeCommandDto remove:
                if (tree.FindNode(remove.NodeId!) is null)
                { error = StructureRejected("节点不存在。", "command.nodeId", "TargetStale"); return true; }
                accepted = SceneTreeEditor.TryDeleteNode(before, remove.NodeId!, out after, out var removeReport);
                if (!accepted) { error = StructureValidationError(removeReport, "command.nodeId"); return true; }
                structureResult = new("removeNode", remove.NodeId, null, null, null, !ReferenceEquals(before, after));
                break;
            case ReorderOrchestrationSlotsCommandDto slots:
                if (!ValidIds(slots.OrderedIds)) { error = InvalidStructureCommand("orderedIds 必须包含不重复的稳定槽位身份。", "command.orderedIds"); return true; }
                accepted = SceneTreeEditor.TryReorderSlots(before, slots.ContainerId!, slots.OrderedIds!, out after, out var slotsReport);
                if (!accepted) { error = StructureValidationError(slotsReport, "command.orderedIds"); return true; }
                structureResult = new("reorderSlots", null, null, slots.ContainerId, slots.OrderedIds, !ReferenceEquals(before, after));
                break;
            default: return false;
        }

        if (ReferenceEquals(before, after)) changeSet = new([], [], [], [], false);
        return true;

        static bool ValidIds(string[]? ids) => ids is { Length: > 0 and <= 2000 }
            && ids.All(IsStableId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    }

    private static BackendErrorResponse InvalidStructureCommand(string message, string field) =>
        new(BackendErrorCode.InvalidRequest, message, field);

    private static BackendErrorResponse StructureRejected(string message, string field, string failureCode) =>
        new(BackendErrorCode.CommandRejected, message, field, FailureCode: failureCode);

    private static BackendErrorResponse StructureValidationError(MakoKaraGenerator.Core.Diagnostics.ValidationReport report, string field) =>
        StructureRejected("节点结构未通过工程校验。", field, "PresentationInvalid");
}
