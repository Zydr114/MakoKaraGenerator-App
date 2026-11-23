using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    // Runs before canonicalization and receipt fingerprint serialization: invalid in-memory
    // doubles must produce InvalidRequest rather than throw from System.Text.Json.
    internal static BackendErrorResponse? CheckOrchestrationPropertiesIntent(ProjectCommandDto command)
    {
        bool recognized = command is SetOrchestrationLayoutDefaultPlacementCommandDto or SetOrchestrationNodeGeometryCommandDto
            or SetOrchestrationLayoutSettingsCommandDto or SetOrchestrationSlotAnimationCommandDto or SetOrchestrationProgramAnimationPolicyCommandDto;
        if (!recognized) return null;
        if (command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
            return PropertiesInvalid("基础编排属性不接受时间或其他目标字段。");
        bool valid = command switch
        {
            SetOrchestrationLayoutDefaultPlacementCommandDto value => value.Placement is null || TryReadPlacement(value.Placement, out _),
            SetOrchestrationNodeGeometryCommandDto value => value.Placement is not null && TryReadPlacement(value.Placement, out _)
                && value.ReferenceRect is { } rect && new SceneReferenceRect(rect.WidthRatio, rect.HeightRatio).IsValid,
            SetOrchestrationLayoutSettingsCommandDto value => value.SafeArea is { } area
                && new NormalizedSafeArea(area.Left, area.Top, area.Right, area.Bottom).IsValid
                && double.IsFinite(value.MaxWidthRatio) && value.MaxWidthRatio is > 0 and <= 1,
            SetOrchestrationSlotAnimationCommandDto value => value.SlotAnimation is null || TryReadSlotAnimation(value.SlotAnimation, out _),
            SetOrchestrationProgramAnimationPolicyCommandDto value => value.AnimationPolicy is null
                || TryNamed<LyricBranchAnimationPolicy>(value.AnimationPolicy, out _),
            _ => false,
        };
        return valid ? null : PropertiesInvalid("基础编排属性必须完整有效，数值必须有限且枚举必须使用公开名称。");
    }

    private bool TryPrepareOrchestrationProperties(Project before, ProjectCommandDto command, out Project after,
        out OrchestrationPropertiesCommandResultDto? result, out BackendErrorResponse? error)
    {
        after = before; result = null; error = CheckOrchestrationPropertiesIntent(command);
        if (error is not null) return true;
        var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
        string? nodeId = command switch
        {
            SetOrchestrationNodeGeometryCommandDto value => value.NodeId,
            SetOrchestrationLayoutSettingsCommandDto value => value.ContainerId,
            SetOrchestrationSlotAnimationCommandDto value => value.ContainerId,
            _ => null,
        };
        string? branchId = (command as SetOrchestrationProgramAnimationPolicyCommandDto)?.BranchId;
        if (command is SetOrchestrationProgramAnimationPolicyCommandDto policy)
        {
            if (!IsStableId(branchId)) { error = PropertiesInvalid("需要稳定branchId。"); return true; }
            var program = tree.FindProgram(branchId!);
            if (program is null) { error = PropertiesStale("显示程序不存在。", "command.branchId"); return true; }
            LyricBranchAnimationPolicy? next = null;
            if (policy.AnimationPolicy is { } name) { TryNamed<LyricBranchAnimationPolicy>(name, out var parsed); next = parsed; }
            if (program.AnimationPolicy != next)
            {
                int index = tree.Programs.IndexOf(program);
                if (!PresentationEditor.TrySetTree(before, tree with { Programs = tree.Programs.SetItem(index, program with { AnimationPolicy = next }) }, out after, out _))
                { after = before; error = PropertiesRejected(); return true; }
            }
        }
        else
        {
            if (!IsStableId(nodeId)) { error = PropertiesInvalid("需要稳定节点身份。"); return true; }
            var node = tree.FindNode(nodeId!);
            if (node is null) { error = PropertiesStale("节点不存在。", "command.nodeId"); return true; }
            SceneContainerDefinition candidate;
            switch (command)
            {
                case SetOrchestrationNodeGeometryCommandDto geometry:
                    TryReadPlacement(geometry.Placement!, out var placement);
                    candidate = node with { Placement = placement!, ReferenceRect = new(geometry.ReferenceRect!.WidthRatio, geometry.ReferenceRect.HeightRatio) };
                    break;
                case SetOrchestrationLayoutSettingsCommandDto settings when node.Layout is { } layout:
                    var area = settings.SafeArea!;
                    candidate = node with { Layout = layout with { SafeArea = new(area.Left, area.Top, area.Right, area.Bottom), MaxWidthRatio = settings.MaxWidthRatio } };
                    break;
                case SetOrchestrationSlotAnimationCommandDto animation when node.Layout is { } layout:
                    SlotAnimationSpec? spec = null;
                    if (animation.SlotAnimation is { } input) TryReadSlotAnimation(input, out spec);
                    candidate = node with { Layout = layout with { SlotAnimation = spec } };
                    break;
                default:
                    error = PropertiesStale("目标节点不是歌词内部布局容器。", "command.containerId"); return true;
            }
            if (candidate != node && !SceneTreeEditor.TrySetNode(before, candidate, out after, out _))
            { after = before; error = PropertiesRejected(); return true; }
        }
        bool changed = !ReferenceEquals(before, after);
        result = new(command.Type["orchestration.".Length..], nodeId, branchId, changed);
        try
        {
            string revision = changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText;
            _ = BuildOrchestration(after, revision);
            ProjectChangeSetDto changes = changed ? new(["orchestration", "renderInputs"], [], [], [], true) : new([], [], [], [], false);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, revision, changed, false, false, false,
                changed ? command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), PropertiesResult: result);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException)
        { after = before; error = new(BackendErrorCode.ResourceTooLarge, "完整编排读投影或回执超过预算；工程未改变。", "command"); }
        return true;
    }

    private static ProjectCommandDto CanonicalOrchestrationPropertiesCommand(ProjectCommandDto command) => command switch
    {
        SetOrchestrationNodeGeometryCommandDto value when value.Placement is { } placement && TryReadPlacement(placement, out var canonical) =>
            value with { Placement = Placement(canonical!) },
        SetOrchestrationSlotAnimationCommandDto value when value.SlotAnimation is { } animation && TryReadSlotAnimation(animation, out var canonical) =>
            value with { SlotAnimation = ProjectSlotAnimation(canonical!) },
        SetOrchestrationProgramAnimationPolicyCommandDto value when value.AnimationPolicy is { } name && TryNamed<LyricBranchAnimationPolicy>(name, out var canonical) =>
            value with { AnimationPolicy = canonical.ToString() },
        _ => command,
    };

    private static bool TryNamed<T>(string? name, out T value) where T : struct, Enum =>
        Enum.TryParse(name, true, out value) && Enum.GetNames<T>().Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool TryReadSlotAnimation(ProjectOrchestrationSlotAnimationDto input, out SlotAnimationSpec? result)
    {
        result = null;
        if (!TryNamed<SlotRevealMode>(input.RevealMode, out var reveal) || !TryNamed<SlotConflictPolicy>(input.ConflictPolicy, out var conflict)
            || !TryNamed<SlotRetentionPolicy>(input.RetentionPolicy, out var retention)
            || !TryParseMicroseconds(input.RevealDelayUs, out long delay)
            || !TryParseMicroseconds(input.RevealLeadUs, out long lead)) return false;
        var value = new SlotAnimationSpec { RevealMode = reveal, RevealProgress = input.RevealProgress, RevealDelayUs = delay,
            RevealLeadUs = lead, MaxVisibleLines = input.MaxVisibleLines, ConflictPolicy = conflict, RetentionPolicy = retention };
        if (!value.ToProfile([new() { Id = "validation-slot" }]).IsValid) return false;
        result = value; return true;
    }

    private static ProjectOrchestrationSlotAnimationDto ProjectSlotAnimation(SlotAnimationSpec value) => new(value.RevealMode.ToString(), value.RevealProgress,
        value.RevealDelayUs.ToString(CultureInfo.InvariantCulture), value.RevealLeadUs.ToString(CultureInfo.InvariantCulture), value.MaxVisibleLines,
        value.ConflictPolicy.ToString(), value.RetentionPolicy.ToString());

    private static BackendErrorResponse PropertiesInvalid(string message) => new(BackendErrorCode.InvalidRequest, message, "command");
    private static BackendErrorResponse PropertiesStale(string message, string field) => new(BackendErrorCode.CommandRejected, message, field, FailureCode: "TargetStale");
    private static BackendErrorResponse PropertiesRejected() => new(BackendErrorCode.CommandRejected,
        "基础编排属性未通过完整展示校验。", "command", FailureCode: "PresentationInvalid");
}
