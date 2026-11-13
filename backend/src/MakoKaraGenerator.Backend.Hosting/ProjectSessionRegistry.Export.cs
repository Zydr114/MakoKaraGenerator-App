using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private IBackendExportExecutor? _exportExecutor;
    private BackendExportOptions? _exportOptions;
    private BackendExportCapabilities? _exportDeployment;
    private BackendJobArtifactStore? _exportArtifacts;
    private int? _exportTimeoutSeconds;
    private string[] _exportSourceProfiles = [];
    private string[] _exportAudioProfiles = [];
    private string[] _exportExecutionProfiles = [];

    public void ConfigureExportTasks(IBackendExportExecutor? executor, BackendExportCapabilities deployment,
        BackendExportOptions? options = null, int? operationTimeoutSeconds = null)
    {
        if (_persistence is null || _exportOptions is not null) throw new InvalidOperationException("导出只在既有持久任务宿主配置一次。");
        ArgumentNullException.ThrowIfNull(deployment);
        options ??= new(); options.Validate();
        if (operationTimeoutSeconds is <= 0) throw new ArgumentOutOfRangeException(nameof(operationTimeoutSeconds));
        EnsureTaskScheduler();
        _exportOptions = options; _exportTimeoutSeconds = operationTimeoutSeconds; _exportDeployment = deployment;
        // Trusted in-process legacy adapters retain only their original WAV capability.
        // The real Worker probe rejects missing new fields before reaching this seam.
        string[] audioProfiles = deployment.AudioProfiles ?? [BackendAudioInputProfiles.Pcm16Wave];
        string[] executionProfiles = deployment.ExecutionProfiles ?? [WorkerExportContract.ExecutionProfile];
        string[] sourceProfiles = deployment.SourceProfiles ?? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis];
        bool validProfiles = sourceProfiles.Length > 0
            && sourceProfiles.Distinct(StringComparer.Ordinal).Count() == sourceProfiles.Length
            && sourceProfiles.All(BackendSnapshotProfiles.IsExportSupported)
            && sourceProfiles.Contains(BackendSnapshotProfiles.StaticLyricsScene, StringComparer.Ordinal)
            && sourceProfiles.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, StringComparer.Ordinal)
            && audioProfiles.Length > 0 && executionProfiles.Length > 0
            && audioProfiles.Distinct(StringComparer.Ordinal).Count() == audioProfiles.Length
            && executionProfiles.Distinct(StringComparer.Ordinal).Count() == executionProfiles.Length
            && audioProfiles.All(BackendAudioInputProfiles.IsSupported)
            && executionProfiles.All(profile => profile is WorkerExportContract.ExecutionProfile or WorkerExportContract.CompressedExecutionProfile)
            && executionProfiles.Contains(WorkerExportContract.ExecutionProfile, StringComparer.Ordinal)
            && audioProfiles.All(profile => executionProfiles.Contains(WorkerExportContract.GetExecutionProfile(profile), StringComparer.Ordinal))
            && executionProfiles.All(profile => profile == WorkerExportContract.ExecutionProfile
                || audioProfiles.Any(audio => WorkerExportContract.GetExecutionProfile(audio) == profile));
        bool validImageProfiles = deployment.ImageBaseProfiles is { Length: 3 } imageBases
            && imageBases.ToHashSet(StringComparer.Ordinal).SetEquals([BackendSnapshotProfiles.StaticLyricsScene,
                BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene]);
        _exportDeployment = deployment with { ImageExportAvailable = deployment.ImageExportAvailable && validImageProfiles };
        _exportExecutor = deployment.GuardAvailable && validProfiles ? executor : null;
        if (_exportExecutor is not null) { _exportSourceProfiles = [.. sourceProfiles]; _exportAudioProfiles = [.. audioProfiles]; _exportExecutionProfiles = [.. executionProfiles]; }
        _exportArtifacts = new(_persistence.StateRoot, options); PruneExportArtifacts();
    }

    public ExportTaskCapabilitiesDto ExportCapabilities
    {
        get
        {
            BackendExportOptions options = _exportOptions ?? new(); BackendTaskSchedulingOptions scheduling = _taskScheduler?.Options ?? new();
            string[] formats = [.. (_exportDeployment?.OpaqueMp4 == true ? new[] { "opaqueMp4" } : []),
                .. (_exportDeployment?.TransparentProRes4444 == true ? new[] { "transparentProRes4444" } : [])];
            bool enabled = _exportExecutor is not null && formats.Length != 0 && !_persistenceStopping;
            return new(enabled, StaticProjectSnapshotCodec.Profile, formats, enabled ? [.. _exportAudioProfiles] : [], "standard", "final",
                options.MaxFrames, options.MaxDurationUs?.ToString(CultureInfo.InvariantCulture), options.MaxFrameRate,
                options.MaxCanvasWidth, options.MaxCanvasHeight, options.MaxRawFrameBytes, _exportTimeoutSeconds, 2,
                options.MaxMediaBytes, options.MaxReportBytes, options.MaxOccurrences, options.MaxArtifactStorageBytes,
                scheduling.MaxRunning, scheduling.MaxRunningPerClient, scheduling.MaxRunningPerProject,
                scheduling.MaxQueued, scheduling.MaxQueuedPerClient,
                _exportDeployment?.GuardAvailable == true ? options.MaxMediaBytes is not null ? "linux-x64-rlimit-fsize" : "linux-process-group" : "unavailable",
                enabled ? null : new(BackendErrorCode.TaskUnavailable, "当前部署缺少已验证的导出Worker、文件限制或编码能力。", "export"),
                options.MaxDiagnosticsPageSize, WorkerExportContract.ExecutionProfile,
                enabled ? [.. _exportSourceProfiles] : [],
                enabled ? [.. _exportExecutionProfiles] : [],
                options.MaxCompressedAudioDurationUs?.ToString(CultureInfo.InvariantCulture),
                WorkerExportContract.CompressedAudioDurationBasis,
                enabled && _exportDeployment?.ImageExportAvailable == true
                    && _exportExecutor is IBackendImageExportExecutor { ImageExportAvailable: true }
                    ? ProjectImageExportWorkerContract.ExportProfile : null,
                enabled && _exportDeployment?.ImageExportAvailable == true
                    && _exportExecutor is IBackendImageExportExecutor { ImageExportAvailable: true }
                    ? [.. _exportDeployment.ImageBaseProfiles ?? []] : null, enabled ? ["draft", "standard", "high"] : [], options.MaxAudioBytes);
        }
    }

    public Task<BackendOperationResult<JobResponse>> StartExportAsync(string clientId, string sessionId,
        ExportJobRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, "POST /api/v1/project-sessions/" + sessionId + "/export-jobs",
            request with { Metadata = null }, () => StartExportCoreAsync(clientId, sessionId, request, cancellationToken), cancellationToken);

    private async Task<BackendOperationResult<JobResponse>> StartExportCoreAsync(string clientId, string sessionId,
        ExportJobRequest request, CancellationToken cancellationToken)
    {
        if (request.RenderBackend is not (null or "cpu" or "gpu"))
            return BackendOperationResult<JobResponse>.Rejected(InvalidRenderBackend());
        if (!ExportCapabilities.Enabled) return BackendOperationResult<JobResponse>.Rejected(ExportCapabilities.DisabledReason!);
        if (request.Quality is not ("draft" or "standard" or "high"))
            return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "导出质量必须为 draft、standard 或 high。");
        if (request.Format is not ("opaqueMp4" or "transparentProRes4444"))
            return Reject<JobResponse>(BackendErrorCode.CommandNotSupported, "导出格式不受支持。");
        if (request.Format == "opaqueMp4" && !_exportDeployment!.OpaqueMp4 || request.Format == "transparentProRes4444" && !_exportDeployment!.TransparentProRes4444)
            return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "当前部署缺少此格式的真实编码能力。");
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
        var selection = await SelectTaskRenderBackendAsync(_exportExecutor!, request.RenderBackend, cancellationToken).ConfigureAwait(false);
        if (!selection.Accepted) return BackendOperationResult<JobResponse>.Rejected(selection.Error!);
        IBackendExportExecutor executor = selection.Response!.Executor;
        request = request with { RenderBackend = selection.Response.RenderBackend };
        BackgroundKind background;
        bool runtimeRequired;
        await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                return BackendOperationResult<JobResponse>.Rejected(mutation);
            background = session.Workspace.Project.Background.Kind;
            runtimeRequired = ProjectRuntimeInputCodec.RequiresPreparation(ProjectSnapshot.FromProject(session.Workspace.Project));
        }
        finally { session.Writer.Release(); }
        if (runtimeRequired && _exportSourceProfiles.Contains(ProjectRuntimeInputCodec.Profile, StringComparer.Ordinal))
            return await StartRuntimeExportCoreAsync(clientId, session, request, executor).ConfigureAwait(false);
        if (background == BackgroundKind.Image) return await StartImageExportCoreAsync(clientId, session, request, executor).ConfigureAwait(false);
        if (runtimeRequired)
            return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "实际导出Worker未提供固定媒体运行输入能力。");
        IDisposable? audioPin = null; BackendTaskScheduler.Reservation? slot = null; BackendJobArtifactStore.Reservation? disk = null;
        await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                return BackendOperationResult<JobResponse>.Rejected(mutation);
            ProjectSnapshot snapshot = ProjectSnapshot.FromProject(session.Workspace.Project);
            if (ScriptBackendError(snapshot, request.RenderBackend) is { } scriptBackendError)
                return BackendOperationResult<JobResponse>.Rejected(scriptBackendError);
            string profile = ProjectSnapshotSourceProfile.Select(snapshot);
            if (!_exportSourceProfiles.Contains(profile, StringComparer.Ordinal))
                return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "实际导出Worker未提供所选来源能力。");
            byte[] encoded = ProjectSnapshotSourceProfile.Encode(snapshot);
            var sourceReport = ProjectValidator.Validate(session.Workspace.Project, ValidationContext.Export);
            if (sourceReport.HasErrors) return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "工程存在阻断导出的领域错误，需先修正歌词或工程设置。");
            CompositionFramePlan plan = ValidateExportPlan(snapshot, request.Format);
            BackendExportAudioGrant? grant = null; BackendExportAudioReferenceDto? audioReference = null; StoredAsset? capturedAudio = null;
            if (request.IncludeAudio)
            {
                if (string.IsNullOrEmpty(session.AudioAssetId)) return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "带音轨导出需要已授权的受管音轨资源。");
                audioPin = _resources.AcquireReference(clientId, session.AudioAssetId);
                StoredAsset audio = _resources.GetAuthorized(clientId, session.AudioAssetId);
                if (new FileInfo(audio.StoragePath).LinkTarget is not null || audio.Size > _exportOptions!.MaxAudioBytes
                    || snapshot.AudioMedia is { } media && (media.Hash != audio.Sha256 || media.ByteLength != audio.Size))
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "工程音频固定身份与授权资源不一致。");
                VerifyFixedAudioSource(clientId, audio, cancellationToken);
                BackendAudioInputInfo info = ClassifyExportAudio(_resources.AcquireRead(clientId, session.AudioAssetId));
                if (!_exportAudioProfiles.Contains(info.AudioProfile, StringComparer.Ordinal))
                    return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "当前部署没有此固定音频格式的导出能力。");
                if (info.SourceDurationUs is { } duration && duration < plan.VideoDurationUs)
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "实际音轨不足计划视频时长。");
                capturedAudio = audio;
                grant = new(audio.StoragePath, audio.Size, audio.Sha256, info.AudioProfile);
                audioReference = new(audio.AssetId, audio.Size, audio.Sha256,
                    info.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? null : info.AudioProfile);
            }
            PruneExportArtifacts();
            disk = _exportArtifacts!.TryReserve();
            if (disk is null) return Reject<JobResponse>(BackendErrorCode.ResourceQuotaExceeded, "导出产物预留配额不足。");
            slot = _taskScheduler!.TryReserve(clientId, sessionId);
            if (slot is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "正式任务排队配额已满。");
            BackendJobInputDto input = new("project", sessionId, request.ExpectedRevision, profile,
                WorkerProtocol.ComputeSha256(encoded), plan.TotalFrames, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
                snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator,
                snapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
            ExportWork work = new(snapshot, request.Format, request.IncludeAudio, clientId, capturedAudio, audioPin, slot, disk, executor);
            BackendOperationResult<JobResponse> accepted = StartJob(clientId, "export", sessionId, job => RunExportAsync(work, job),
                input: input, failureMapper: ExportFailure, cancelOnShutdown: true, deferRunning: true,
                exportInput: new(request.Format, request.IncludeAudio, audioReference,
                    grant is { } audioGrant && WorkerExportContract.GetExecutionProfile(audioGrant.AudioProfile) == WorkerExportContract.CompressedExecutionProfile
                        ? WorkerExportContract.CompressedExecutionProfile : null, Quality: request.Quality, RenderBackend: request.RenderBackend), beforeRegistration: job =>
                {
                    string candidate = _exportArtifacts.CreateCandidate(work.Disk!, job.Id, request.Format);
                    work.Execution = new(request.Format, request.IncludeAudio, candidate, grant, request.Quality,
                        _exportOptions!.ResourcePolicy(work.Disk!.MaximumMediaBytes, _exportTimeoutSeconds));
                    try { work.Executor.ValidateInput(snapshot, work.Execution); return null; }
                    catch (Exception error) when (error is WorkerProtocolException or SnapshotContractException or RenderSessionFailureException or ArgumentException)
                    { return new(error is WorkerProtocolException { Code: "WorkerSnapshotBudgetExceeded" }
                        ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.ExportInputRejected,
                        "固定来源及动态任务封包不满足导出合同。"); }
                });
            if (accepted.Accepted) { audioPin = null; slot = null; disk = null; }
            return accepted;
        }
        catch (SnapshotContractException error)
        { return Reject<JobResponse>(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "工程超出静态导出来源能力或总输入预算。"); }
        catch (WorkerProtocolException)
        { return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "固定音频不是本能力支持的自包含WAV／MP3／FLAC候选。"); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "画布、帧率、时长或计划超出导出边界。"); }
        catch (ResourceStoreException error) { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "固定音频资源不可用。", "audio")); }
        catch (IOException) { return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "固定来源或受控候选存储不可用。"); }
        finally { session.Writer.Release(); try { audioPin?.Dispose(); } finally { try { slot?.Dispose(); } finally { disk?.Dispose(); } } }
    }

    private async Task<BackendOperationResult<JobResponse>> StartRuntimeExportCoreAsync(string clientId, ProjectSession session,
        ExportJobRequest request, IBackendExportExecutor executor)
    {
        BackendProjectRuntimeInputLease? inputLease = null;
        BackendPreparedProjectRuntimeInput? prepared = null;
        IDisposable? audioPin = null;
        BackendTaskScheduler.Reservation? slot = null;
        BackendJobArtifactStore.Reservation? disk = null;
        try
        {
            ProjectSnapshot snapshot;
            StoredAsset? capturedAudio = null;
            CompositionFramePlan plan;
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                snapshot = ProjectSnapshot.FromProject(session.Workspace.Project);
                if (ScriptBackendError(snapshot, request.RenderBackend) is { } scriptBackendError)
                    return BackendOperationResult<JobResponse>.Rejected(scriptBackendError);
                string basis = ProjectSnapshotSourceProfile.Select(snapshot with { Background = BackgroundSource.Color });
                if (!_exportSourceProfiles.Contains(basis, StringComparer.Ordinal))
                    return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "实际导出Worker未提供所选基础来源能力。");
                if (ProjectValidator.Validate(session.Workspace.Project, ValidationContext.Export).HasErrors)
                    return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "工程存在阻断导出的领域错误。");
                plan = ValidateExportPlan(snapshot, request.Format);
                inputLease = session.AcquireProjectRuntimeInput(snapshot);
                inputLease.TransparentOutput = request.Format == "transparentProRes4444";
                inputLease.WaitForMediaFeatures = true;
                if (!string.IsNullOrEmpty(session.AudioAssetId))
                {
                    audioPin = _resources.AcquireReference(clientId, session.AudioAssetId);
                    if (request.IncludeAudio) capturedAudio = _resources.GetAuthorized(clientId, session.AudioAssetId);
                }
                else if (request.IncludeAudio)
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "带音轨导出需要已授权的受管音轨资源。");
            }
            finally { session.Writer.Release(); }

            BackendOperationResult<BackendPreparedProjectRuntimeInput> preparation = await inputLease.PrepareAsync().ConfigureAwait(false);
            inputLease.Dispose(); inputLease = null;
            if (!preparation.Accepted) return BackendOperationResult<JobResponse>.Rejected(preparation.Error!);
            prepared = preparation.Response!;
            if (prepared.SourceProfile != ProjectRuntimeInputCodec.Profile)
                return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "媒体导出缺少固定授权闭包。");
            BackendExportAudioGrant? grant = null;
            BackendExportAudioReferenceDto? audioReference = null;
            if (capturedAudio is { } audio)
            {
                if (new FileInfo(audio.StoragePath).LinkTarget is not null || audio.Size > _exportOptions!.MaxAudioBytes
                    || snapshot.AudioMedia is { } media && (media.Hash != audio.Sha256 || media.ByteLength != audio.Size))
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "工程音频固定身份与授权资源不一致。");
                VerifyFixedAudioSource(clientId, audio);
                BackendAudioInputInfo info = ClassifyExportAudio(_resources.AcquireRead(clientId, audio.AssetId));
                if (!_exportAudioProfiles.Contains(info.AudioProfile, StringComparer.Ordinal))
                    return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "当前部署没有此固定音频格式的导出能力。");
                if (info.SourceDurationUs is { } duration && duration < plan.VideoDurationUs)
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "实际音轨不足计划视频时长。");
                VerifyFixedAudioSource(clientId, audio);
                grant = new(audio.StoragePath, audio.Size, audio.Sha256, info.AudioProfile);
                audioReference = new(audio.AssetId, audio.Size, audio.Sha256,
                    info.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? null : info.AudioProfile);
            }
            executor.ValidateInput(snapshot, new(request.Format, request.IncludeAudio,
                Path.Combine(_persistence!.StateRoot, "export-artifacts", ".staging", new string('0', 32),
                    request.Format == "opaqueMp4" ? "media.mp4" : "media.mov"), grant, request.Quality,
                _exportOptions!.ResourcePolicy(operationTimeoutSeconds: _exportTimeoutSeconds), prepared.SourceBytes));

            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                PruneExportArtifacts();
                disk = _exportArtifacts!.TryReserve();
                if (disk is null) return Reject<JobResponse>(BackendErrorCode.ResourceQuotaExceeded, "导出产物预留配额不足。");
                slot = _taskScheduler!.TryReserve(clientId, session.ProjectSessionId);
                if (slot is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "正式任务排队配额已满。");
                BackendJobInputDto input = new("project", session.ProjectSessionId, request.ExpectedRevision,
                    prepared.SourceProfile, WorkerProtocol.ComputeSha256(prepared.SourceBytes.Span), plan.TotalFrames,
                    snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
                    snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator,
                    snapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
                ExportWork work = new(snapshot, request.Format, request.IncludeAudio, clientId, capturedAudio,
                    audioPin, slot, disk, executor, runtimeInput: prepared);
                BackendOperationResult<JobResponse> accepted = StartJob(clientId, "export", session.ProjectSessionId,
                    job => RunExportAsync(work, job), input: input, failureMapper: ExportFailure, cancelOnShutdown: true, deferRunning: true,
                    exportInput: new(request.Format, request.IncludeAudio, audioReference,
                        grant is { } audioGrant && WorkerExportContract.GetExecutionProfile(audioGrant.AudioProfile) == WorkerExportContract.CompressedExecutionProfile
                            ? WorkerExportContract.CompressedExecutionProfile : null, Quality: request.Quality, RenderBackend: request.RenderBackend), beforeRegistration: job =>
                    {
                        string candidate = _exportArtifacts.CreateCandidate(work.Disk!, job.Id, request.Format);
                        work.Execution = new(request.Format, request.IncludeAudio, candidate, grant, request.Quality,
                            _exportOptions!.ResourcePolicy(work.Disk!.MaximumMediaBytes, _exportTimeoutSeconds), work.RuntimeInput!.SourceBytes);
                        try { work.Executor.ValidateInput(snapshot, work.Execution); return null; }
                        catch (Exception error) when (error is WorkerProtocolException or SnapshotContractException or RenderSessionFailureException or ArgumentException)
                        { return new(BackendErrorCode.ExportInputRejected, "固定运行来源与实际任务封包不满足导出合同。"); }
                    });
                if (accepted.Accepted) { prepared = null; audioPin = null; slot = null; disk = null; }
                return accepted;
            }
            finally { session.Writer.Release(); }
        }
        catch (SnapshotContractException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "工程运行输入不满足当前导出合同。", "source", FailureCode: error.Code)); }
        catch (WorkerProtocolException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code == "WorkerSnapshotBudgetExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.ExportInputRejected,
            "固定运行输入或音频格式不满足导出合同。", "source", FailureCode: error.Code)); }
        catch (Exception error) when (error is ArgumentException or OverflowException or RenderSessionFailureException)
        { return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "导出固定来源、画布、帧率或时长超出边界。"); }
        catch (ResourceStoreException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "导出来源资源不可用。", "source")); }
        catch (IOException)
        { return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "固定来源或受控候选存储不可用。"); }
        finally
        {
            try { inputLease?.Dispose(); }
            finally { try { prepared?.Dispose(); } finally { try { audioPin?.Dispose(); } finally { try { slot?.Dispose(); } finally { disk?.Dispose(); } } } }
        }
    }

    private async Task<BackendOperationResult<JobResponse>> StartImageExportCoreAsync(string clientId, ProjectSession session,
        ExportJobRequest request, IBackendExportExecutor executor)
    {
        if (_exportDeployment?.ImageExportAvailable != true || executor is not IBackendImageExportExecutor imageExecutor
            || !imageExecutor.ImageExportAvailable)
            return BackendOperationResult<JobResponse>.Rejected(new(BackendErrorCode.TaskUnavailable,
                "导出 Worker 未提供独立图片导出能力。", "worker", FailureCode: "WorkerImageExportUnavailable"));
        BackendProjectImageReadLease? imageLease = null;
        IDisposable? audioPin = null;
        BackendTaskScheduler.Reservation? slot = null;
        BackendJobArtifactStore.Reservation? disk = null;
        try
        {
            ProjectSnapshot snapshot;
            StoredAsset? capturedAudio = null;
            CompositionFramePlan plan;
            string baseProfile;
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                snapshot = ProjectSnapshot.FromProject(session.Workspace.Project);
                if (ScriptBackendError(snapshot, request.RenderBackend) is { } scriptBackendError)
                    return BackendOperationResult<JobResponse>.Rejected(scriptBackendError);
                baseProfile = ProjectPreviewSnapshotProfile.Select(snapshot with { Background = BackgroundSource.Color });
                if (_exportDeployment.ImageBaseProfiles is not { } bases || !bases.Contains(baseProfile, StringComparer.Ordinal))
                    return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "实际图片导出 Worker 未提供所选基础来源能力。");
                if (ProjectValidator.Validate(session.Workspace.Project, ValidationContext.Export).HasErrors)
                    return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "工程存在阻断导出的领域错误。");
                plan = ValidateExportPlan(snapshot, request.Format);
                imageLease = session.AcquireProjectImageRead(snapshot);
                if (!string.IsNullOrEmpty(session.AudioAssetId))
                {
                    audioPin = _resources.AcquireReference(clientId, session.AudioAssetId);
                    if (request.IncludeAudio) capturedAudio = _resources.GetAuthorized(clientId, session.AudioAssetId);
                }
                else if (request.IncludeAudio)
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "带音轨导出需要已授权的受管音轨资源。");
            }
            finally { session.Writer.Release(); }

            // Native decoding remains Worker-only. All file reads, content checks and envelope encoding happen outside Writer.
            BackendOperationResult<byte[]> read = await imageLease.ReadAsync().ConfigureAwait(false);
            imageLease.Dispose(); imageLease = null;
            if (!read.Accepted) return BackendOperationResult<JobResponse>.Rejected(read.Error!);
            byte[] encoded = ProjectImagePreviewSnapshotCodec.Encode(snapshot, read.Response!);
            BackendExportAudioGrant? grant = null;
            BackendExportAudioReferenceDto? audioReference = null;
            if (capturedAudio is { } audio)
            {
                if (new FileInfo(audio.StoragePath).LinkTarget is not null || audio.Size > _exportOptions!.MaxAudioBytes
                    || snapshot.AudioMedia is { } media && (media.Hash != audio.Sha256 || media.ByteLength != audio.Size))
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "工程音频固定身份与授权资源不一致。");
                VerifyFixedAudioSource(clientId, audio);
                BackendAudioInputInfo info = ClassifyExportAudio(_resources.AcquireRead(clientId, audio.AssetId));
                if (!_exportAudioProfiles.Contains(info.AudioProfile, StringComparer.Ordinal))
                    return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "当前部署没有此固定音频格式的导出能力。");
                if (info.SourceDurationUs is { } duration && duration < plan.VideoDurationUs)
                    return Reject<JobResponse>(BackendErrorCode.ExportAudioUnsupported, "实际音轨不足计划视频时长。");
                VerifyFixedAudioSource(clientId, audio);
                grant = new(audio.StoragePath, audio.Size, audio.Sha256, info.AudioProfile);
                audioReference = new(audio.AssetId, audio.Size, audio.Sha256,
                    info.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? null : info.AudioProfile);
            }
            // Validate the complete task metadata budget before reacquiring Writer; the candidate is a fixed-shape local path.
            imageExecutor.ValidateImageInput(snapshot, read.Response!, new(request.Format, request.IncludeAudio,
                Path.Combine(_persistence!.StateRoot, "export-artifacts", ".staging", new string('0', 32),
                    request.Format == "opaqueMp4" ? "media.mp4" : "media.mov"), grant, request.Quality, _exportOptions!.ResourcePolicy(operationTimeoutSeconds: _exportTimeoutSeconds)));

            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryGet(clientId, session.ProjectSessionId, out ProjectSession current) || !ReferenceEquals(session, current))
                    return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
                if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                    return BackendOperationResult<JobResponse>.Rejected(mutation);
                PruneExportArtifacts();
                disk = _exportArtifacts!.TryReserve();
                if (disk is null) return Reject<JobResponse>(BackendErrorCode.ResourceQuotaExceeded, "导出产物预留配额不足。");
                slot = _taskScheduler!.TryReserve(clientId, session.ProjectSessionId);
                if (slot is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "正式任务排队配额已满。");
                BackendJobInputDto input = new("project", session.ProjectSessionId, request.ExpectedRevision,
                    ProjectImageExportWorkerContract.ExportProfile, WorkerProtocol.ComputeSha256(encoded), plan.TotalFrames,
                    snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
                    snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator,
                    snapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
                ExportWork work = new(snapshot, request.Format, request.IncludeAudio, clientId, capturedAudio,
                    audioPin, slot, disk, executor, read.Response);
                BackendOperationResult<JobResponse> accepted = StartJob(clientId, "export", session.ProjectSessionId,
                    job => RunExportAsync(work, job), input: input, failureMapper: ExportFailure, cancelOnShutdown: true, deferRunning: true,
                    exportInput: new(request.Format, request.IncludeAudio, audioReference,
                        grant is { } audioGrant && WorkerExportContract.GetExecutionProfile(audioGrant.AudioProfile) == WorkerExportContract.CompressedExecutionProfile
                            ? WorkerExportContract.CompressedExecutionProfile : null,
                        ProjectImageExportWorkerContract.ExportProfile, baseProfile, request.Quality, request.RenderBackend), beforeRegistration: job =>
                    {
                        string candidate = _exportArtifacts.CreateCandidate(work.Disk!, job.Id, request.Format);
                        work.Execution = new(request.Format, request.IncludeAudio, candidate, grant, request.Quality,
                        _exportOptions!.ResourcePolicy(work.Disk!.MaximumMediaBytes, _exportTimeoutSeconds));
                        try { imageExecutor.ValidateImageInput(snapshot, read.Response!, work.Execution); return null; }
                        catch (Exception error) when (error is WorkerProtocolException or SnapshotContractException or RenderSessionFailureException or ArgumentException)
                        { return new(BackendErrorCode.ExportInputRejected, "固定图片来源与实际任务封包不满足导出合同。"); }
                    });
                if (accepted.Accepted) { audioPin = null; slot = null; disk = null; }
                return accepted;
            }
            finally { session.Writer.Release(); }
        }
        catch (SnapshotContractException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "工程图片来源不满足当前导出合同。", "background", FailureCode: error.Code)); }
        catch (WorkerProtocolException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code == "WorkerSnapshotBudgetExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.ExportInputRejected,
            "固定图片任务封包或音频格式不满足导出合同。", "source", FailureCode: error.Code)); }
        catch (Exception error) when (error is ArgumentException or OverflowException or RenderSessionFailureException)
        { return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "图片导出固定来源、画布、帧率或时长超出边界。"); }
        catch (ResourceStoreException error)
        { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "图片导出来源资源不可用。", "source")); }
        catch (IOException)
        { return Reject<JobResponse>(BackendErrorCode.ExportInputRejected, "固定来源或受控候选存储不可用。"); }
        finally
        {
            try { imageLease?.Dispose(); }
            finally { try { audioPin?.Dispose(); } finally { try { slot?.Dispose(); } finally { disk?.Dispose(); } } }
        }
    }

    private CompositionFramePlan ValidateExportPlan(ProjectSnapshot snapshot, string format)
    {
        var composition = snapshot.Composition; BackendExportOptions options = _exportOptions!;
        if (composition.OutputDurationUs is not { } duration || duration <= 0 || duration > options.MaxDurationUs
            || composition.FrameRate.Value > options.MaxFrameRate || composition.CanvasWidth < 1 || composition.CanvasHeight < 1
            || composition.CanvasWidth > options.MaxCanvasWidth || composition.CanvasHeight > options.MaxCanvasHeight
            || checked((long)composition.CanvasWidth * composition.CanvasHeight * 4) > options.MaxRawFrameBytes
            || format == "opaqueMp4" && (composition.CanvasWidth % 2 != 0 || composition.CanvasHeight % 2 != 0))
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        CompositionFramePlan plan = CompositionFramePlan.Create(duration, composition.FrameRate);
        if (plan.TotalFrames > options.MaxFrames || plan.VideoDurationUs > options.MaxDurationUs) throw new ArgumentOutOfRangeException(nameof(snapshot));
        return plan;
    }

    private static BackendAudioInputInfo ClassifyExportAudio(ResourceReadLease read)
    {
        Exception? primary = null;
        try { return BackendAudioInputClassifier.Classify(read.Stream, read.Asset.Size); }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { read.Dispose(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["ExportAudioReadCleanupFailure"] = cleanup; }
        }
    }

    private async Task RunExportAsync(ExportWork work, BackendJob job)
    {
        Exception? primary = null;
        try
        {
            await work.Slot!.WaitAsync(job.Cancellation).ConfigureAwait(false);
            lock (_jobGate) { job.Cancellation.ThrowIfCancellationRequested(); job.SetState("running", true); }
            ExportReportDto report = work.ImageBytes is { } imageBytes
                ? await ((IBackendImageExportExecutor)work.Executor).ExecuteImageAsync(work.Snapshot!, imageBytes, work.Execution!,
                    new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false)
                : await work.Executor.ExecuteAsync(work.Snapshot!, work.Execution!, new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false);
            string? backend = job.Read().Input?.RenderBackend;
            if (report.RenderBackend is not null && report.RenderBackend != backend)
                throw new ExportTaskException("ExportReportInvalid", "导出报告渲染后端与固定任务不一致。");
            report = report with { RenderBackend = backend };
            if (work.AudioAsset is { } capturedAudio) VerifyFixedAudioSource(work.ClientId, capturedAudio, job.Cancellation);
            work.ReleaseAudio();
            var composition = work.Snapshot!.Composition; CompositionFramePlan plan = ValidateExportPlan(work.Snapshot, work.Format);
            WorkerExportReportExpectation expected = new(work.Format, work.IncludeAudio, composition.CanvasWidth, composition.CanvasHeight,
                composition.FrameRate.Numerator, composition.FrameRate.Denominator, plan.TotalFrames, plan.VideoDurationUs, work.Execution!.Audio?.AudioProfile,
                work.Execution.Quality, work.Execution.ResourcePolicy);
            byte[] bytes = WorkerExportContract.EncodeReport(report, expected);
            if (bytes.Length > _exportOptions!.MaxReportBytes || report.Diagnostics.Length > _exportOptions.MaxOccurrences)
                throw new ExportTaskException("ExportReportBudgetExceeded", "完整导出报告超过公布预算。");
            await _exportArtifacts!.PrepareAsync(work.Disk!, job.Id, report, bytes, job.Cancellation).ConfigureAwait(false);
            job.Cancellation.ThrowIfCancellationRequested();
            lock (_jobGate)
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                ExportJobResultDto result = _exportArtifacts.Publish(work.Disk!);
                job.Finish(null, exportResult: result);
            }
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is RenderSessionFailureException actual && (actual.DiagnosticSnapshot is { Count: > 0 } || actual.SourceMaterialFailure is not null))
            {
                try
                {
                    if (work.AudioAsset is { } capturedAudio) VerifyFixedAudioSource(work.ClientId, capturedAudio, job.Cancellation);
                    work.ReleaseAudio();
                    ExportFailureReportDto report = BackendExportFailureReportCodec.Project(actual, job.Read(), _exportOptions!.MaxOccurrences);
                    byte[] bytes = BackendExportFailureReportCodec.Encode(report, _exportOptions.MaxReportBytes, _exportOptions.MaxOccurrences);
                    _exportArtifacts!.PrepareFailure(work.Disk!, job.Id, bytes);
                    job.Cancellation.ThrowIfCancellationRequested();
                    lock (_jobGate)
                    {
                        if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                        BackendJobReportReferenceDto reference = _exportArtifacts.PublishFailure(work.Disk!, job.Id);
                        job.Finish(null, ExportFailure(actual), failureCode: actual.Code, failureReportRef: reference);
                    }
                    return;
                }
                catch (Exception reportFailure) { failure.Data["ExportFailureReportFailure"] = reportFailure; }
            }
            throw;
        }
        finally
        {
            try { work.Dispose(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["ExportInputCleanupFailure"] = cleanup; }
        }
    }

    private static BackendErrorResponse ExportFailure(Exception error) => new(BackendErrorCode.TaskExecutionFailed,
        error is ExportTaskException ? "导出候选、完整报告或公开提交失败。" : "独立导出执行器失败，未公开新的成片。");

    private JobResponse? FindExportJob(string clientId, string jobId, bool recovered) => recovered
        ? ReadRecoveredJobs(clientId).FirstOrDefault(value => value.Job.JobId == jobId)?.Job
        : TryFindJob(clientId, jobId, out BackendJob job) ? job.Read() : null;

    public BackendOperationResult<byte[]> ReadExportResult(string clientId, string jobId, bool recovered = false)
    {
        JobResponse? job = FindExportJob(clientId, jobId, recovered);
        if (job is null || (job.ExportResult is null && job.FailureReportRef is null) || _exportArtifacts is null)
            return Reject<byte[]>(BackendErrorCode.ExportArtifactNotFound, "此任务没有登记可读取的完整产物或实际失败报告。");
        try
        {
            if (job.ExportResult is not null && job.FailureReportRef is not null) throw new IOException("成功与失败报告引用不能并存。");
            if (job.FailureReportRef is { } failure)
            {
                _ = ReportExpectation(job);
                byte[] failedBytes = _exportArtifacts.ReadFailureReport(jobId, failure);
                ExportFailureReportDto failed = BackendExportFailureReportCodec.Decode(failedBytes, _exportOptions!.MaxReportBytes, _exportOptions.MaxOccurrences);
                if (failed.Source != job.Input || failed.Input != job.ExportInput || failed.FailureCode != job.FailureCode)
                    throw new IOException("失败报告来源与唯一账本不一致。");
                return BackendOperationResult<byte[]>.Success(failedBytes);
            }
            byte[] bytes = _exportArtifacts.ReadReport(jobId, job.ExportResult!);
            var expected = ReportExpectation(job);
            ExportReportDto report = WorkerExportContract.DecodeReport(bytes, expected);
            if (report.RenderBackend != job.Input!.RenderBackend)
                throw new IOException("导出报告渲染后端与唯一账本不一致。");
            if (report.MediaSize != job.ExportResult!.Artifact.SizeBytes || report.Sha256 != job.ExportResult.Artifact.Sha256)
                throw new IOException("产物报告与账本不一致。");
            return BackendOperationResult<byte[]>.Success(bytes);
        }
        catch (Exception failure) when (failure is IOException or WorkerProtocolException or ArgumentException or OverflowException)
        { return Reject<byte[]>(BackendErrorCode.TaskExecutionFailed, "已登记导出报告固定计划或完整性校验失败。"); }
    }

    private WorkerExportReportExpectation ReportExpectation(JobResponse job)
    {
        if (job.Kind != "export" || job.Input is not { SourceKind: "project" } source || job.ExportInput is null
            || !ValidExportSourceProfile(source.SourceProfile, job.ExportInput)
            || source.RenderBackend is not (null or "cpu" or "gpu") || source.RenderBackend != job.ExportInput.RenderBackend
            || source.SourceId != job.ProjectSessionId || !ValidHash(source.SourceSha256)
            || !long.TryParse(source.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision < 0
            || job.ExportInput is null || !ValidExportAudioReference(job.ExportInput)
            || !long.TryParse(source.OutputDurationUs, NumberStyles.None, CultureInfo.InvariantCulture, out long duration))
            throw new IOException("登记来源不符合导出合同。");
        FrameRate rate = new(source.FrameRateNumerator, source.FrameRateDenominator); CompositionFramePlan plan = CompositionFramePlan.Create(duration, rate);
        if (plan.TotalFrames != source.FramesPerOutput) throw new IOException("登记帧计划不一致。");
        return new(job.ExportInput.Format, job.ExportInput.IncludeAudio, source.CanvasWidth, source.CanvasHeight,
            source.FrameRateNumerator, source.FrameRateDenominator, plan.TotalFrames, plan.VideoDurationUs, job.ExportInput.Audio?.AudioProfile,
            job.ExportInput.Quality, _exportOptions!.ResourcePolicy(operationTimeoutSeconds: _exportTimeoutSeconds));
    }

    internal static bool ValidExportSourceProfile(string sourceProfile, BackendExportInputDto input) =>
        sourceProfile == ProjectImageExportWorkerContract.ExportProfile
            ? input.TaskProfile == ProjectImageExportWorkerContract.ExportProfile
                && input.BaseSourceProfile is BackendSnapshotProfiles.StaticLyricsScene or BackendSnapshotProfiles.StaticLyricsSceneAnalysis
                    or BackendSnapshotProfiles.PackageLyricsScene
                
            : BackendSnapshotProfiles.IsExportSupported(sourceProfile) && input.TaskProfile is null && input.BaseSourceProfile is null;

    private static bool ValidExportAudioReference(BackendExportInputDto input)
    {
        string execution = input.ExecutionProfile ?? WorkerExportContract.ExecutionProfile;
        if (!input.IncludeAudio) return input.Audio is null && execution == WorkerExportContract.ExecutionProfile;
        if (input.Audio is not { } audio || !BackendAudioInputProfiles.TryResolveExportAudioProfile(audio.AudioProfile, out string profile)) return false;
        return execution == WorkerExportContract.GetExecutionProfile(profile)
            && audio.SizeBytes >= 4
            && (profile != BackendAudioInputProfiles.Pcm16Wave || audio.SizeBytes >= 44)
            && audio.AssetId is { Length: > 6 and <= 80 } && audio.AssetId.StartsWith("asset-", StringComparison.Ordinal)
            && audio.AssetId.Skip(6).All(char.IsAsciiLetterOrDigit) && ValidHash(audio.Sha256);
    }
    private static bool ValidHash(string? hash) => hash is { Length: 64 }
        && hash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    public BackendOperationResult<BackendExportReadLease> AcquireExportRead(string clientId, string jobId, bool recovered = false)
    {
        BackendOperationResult<byte[]> report = ReadExportResult(clientId, jobId, recovered);
        if (!report.Accepted) return BackendOperationResult<BackendExportReadLease>.Rejected(report.Error!);
        JobResponse? job = FindExportJob(clientId, jobId, recovered);
        if (job?.ExportResult is null) return Reject<BackendExportReadLease>(BackendErrorCode.ExportArtifactNotFound, "产物已到期。");
        try { return BackendOperationResult<BackendExportReadLease>.Success(_exportArtifacts!.AcquireRead(jobId, job.ExportResult)); }
        catch (IOException) { return Reject<BackendExportReadLease>(BackendErrorCode.TaskExecutionFailed, "已登记媒体长度或类型校验失败。"); }
    }

    public BackendOperationResult<ExportDiagnosticsPageDto> ReadExportDiagnostics(string clientId, string jobId, string? cursor, int? limit, bool recovered = false)
    {
        BackendOperationResult<byte[]> bytes = ReadExportResult(clientId, jobId, recovered);
        if (!bytes.Accepted) return BackendOperationResult<ExportDiagnosticsPageDto>.Rejected(bytes.Error!);
        JobResponse? job = FindExportJob(clientId, jobId, recovered);
        if (job is null || (job.ExportResult is null && job.FailureReportRef is null))
            return Reject<ExportDiagnosticsPageDto>(BackendErrorCode.ExportArtifactNotFound, "报告已到期。");
        int size = limit ?? _exportOptions!.MaxDiagnosticsPageSize;
        if (size < 1 || size > _exportOptions!.MaxDiagnosticsPageSize) return Reject<ExportDiagnosticsPageDto>(BackendErrorCode.DiagnosticLimitExceeded, "诊断页大小超限。");
        string hash = job.FailureReportRef?.Sha256 ?? job.ExportResult!.Report.Sha256; string prefix = jobId + ":" + hash + ":"; int offset = 0;
        if (cursor is not null && (!cursor.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
            return Reject<ExportDiagnosticsPageDto>(BackendErrorCode.DiagnosticCursorExpired, "游标不属于此导出报告。");
        BackendDiagnosticDto[] diagnostics = job.FailureReportRef is null
            ? WorkerExportContract.DecodeReport(bytes.Response!, ReportExpectation(job)).Diagnostics
            : BackendExportFailureReportCodec.Decode(bytes.Response!, _exportOptions!.MaxReportBytes, _exportOptions.MaxOccurrences).Diagnostics;
        if (offset > diagnostics.Length) return Reject<ExportDiagnosticsPageDto>(BackendErrorCode.DiagnosticCursorExpired, "游标超出报告范围。");
        BackendDiagnosticDto[] page = diagnostics.Skip(offset).Take(size).ToArray();
        string? next = offset + page.Length < diagnostics.Length ? prefix + (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null;
        return BackendOperationResult<ExportDiagnosticsPageDto>.Success(new(jobId, hash, page, next, cursor));
    }

    private void PruneExportArtifacts()
    {
        if (_exportArtifacts is null) return;
        lock (_jobGate)
        {
            IEnumerable<JobResponse> current = _jobs.Values.Select(value => value.Read()).Where(value => value.ExpiresAtUtc > _timeProvider.GetUtcNow() || !BackendJobLedger.IsTerminal(value.State));
            IEnumerable<JobResponse> recovered = _ledger?.ReadRecovered().Select(value => value.Job) ?? [];
            _exportArtifacts.Prune(current.Concat(recovered).Where(value => value.ExportResult is not null || value.FailureReportRef is not null)
                .Select(value => value.ExportResult?.Artifact.ArtifactId ?? "artifact-" + value.JobId[4..]));
        }
    }

    private sealed class ExportWork(ProjectSnapshot snapshot, string format, bool includeAudio, string clientId, StoredAsset? audioAsset,
        IDisposable? audio, BackendTaskScheduler.Reservation slot, BackendJobArtifactStore.Reservation disk, IBackendExportExecutor executor, byte[]? imageBytes = null,
        BackendPreparedProjectRuntimeInput? runtimeInput = null) : IDisposable
    {
        internal ProjectSnapshot? Snapshot = snapshot;
        internal byte[]? ImageBytes = imageBytes;
        internal BackendPreparedProjectRuntimeInput? RuntimeInput = runtimeInput;
        internal string Format { get; } = format;
        internal bool IncludeAudio { get; } = includeAudio;
        internal string ClientId { get; } = clientId;
        internal StoredAsset? AudioAsset { get; } = audioAsset;
        internal BackendExportExecutionInput? Execution;
        internal BackendTaskScheduler.Reservation? Slot = slot;
        internal BackendJobArtifactStore.Reservation? Disk = disk;
        internal IBackendExportExecutor Executor { get; } = executor;
        private IDisposable? _audio = audio;
        internal void ReleaseAudio() => Interlocked.Exchange(ref _audio, null)?.Dispose();
        public void Dispose()
        {
            try { ReleaseAudio(); }
            finally { Snapshot = null; ImageBytes = null; Execution = null;
                try { Interlocked.Exchange(ref RuntimeInput, null)?.Dispose(); }
                finally { try { Interlocked.Exchange(ref Slot, null)?.Dispose(); } finally { Interlocked.Exchange(ref Disk, null)?.Dispose(); } } }
        }
    }
}
