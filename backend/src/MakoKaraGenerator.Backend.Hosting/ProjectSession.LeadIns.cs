using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private bool TryPrepareLeadInCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectChangeSetDto? changes, out OrchestrationLeadInCommandResultDto? result, out BackendErrorResponse? error)
    {
        after = before; changes = null; result = null; error = null;
        if (command is not (CreateOrchestrationCountdownCommandDto or CreateOrchestrationLeadInCommandDto
            or UpdateOrchestrationLeadInCommandDto or DeleteOrchestrationLeadInCommandDto)) return false;
        try
        {
            // The same strict shape applies to in-process callers and HTTP.
            byte[] intent = JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson);
            if (intent.Length > 16 * 1024) throw new ReadModelBudgetExceededException();
            _ = JsonSerializer.Deserialize<ProjectCommandDto>(intent, ReadModelJson);
            var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
            Project input = ReferenceEquals(tree, before.LyricPresentation) ? before : before with { LyricPresentation = tree };
            ValidationReport report;
            if (command is CreateOrchestrationCountdownCommandDto countdown)
            {
                if (!IsStableId(countdown.ParentId) || countdown.Count is not (>= 1 and <= 64))
                { error = LeadInInvalid("提示容器需要有效parentId和1..64个标记。"); return true; }
                if (tree.FindNode(countdown.ParentId!) is not { Content: null, Layout: null })
                { error = LeadInRejected("提示父节点必须是已有的普通组。", "command.parentId", "ContainerInvalid"); return true; }
                if (!LeadInEditor.TryCreateCountdown(input, countdown.ParentId!, countdown.Name ?? "前奏提示", countdown.Count.Value,
                    out string id, out after, out report))
                { error = LeadInValidation(report); after = before; return true; }
                var sequence = (CountdownSequenceContent)after.LyricPresentation.FindNode(id)!.Content!;
                result = new("createCountdown", null, null, id, sequence.MarkNodeIds.ToArray(), true);
            }
            else
            {
                (string? branchId, string? cueId) = command switch
                {
                    CreateOrchestrationLeadInCommandDto c => (c.BranchId, c.LeadInId),
                    UpdateOrchestrationLeadInCommandDto c => (c.BranchId, c.LeadInId),
                    DeleteOrchestrationLeadInCommandDto c => (c.BranchId, c.LeadInId),
                    _ => (null, null),
                };
                if (!IsStableId(branchId) || (command is CreateOrchestrationLeadInCommandDto ? !IsStableId(cueId) : !LeadInCueIdentity(cueId))) { error = LeadInInvalid("需要有效branchId和leadInId。"); return true; }
                var program = tree.FindProgram(branchId!);
                if (program is null) { error = LeadInRejected("显示来源不存在。", "command.branchId", "SourceMissing"); return true; }
                var existing = program.LeadIns.FirstOrDefault(c => c.Id == cueId);
                if (command is CreateOrchestrationLeadInCommandDto && tree.Programs.Any(p => p.LeadIns.Any(c => c.Id == cueId)))
                { error = LeadInRejected("前奏身份已存在。", "command.leadInId", "IdentityConflict"); return true; }
                if (command is not CreateOrchestrationLeadInCommandDto && existing is null)
                { error = LeadInRejected("前奏不存在。", "command.leadInId", "TargetStale"); return true; }
                string operation;
                if (command is DeleteOrchestrationLeadInCommandDto)
                {
                    if (!LeadInEditor.TryDeleteLeadIn(input, program.Id, cueId!, false, out after, out report))
                    { error = LeadInValidation(report); after = before; return true; }
                    operation = "deleteLeadIn";
                }
                else
                {
                    (string? name, string? target, string? indicator, string? duration, bool? enabled) = command switch
                    {
                        CreateOrchestrationLeadInCommandDto c => (c.Name, c.TargetLineId, c.IndicatorContainerId, c.DurationUs, c.Enabled),
                        UpdateOrchestrationLeadInCommandDto c => (c.Name, c.TargetLineId, c.IndicatorContainerId, c.DurationUs, c.Enabled),
                        _ => (null, null, null, null, null),
                    };
                    if (!IsStableId(target) || !IsStableId(indicator) || !TryParseMicroseconds(duration, out long durationUs)
                        || durationUs <= 0 || enabled is null)
                    { error = LeadInInvalid("需要有效目标、提示容器、正整数微秒时长及enabled。"); return true; }
                    var branch = before.Lyrics.Branches.FirstOrDefault(b => b.Id == branchId);
                    if (branch is null || !branch.Lines.Any(l => l.Id == target))
                    { error = LeadInRejected("目标歌词行不存在。", "command.targetLineId", "TargetStale"); return true; }
                    if (tree.FindNode(indicator!)?.Content is not CountdownSequenceContent || !Under(tree, indicator!, program.RootContainerId))
                    { error = LeadInRejected("提示容器必须属于当前程序根。", "command.indicatorContainerId", "ContainerInvalid"); return true; }
                    var cue = new LyricLeadIn { Id = cueId!, Name = name, TargetLineId = target!, IndicatorContainerId = indicator!,
                        DurationUs = durationUs, Enabled = enabled.Value, Origin = existing?.Origin ?? LeadInOrigin.Manual };
                    if (!LeadInEditor.TrySetLeadIn(input, program.Id, cue, out after, out report))
                    { error = LeadInValidation(report); after = before; return true; }
                    operation = command is CreateOrchestrationLeadInCommandDto ? "createLeadIn" : "updateLeadIn";
                }
                // A logically unchanged edit must preserve the exact original reference and redo.
                if (ReferenceEquals(input, after)) after = before;
                result = new(operation, branchId, cueId,
                    command is DeleteOrchestrationLeadInCommandDto ? existing!.IndicatorContainerId
                        : after.LyricPresentation.FindProgram(branchId!)!.LeadIns.First(c => c.Id == cueId).IndicatorContainerId,
                    null, !ReferenceEquals(before, after));
            }
            bool changed = !ReferenceEquals(before, after);
            changes = changed ? new(["orchestration", "renderInputs"], [], [], [], true) : new([], [], [], [], false);
            string nextRevision = changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText;
            _ = BuildOrchestration(after, nextRevision);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, nextRevision, changed, false, false, false,
                changed ? command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), LeadInResult: result);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException)
        { after = before; error = new(BackendErrorCode.ResourceTooLarge, "前奏意图、完整编排读投影或回执超过预算；工程未改变。", "command"); }
        catch (JsonException)
        { after = before; error = LeadInInvalid("前奏命令字段无效。"); }
        return true;
    }

    private static BackendErrorResponse LeadInInvalid(string message) => new(BackendErrorCode.InvalidRequest, message, "command");
    private static BackendErrorResponse LeadInRejected(string message, string field, string failure) =>
        new(BackendErrorCode.CommandRejected, message, field, FailureCode: failure);
    private static BackendErrorResponse LeadInValidation(ValidationReport report) =>
        LeadInRejected("前奏未通过工程展示校验。", "command", report.Contains(DiagnosticCode.PresentationTimeConflict)
            ? "LeadInTimeConflict" : "PresentationInvalid");
}
