using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static BackendErrorResponse? CheckLineLocalTransformIntent(ProjectCommandDto command)
    {
        if (command is not SetLineLocalTransformCommandDto value) return null;
        if (value.Target is not null || value.StartUs is not null || value.EndUs is not null || value.AnalysisJobId is not null)
            return new(BackendErrorCode.InvalidRequest, "本句微调不接受时间或其他目标字段。", "command");
        if (value.Transform is { } input && (!double.IsFinite(input.OffsetXRatio) || !double.IsFinite(input.OffsetYRatio)
            || !double.IsFinite(input.RotationDegrees) || !double.IsFinite(input.Scale) || input.Scale <= 0))
            return new(BackendErrorCode.InvalidRequest, "微调参数必须有限，scale 必须大于零。", "command.transform");
        return null;
    }

    private static bool TryExecuteLineLocalTransformCommand(Project before, ProjectCommandDto command,
        out Project after, out LineLocalTransformCommandResultDto? result, out BackendErrorResponse? error)
    {
        after = before; result = null; error = null;
        if (command is not SetLineLocalTransformCommandDto value) return false;
        if (!IsStableId(value.BranchId) || !IsStableId(value.LineId))
        {
            error = new(BackendErrorCode.InvalidRequest, "需要稳定 branchId 和 lineId。", "command");
            return true;
        }
        LyricLocalTransform? transform = value.Transform is { } input ? new()
        {
            OffsetXRatio = input.OffsetXRatio, OffsetYRatio = input.OffsetYRatio,
            RotationDegrees = input.RotationDegrees, Scale = input.Scale,
        } : null;
        if (transform is { IsValid: false })
        {
            error = new(BackendErrorCode.InvalidRequest, "微调参数必须有限，scale 必须大于零。", "command.transform");
            return true;
        }
        if (!HasLocalTransformTarget(before, value.BranchId!, value.LineId!))
        {
            error = new(BackendErrorCode.CommandRejected, "本句微调目标不存在于指定来源。", "command.lineId", FailureCode: "TargetStale");
            return true;
        }
        var current = before.LyricPresentation.FindLine(value.LineId!);
        // Avoid materializing implicit programs or recording a history entry for a no-op.
        if (current?.LocalTransform != transform)
        {
            if (!SceneTreeEditor.TrySetLocalTransform(before, value.BranchId!, value.LineId!, transform,
                out after, out _))
            {
                after = before;
                error = new(BackendErrorCode.CommandRejected, "本句微调未通过完整展示校验。", "command.transform", FailureCode: "PresentationInvalid");
                return true;
            }
        }
        result = new(value.BranchId!, value.LineId!, value.Transform, !ReferenceEquals(before, after));
        return true;
    }

    internal Task<BackendOperationResult<ProjectLineLocalTransformResponse>> ReadLineLocalTransformAsync(
        string? expectedRevision, string? branchId, string? lineId, CancellationToken cancellationToken) =>
        ReadModelAsync(expectedRevision, () =>
        {
            if (!IsStableId(branchId) || !IsStableId(lineId))
                return BackendOperationResult<ProjectLineLocalTransformResponse>.Rejected(new(BackendErrorCode.InvalidRequest,
                    "需要稳定 branchId 和 lineId。", "query"));
            Project project = Workspace.Project;
            if (!HasLocalTransformTarget(project, branchId!, lineId!))
                return BackendOperationResult<ProjectLineLocalTransformResponse>.Rejected(new(BackendErrorCode.CommandRejected,
                    "本句微调目标不存在于指定来源。", "lineId", FailureCode: "TargetStale"));
            var transform = project.LyricPresentation.FindLine(lineId!)?.LocalTransform;
            return BoundedReadModel(new ProjectLineLocalTransformResponse(ProjectSessionId, RevisionText, branchId!, lineId!,
                transform is null ? null : new(transform.OffsetXRatio, transform.OffsetYRatio, transform.RotationDegrees, transform.Scale)));
        }, cancellationToken, "lineLocalTransform");

    private static bool HasLocalTransformTarget(Project project, string branchId, string lineId) =>
        project.Lyrics.Branches.Any(branch => branch.Id == branchId && branch.Lines.Any(line => line.Id == lineId))
        && (project.LyricPresentation.FindLine(lineId) is not { } current || current.BranchId == branchId);
}
