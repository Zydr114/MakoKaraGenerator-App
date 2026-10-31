using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private static bool TryExecuteClipCommand(Project before, ProjectCommandDto command,
        out Project after, out ProjectChangeSetDto? changeSet,
        out OrchestrationClipCommandResultDto? clipResult, out BackendErrorResponse? error)
    {
        after = before;
        changeSet = null;
        clipResult = null;
        error = null;
        if (command.Type is not ("orchestration.createClip" or "orchestration.renameClip"
            or "orchestration.setClipContainer" or "orchestration.deleteClip")) return false;

        var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
        string? branchId = command switch
        {
            CreateOrchestrationClipCommandDto ccreate => ccreate.BranchId,
            RenameOrchestrationClipCommandDto rename => rename.BranchId,
            SetOrchestrationClipContainerCommandDto move => move.BranchId,
            DeleteOrchestrationClipCommandDto delete => delete.BranchId,
            _ => null,
        };
        string? clipId = command switch
        {
            CreateOrchestrationClipCommandDto ccreate => ccreate.ClipId,
            RenameOrchestrationClipCommandDto rename => rename.ClipId,
            SetOrchestrationClipContainerCommandDto move => move.ClipId,
            DeleteOrchestrationClipCommandDto delete => delete.ClipId,
            _ => null,
        };
        if (!IsStableId(branchId) || !IsStableId(clipId))
        {
            error = InvalidClipCommand("片段命令需要非空、长度受限的 branchId 和 clipId。", "command");
            return true;
        }
        var branch = before.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
        var program = tree.FindProgram(branchId!);
        if (branch is null || program is null)
        {
            error = ClipRejected("来源分支或展示程序不存在。", "command.branchId", "SourceMissing");
            return true;
        }
        if (branch.TimingMode != LyricBranchTimingMode.Independent || EffectivePolicy(program, branch) != LyricBranchAnimationPolicy.Karaoke)
        {
            error = ClipRejected("片段仅支持独立计时的逐字歌词来源。", "command.branchId", "UnsupportedSource");
            return true;
        }

        LyricDisplayClip? existing = program.Clips.FirstOrDefault(item => item.Id == clipId);
        if (command is CreateOrchestrationClipCommandDto create)
        {
            if (tree.Programs.SelectMany(item => item.Clips).Any(item => item.Id == clipId))
            {
                error = ClipRejected("展示片段身份已存在。", "command.clipId", "ClipExists");
                return true;
            }
            string? rangeCode = null;
            if (create.LineIds is not { Length: > 0 and <= 1000 } lineIds || !TryValidateRange(branch, lineIds, out rangeCode))
            {
                error = ClipRejected(rangeCode == "DiscontiguousRange" ? "片段成员必须按来源顺序连续。" : "片段成员必须是当前来源中不重复的有效行。",
                    "command.lineIds", rangeCode ?? "InvalidRange");
                return true;
            }
            if (!TryValidateContainer(tree, program, create.ContainerId, out BackendErrorResponse? containerError))
            {
                error = containerError;
                return true;
            }
            LyricDisplayClip clip = new() { Id = clipId!, Name = create.Name, LineIds = [.. lineIds], ContainerId = create.ContainerId! };
            if (!SceneTreeEditor.TrySetClip(before, branchId!, clip, out Project candidate, out var report))
            {
                error = ClipValidationError(report, "command");
                return true;
            }
            after = candidate;
            clipResult = new(branchId!, clip.Id, clip.Name, clip.LineIds.ToArray(), clip.ContainerId, !ReferenceEquals(before, after));
        }
        else
        {
            if (existing is null)
            {
                error = ClipRejected("展示片段不存在。", "command.clipId", "TargetStale");
                return true;
            }
            LyricDisplayClip next = existing;
            string field = "command";
            switch (command)
            {
                case RenameOrchestrationClipCommandDto rename:
                    next = existing with { Name = rename.Name };
                    field = "command.name";
                    break;
                case SetOrchestrationClipContainerCommandDto move:
                    if (!TryValidateContainer(tree, program, move.ContainerId, out BackendErrorResponse? containerError))
                    {
                        error = containerError;
                        return true;
                    }
                    next = existing with { ContainerId = move.ContainerId! };
                    field = "command.containerId";
                    break;
                case DeleteOrchestrationClipCommandDto:
                    if (!SceneTreeEditor.TryDeleteClip(before, branchId!, clipId!, out Project deleted, out _))
                    {
                        error = ClipRejected("展示片段不存在。", "command.clipId", "TargetStale");
                        return true;
                    }
                    after = deleted;
                    clipResult = new(branchId!, existing.Id, existing.Name, existing.LineIds.ToArray(), existing.ContainerId, true);
                    break;
            }
            if (command is not DeleteOrchestrationClipCommandDto)
            {
                if (!SceneTreeEditor.TrySetClip(before, branchId!, next, out Project candidate, out var report))
                {
                    error = ClipValidationError(report, field);
                    return true;
                }
                after = candidate;
                clipResult = new(branchId!, next.Id, next.Name, next.LineIds.ToArray(), next.ContainerId, !ReferenceEquals(before, after));
            }
        }

        if (ReferenceEquals(before, after))
        {
            changeSet = new([], [], [], [], false);
            return true;
        }
        // A HTTP command is one complete history step. Do not let it merge with an
        // adjacent desktop edit, and preserve the validated immutable candidate.
        return true;
    }

    private static LyricBranchAnimationPolicy EffectivePolicy(LyricDisplayProgram program, LyricBranch branch) =>
        program.AnimationPolicy ?? (branch.Role == LyricBranchRole.Primary
            ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static);

    private static bool TryValidateRange(LyricBranch branch, string[] ids, out string? failureCode)
    {
        failureCode = null;
        if (ids.Any(id => !IsStableId(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            failureCode = "InvalidRange";
            return false;
        }
        var ordered = branch.Lines.Where(line => ids.Contains(line.Id, StringComparer.Ordinal)).ToArray();
        if (ordered.Length != ids.Length)
        {
            failureCode = "InvalidRange";
            return false;
        }
        if (!ids.SequenceEqual(ordered.Select(line => line.Id), StringComparer.Ordinal))
        {
            failureCode = "DiscontiguousRange";
            return false;
        }
        int first = branch.Lines.IndexOf(ordered[0]);
        if (first < 0 || branch.Lines.IndexOf(ordered[^1]) - first + 1 != ordered.Length)
        {
            failureCode = "DiscontiguousRange";
            return false;
        }
        if (ordered.Any(line => line.Time is not { IsValid: true }))
        {
            failureCode = "InvalidRange";
            return false;
        }
        return true;
    }

    private static bool TryValidateContainer(LyricPresentationDefinition tree, LyricDisplayProgram program,
        string? containerId, out BackendErrorResponse? error)
    {
        error = null;
        SceneContainerDefinition? container = IsStableId(containerId) ? tree.FindNode(containerId!) : null;
        if (container is null || container.Layout is null)
        {
            error = ClipRejected("目标容器必须是当前工程中存在的歌词容器。", "command.containerId", "ContainerInvalid");
            return false;
        }
        if (!Under(tree, container.Id, program.RootContainerId))
        {
            error = ClipRejected("目标容器必须属于当前来源展示根。", "command.containerId", "ContainerInvalid");
            return false;
        }
        return true;
    }

    private static bool Under(LyricPresentationDefinition tree, string id, string root)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (tree.FindNode(id) is { } node && visited.Add(id))
        {
            if (id == root) return true;
            if (node.ParentId is null) return false;
            id = node.ParentId;
        }
        return false;
    }

    private static BackendErrorResponse InvalidClipCommand(string message, string field) =>
        new(BackendErrorCode.InvalidRequest, message, field);

    private static BackendErrorResponse ClipRejected(string message, string field, string failureCode) =>
        new(BackendErrorCode.CommandRejected, message, field, FailureCode: failureCode);

    private static BackendErrorResponse ClipValidationError(MakoKaraGenerator.Core.Diagnostics.ValidationReport report, string field)
    {
        string failure = report.Contains(MakoKaraGenerator.Core.Diagnostics.DiagnosticCode.PresentationTimeConflict)
            ? "ClipScheduleConflict" : "TargetConflict";
        return ClipRejected("片段未通过工程展示校验。", field, failure);
    }
}
