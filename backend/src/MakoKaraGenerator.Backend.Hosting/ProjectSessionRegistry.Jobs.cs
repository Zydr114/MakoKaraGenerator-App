using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private BackendTaskScheduler? _taskScheduler;
    private void EnsureTaskScheduler(BackendTaskSchedulingOptions? options = null)
    {
        options ??= _taskScheduler?.Options ?? new();
        if (_taskScheduler is not null && _taskScheduler.Options != options)
            throw new InvalidOperationException("所有正式任务必须使用同一调度预算。");
        _taskScheduler ??= new(options);
    }

    public BackendOperationResult<JobResponse> ReadJob(string clientId, string jobId) =>
        TryFindJob(clientId, jobId, out BackendJob job) ? BackendOperationResult<JobResponse>.Success(job.Read())
            : Reject<JobResponse>(BackendErrorCode.JobNotFound, "任务不存在或保留期已经结束。");

    /// <summary>通过 Hosting 的唯一 client 级回执取消一个仍可取消的任务。</summary>
    public Task<BackendOperationResult<JobResponse>> CancelJobAsync(string clientId, string jobId,
        RequestMetadataDto? metadata, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, metadata, "POST /api/v1/jobs/" + jobId + "/cancel", null,
            () => Task.FromResult(CancelJob(clientId, jobId)), cancellationToken);

    public BackendOperationResult<JobResponse> CancelJob(string clientId, string jobId) =>
        TryFindJob(clientId, jobId, out BackendJob job) ? job.Cancel()
            : Reject<JobResponse>(BackendErrorCode.JobNotFound, "任务不存在或保留期已经结束。");
    private BackendOperationResult<JobResponse> StartJob(string clientId, string kind, string? sessionId, Func<BackendJob, Task> action,
        string? outputDirectory = null, BackendJobInputDto? input = null,
        Func<Exception, BackendErrorResponse>? failureMapper = null, bool cancelOnShutdown = false, bool deferRunning = false, BackendExportInputDto? exportInput = null,
        Func<BackendJob, BackendErrorResponse?>? beforeRegistration = null, BackendAnalysisJobInputDto? analysisInput = null,
        AuthorCompilationJobInputDto? authorCompilationInput = null, AuthorValidationJobInputDto? authorValidationInput = null)
    {
        lock (_jobGate)
        {
            if (_persistence is null || _persistenceStopping) return Reject<JobResponse>(BackendErrorCode.SessionClosing, "持久服务未配置或正在停止。");
            foreach (BackendJob expired in _jobs.Values.Where(job => job.Read().ExpiresAtUtc <= _timeProvider.GetUtcNow() && job.Completion.IsCompleted).ToArray())
            { _jobs.TryRemove(expired.Id, out _); expired.Dispose(); }
            if (_jobs.Values.Count(job => job.Owner == clientId) >= _persistence.MaxJobsPerClient)
                return Reject<JobResponse>(BackendErrorCode.ResourceQuotaExceeded, "任务保留配额已达到上限。");
            (string? root, string? relative) = DescribeOutput(outputDirectory);
            BackendJob job = new(clientId, kind, sessionId, _timeProvider.GetUtcNow() + _persistence.JobRetention,
                response => _ledger!.Write(response, clientId, root, relative, outputDirectory), _timeProvider, _persistence.JobRetention,
                _jobGate, summary => _events.AppendJob(clientId, BackendJobLedger.IsTerminal(summary.State) ? "job.terminal" :
                    summary.Progress is null && summary.AnalysisProgress is null ? "job.changed" : "job.progress", summary), input, failureMapper, cancelOnShutdown, exportInput, analysisInput,
                authorCompilationInput: authorCompilationInput, authorValidationInput: authorValidationInput);
            try
            {
                if (beforeRegistration?.Invoke(job) is { } preflight)
                { job.Dispose(); return BackendOperationResult<JobResponse>.Rejected(preflight); }
                job.PersistQueued();
            }
            catch (Exception error) { job.Dispose(); return failureMapper is null || error is ResourceStoreException
                ? PersistenceFailure<JobResponse>(error) : BackendOperationResult<JobResponse>.Rejected(failureMapper(error)); }
            _jobs.TryAdd(job.Id, job);
            job.Start(action, deferRunning);
            return BackendOperationResult<JobResponse>.Success(job.Read());
        }
    }

    private (string? Root, string? Relative) DescribeOutput(string? path)
    {
        if (path is null || _persistence is null) return (null, null);
        foreach ((string id, string root) in _persistence.AllowedProjectRoots)
        {
            string relative = Path.GetRelativePath(ProjectWriteLease.ResolvePhysicalPath(root), path);
            try { if (ProjectDirectoryHandles.Resolve(ProjectWriteLease.ResolvePhysicalPath(root), relative) == path) return (id, relative.Replace('\\', '/')); }
            catch (IOException) { }
        }
        return (null, null);
    }
    private bool TryFindJob(string clientId, string jobId, out BackendJob job) =>
        _jobs.TryGetValue(jobId, out job!) && job.Owner == clientId && (job.Read().ExpiresAtUtc > _timeProvider.GetUtcNow() || !job.Completion.IsCompleted);
    private BackendJob[] ActiveJobs(string sessionId) => [.. _jobs.Values.Where(job => job.Read().ProjectSessionId == sessionId && !job.Completion.IsCompleted)];
    internal JobSummaryDto[] BuildJobSummaries(string clientId) => [.. _jobs.Values.Where(job => job.Owner == clientId
        && (job.Read().ExpiresAtUtc > _timeProvider.GetUtcNow() || !job.Completion.IsCompleted)).Select(job => job.ToSummary())];
}
