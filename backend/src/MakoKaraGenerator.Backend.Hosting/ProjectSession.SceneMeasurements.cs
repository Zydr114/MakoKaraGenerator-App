using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal sealed record SceneMeasurementCapture(Project Project, ProjectSnapshot Snapshot, string Revision,
        byte[] Source, string SourceSha256, BackendProjectRuntimeInputLease? RuntimeLease = null);

    internal async Task<(SceneMeasurementCapture? Capture, BackendErrorResponse? Error)> CaptureSceneMeasurementAsync(
        string expectedRevision, bool mutation, CancellationToken token, bool encodeSource = true)
    {
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (MutationBlocked) return (null, new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            if (mutation && IsReadOnly) return (null, new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。"));
            if (!TryParseRevision(expectedRevision, out long revision)) return (null, new(BackendErrorCode.InvalidRequest, "expectedRevision无效。", "expectedRevision"));
            if (revision != ProjectRevision) return (null, new(BackendErrorCode.RevisionConflict, "工程修订已经改变。", "expectedRevision"));
            var project = Workspace.Project;
            var snapshot = ProjectSnapshot.FromProject(project);
            var tree = snapshot.LyricPresentation;
            if (tree.Nodes.Length > ProjectGeometryReferenceLimits.MaxNodes || tree.Programs.Length > 1024 || tree.LineOverrides.Length > 20000)
                return (null, new(BackendErrorCode.ResourceTooLarge, "完整测量源超过预算。", FailureCode: "SceneMeasurementBudgetExceeded"));
            var nodes = tree.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            foreach (var node in tree.Nodes)
            {
                var current = node; HashSet<string> path = new(StringComparer.Ordinal);
                while (true)
                {
                    if (!path.Add(current.Id) || path.Count > ProjectGeometryReferenceLimits.MaxDepth)
                        return (null, new(BackendErrorCode.ResourceTooLarge, "测量树超过深度预算或包含循环。", FailureCode: "SceneMeasurementBudgetExceeded"));
                    if (current.ParentId is null) break;
                    if (!nodes.TryGetValue(current.ParentId, out current!)) return (null, new(BackendErrorCode.CommandRejected, "测量源父节点不存在。"));
                }
            }
            var projected = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
            if (projected.Nodes.Length > ProjectGeometryReferenceLimits.MaxNodes)
                return (null, new(BackendErrorCode.ResourceTooLarge, "投影测量树超过节点预算。", FailureCode: "SceneMeasurementBudgetExceeded"));
            var references = SceneTreeGeometryResolver.Resolve(projected.Nodes, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
            if (references.Values.Any(value => !ValidReference(value)))
                return (null, new(BackendErrorCode.CommandRejected, "测量源固定参考不可逆或非有限。", FailureCode: "SceneMeasurementGeometryInvalid"));
            byte[] source = [];
            return (new(project, snapshot, RevisionText, source, WorkerProtocol.ComputeSha256(source),
                AcquireProjectRuntimeInput(snapshot)), null);
        }
        catch (SnapshotContractException error)
        { return (null, new(BackendErrorCode.CommandRejected, "当前工程不支持静态测量。", FailureCode: error.Code)); }
        finally { Writer.Release(); }
    }

    internal async Task<ProjectCommandExecutionResult> CommitAttachmentAsync(SceneMeasurementCapture capture,
        SetLeadInAttachmentCommandDto command, WorkerAttachmentGeometryDto? geometry, CancellationToken token)
    {
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (MutationBlocked) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            if (IsReadOnly) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。"));
            if (RevisionText != capture.Revision || !ReferenceEquals(Workspace.Project, capture.Project))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "测量期间工程修订已经改变。", "expectedRevision"));
            if (!Enum.TryParse<SceneAttachmentMode>(command.Mode, out var mode) || !Enum.IsDefined(mode))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "附着模式无效。", "command.mode"));
            SceneTreeGeometry From(ProjectGeometryReferenceDto value) => new(value.Width, value.Height,
                new(value.WorldMatrix.A,value.WorldMatrix.B,value.WorldMatrix.C,value.WorldMatrix.D,value.WorldMatrix.Tx,value.WorldMatrix.Ty),new(value.Pivot.X,value.Pivot.Y));
            Project candidate = capture.Project;
            var node = capture.Project.LyricPresentation.FindNode(command.ContainerId!);
            if ((node?.Attachment?.Mode ?? SceneAttachmentMode.FixedParent) != mode)
            {
                if (geometry is null) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "附着真实参考不可用。"));
                var reference = mode == SceneAttachmentMode.FixedParent ? geometry.FixedParent : geometry.TargetText;
                if (!LeadInEditor.TrySetAttachment(capture.Project, command.ContainerId!, mode, From(geometry.Previous), From(reference), out candidate, out _))
                    return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "附着参考不可用或变换无效。", FailureCode: "SceneAttachmentReferenceUnavailable"));
            }
            bool changed = !ReferenceEquals(candidate, capture.Project);
            string next = changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText;
            var changes = changed ? new ProjectChangeSetDto(["orchestration", "renderInputs"], [], [], [], true) : new([], [], [], [], false);
            var feedback = new OrchestrationPropertiesCommandResultDto("setLeadInAttachment", command.ContainerId, command.BranchId, changed);
            try
            {
                _ = BuildOrchestration(candidate, next);
                var upper = new ProjectCommandResponse(ProjectSessionId, capture.Revision, next, changed, true, true, true,
                    command.Type, command.Type, changes, EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue,
                    PropertiesResult: feedback);
                if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                    throw new ReadModelBudgetExceededException();
            }
            catch (ReadModelBudgetExceededException)
            { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ResourceTooLarge, "完整编排读模型或回执超过预算。")); }
            token.ThrowIfCancellationRequested();
            if (changed)
            {
                Workspace.History.BreakMergeGroup();
                Workspace.Execute(ProjectCommand.Create(command.Type, _ => candidate));
                Workspace.History.BreakMergeGroup(); ProjectRevision = checked(ProjectRevision + 1);
            }
            string cursor = changed ? PublishEvent("project.changed", changes) : _events.Cursor(OwnerClientId);
            return ProjectCommandExecutionResult.FromResponse(new(ProjectSessionId, capture.Revision, RevisionText, changed,
                Workspace.IsDirty, Workspace.CanUndo, Workspace.CanRedo, Workspace.UndoName, Workspace.RedoName, changes,
                EventCursor: cursor, PropertiesResult: feedback));
        }
        finally { Writer.Release(); }
    }
}
