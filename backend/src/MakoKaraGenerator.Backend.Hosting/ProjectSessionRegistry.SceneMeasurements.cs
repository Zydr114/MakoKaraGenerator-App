using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private WorkerProcessOptions? _sceneMeasurementWorker;
    private readonly object _sceneMeasurementGate = new();
    private readonly Dictionary<string, TaskCompletionSource> _sceneMeasurementClients = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _sceneMeasurementShutdown = new();
    private bool _sceneMeasurementStopping;
    /// <summary>只配置受管理Worker；不回退到Host native。</summary>
    public void ConfigureSceneMeasurements(WorkerProcessOptions? worker) => _sceneMeasurementWorker = worker;

    /// <summary>固定精确工程修订，在独立受管理Worker中产生一次有界同帧测量。</summary>
    public Task<BackendOperationResult<SceneMeasurementResponse>> MeasureSceneAsync(string clientId, string sessionId,
        SceneMeasurementRequest request, CancellationToken cancellationToken = default) =>
        MeasureSceneAsync(clientId, sessionId, request, null, cancellationToken);

    /// <summary>测量当前工程或已固定的编辑候选；候选路径只读预览快照，不写工程。</summary>
    public async Task<BackendOperationResult<SceneMeasurementResponse>> MeasureSceneAsync(string clientId, string sessionId,
        SceneMeasurementRequest request, BackendPreviewSessionRegistry? previews,
        CancellationToken cancellationToken = default)
    {
        try { SceneMeasurementContract.ValidateRequest(request); }
        catch (ArgumentException) { return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.InvalidRequest, "测量请求无效。")); }
        ProjectSession session;
        await _membershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0) return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.SessionClosing, "宿主正在停止。"));
            if (!TryGet(clientId, sessionId, out session!)) return BackendOperationResult<SceneMeasurementResponse>.Rejected(MissingSessionError(sessionId));
            if (_sceneMeasurementWorker is null) return BackendOperationResult<SceneMeasurementResponse>.Rejected(MeasurementUnavailable());
            if (!ReserveMeasurement(clientId)) return BackendOperationResult<SceneMeasurementResponse>.Rejected(MeasurementBusy());
        }
        finally { _membershipGate.Release(); }
        IDisposable? candidateReference = null;
        BackendProjectRuntimeInputLease? runtimeLease = null;
        BackendPreparedProjectRuntimeInput? preparedInput = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sceneMeasurementShutdown.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            ProjectSession.SceneMeasurementCapture capture;
            string? candidateBackend = null;
            if (request.PreviewId is { } previewId)
            {
                if (previews is null)
                    return BackendOperationResult<SceneMeasurementResponse>.Rejected(MeasurementUnavailable());
                BackendOperationResult<BackendPreviewPinnedInput> pinned = previews.ReadEditCandidateInput(clientId, sessionId,
                    previewId, request.ExpectedPreviewGeneration!, request.CandidateRevision!);
                if (!pinned.Accepted) return BackendOperationResult<SceneMeasurementResponse>.Rejected(pinned.Error!);
                candidateReference = pinned.Response!.RetentionLease;
                candidateBackend = pinned.Response.RenderBackend;
                if (pinned.Response.EditCandidate!.BaseProjectRevision != request.ExpectedRevision)
                    return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.RevisionConflict,
                        "候选基础工程修订已经改变。", "expectedRevision"));
                try
                {
                    byte[] source = pinned.Response.PreparedInput?.SourceBytes.ToArray() ?? ProjectSnapshotSourceProfile.Encode(pinned.Response.Snapshot);
                    capture = new(session.Workspace.Project, pinned.Response.Snapshot, pinned.Response.EditCandidate.CandidateRevision,
                        source, WorkerProtocol.ComputeSha256(source));
                }
                catch (SnapshotContractException error)
                {
                    return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(
                        error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandRejected,
                        "候选预览固定来源不支持场景测量。", "previewId", FailureCode: error.Code));
                }
            }
            else
            {
                var captured = await session.CaptureSceneMeasurementAsync(request.ExpectedRevision, false, deadline.Token).ConfigureAwait(false);
                if (captured.Error is { } error) return BackendOperationResult<SceneMeasurementResponse>.Rejected(error);
                capture = captured.Capture!;
                runtimeLease = capture.RuntimeLease;
                var preparation = await runtimeLease!.PrepareAsync(deadline.Token).ConfigureAwait(false);
                if (!preparation.Accepted) return BackendOperationResult<SceneMeasurementResponse>.Rejected(preparation.Error!);
                preparedInput = preparation.Response!;
                byte[] source = preparedInput.SourceBytes.ToArray();
                capture = capture with { Source = source, SourceSha256 = WorkerProtocol.ComputeSha256(source) };
            }
            var selection = await SelectMeasurementRenderBackendAsync(request.RenderBackend, candidateBackend, deadline.Token).ConfigureAwait(false);
            if (!selection.Accepted) return BackendOperationResult<SceneMeasurementResponse>.Rejected(selection.Error!);
            WorkerProcessOptions workerOptions = selection.Response!.Executor;
            request = request with { RenderBackend = selection.Response.RenderBackend };
            Task<WorkerSceneMeasurementOutput> measurement;
            if (request.PreviewId is null)
            {
                await session.Writer.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    if (!TryGet(clientId, sessionId, out var current) || !ReferenceEquals(current, session) || session.OwnerClientId != clientId)
                        return BackendOperationResult<SceneMeasurementResponse>.Rejected(MissingSessionError(sessionId));
                    if (session.MutationBlocked) return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
                    if (session.ProjectRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) != request.ExpectedRevision
                        || !ReferenceEquals(session.Workspace.Project, capture.Project))
                        return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.RevisionConflict, "固定媒体准备期间工程修订已经改变。", "expectedRevision"));
                    measurement = MeasureCapturedAsync(capture, request, null, null, null, workerOptions, deadline.Token);
                }
                finally { session.Writer.Release(); }
            }
            else measurement = MeasureCapturedAsync(capture, request, null, null, null, workerOptions, deadline.Token);
            var measured = await measurement.ConfigureAwait(false);
            var response = new SceneMeasurementResponse(sessionId, capture.Revision, measured.Measurement);
            _ = SceneMeasurementContract.EncodeBounded(response);
            return BackendOperationResult<SceneMeasurementResponse>.Success(response);
        }
        catch (OperationCanceledException) when (_sceneMeasurementShutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.SessionClosing, "宿主正在停止。", FailureCode: "SceneMeasurementStopping")); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.TaskUnavailable, "测量超过20秒期限。", FailureCode: "SceneMeasurementDeadlineExceeded")); }
        catch (WorkerProtocolException error) { return BackendOperationResult<SceneMeasurementResponse>.Rejected(MeasurementFailure(error)); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return BackendOperationResult<SceneMeasurementResponse>.Rejected(new(BackendErrorCode.TaskUnavailable, "受管理测量Worker不可用。", FailureCode: "SceneMeasurementWorkerFailed")); }
        finally { ReleaseMeasurementInputs(clientId, preparedInput, runtimeLease, candidateReference); }
    }

    private async Task<ProjectCommandExecutionResult> ExecuteLeadInAttachmentAsync(string clientId, ProjectSession session, ProjectCommandRequest request,
        ProjectSession? sourceSession = null, long sourceRevision = 0)
    {
        if (request.Command is not SetLeadInAttachmentCommandDto command || command.Mode is not ("FixedParent" or "CueTargetText" or "CueTargetAboveStart")
            || string.IsNullOrWhiteSpace(command.BranchId) || string.IsNullOrWhiteSpace(command.LeadInId) || string.IsNullOrWhiteSpace(command.ContainerId))
            return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "附着命令需要明确分支、前奏、容器及模式。"));
        if (!ReserveMeasurement(clientId)) return ProjectCommandExecutionResult.Rejected(MeasurementBusy());
        BackendProjectRuntimeInputLease? runtimeLease = null;
        BackendPreparedProjectRuntimeInput? preparedInput = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_sceneMeasurementShutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var captured = await session.CaptureSceneMeasurementAsync(request.ExpectedRevision!, true, deadline.Token, encodeSource: false).ConfigureAwait(false);
            if (captured.Error is { } error) return ProjectCommandExecutionResult.Rejected(error);
            var capture = captured.Capture!;
            runtimeLease = capture.RuntimeLease;
            var tree = capture.Project.LyricPresentation.WithDefaultPrograms(capture.Project.Lyrics);
            var program = tree.FindProgram(command.BranchId);
            if (program is null)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "来源不存在。", "command.branchId", FailureCode: "TargetStale"));
            var cue = program.LeadIns.FirstOrDefault(cue => cue.Id == command.LeadInId);
            if (cue is null)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "前奏不存在于指定来源。", "command.leadInId", FailureCode: "TargetStale"));
            var group = tree.FindNode(command.ContainerId);
            if (group is null || group.Content is not CountdownSequenceContent || cue.IndicatorContainerId != command.ContainerId)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "容器必须是此前奏的提示组。", "command.containerId", FailureCode: "ContainerInvalid"));
            if ((group.Attachment?.Mode ?? SceneAttachmentMode.FixedParent).ToString() == command.Mode)
                return await session.CommitAttachmentAsync(capture, command, null, deadline.Token).ConfigureAwait(false);
            if (_sceneMeasurementWorker is null) return ProjectCommandExecutionResult.Rejected(MeasurementUnavailable());
            if (sourceSession is not null)
            {
                // Detached edits borrow authorization from the live source session;
                // its private media directory and history references stay owned there.
                runtimeLease?.Dispose(); runtimeLease = null;
                var pinned = await sourceSession.CaptureCandidateRuntimeInputAsync(clientId, sourceRevision, capture.Snapshot,
                    () => TryGet(clientId, sourceSession.ProjectSessionId, out var current) && ReferenceEquals(current, sourceSession),
                    deadline.Token).ConfigureAwait(false);
                if (pinned.Error is not null) return ProjectCommandExecutionResult.Rejected(pinned.Error);
                runtimeLease = pinned.Lease;
            }
            var preparation = await runtimeLease!.PrepareAsync(deadline.Token).ConfigureAwait(false);
            if (!preparation.Accepted) return ProjectCommandExecutionResult.Rejected(preparation.Error!);
            preparedInput = preparation.Response!;
            byte[] source = preparedInput.SourceBytes.ToArray();
            capture = capture with { Source = source, SourceSha256 = WorkerProtocol.ComputeSha256(source) };
            var input = new SceneMeasurementRequest(capture.Revision, "time", "0", 64, 64, "transparent");
            var selection = await SelectMeasurementRenderBackendAsync(null, null, deadline.Token).ConfigureAwait(false);
            if (!selection.Accepted) return ProjectCommandExecutionResult.Rejected(selection.Error!);
            WorkerProcessOptions workerOptions = selection.Response!.Executor;
            input = input with { RenderBackend = selection.Response.RenderBackend };
            Task<WorkerSceneMeasurementOutput> measurement;
            if (sourceSession is not null)
            {
                await sourceSession.Writer.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    if (sourceSession.OwnerClientId != clientId || !TryGet(clientId, sourceSession.ProjectSessionId, out var current)
                        || !ReferenceEquals(current, sourceSession))
                        return ProjectCommandExecutionResult.Rejected(MissingSessionError(sourceSession.ProjectSessionId));
                    if (sourceSession.MutationBlocked)
                        return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
                    if (sourceSession.ProjectRevision != sourceRevision)
                        return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "固定媒体准备期间工程修订已经改变。", "expectedRevision"));
                    measurement = MeasureCapturedAsync(capture, input, cue.TargetLineId, cue.Id, command.ContainerId, workerOptions, deadline.Token);
                }
                finally { sourceSession.Writer.Release(); }
            }
            else measurement = MeasureCapturedAsync(capture, input, cue.TargetLineId, cue.Id, command.ContainerId, workerOptions, deadline.Token);
            var measured = await measurement.ConfigureAwait(false);
            if (measured.Attachment is not { } geometry || geometry.ContainerId != command.ContainerId || geometry.LineId != cue.TargetLineId)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "真实目标文字参考不可用。", FailureCode: "SceneAttachmentReferenceUnavailable"));
            return await session.CommitAttachmentAsync(capture, command, geometry, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_sceneMeasurementShutdown.IsCancellationRequested)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "宿主正在停止。", FailureCode: "SceneMeasurementStopping")); }
        catch (OperationCanceledException) { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.TaskUnavailable, "附着测量超过20秒期限。", FailureCode: "SceneMeasurementDeadlineExceeded")); }
        catch (SnapshotContractException error)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "当前工程不支持静态附着测量。", FailureCode: error.Code)); }
        catch (WorkerProtocolException error) { return ProjectCommandExecutionResult.Rejected(MeasurementFailure(error)); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.TaskUnavailable, "受管理测量Worker不可用。", FailureCode: "SceneMeasurementWorkerFailed")); }
        finally { ReleaseMeasurementInputs(clientId, preparedInput, runtimeLease); }
    }

    private void ReleaseMeasurementInputs(string clientId, params IDisposable?[] inputs)
    {
        try
        {
            foreach (var input in inputs)
            {
                try { input?.Dispose(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"BACKEND_SCENE_MEASUREMENT_CLEANUP_FAILED type={error.GetType().Name} hresult={error.HResult:X8}");
                }
            }
        }
        finally { ReleaseMeasurement(clientId); }
    }

    private static async Task<WorkerSceneMeasurementOutput> MeasureCapturedAsync(ProjectSession.SceneMeasurementCapture capture,
        SceneMeasurementRequest request, string? lineId, string? cueId, string? containerId,
        WorkerProcessOptions workerOptions, CancellationToken token)
    {
        var input = new WorkerSceneMeasurementInput(request, capture.SourceSha256, "", lineId, cueId, containerId);
        input = input with { SampleInputFingerprint = WorkerSceneMeasurementContract.Fingerprint(capture.Source, input) };
        await using var worker = new WorkerProcessProxy(workerOptions);
        WorkerSceneMeasurementOutput result = await worker.MeasureSceneAsync(input, capture.Source, capture.Snapshot, token).ConfigureAwait(false);
        if (result.Measurement.RenderBackend is not null && result.Measurement.RenderBackend != request.RenderBackend)
            throw new WorkerProtocolException("测量回执渲染后端不属于当前固定输入。", "SceneMeasurementProtocolInvalid");
        return result with { Measurement = result.Measurement with { RenderBackend = request.RenderBackend } };
    }
    private bool ReserveMeasurement(string clientId)
    {
        lock (_sceneMeasurementGate)
        {
            if (_sceneMeasurementStopping || Volatile.Read(ref _stopping) != 0 || _sceneMeasurementClients.Count >= 2 || _sceneMeasurementClients.ContainsKey(clientId)) return false;
            _sceneMeasurementClients.Add(clientId, new(TaskCreationOptions.RunContinuationsAsynchronously)); return true;
        }
    }
    private void ReleaseMeasurement(string clientId)
    {
        TaskCompletionSource? done;
        lock (_sceneMeasurementGate) _sceneMeasurementClients.Remove(clientId, out done);
        done?.TrySetResult();
    }
    private bool HasSceneMeasurement(string clientId) { lock (_sceneMeasurementGate) return _sceneMeasurementClients.ContainsKey(clientId); }
    private async Task StopSceneMeasurementsAsync()
    {
        Task[] pending;
        lock (_sceneMeasurementGate)
        {
            _sceneMeasurementStopping = true;
            pending = _sceneMeasurementClients.Values.Select(done => done.Task).ToArray();
        }
        _sceneMeasurementShutdown.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    private static BackendErrorResponse MeasurementUnavailable() => new(BackendErrorCode.TaskUnavailable, "受管理同帧测量Worker未部署。", FailureCode: "SceneMeasurementUnavailable");
    private static BackendErrorResponse MeasurementBusy() => new(BackendErrorCode.RequestQuotaExceeded, "测量名额已用尽；全局最多2个，每client最多1个。");
    private static BackendErrorResponse MeasurementFailure(WorkerProtocolException error) => new(
        error.Code == "SceneMeasurementBudgetExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.TaskUnavailable,
        "受管理同帧测量未完成。", FailureCode: error.Code);
}
