using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static BackendErrorResponse? CheckClipMembersIntent(ProjectCommandDto command)
    {
        if (command is not (SetOrchestrationClipMembersCommandDto or SplitOrchestrationClipCommandDto or MergeOrchestrationClipsCommandDto)) return null;
        bool List(string[]? ids, int minimum) => ids is { Length: <= 1000 } && ids.Length >= minimum
            && ids.All(IsStableId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
        bool valid = command.Target is null && command.StartUs is null && command.EndUs is null && command.AnalysisJobId is null && (command switch
        {
            SetOrchestrationClipMembersCommandDto v => v.Type == "orchestration.setClipMembers" && IsStableId(v.BranchId) && IsStableId(v.ClipId) && List(v.LineIds, 1),
            SplitOrchestrationClipCommandDto v => v.Type == "orchestration.splitClip" && IsStableId(v.BranchId) && IsStableId(v.ClipId) && IsStableId(v.SplitBeforeLineId) && IsStableId(v.NewClipId),
            MergeOrchestrationClipsCommandDto v => v.Type == "orchestration.mergeClips" && IsStableId(v.BranchId) && IsStableId(v.TargetClipId) && List(v.ClipIds, 2) && v.ClipIds!.Contains(v.TargetClipId),
            _ => false,
        });
        if (!valid) return new(BackendErrorCode.InvalidRequest, "片段成员命令需要完整唯一稳定身份与成员，不接受其他目标或时间字段。", "command");
        return JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson).Length > StyleEditingLimits.MaxIntentBytes
            ? new(BackendErrorCode.ResourceTooLarge, "完整片段成员意图超过64KiB；工程未改变。", "command") : null;
    }

    private bool TryPrepareClipMembers(Project before, ProjectCommandDto command, out Project after,
        out OrchestrationClipMembersCommandResultDto? result, out BackendErrorResponse? error)
    {
        after = before; result = null; error = CheckClipMembersIntent(command);
        if (command is not (SetOrchestrationClipMembersCommandDto or SplitOrchestrationClipCommandDto or MergeOrchestrationClipsCommandDto)) return false;
        if (error is not null) return true;
        string branchId;
        string[] affected;
        string[] removed = [];
        ClipMembershipError? failure;
        try
        {
            GuardNodeCreationTree(before);
            GuardClipMembersReferences(before);
            bool accepted;
            switch (command)
            {
                case SetOrchestrationClipMembersCommandDto value:
                    branchId = value.BranchId!; affected = [value.ClipId!];
                    accepted = ClipMembershipOperation.TrySetMembers(before, branchId, value.ClipId!, value.LineIds!, out after, out failure); break;
                case SplitOrchestrationClipCommandDto value:
                    branchId = value.BranchId!; affected = [value.ClipId!, value.NewClipId!];
                    accepted = ClipMembershipOperation.TrySplit(before, branchId, value.ClipId!, value.SplitBeforeLineId!, value.NewClipId!, out after, out failure); break;
                default:
                    var merge = (MergeOrchestrationClipsCommandDto)command;
                    branchId = merge.BranchId!; affected = [merge.TargetClipId!]; removed = [.. merge.ClipIds!.Where(id => id != merge.TargetClipId)];
                    accepted = ClipMembershipOperation.TryMerge(before, branchId, merge.ClipIds!, merge.TargetClipId!, out after, out failure); break;
            }
            if (!accepted)
            { after = before; error = new(BackendErrorCode.CommandRejected, failure!.Detail, "command", FailureCode: failure.Code); return true; }
            bool changed = !ReferenceEquals(before, after);
            string revision = changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText;
            GuardNodeCreationTree(after);
            _ = BuildOrchestration(after, revision);
            ValidatePresetApplicationsProjection(after, revision);
            var program = after.LyricPresentation.WithDefaultPrograms(after.Lyrics).FindProgram(branchId)!;
            Project fixedCandidate = after;
            ReadModelBudget budget = new();
            result = new(command.Type["orchestration.".Length..], branchId,
                [.. affected.Select(id => ProjectClip(program.Clips.Single(clip => clip.Id == id), budget))], removed, changed,
                [.. fixedCandidate.PresetApplications.Items.Where(app => !before.PresetApplications.Items.Any(old => old.Id == app.Id)).Select(app => app.Id)],
                [.. before.PresetApplications.Items.Where(app => !fixedCandidate.PresetApplications.Items.Any(next => next.Id == app.Id)).Select(app => app.Id)]);
            ProjectChangeSetDto changes = changed ? new(["orchestration", "presetApplications", "renderInputs"], [], [], [], true) : new([], [], [], [], false);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, revision, changed, false, false, false,
                changed ? command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), ClipMembersResult: result);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException)
        { after = before; result = null; error = new(BackendErrorCode.ResourceTooLarge, "完整片段候选、读投影或回执超过预算；工程未改变。", "command"); }
        catch (InvalidNodeCreationTreeException)
        { after = before; result = null; error = new(BackendErrorCode.CommandRejected, "工程展示树存在失效或循环引用。", "command", FailureCode: "TargetConflict"); }
        return true;
    }

    private static void GuardClipMembersReferences(Project project)
    {
        ReadModelBudget budget = new();
        budget.Item(project.Effects.Instances.Length + project.PresetApplications.Items.Length);
        foreach (var instance in project.Effects.Instances)
        { budget.Text(instance.Id); budget.Item(instance.Binding.Ids.Length); foreach (string id in instance.Binding.Ids) budget.Text(id); }
        foreach (var application in project.PresetApplications.Items)
        {
            budget.Text(application.Id); budget.Item(application.Identities.Length + application.Snapshot.EffectRecipes.Length + application.Target.Ids.Length);
            foreach (string id in application.Target.Ids) budget.Text(id);
            foreach (var identity in application.Identities)
            { budget.Text(identity.InstanceId); budget.Item(identity.Binding.Ids.Length); foreach (string id in identity.Binding.Ids) budget.Text(id); }
        }
    }
}
