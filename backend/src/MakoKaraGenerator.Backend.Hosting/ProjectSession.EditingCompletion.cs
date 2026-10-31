using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static BackendErrorResponse? CheckLayoutAndLineAnimationIntent(ProjectCommandDto command)
    {
        if (command is not (SetOrchestrationLayoutKindCommandDto or SetLineEnterAnimationCommandDto or SetProjectEnterAnimationCommandDto)) return null;
        if (!EditingCommandTypeMatches(command) || command.Target is not null || command.StartUs is not null
            || command.EndUs is not null || command.AnalysisJobId is not null) return CompletionInvalid();
        bool valid = command switch
        {
            SetOrchestrationLayoutKindCommandDto value => IsStableId(value.ContainerId) && value.SlotIds is { Length: <= 64 } ids
                && ids.All(IsStableId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
                && value.NewSlotCount is >= 0 and <= 64 && (value.Kind == "Auto" ? ids.Length == 0 && value.NewSlotCount == 0
                    : value.Kind == "SlotSequence" && ids.Length + value.NewSlotCount is >= 1 and <= 64),
            SetLineEnterAnimationCommandDto value => IsStableId(value.BranchId) && IsStableId(value.LineId) && TryReadLineAnimation(value.Animation, out _),
            SetProjectEnterAnimationCommandDto value => TryReadLineAnimation(value.Animation, out _),
            _ => false,
        };
        if (!valid) return CompletionInvalid();
        return JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson).Length > OrchestrationNodeCreationLimits.MaxIntentBytes
            ? CompletionBudget() : null;
    }

    private static ProjectCommandDto CanonicalLayoutAndLineAnimationCommand(ProjectCommandDto command) => command switch
    {
        SetLineEnterAnimationCommandDto value when TryReadLineAnimation(value.Animation, out var animation) => value with { Animation = ProjectLineAnimation(animation) },
        SetProjectEnterAnimationCommandDto value when TryReadLineAnimation(value.Animation, out var animation) => value with { Animation = ProjectLineAnimation(animation) },
        _ => command,
    };

    private void TryPrepareLayoutAndLineAnimation(Project before, ProjectCommandDto command, out Project after,
        out OrchestrationPropertiesCommandResultDto? result, out BackendErrorResponse? error)
    {
        after = before; result = null; error = CheckLayoutAndLineAnimationIntent(command);
        if (error is not null) return;
        try
        {
            GuardNodeCreationTree(before);
            _ = BuildOrchestration(before, RevisionText);
            if (PresentationValidator.Validate(before).HasErrors) { error = CompletionRejected(); return; }
            string? nodeId = null, branchId = null;
            switch (command)
            {
                case SetOrchestrationLayoutKindCommandDto layout:
                    nodeId = layout.ContainerId;
                    PrepareLayoutKind(before, layout, out after, out error);
                    if (error is not null) return;
                    break;
                case SetLineEnterAnimationCommandDto line:
                    branchId = line.BranchId;
                    if (!HasLocalTransformTarget(before, line.BranchId!, line.LineId!))
                    { error = PropertiesStale("本句动画目标不存在于指定来源。", "command.lineId"); return; }
                    TryReadLineAnimation(line.Animation, out var animation);
                    if (!SceneTreeEditor.TrySetLineEnterAnimation(before, line.BranchId!, line.LineId!, animation, out after, out _))
                    { error = CompletionRejected(); return; }
                    break;
                case SetProjectEnterAnimationCommandDto project:
                    TryReadLineAnimation(project.Animation, out var projectAnimation);
                    after = LineAnimationEditor.SetProjectAnimation(before, projectAnimation);
                    break;
                default: error = CompletionInvalid(); return;
            }
            GuardNodeCreationTree(after);
            if (PresentationValidator.Validate(after).HasErrors) { after = before; error = CompletionRejected(); return; }
            bool changed = !ReferenceEquals(before, after);
            string revision = changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText;
            result = new(command.Type["orchestration.".Length..], nodeId, branchId, changed);
            _ = BuildOrchestration(after, revision);
            if (command is SetLineEnterAnimationCommandDto target) _ = BuildLinePresentation(after, revision, target.BranchId!, target.LineId!);
            ProjectChangeSetDto changes = changed ? new(["orchestration", "renderInputs"], [], [], [], true) : new([], [], [], [], false);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, revision, changed, false, false, false,
                changed ? command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), PropertiesResult: result);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException) { after = before; result = null; error = CompletionBudget(); }
        catch (Exception exception) when (exception is InvalidNodeCreationTreeException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { after = before; result = null; error = CompletionRejected(); }
    }

    private static void PrepareLayoutKind(Project before, SetOrchestrationLayoutKindCommandDto command, out Project after, out BackendErrorResponse? error)
    {
        after = before; error = null;
        var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
        var node = tree.FindNode(command.ContainerId!);
        if (node is null) { error = PropertiesStale("布局容器不存在。", "command.containerId"); return; }
        if (node is not { Layout: { } layout, Content: null })
        { error = new(BackendErrorCode.CommandRejected, "目标必须是已有歌词布局容器。", "command.containerId", FailureCode: "ContainerInvalid"); return; }
        var selected = command.SlotIds!;
        if (selected.Any(id => tree.FindNode(id) is not { Layout: null, Content: null, Attachment: null } child || child.ParentId != node.Id))
        { error = new(BackendErrorCode.CommandRejected, "槽位必须是该容器的已有直接普通子节点。", "command.slotIds", FailureCode: "ContainerInvalid"); return; }
        int addedCount = command.NewSlotCount!.Value;
        if (tree.Nodes.Length + addedCount > OrchestrationNodeCreationLimits.MaxNodes) throw new ReadModelBudgetExceededException();
        HashSet<string> ids = new(tree.Nodes.Select(value => value.Id), StringComparer.Ordinal);
        List<SceneContainerDefinition> added = [];
        for (int i = 0; i < addedCount; i++)
        {
            string id;
            do { id = "node/" + Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
            added.Add(new() { Id = id, ParentId = node.Id });
        }
        string[] slots = [.. selected.Concat(added.Select(value => value.Id))];
        LyricContainerLayout next = command.Kind == "Auto" ? layout with { Kind = LyricContainerLayoutKind.Auto, SlotIds = [], SlotAnimation = null }
            : layout with { Kind = LyricContainerLayoutKind.SlotSequence, SlotIds = [.. slots],
                SlotAnimation = layout.SlotAnimation ?? new() { MaxVisibleLines = Math.Min(2, slots.Length) } };
        if (next == layout && addedCount == 0) return;
        var nodes = tree.Nodes.SetItem(tree.Nodes.IndexOf(node), node with { Layout = next }).AddRange(added);
        var candidate = tree with { Nodes = nodes };
        GuardNodeCreationTree(before with { LyricPresentation = candidate });
        if (!PresentationEditor.TrySetTree(before, candidate, out after, out _)) error = CompletionRejected();
    }

    internal Task<BackendOperationResult<ProjectLinePresentationResponse>> ReadLinePresentationAsync(string? expectedRevision,
        string? branchId, string? lineId, CancellationToken cancellationToken) => ReadModelAsync(expectedRevision, () =>
        {
            if (!IsStableId(branchId) || !IsStableId(lineId))
                return BackendOperationResult<ProjectLinePresentationResponse>.Rejected(CompletionInvalid());
            if (!HasLocalTransformTarget(Workspace.Project, branchId!, lineId!))
                return BackendOperationResult<ProjectLinePresentationResponse>.Rejected(PropertiesStale("本句展示目标不存在于指定来源。", "lineId"));
            return BuildLinePresentation(Workspace.Project, RevisionText, branchId!, lineId!);
        }, cancellationToken, "linePresentation");

    private BackendOperationResult<ProjectLinePresentationResponse> BuildLinePresentation(Project project, string revision, string branchId, string lineId)
    {
        var local = project.LyricPresentation.FindLine(lineId);
        var transform = local?.LocalTransform;
        return BoundedReadModel(new ProjectLinePresentationResponse(ProjectSessionId, revision, branchId, lineId,
            transform is null ? null : new(transform.OffsetXRatio, transform.OffsetYRatio, transform.RotationDegrees, transform.Scale),
            ProjectLineAnimation(local?.EnterAnimation), ProjectLineAnimation(local?.EnterAnimation ?? project.EnterAnimation),
            local?.EnterAnimation is not null ? "line" : project.EnterAnimation is not null ? "project" : "none"));
    }

    private static bool TryReadLineAnimation(ProjectLineEnterAnimationDto? value, out LineAnimation? animation)
    {
        animation = null;
        if (value is null) return true;
        if (!TryParseMicroseconds(value.DurationUs, out long duration) || !Enum.GetNames<AnimationEasing>().Contains(value.Easing, StringComparer.Ordinal)) return false;
        animation = new() { DurationUs = duration, Easing = Enum.Parse<AnimationEasing>(value.Easing), StartOpacity = value.StartOpacity,
            StartScale = value.StartScale, StartOffsetPx = value.StartOffsetPx };
        return animation.IsValid;
    }

    private static ProjectLineEnterAnimationDto? ProjectLineAnimation(LineAnimation? value) => value is null ? null
        : new(value.DurationUs.ToString(CultureInfo.InvariantCulture), value.Easing.ToString(), value.StartOpacity, value.StartScale, value.StartOffsetPx);
    private static BackendErrorResponse CompletionInvalid() => new(BackendErrorCode.InvalidRequest, "布局类型或进入动画必须符合完整、有限的公开字段。", "command");
    private static BackendErrorResponse CompletionRejected() => new(BackendErrorCode.CommandRejected, "布局或进入动画未通过完整展示校验。", "command", FailureCode: "PresentationInvalid");
    private static BackendErrorResponse CompletionBudget() => new(BackendErrorCode.ResourceTooLarge, "完整意图、节点树、读投影或回执超过预算；工程未改变。", "command");
}
