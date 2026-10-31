using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private sealed record PreparedEdits(ProjectSession.EditCandidateCapture Capture, ProjectChangeSetDto Changes,
        ProjectEditResultDto[] Results);

    /// <summary>复用单命令的原解释器，在独立工作区顺序准备编辑候选。</summary>
    public async Task<BackendPreviewInputCaptureResult> CaptureEditPreviewInputAsync(string clientId, string sessionId,
        ProjectEditPreviewRequest request, IRenderFrameSessionFactory factory,
        Func<string, IRenderFrameSessionFactory>? captureFactory = null, CancellationToken cancellationToken = default)
    {
        if (!TryGet(clientId, sessionId, out var session)) return new(null, MissingSessionError(sessionId));
        IRenderFrameSessionFactory fixedFactory = factory;
        var prepared = await PrepareEditsAsync(clientId, session, request.ExpectedRevision, request.Edits, false,
            audio => fixedFactory = captureFactory?.Invoke(audio) ?? factory, cancellationToken).ConfigureAwait(false);
        if (prepared.Error is { } error) return new(null, error);
        var work = prepared.Prepared!;
        try
        {
            ProjectSnapshot snapshot = ProjectSnapshot.FromProject(work.Capture.Candidate.Workspace.Project);
            var runtime = await session.CaptureCandidateRuntimeInputAsync(clientId, work.Capture.Revision, snapshot,
                () => TryGet(clientId, sessionId, out var current) && ReferenceEquals(current, session), cancellationToken).ConfigureAwait(false);
            if (runtime.Error is not null) return new(null, runtime.Error);
            string revision = "candidate-" + Guid.NewGuid().ToString("N");
            return new(new(new("editCandidate", sessionId, revision), snapshot, fixedFactory)
            { CapturedProjectSession = session, EditCandidate = new(sessionId, work.Capture.Revision.ToString(CultureInfo.InvariantCulture), revision, work.Changes, work.Results) },
                null, RuntimeInput: runtime.Lease);
        }
        finally { work.Capture.ImageSource?.Dispose(); }
    }

    private async Task<ProjectCommandExecutionResult> ExecuteApplyEditsAsync(string clientId, ProjectSession session,
        ProjectCommandRequest request)
    {
        if (request.Command is not ApplyProjectEditsCommandDto command)
            return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "批量编辑命令类型无效。", "command"));
        var work = await PrepareEditsAsync(clientId, session, request.ExpectedRevision, command.Edits, true, null, CancellationToken.None).ConfigureAwait(false);
        if (work.Error is { } error) return ProjectCommandExecutionResult.Rejected(error);
        if (!TryGet(clientId, session.ProjectSessionId, out var registered) || !ReferenceEquals(session, registered))
            return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionNotFound, "工程会话已经关闭。"));
        return await session.CommitEditCandidateAsync(work.Prepared!.Capture, work.Prepared.Changes, work.Prepared.Results, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<(PreparedEdits? Prepared, BackendErrorResponse? Error)> PrepareEditsAsync(string clientId,
        ProjectSession session, string expectedRevision, ProjectCommandDto[]? edits, bool mutation,
        Func<string, IRenderFrameSessionFactory>? captureFactory, CancellationToken cancellationToken)
    {
        if (edits is null || edits.Any(edit => edit is null)) return (null, new(BackendErrorCode.InvalidRequest, "edits 必须是具名编辑对象数组。", "edits"));
        foreach (var edit in edits)
            if (edit.Type is "project.applyEdits" or "history.undo" or "history.redo" or "audio.replaceAsset" or "audio.clear"
                or "audio.analysis.apply" or "lyrics.importAsset" or "composition.setBackgroundAsset")
                return (null, new(BackendErrorCode.CommandNotSupported, "编辑草稿不接受资源导入、外部任务、历史或嵌套批处理。", "edits"));
        edits = edits.Select(edit => ProjectSession.CanonicalCommand(new("0", edit)).Command).ToArray();
        var captured = await session.CaptureEditCandidateAsync(clientId, expectedRevision, mutation, captureFactory, cancellationToken).ConfigureAwait(false);
        if (captured.Error is { } captureError) return (null, captureError);
        var capture = captured.Capture!;
        bool transferred = false;
        try
        {
            if (EditPreparationCheckpoint is { } checkpoint) await checkpoint().ConfigureAwait(false);
            List<ProjectChangeSetDto> changes = [];
            List<ProjectEditResultDto> results = [];
            for (int index = 0; index < edits.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectCommandDto edit = edits[index];
                if (ValidateCommandIntent(edit) is { } intentError) return (null, intentError with { Field = "edits[" + index + "]." + intentError.Field });
                ProjectCommandRequest input = new(capture.Candidate.ProjectRevision.ToString(CultureInfo.InvariantCulture), edit);
                var result = await ExecuteDetachedEditAsync(clientId, capture.Candidate, input, session, capture.Revision).ConfigureAwait(false);
                if (!result.Accepted) return (null, result.Error! with { Field = "edits[" + index + "]." + result.Error!.Field });
                var response = result.Response!;
                changes.Add(response.ChangeSet);
                results.Add(new(index, edit.Type, response.Changed, response.TimingResult, response.Feedback,
                    response.BranchResult, response.TokenResult, response.ImportResult, response.StyleResult,
                    response.PresetApplicationResult, response.OrchestrationResult, response.LayoutResult, response.StructureResult,
                    response.BasicLayoutResult, response.ClipResult, response.LeadInResult, response.LineLocalTransformResult,
                    response.PropertiesResult, response.LeadInGenerationResult, response.ClipMembersResult, response.RubyResult));
            }
            if (JsonSerializer.SerializeToUtf8Bytes(results).Length > Receipts.MaxReceiptBytes)
                return (null, new(BackendErrorCode.ResourceTooLarge, "完整编辑反馈超过命令回执预算。", "edits"));
            var merged = MergeEditChanges(changes);
            transferred = true;
            return (new(capture, merged, [.. results]), null);
        }
        finally { if (!transferred) capture.ImageSource?.Dispose(); }
    }

    private async Task<ProjectCommandExecutionResult> ExecuteDetachedEditAsync(string clientId, ProjectSession candidate, ProjectCommandRequest input,
        ProjectSession sourceSession, long sourceRevision)
    {
        if (input.Command is AppendTextCommandDto) return await ExecuteImportCommandAsync(clientId, candidate, input).ConfigureAwait(false);
        if (input.Command is SetLeadInAttachmentCommandDto)
            return await ExecuteLeadInAttachmentAsync(clientId, candidate, input, sourceSession, sourceRevision).ConfigureAwait(false);
        if (input.Command is ClearBackgroundCommandDto or SetBackgroundFitCommandDto or SetBackgroundPlaybackCommandDto)
        {
            var captured = await candidate.CaptureBackgroundCommandAsync(input).ConfigureAwait(false);
            if (captured.Error is { } error) return ProjectCommandExecutionResult.Rejected(error);
            // Pure background edits share the original interpreter. Media ownership stays with
            // the source session; a detached candidate must never acquire or release its resources.
            return await candidate.CommitBackgroundCommandAsync(input, captured.Capture!,
                () => sourceSession.OwnerClientId == clientId && TryGet(clientId, sourceSession.ProjectSessionId, out var current)
                    && ReferenceEquals(current, sourceSession),
                _ => throw new InvalidOperationException("候选背景编辑不能解析外部资源。"),
                _ => throw new InvalidOperationException("候选背景编辑不能取得外部资源租约。")).ConfigureAwait(false);
        }
        StyleTemplate? template = null;
        if (input.Command is ApplyPresetApplicationCommandDto or UpdatePresetApplicationCommandDto)
        {
            if (!BeginTemplateOperation()) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "模板库正在关闭。"));
            try
            {
                await _templateIo.WaitAsync().ConfigureAwait(false);
                try { template = CaptureAssignmentTemplate(input.Command); }
                finally { _templateIo.Release(); }
            }
            finally { EndTemplateOperation(); }
        }
        return await candidate.ExecuteCommandAsync(input, CancellationToken.None, resolveTemplate: _ => template).ConfigureAwait(false);
    }

    private static ProjectChangeSetDto MergeEditChanges(List<ProjectChangeSetDto> edits)
    {
        static string[] Merge(IEnumerable<string[]> values) => [.. values.SelectMany(value => value).Distinct(StringComparer.Ordinal)];
        var merged = new ProjectChangeSetDto(Merge(edits.Select(edit => edit.Areas)), Merge(edits.Select(edit => edit.BranchIds)),
            Merge(edits.Select(edit => edit.LineIds)), Merge(edits.Select(edit => edit.TokenIds)), edits.Any(edit => edit.RequiresFullRefresh));
        if (JsonSerializer.SerializeToUtf8Bytes(merged).Length > ProjectCommandLimits.MaxChangeSetBytes)
            merged = merged with { BranchIds = [], LineIds = [], TokenIds = [], RequiresFullRefresh = true };
        return merged;
    }
}
