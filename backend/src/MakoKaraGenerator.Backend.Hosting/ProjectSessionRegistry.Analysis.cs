using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private IBackendAnalysisExecutor? _analysisExecutor;
    private BackendAnalysisOptions? _analysisOptions;
    private BackendAnalysisCapabilities? _analysisDeployment;
    private BackendAnalysisResultStore? _analysisResults;
    private int _analysisTimeoutSeconds = 60;
    private string[] _analysisAudioProfiles = [];
    private string[] _analysisSourceProfiles = [];

    public void ConfigureAnalysisTasks(IBackendAnalysisExecutor? executor, BackendAnalysisCapabilities deployment,
        BackendAnalysisOptions? options = null, int operationTimeoutSeconds = 60)
    {
        if (_persistence is null || _analysisOptions is not null) throw new InvalidOperationException("音频分析只在既有任务宿主配置一次。");
        ArgumentNullException.ThrowIfNull(deployment); options ??= new(); options.Validate();
        if (operationTimeoutSeconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(operationTimeoutSeconds));
        EnsureTaskScheduler(); _analysisOptions = options; _analysisDeployment = deployment; _analysisTimeoutSeconds = operationTimeoutSeconds;
        string[] audioProfiles = deployment.AudioProfiles ?? [BackendAudioInputProfiles.Pcm16Wave];
        string[] sourceProfiles = deployment.SourceProfiles ?? [BackendAudioInputProfiles.LegacyAnalysisSource];
        bool validProfiles = audioProfiles.Length > 0 && sourceProfiles.Length > 0
            && audioProfiles.Distinct(StringComparer.Ordinal).Count() == audioProfiles.Length
            && sourceProfiles.Distinct(StringComparer.Ordinal).Count() == sourceProfiles.Length
            && audioProfiles.All(BackendAudioInputProfiles.IsSupported)
            && sourceProfiles.All(BackendAudioInputProfiles.IsAnalysisSourceSupported)
            && audioProfiles.All(profile => sourceProfiles.Contains(BackendAudioInputProfiles.GetAnalysisSourceProfile(profile), StringComparer.Ordinal))
            && sourceProfiles.All(profile => audioProfiles.Any(audio => BackendAudioInputProfiles.GetAnalysisSourceProfile(audio) == profile));
        _analysisExecutor = deployment.GuardAvailable && deployment.ToolStartupVerified && validProfiles ? executor : null;
        if (_analysisExecutor is not null) { _analysisAudioProfiles = [.. audioProfiles]; _analysisSourceProfiles = [.. sourceProfiles]; }
        _analysisResults = new(_persistence.StateRoot, options); PruneAnalysisResults();
    }
    public AnalysisTaskCapabilitiesDto AnalysisCapabilities
    {
        get
        {
            BackendAnalysisOptions options = _analysisOptions ?? new(); BackendTaskSchedulingOptions scheduling = _taskScheduler?.Options ?? new();
            bool enabled = _analysisExecutor is not null && !_persistenceStopping;
            return new(enabled, AnalysisResultCodec.SourceProfile, AudioAnalysisProfile.AlgorithmVersion, enabled ? [.. _analysisAudioProfiles] : [],
                AnalysisResultCodec.MaximumAudioBytes, 192000, Format(AnalysisResultCodec.MaximumSourceDurationUs),
                AudioAnalysisProfile.SampleRate, AudioAnalysisProfile.HopSamples, Format(AnalysisResultCodec.MaximumSamples), AnalysisResultCodec.MaximumPoints,
                options.MaxResultBytes, options.MaxResultStorageBytes, _analysisTimeoutSeconds, 2,
                scheduling.MaxRunning, scheduling.MaxRunningPerClient, scheduling.MaxRunningPerProject, scheduling.MaxQueued, scheduling.MaxQueuedPerClient,
                _analysisDeployment?.GuardAvailable == true, _analysisDeployment?.ToolStartupVerified == true,
                enabled ? null : new(BackendErrorCode.TaskUnavailable, "当前部署缺少已验证的音频分析Worker进程归属或工具启动能力。", "analysis"), enabled ? [.. _analysisSourceProfiles] : []);
        }
    }
    public Task<BackendOperationResult<JobResponse>> StartAnalysisAsync(string clientId, string sessionId,
        AnalysisJobRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, "POST /api/v1/project-sessions/" + sessionId + "/analysis-jobs",
            request with { Metadata = null }, () => StartAnalysisCoreAsync(clientId, sessionId, request), cancellationToken);

    private async Task<BackendOperationResult<JobResponse>> StartAnalysisCoreAsync(string clientId, string sessionId, AnalysisJobRequest request)
    {
        if (!AnalysisCapabilities.Enabled) return BackendOperationResult<JobResponse>.Rejected(AnalysisCapabilities.DisabledReason!);
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
        IDisposable? pin = null; BackendTaskScheduler.Reservation? slot = null; BackendJobSidecarStore.Reservation? disk = null;
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } mutation)
                return BackendOperationResult<JobResponse>.Rejected(mutation);
            if (string.IsNullOrEmpty(session.AudioAssetId)) return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "分析需要工程当前授权音频。");
            pin = _resources.AcquireReference(clientId, session.AudioAssetId);
            using ResourceReadLease read = _resources.AcquireRead(clientId, session.AudioAssetId);
            StoredAsset asset = read.Asset;
            if (asset.Size > AnalysisResultCodec.MaximumAudioBytes)
                return Reject<JobResponse>(BackendErrorCode.ResourceTooLarge, "源音频超过16MiB分析边界。");
            if (new FileInfo(asset.StoragePath).LinkTarget is not null || session.Workspace.Project.AudioMedia is { } media
                && (media.Hash != asset.Sha256 || media.ByteLength != asset.Size))
                return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "工程音频身份与固定授权资源不一致。");
            BackendAudioInputInfo audio = BackendAudioInputClassifier.Classify(read.Stream, asset.Size);
            if (!_analysisAudioProfiles.Contains(audio.AudioProfile, StringComparer.Ordinal))
                return Reject<JobResponse>(BackendErrorCode.TaskUnavailable, "当前部署没有此固定音频格式的分析能力。");
            if (audio.SourceSampleCount is { } samples
                && (Int128)samples * 1_000_000 > (Int128)AnalysisResultCodec.MaximumSourceDurationUs * audio.SampleRate)
                return Reject<JobResponse>(BackendErrorCode.ResourceTooLarge, "源音频超过300秒分析边界。");
            BackendAudioAnalysisGrant grant = new(asset.StoragePath, asset.Size, asset.Sha256, audio.AudioProfile);
            _analysisExecutor!.ValidateInput(grant);
            PruneAnalysisResults(); disk = _analysisResults!.TryReserve();
            if (disk is null) return Reject<JobResponse>(BackendErrorCode.ResourceQuotaExceeded, "完整分析结果预留配额不足。");
            slot = _taskScheduler!.TryReserve(clientId, sessionId);
            if (slot is null) return Reject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "正式任务排队配额已满。");
            BackendAnalysisJobInputDto input = new("project", sessionId, request.ExpectedRevision, BackendAudioInputProfiles.GetAnalysisSourceProfile(audio.AudioProfile),
                new(asset.AssetId, asset.Size, asset.Sha256, audio.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? null : audio.AudioProfile),
                audio.SourceDurationUs is { } duration ? Format(duration) : null);
            AnalysisWork work = new(grant, pin, slot, disk, _analysisExecutor);
            BackendOperationResult<JobResponse> accepted = StartJob(clientId, "analysis", sessionId, job => RunAnalysisAsync(work, job),
                failureMapper: AnalysisFailure, cancelOnShutdown: true, deferRunning: true, analysisInput: input);
            if (accepted.Accepted) { pin = null; slot = null; disk = null; }
            return accepted;
        }
        catch (WorkerProtocolException) { return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "固定音频不满足受支持WAV／MP3／FLAC候选或分析输入预算。"); }
        catch (ResourceStoreException error) { return BackendOperationResult<JobResponse>.Rejected(new(error.Code, "固定音频资源不可用。", "audio")); }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException or RenderSessionFailureException)
        { return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "固定分析来源不可用或不符合当前输入合同。"); }
        finally { session.Writer.Release(); try { pin?.Dispose(); } finally { try { slot?.Dispose(); } finally { disk?.Dispose(); } } }
    }
    private async Task RunAnalysisAsync(AnalysisWork work, BackendJob job)
    {
        Exception? primary = null;
        BackendJobSidecarStore.Prepared? prepared = null;
        try
        {
            await work.Slot!.WaitAsync(job.Cancellation).ConfigureAwait(false);
            lock (_jobGate)
            { job.Cancellation.ThrowIfCancellationRequested(); job.SetState("running", true); job.ReportAnalysisProgress(new("validating-source", "0")); }
            byte[] bytes = await work.Executor.ExecuteAsync(work.Grant!, new InlineAnalysisProgress(job), job.Cancellation).ConfigureAwait(false);
            // ExecuteAsync owns cancellation/deadline cleanup and returns only after all children and pipes are drained.
            work.ReleaseAudio();
            AudioAnalysisRevision result = AnalysisResultCodec.Decode(bytes, work.Grant!.Sha256);
            job.ReportAnalysisProgress(new("prepared", Format(result.SampleCount), Format(result.SampleCount), Format(result.DurationUs)));
            prepared = _analysisResults!.Prepare(job.Id, bytes, work.Disk!);
            job.Cancellation.ThrowIfCancellationRequested();
            lock (_jobGate)
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                BackendJobReportReferenceDto reference = prepared.Commit();
                job.Finish(null, analysisResult: SummarizeAnalysis(result, reference));
            }
        }
        catch (JobSidecarException error) { primary = error; throw new AnalysisTaskException(error.Code, error); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { prepared?.Dispose(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["AnalysisResultCleanupFailure"] = cleanup; }
            finally
            {
                try { work.Dispose(); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["AnalysisInputCleanupFailure"] = cleanup; }
            }
        }
    }
    private static AnalysisJobResultDto SummarizeAnalysis(AudioAnalysisRevision result, BackendJobReportReferenceDto reference) =>
        new(AudioAnalysisProfile.AlgorithmVersion, AudioAnalysisProfile.SampleRate, AudioAnalysisProfile.HopSamples, "0",
            result.SourceHash, result.PcmHash, Format(result.SampleCount), Format(result.DurationUs), result.Revision, result.ContentHash, result.Points.Length, reference);
    private static BackendErrorResponse AnalysisFailure(Exception error) =>
        new(error is AnalysisTaskException { Code: "AnalysisResultStorageQuotaExceeded" } ? BackendErrorCode.ResourceQuotaExceeded : BackendErrorCode.TaskExecutionFailed,
            "离线音频分析执行、完整结果校验或发布失败。");

    public BackendOperationResult<BackendAnalysisReadLease> AcquireAnalysisRead(string clientId, string jobId, bool recovered = false)
    {
        JobResponse? response = recovered ? ReadRecoveredJobs(clientId).FirstOrDefault(value => value.Job.JobId == jobId)?.Job
            : TryFindJob(clientId, jobId, out BackendJob job) ? job.Read() : null;
        if (response?.AnalysisResult is null || _analysisResults is null)
            return Reject<BackendAnalysisReadLease>(BackendErrorCode.JobNotFound, "此任务没有已登记的完整分析结果或保留期已结束。");
        BackendJobSidecarStore.ReadLease? lease = null;
        try
        {
            (lease, _) = ReadRegisteredAnalysis(jobId, response);
            return BackendOperationResult<BackendAnalysisReadLease>.Success(new(lease));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkerProtocolException or ArgumentException)
        { lease?.Dispose(); return Reject<BackendAnalysisReadLease>(BackendErrorCode.TaskExecutionFailed, "已登记完整分析结果不可用或完整性校验失败。"); }
    }

    private (BackendJobSidecarStore.ReadLease Lease, AudioAnalysisRevision Data) ReadRegisteredAnalysis(string jobId, JobResponse response)
    {
        BackendJobSidecarStore.ReadLease? lease = null;
        try
        {
            if (response.Kind != "analysis" || response.AnalysisInput is not { SourceKind: "project" } input
                || input.Audio is null
                || !BackendAudioInputProfiles.TryResolveAnalysisAudioProfile(input.SourceProfile, input.Audio.AudioProfile, out string audioProfile)
                || input.SourceId != response.ProjectSessionId || !long.TryParse(input.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision < 0
                || input.Audio.SizeBytes is < 4 or > AnalysisResultCodec.MaximumAudioBytes
                || audioProfile == BackendAudioInputProfiles.Pcm16Wave && input.Audio.SizeBytes < 44
                || string.IsNullOrEmpty(input.Audio.AssetId) || input.Audio.Sha256 is not { Length: 64 }
                || input.Audio.Sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || !ValidAnalysisSourceDuration(audioProfile, input.SourceDurationUs))
                throw new IOException("分析来源合同不符。");
            lease = _analysisResults!.AcquireRead(jobId, response.AnalysisResult!.Report);
            AudioAnalysisRevision result = AnalysisResultCodec.Decode(lease.ReadVerifiedBytes(), input.Audio.Sha256);
            if (SummarizeAnalysis(result, lease.Reference) != response.AnalysisResult) throw new IOException("分析摘要与账本不一致。");
            return (lease, result);
        }
        catch (Exception primary)
        {
            try { lease?.Dispose(); }
            catch (Exception cleanup) { primary.Data["AnalysisReadCleanupFailure"] = cleanup; }
            throw;
        }
    }
    private static bool ValidAnalysisSourceDuration(string audioProfile, string? duration) =>
        audioProfile == BackendAudioInputProfiles.Pcm16Wave
            ? long.TryParse(duration, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                && value is > 0 and <= AnalysisResultCodec.MaximumSourceDurationUs
            : duration is null;
    private void PruneAnalysisResults()
    {
        if (_analysisResults is null) return;
        lock (_jobGate)
        {
            IEnumerable<JobResponse> current = _jobs.Values.Select(value => value.Read()).Where(value => value.ExpiresAtUtc > _timeProvider.GetUtcNow());
            IEnumerable<JobResponse> recovered = _ledger?.ReadRecovered().Select(value => value.Job) ?? [];
            _analysisResults.Prune(current.Concat(recovered).Where(value => value.AnalysisResult is not null).Select(value => value.AnalysisResult!.Report.ReportId));
        }
    }
    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    private sealed class InlineAnalysisProgress(BackendJob job) : IProgress<BackendAnalysisProgressDto>
    { public void Report(BackendAnalysisProgressDto value) => job.ReportAnalysisProgress(value); }
    private sealed class AnalysisWork(BackendAudioAnalysisGrant grant, IDisposable audio, BackendTaskScheduler.Reservation slot,
        BackendJobSidecarStore.Reservation disk, IBackendAnalysisExecutor executor) : IDisposable
    {
        internal BackendAudioAnalysisGrant? Grant = grant;
        internal BackendTaskScheduler.Reservation? Slot = slot;
        internal BackendJobSidecarStore.Reservation? Disk = disk;
        internal IBackendAnalysisExecutor Executor { get; } = executor;
        private IDisposable? _audio = audio;
        internal void ReleaseAudio() => Interlocked.Exchange(ref _audio, null)?.Dispose();
        public void Dispose()
        { try { ReleaseAudio(); } finally { Grant = null; try { Interlocked.Exchange(ref Slot, null)?.Dispose(); } finally { Interlocked.Exchange(ref Disk, null)?.Dispose(); } } }
    }
}
