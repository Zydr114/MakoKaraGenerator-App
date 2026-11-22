using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal sealed record EditCandidateCapture(Project Before, long Revision, ProjectSession Candidate);

    internal async Task<(EditCandidateCapture? Capture, BackendErrorResponse? Error)> CaptureEditCandidateAsync(
        string client, string expectedRevision, bool mutation, Func<string, IRenderFrameSessionFactory>? captureFactory,
        CancellationToken cancellationToken)
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OwnerClientId != client) return (null, new(BackendErrorCode.SessionNotFound, "工程会话不存在。"));
            if (MutationBlocked) return (null, new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            if (mutation && IsReadOnly) return (null, new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。"));
            if (!TryParseRevision(expectedRevision, out long revision))
                return (null, new(BackendErrorCode.InvalidRequest, "expectedRevision 必须是非负十进制修订。", "expectedRevision"));
            if (revision != ProjectRevision) return (null, new(BackendErrorCode.RevisionConflict, "工程修订已经改变。", "expectedRevision"));
            ProjectSession detached = new(ProjectSessionId, HostInstanceId, OwnerClientId,
                ProjectWorkspace.Adopt(Workspace.Project, Workspace.DirectoryPath), AudioAssetId, AudioDurationUs,
                _timeProvider, new() { MaxReceiptBytes = _branchReceiptMaxBytes }, new(HostInstanceId, 1, _timeProvider));
            // These references belong to the original session. The detached workspace never owns or disposes them.
            _ = captureFactory?.Invoke(AudioAssetId);
            return (new(Workspace.Project, revision, detached), null);
        }
        finally { Writer.Release(); }
    }

    internal async Task<(BackendProjectRuntimeInputLease? Lease, BackendErrorResponse? Error)> CaptureCandidateRuntimeInputAsync(
        string clientId, long revision, ProjectSnapshot snapshot, Func<bool> registered, CancellationToken token)
    {
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (OwnerClientId != clientId || !registered()) return (null, new(BackendErrorCode.SessionNotFound, "工程会话已经关闭。"));
            if (MutationBlocked) return (null, new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            if (ProjectRevision != revision) return (null, new(BackendErrorCode.RevisionConflict, "候选准备期间工程修订已经改变。", "expectedRevision"));
            return (AcquireProjectRuntimeInput(snapshot), null);
        }
        finally { Writer.Release(); }
    }

    internal async Task<ProjectCommandExecutionResult> CommitEditCandidateAsync(EditCandidateCapture capture,
        ProjectChangeSetDto changes, ProjectEditResultDto[] results, CancellationToken cancellationToken)
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (MutationBlocked) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            if (IsReadOnly) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。"));
            if (ProjectRevision != capture.Revision || !ReferenceEquals(Workspace.Project, capture.Before))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "候选准备期间工程修订已经改变。", "expectedRevision"));
            Project candidate = capture.Candidate.Workspace.Project;
            bool changed = !capture.Before.Equals(candidate);
            string previous = RevisionText;
            string next = changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : previous;
            ProjectChangeSetDto actualChanges = changed ? changes : new([], [], [], [], false);
            var upper = new ProjectCommandResponse(ProjectSessionId, previous, next, changed, true, true, true,
                "project.applyEdits", "project.applyEdits", actualChanges,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue, EditResults: results);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ResourceTooLarge, "完整编辑反馈超过命令回执预算。", "command.edits"));
            cancellationToken.ThrowIfCancellationRequested();
            if (changed)
            {
                Workspace.History.BreakMergeGroup();
                Workspace.Execute(ProjectCommand.Create("project.applyEdits", _ => candidate));
                Workspace.History.BreakMergeGroup();
                ProjectRevision = checked(ProjectRevision + 1);
            }
            string cursor = changed ? PublishEvent("project.changed", actualChanges) : _events.Cursor(OwnerClientId);
            return ProjectCommandExecutionResult.FromResponse(new(ProjectSessionId, previous, next, changed,
                Workspace.IsDirty, Workspace.CanUndo, Workspace.CanRedo, Workspace.UndoName, Workspace.RedoName,
                actualChanges, EventCursor: cursor, EditResults: results));
        }
        finally { Writer.Release(); }
    }
}
