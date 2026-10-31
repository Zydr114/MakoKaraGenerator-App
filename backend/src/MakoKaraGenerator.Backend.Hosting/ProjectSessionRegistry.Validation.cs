using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private BackendValidationOptions? _validationOptions;
    private BackendValidationReportStore? _validationReports;
    private IBackendValidationExecutor? _validationExecutor;
    private BackendErrorResponse? _validationDisabledReason;
    private int _validationTimeoutSeconds = 60;

    public void ConfigureValidationTasks(IBackendValidationExecutor? executor, BackendValidationOptions? options = null,
        int operationTimeoutSeconds = 60, BackendErrorResponse? disabledReason = null)
    {
        if (_persistence is null) throw new InvalidOperationException("先配置唯一持久任务宿主。");
        if (_validationOptions is not null) throw new InvalidOperationException("正式验证任务只配置一次。");
        options ??= new(); options.Validate();
        if (operationTimeoutSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(operationTimeoutSeconds));
        _validationOptions = options; _validationExecutor = executor; _validationTimeoutSeconds = operationTimeoutSeconds;
        _validationDisabledReason = disabledReason ?? (executor is null ? new(BackendErrorCode.TaskUnavailable, "正式验证Worker未部署。") : null);
        EnsureTaskScheduler(new(options.MaxRunning, options.MaxRunningPerClient, options.MaxRunningPerProject, options.MaxQueued, options.MaxQueuedPerClient));
        _validationReports = new(_persistence.StateRoot, options);
        PruneValidationReports();
    }

    public ValidationTaskCapabilitiesDto ValidationCapabilities
    {
        get
        {
            BackendValidationOptions options = _validationOptions ?? new();
            string[] profiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets];
            if (_validationExecutor is IBackendPackageValidationExecutor { PackageValidationAvailable: true })
                profiles = [.. profiles, PackageProjectSnapshotCodec.Profile];
            if (_validationExecutor is IBackendImageValidationExecutor { ImageValidationAvailable: true })
                profiles = [.. profiles, ProjectImageValidationWorkerContract.ValidationProfile];
            if (_validationExecutor is IBackendRuntimeValidationExecutor { RuntimeValidationAvailable: true })
                profiles = [.. profiles, PackagePresetProjectSnapshotCodec.Profile, ProjectRuntimeInputCodec.Profile];
            if (_validationExecutor is IBackendRuntimeValidationExecutor { RuntimeValidationAvailable: true })
                profiles = [.. profiles, AuthorValidationProfile];
            return new(_validationExecutor is not null && !_persistenceStopping, StaticProjectSnapshotCodec.Profile,
                options.MaxFramesPerOutput, options.MaxCanvasWidth, options.MaxCanvasHeight, options.MaxRawFrameBytes,
                _validationTimeoutSeconds, options.MaxReportBytes, options.MaxIssues, options.MaxOccurrences,
                options.MaxRunning, options.MaxRunningPerClient, options.MaxRunningPerProject, options.MaxQueued,
                options.MaxQueuedPerClient, options.MaxReportStorageBytes, _validationDisabledReason, options.MaxDiagnosticsPageSize,
                profiles);
        }
    }

    public Task<BackendOperationResult<JobResponse>> StartValidationAsync(string clientId, string sessionId,
        ValidationJobRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, "POST /api/v1/project-sessions/" + sessionId + "/validation-jobs",
            request with { Metadata = null }, () => StartValidationCoreAsync(clientId, sessionId, request, cancellationToken), cancellationToken);

    private async Task<BackendOperationResult<JobResponse>> StartValidationCoreAsync(string clientId, string sessionId,
        ValidationJobRequest request, CancellationToken cancellationToken)
    {
        if (request.RenderBackend is not (null or "cpu" or "gpu"))
            return BackendOperationResult<JobResponse>.Rejected(InvalidRenderBackend());
        if (_validationExecutor is null) return BackendOperationResult<JobResponse>.Rejected(_validationDisabledReason
            ?? new(BackendErrorCode.CommandNotSupported, "正式验证任务尚未配置。"));
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
        var selection = await SelectTaskRenderBackendAsync(_validationExecutor, request.RenderBackend, cancellationToken).ConfigureAwait(false);
        if (!selection.Accepted) return BackendOperationResult<JobResponse>.Rejected(selection.Error!);
        IBackendValidationExecutor executor = selection.Response!.Executor;
        request = request with { RenderBackend = selection.Response.RenderBackend };
        if (executor is IBackendRuntimeValidationExecutor { RuntimeValidationAvailable: true } runtimeExecutor)
            return await StartRuntimeValidationCoreAsync(clientId, session, request, runtimeExecutor, executor).ConfigureAwait(false);
        bool imageBackground = false;
        await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                return BackendOperationResult<JobResponse>.Rejected(mutation);
            imageBackground = ProjectSnapshot.FromProject(session.Workspace.Project).Background.Kind == BackgroundKind.Image;
        }
        finally { session.Writer.Release(); }
        if (imageBackground)
            return await StartImageValidationCoreAsync(clientId, session, request, executor).ConfigureAwait(false);
        IDisposable? audio = null;
        BackendTaskScheduler.Reservation? reservation = null;
        await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                return BackendOperationResult<JobResponse>.Rejected(mutation);
            ProjectSnapshot snapshot = ProjectSnapshot.FromProject(session.Workspace.Project);
            ValidateValidationPlan(snapshot, request.FramesPerOutput);
            string profile = ProjectPreviewSnapshotProfile.Select(snapshot);
            byte[] encoded = profile == PackageProjectSnapshotCodec.Profile
                ? PackageProjectSnapshotCodec.Encode(snapshot)
                : StaticProjectSnapshotCodec.Encode(snapshot, profile);
            if (profile == PackageProjectSnapshotCodec.Profile
                && executor is not IBackendPackageValidationExecutor { PackageValidationAvailable: true })
                return Reject<JobResponse>(BackendErrorCode.CommandNotSupported, "正式验证Worker未提供包工程能力。");
            if (encoded.Length > WorkerValidationContract.MaximumSnapshotBytes)
                return Reject<JobResponse>(BackendErrorCode.ResourceTooLarge, "固定输入超过扣除任务头部后的快照预算。");
            reservation = _taskScheduler!.TryReserve(clientId, sessionId);
            if (reservation is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "验证任务排队配额已满。");
            if (!string.IsNullOrEmpty(session.AudioAssetId)) audio = _resources.AcquireReference(clientId, session.AudioAssetId);
            BackendJobInputDto input = new("project", sessionId, request.ExpectedRevision, profile,
                WorkerProtocol.ComputeSha256(encoded), request.FramesPerOutput, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
                snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator,
                snapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
            PruneValidationReports();
            ValidationWork work = new(snapshot, profile, request.FramesPerOutput, audio, reservation, executor);
            BackendOperationResult<JobResponse> accepted = StartJob(clientId, "validation", sessionId,
                job => RunValidationAsync(work, job), input: input, failureMapper: ValidationFailure,
                cancelOnShutdown: true, deferRunning: true);
            if (accepted.Accepted) { audio = null; reservation = null; }
            return accepted;
        }
        catch (SnapshotContractException error)
        { return Reject<JobResponse>(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "工程来源不满足当前静态正式验证合同。"); }
        catch (Exception error) when (error is WorkerProtocolException or ArgumentException or OverflowException)
        { return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "正式验证帧计划、尺寸或时长不合法。"); }
        catch (ResourceStoreException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "验证来源资源不可用。", "source")); }
        finally
        {
            session.Writer.Release();
            audio?.Dispose(); reservation?.Dispose();
        }
    }

    private async Task<BackendOperationResult<JobResponse>> StartRuntimeValidationCoreAsync(string clientId, ProjectSession session,
        ValidationJobRequest request, IBackendRuntimeValidationExecutor runtimeExecutor, IBackendValidationExecutor executor)
    {
        BackendProjectRuntimeInputLease? lease = null;
        BackendPreparedProjectRuntimeInput? prepared = null;
        IDisposable? audio = null;
        BackendTaskScheduler.Reservation? reservation = null;
        try
        {
            ProjectSnapshot snapshot;
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                snapshot = ProjectSnapshot.FromProject(session.Workspace.Project);
                ValidateValidationPlan(snapshot, request.FramesPerOutput);
                lease = session.AcquireProjectRuntimeInput(snapshot);
                if (!string.IsNullOrEmpty(session.AudioAssetId)) audio = _resources.AcquireReference(clientId, session.AudioAssetId);
                reservation = _taskScheduler!.TryReserve(clientId, session.ProjectSessionId);
                if (reservation is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "验证任务排队配额已满。");
            }
            finally { session.Writer.Release(); }

            BackendOperationResult<BackendPreparedProjectRuntimeInput> captured = await lease.PrepareAsync().ConfigureAwait(false);
            lease.Dispose(); lease = null;
            if (!captured.Accepted) return BackendOperationResult<JobResponse>.Rejected(captured.Error!);
            prepared = captured.Response!;
            if (prepared.SourceBytes.Length > RuntimeValidationWorkerContract.MaximumInputBytes)
                return Reject<JobResponse>(BackendErrorCode.ResourceTooLarge, "固定Runtime输入超过正式验证封包预算。");

            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                PruneValidationReports();
                BackendJobInputDto input = new("project", session.ProjectSessionId, request.ExpectedRevision, prepared.SourceProfile,
                    WorkerProtocol.ComputeSha256(prepared.SourceBytes.Span), request.FramesPerOutput,
                    snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, snapshot.Composition.FrameRate.Numerator,
                    snapshot.Composition.FrameRate.Denominator, snapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
                ValidationWork work = new(snapshot, prepared.SourceProfile, request.FramesPerOutput, audio, reservation, executor,
                    runtimeInput: prepared, runtimeExecutor: runtimeExecutor);
                BackendOperationResult<JobResponse> accepted = StartJob(clientId, "validation", session.ProjectSessionId,
                    job => RunValidationAsync(work, job), input: input, failureMapper: ValidationFailure,
                    cancelOnShutdown: true, deferRunning: true);
                if (accepted.Accepted) { audio = null; reservation = null; prepared = null; }
                return accepted;
            }
            finally { session.Writer.Release(); }
        }
        catch (SnapshotContractException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "工程固定Runtime来源不满足正式验证合同。", "source", FailureCode: error.Code)); }
        catch (Exception error) when (error is WorkerProtocolException or ArgumentException or OverflowException)
        { return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "正式验证帧计划、尺寸或时长不合法。"); }
        catch (ResourceStoreException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "验证来源资源不可用。", "source")); }
        finally
        {
            try { lease?.Dispose(); }
            finally { try { prepared?.Dispose(); } finally { try { audio?.Dispose(); } finally { reservation?.Dispose(); } } }
        }
    }

    private async Task<BackendOperationResult<JobResponse>> StartImageValidationCoreAsync(string clientId, ProjectSession session,
        ValidationJobRequest request, IBackendValidationExecutor executor)
    {
        if (executor is not IBackendImageValidationExecutor imageExecutor || !imageExecutor.ImageValidationAvailable)
            return BackendOperationResult<JobResponse>.Rejected(new(BackendErrorCode.TaskUnavailable,
                "正式验证Worker未提供独立图片验证能力。", "worker", FailureCode: "WorkerImageValidationUnavailable"));
        BackendProjectImageReadLease? imageLease = null;
        IDisposable? audio = null;
        BackendTaskScheduler.Reservation? reservation = null;
        try
        {
            ProjectSnapshot snapshot;
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                snapshot = ProjectSnapshot.FromProject(session.Workspace.Project);
                ValidateValidationPlan(snapshot, request.FramesPerOutput);
                imageLease = session.AcquireProjectImageRead(snapshot);
                if (!string.IsNullOrEmpty(session.AudioAssetId)) audio = _resources.AcquireReference(clientId, session.AudioAssetId);
            }
            finally { session.Writer.Release(); }

            BackendOperationResult<byte[]> read = await imageLease.ReadAsync().ConfigureAwait(false);
            imageLease.Dispose(); imageLease = null;
            if (!read.Accepted) return BackendOperationResult<JobResponse>.Rejected(read.Error!);
            byte[] encoded = ProjectImagePreviewSnapshotCodec.Encode(snapshot, read.Response!);
            if (encoded.Length > ProjectImageValidationWorkerContract.MaximumInputBytes)
                return BackendOperationResult<JobResponse>.Rejected(new(BackendErrorCode.ResourceTooLarge,
                    "图片验证完整封包超过4MiB扣除头部后的预算。", "background", FailureCode: "SnapshotSizeExceeded"));

            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                reservation = _taskScheduler!.TryReserve(clientId, session.ProjectSessionId);
                if (reservation is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "验证任务排队配额已满。");
                PruneValidationReports();
                BackendJobInputDto input = new("project", session.ProjectSessionId, request.ExpectedRevision,
                    ProjectImageValidationWorkerContract.ValidationProfile, WorkerProtocol.ComputeSha256(encoded), request.FramesPerOutput,
                    snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, snapshot.Composition.FrameRate.Numerator,
                    snapshot.Composition.FrameRate.Denominator, snapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
                ValidationWork work = new(snapshot, ProjectImageValidationWorkerContract.ValidationProfile, request.FramesPerOutput,
                    audio, reservation, executor, read.Response);
                BackendOperationResult<JobResponse> accepted = StartJob(clientId, "validation", session.ProjectSessionId,
                    job => RunValidationAsync(work, job), input: input, failureMapper: ValidationFailure,
                    cancelOnShutdown: true, deferRunning: true);
                if (accepted.Accepted) { audio = null; reservation = null; }
                return accepted;
            }
            finally { session.Writer.Release(); }
        }
        catch (SnapshotContractException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "工程图片来源不满足当前正式验证合同。", "background", FailureCode: error.Code)); }
        catch (Exception error) when (error is WorkerProtocolException or ArgumentException or OverflowException)
        { return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "正式验证帧计划、尺寸或时长不合法。"); }
        catch (ResourceStoreException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "验证来源资源不可用。", "source")); }
        finally
        {
            try { imageLease?.Dispose(); }
            finally { try { audio?.Dispose(); } finally { reservation?.Dispose(); } }
        }
    }

    private void ValidateValidationPlan(ProjectSnapshot snapshot, int framesPerOutput)
    {
        BackendValidationOptions options = _validationOptions!;
        var composition = snapshot.Composition;
        if (framesPerOutput <= 0 || framesPerOutput > options.MaxFramesPerOutput || composition.CanvasWidth > options.MaxCanvasWidth
            || composition.CanvasHeight > options.MaxCanvasHeight || checked((long)composition.CanvasWidth * composition.CanvasHeight * 4) > options.MaxRawFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(framesPerOutput));
        WorkerValidationContract.ValidateGeometryAndTiming(composition.CanvasWidth, composition.CanvasHeight, composition.OutputDurationUs,
            composition.FrameRate.Numerator, composition.FrameRate.Denominator, framesPerOutput);
    }

    private async Task RunValidationAsync(ValidationWork work, BackendJob job)
    {
        try
        {
            await work.Reservation!.WaitAsync(job.Cancellation).ConfigureAwait(false);
            lock (_jobGate)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.SetState("running", true);
                job.ReportProgress(new("validating", 0, checked(work.FramesPerOutput * 2)));
            }
            ValidationReportDto report = work.RuntimeInput is { } runtimeInput
                ? await work.RuntimeExecutor!.ExecuteRuntimeAsync(runtimeInput.SourceBytes, work.FramesPerOutput,
                    new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false)
                : work.Profile == ProjectImageValidationWorkerContract.ValidationProfile
                ? await ((IBackendImageValidationExecutor)work.Executor).ExecuteImageAsync(work.Snapshot!, work.ImageBytes!, work.FramesPerOutput,
                    new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false)
                : work.Profile == PackageProjectSnapshotCodec.Profile
                ? await ((IBackendPackageValidationExecutor)work.Executor).ExecutePackageAsync(work.Snapshot!, work.FramesPerOutput,
                    new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false)
                : await work.Executor.ExecuteAsync(work.Snapshot!, work.FramesPerOutput,
                    new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false);
            string? backend = job.Read().Input?.RenderBackend;
            if (report.RenderBackend is not null && report.RenderBackend != backend)
                throw new ValidationTaskException("ValidationReportInvalid", "验证报告渲染后端与固定任务不一致。");
            report = report with { RenderBackend = backend };
            // The executor contract returns only after native sessions and Worker have been drained.
            work.ReleaseAudio();
            job.ReportProgress(new("disposing", report.OpaqueFrames + report.TransparentFrames, checked(work.FramesPerOutput * 2)));
            byte[] bytes = _validationReports!.Encode(report, work.FramesPerOutput);
            job.Cancellation.ThrowIfCancellationRequested();
            lock (_jobGate)
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                BackendJobReportReferenceDto reference = _validationReports.Publish(job.Id, bytes);
                ValidationJobResultDto result = new(report.Succeeded, report.OpaqueFrames, report.TransparentFrames, reference);
                job.Finish(null, report.Succeeded ? null : new(BackendErrorCode.ValidationFailed, "此固定来源及帧计划的正式验证未通过。"),
                    validationResult: result, failureCode: report.Succeeded ? null : "ValidationFailed");
            }
        }
        finally { work.Dispose(); }
    }

    private static BackendErrorResponse ValidationFailure(Exception error) => error switch
    {
        ValidationTaskException validation when validation.Code == "ValidationReportStorageQuotaExceeded" =>
            new(BackendErrorCode.ResourceQuotaExceeded, "完整验证报告存储配额不足。"),
        ValidationTaskException => new(BackendErrorCode.TaskExecutionFailed, "完整验证报告无法验证或发布。"),
        RenderSessionFailureException => new(BackendErrorCode.TaskExecutionFailed, "正式验证Worker执行失败。"),
        ResourceStoreException => new(BackendErrorCode.TaskExecutionFailed, "正式验证固定资源不可用。"),
        _ => new(BackendErrorCode.TaskExecutionFailed, "正式验证任务执行失败。"),
    };

    public BackendOperationResult<byte[]> ReadValidationResult(string clientId, string jobId)
    {
        if (!TryFindJob(clientId, jobId, out BackendJob job)) return Reject<byte[]>(BackendErrorCode.JobNotFound, "任务不存在或保留期已结束。");
        JobResponse response = job.Read();
        if (response.ValidationResult is null || _validationReports is null)
            return Reject<byte[]>(BackendErrorCode.JobNotFound, "此任务尚无完整正式验证报告。");
        return ReadRegisteredValidationReport(response);
    }

    public BackendOperationResult<byte[]> ReadRecoveredValidationResult(string clientId, string jobId)
    {
        // Recovery is the existing local profile authority; current-host jobs never enter this path.
        JobResponse? response = ReadRecoveredJobs(clientId).FirstOrDefault(value => value.Job.JobId == jobId)?.Job;
        if (response?.ValidationResult is null || _validationReports is null)
            return Reject<byte[]>(BackendErrorCode.JobNotFound, "前一宿主没有登记此完整验证报告。");
        return ReadRegisteredValidationReport(response);
    }

    private BackendOperationResult<byte[]> ReadRegisteredValidationReport(JobResponse response)
    {
        try
        {
            if (response.Kind != "validation" || response.Input is not { } input
                || !(input.SourceKind == "authorDraft" && input.SourceProfile == AuthorValidationProfile
                    || input.SourceKind == "project" && (BackendSnapshotProfiles.IsSupported(input.SourceProfile) || input.SourceProfile == PackageProjectSnapshotCodec.Profile
                    || input.SourceProfile == ProjectImageValidationWorkerContract.ValidationProfile
                    || input.SourceProfile == PackagePresetProjectSnapshotCodec.Profile || input.SourceProfile == ProjectRuntimeInputCodec.Profile)))
                throw new IOException("报告固定来源合同无效。");
            if (input.SourceKind == "authorDraft" && (response.ProjectSessionId is not null
                || response.AuthorValidationInput is not { } author || author.DraftId != input.SourceId
                || author.SourceRevision != input.SourceRevision || !ValidDraftId(author.DraftId)
                || !ValidRevision(author.DraftRevision) || !ValidRevision(author.SourceRevision)
                || author.SampleId is not (TemplatePreviewLimits.SingleLineSampleId or TemplatePreviewLimits.MultiLineSampleId)
                || string.IsNullOrWhiteSpace(author.TargetKey) || input.FramesPerOutput != ScriptSampleValidator.FramesPerOutput
                || input.CanvasWidth != 640 || input.CanvasHeight != 360 || input.FrameRateNumerator != 30
                || input.FrameRateDenominator != 1 || input.OutputDurationUs != TemplateSampleScene.DurationUs.ToString(CultureInfo.InvariantCulture)))
                throw new IOException("作者报告草稿或样本身份与账本不一致。");
            byte[] bytes = _validationReports!.Read(response.JobId, response.ValidationResult!.Report);
            if (!long.TryParse(input.OutputDurationUs, NumberStyles.None, CultureInfo.InvariantCulture, out long duration))
                throw new IOException("报告帧计划缺少固定时长。");
            WorkerValidationContract.ValidateGeometryAndTiming(input.CanvasWidth, input.CanvasHeight, duration,
                input.FrameRateNumerator, input.FrameRateDenominator, input.FramesPerOutput);
            ValidationReportDto report = WorkerValidationContract.DecodeReport(bytes, input.FramesPerOutput);
            WorkerValidationContract.ValidateReportTimes(report, checked((long)(input.FramesPerOutput - 1) * 1_000_000 * input.FrameRateDenominator / input.FrameRateNumerator));
            if (input.RenderBackend is not (null or "cpu" or "gpu") || report.RenderBackend != input.RenderBackend
                || input.SourceKind == "authorDraft" && response.AuthorValidationInput?.RenderBackend != input.RenderBackend)
                throw new IOException("报告渲染后端与唯一账本不一致。");
            if (report.Succeeded != response.ValidationResult.Passed || report.OpaqueFrames != response.ValidationResult.OpaqueFrames
                || report.TransparentFrames != response.ValidationResult.TransparentFrames)
                throw new IOException("报告摘要与账本不一致。");
            return BackendOperationResult<byte[]>.Success(bytes);
        }
        catch (WorkerProtocolException) { return Reject<byte[]>(BackendErrorCode.TaskExecutionFailed, "已登记报告固定计划校验失败。"); }
        catch (IOException) { return Reject<byte[]>(BackendErrorCode.TaskExecutionFailed, "已登记报告不可用或完整性校验失败。"); }
    }

    public BackendOperationResult<ValidationDiagnosticsPageDto> ReadValidationDiagnostics(string clientId, string jobId, string? cursor, int? limit)
    {
        BackendOperationResult<byte[]> read = ReadValidationResult(clientId, jobId);
        if (!read.Accepted) return BackendOperationResult<ValidationDiagnosticsPageDto>.Rejected(read.Error!);
        BackendOperationResult<JobResponse> jobRead = ReadJob(clientId, jobId);
        if (!jobRead.Accepted) return BackendOperationResult<ValidationDiagnosticsPageDto>.Rejected(jobRead.Error!);
        JobResponse response = jobRead.Response!;
        string hash = response.ValidationResult!.Report.Sha256;
        int size = limit ?? _validationOptions!.MaxDiagnosticsPageSize;
        if (size <= 0 || size > _validationOptions!.MaxDiagnosticsPageSize)
            return Reject<ValidationDiagnosticsPageDto>(BackendErrorCode.DiagnosticLimitExceeded, "诊断页大小超过公布上限。");
        string prefix = jobId + ":" + hash + ":";
        int offset = 0;
        if (cursor is not null && (!cursor.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
            return Reject<ValidationDiagnosticsPageDto>(BackendErrorCode.DiagnosticCursorExpired, "诊断游标不属于此任务报告。");
        ValidationReportDto report = BackendValidationReportStore.Decode(read.Response!);
        if (offset > report.Diagnostics.Length)
            return Reject<ValidationDiagnosticsPageDto>(BackendErrorCode.DiagnosticCursorExpired, "诊断游标超过完整报告范围。");
        ValidationDiagnosticDto[] page = report.Diagnostics.Skip(offset).Take(size).ToArray();
        string? next = offset + page.Length < report.Diagnostics.Length ? prefix + (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null;
        return BackendOperationResult<ValidationDiagnosticsPageDto>.Success(new(jobId, hash, page, next, cursor));
    }

    private void PruneValidationReports()
    {
        if (_validationReports is null) return;
        lock (_jobGate)
        {
            IEnumerable<JobResponse> current = _jobs.Values.Select(value => value.Read()).Where(value => value.ExpiresAtUtc > _timeProvider.GetUtcNow());
            IEnumerable<JobResponse> recovered = _ledger?.ReadRecovered().Select(value => value.Job) ?? [];
            _validationReports.Prune(current.Concat(recovered).Where(value => value.ValidationResult is not null)
                .Select(value => value.ValidationResult!.Report.ReportId));
        }
    }

    private sealed class InlineValidationProgress(BackendJob job) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => job.ReportProgress(value); }

    private sealed class ValidationWork(ProjectSnapshot snapshot, string profile, int framesPerOutput, IDisposable? audio,
        BackendTaskScheduler.Reservation reservation, IBackendValidationExecutor executor, byte[]? imageBytes = null,
        BackendPreparedProjectRuntimeInput? runtimeInput = null, IBackendRuntimeValidationExecutor? runtimeExecutor = null) : IDisposable
    {
        internal ProjectSnapshot? Snapshot = snapshot;
        internal string Profile { get; } = profile;
        internal int FramesPerOutput { get; } = framesPerOutput;
        internal BackendTaskScheduler.Reservation? Reservation = reservation;
        internal IBackendValidationExecutor Executor { get; } = executor;
        internal byte[]? ImageBytes = imageBytes;
        internal BackendPreparedProjectRuntimeInput? RuntimeInput = runtimeInput;
        internal IBackendRuntimeValidationExecutor? RuntimeExecutor { get; } = runtimeExecutor;
        private IDisposable? _audio = audio;
        internal void ReleaseAudio() => Interlocked.Exchange(ref _audio, null)?.Dispose();
        public void Dispose()
        {
            try { ReleaseAudio(); }
            finally
            {
                try { Interlocked.Exchange(ref RuntimeInput, null)?.Dispose(); }
                finally { Snapshot = null; ImageBytes = null; Interlocked.Exchange(ref Reservation, null)?.Dispose(); }
            }
        }
    }
}
