using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed class BackendJob : IDisposable
{
    private readonly object _gate;
    private JobResponse _response;
    private readonly CancellationTokenSource _cancel = new();
    private Task _execution = Task.CompletedTask;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<JobResponse> _persist;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly Action<JobSummaryDto> _publish;
    private readonly Func<Exception, BackendErrorResponse>? _failureMapper;
    internal BackendJob(string owner, string kind, string? sessionId, DateTimeOffset expires,
        Action<JobResponse> persist, TimeProvider time, TimeSpan retention, object gate, Action<JobSummaryDto> publish,
        BackendJobInputDto? input = null, Func<Exception, BackendErrorResponse>? failureMapper = null, bool cancelOnShutdown = false, BackendExportInputDto? exportInput = null, BackendAnalysisJobInputDto? analysisInput = null,
        AuthorCompilationJobInputDto? authorCompilationInput = null, AuthorValidationJobInputDto? authorValidationInput = null)
    {
        Owner = owner;
        _persist = persist; _time = time; _retention = retention;
        _gate = gate; _publish = publish;
        _failureMapper = failureMapper; CancelOnShutdown = cancelOnShutdown;
        _response = new("job-" + Guid.NewGuid().ToString("N"), kind, "queued", sessionId, null, null, null, null, null, expires, false, true, Input: input, ExportInput: exportInput, AnalysisInput: analysisInput,
            AuthorCompilationInput: authorCompilationInput, AuthorValidationInput: authorValidationInput);
    }
    internal string Owner { get; }
    internal string Id => _response.JobId;
    internal CancellationToken Cancellation => _cancel.Token;
    internal Task Completion => _completion.Task;
    internal bool CancelOnShutdown { get; }
    internal void PersistQueued() { _persist(Read()); _publish(ToSummary()); }
    internal JobResponse Read() { lock (_gate) return _response with { Diagnostics = _response.Diagnostics is { } diagnostics ? [.. diagnostics] : null }; }
    internal JobSummaryDto ToSummary() { lock (_gate) return new(_response.JobId, _response.Kind, _response.State,
        _response.ProjectSessionId, _response.CanCancel, _response.ExpiresAtUtc, _response.Error?.Code, _response.Progress, _response.FailureCode, _response.AnalysisProgress); }
    internal void Start(Func<BackendJob, Task> action, bool deferRunning = false)
    {
        // The owner keeps this task and awaits it during close/shutdown; it is never fire-and-forget disposal.
        _execution = Task.Run(async () =>
        {
            try
            {
                if (!deferRunning) SetState("running", true);
                await action(this).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested || _failureMapper is null)
            { Finish(null, new(BackendErrorCode.CommandRejected, "任务在发布前取消。"), cancelled: true); }
            catch (Exception error)
            {
                BackendErrorResponse failure = _failureMapper?.Invoke(error) ?? (error switch
                {
                    ResourceStoreException resource => new(resource.Code, resource.Message, resource.Field),
                    Application.ProjectWriteLeaseConflictException => new(BackendErrorCode.WriteLeaseConflict, "工程目录已经有写者。"),
                    _ => new(BackendErrorCode.ProjectLoadFailed, "持久操作失败，原工程与原会话保留。"),
                });
                Finish(null, failure, failureCode: error is RenderSessionFailureException runtime ? runtime.Code : error is ValidationTaskException validation ? validation.Code : error is ExportTaskException export ? export.Code : error is AnalysisTaskException analysis ? analysis.Code : error is WorkerProtocolException protocol ? protocol.Code : _failureMapper is null || _response.Kind is "open" or "recover" ? null : "TaskExecutionFailed");
            }
            finally
            {
                lock (_gate) _execution = Task.CompletedTask;
                _completion.TrySetResult();
            }
        });
    }
    internal void SetState(string state, bool canCancel)
    { lock (_gate) { _response = _response with { State = state, CanCancel = canCancel }; _publish(ToSummary()); } }
    internal void ReportProgress(BackendJobProgressDto progress)
    {
        lock (_gate)
        {
            if (_response.State != "running" || progress.CompletedFrames < 0 || progress.TotalFrames <= 0
                || progress.CompletedFrames > progress.TotalFrames
                || _response.Progress is { } previous && (progress.TotalFrames != previous.TotalFrames || progress.CompletedFrames < previous.CompletedFrames))
                return;
            _response = _response with { Progress = progress };
            _publish(ToSummary());
        }
    }
    internal void ReportAnalysisProgress(BackendAnalysisProgressDto progress)
    {
        lock (_gate)
        {
            if (_response.State != "running" || !AnalysisProgressValid(progress)
                || _response.AnalysisProgress is { } previous &&
                (long.Parse(progress.CompletedSamples, System.Globalization.CultureInfo.InvariantCulture) < long.Parse(previous.CompletedSamples, System.Globalization.CultureInfo.InvariantCulture)
                || previous.TotalSamples is not null && progress.TotalSamples != previous.TotalSamples)) return;
            _response = _response with { AnalysisProgress = progress }; _publish(ToSummary());
        }
    }
    private static bool AnalysisProgressValid(BackendAnalysisProgressDto progress) =>
        !string.IsNullOrWhiteSpace(progress.Stage) && progress.Stage.Length <= 64
        && long.TryParse(progress.CompletedSamples, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long done)
        && done is >= 0 and <= AnalysisResultCodec.MaximumSamples
        && (progress.TotalSamples is null || long.TryParse(progress.TotalSamples, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long total)
            && total is > 0 and <= AnalysisResultCodec.MaximumSamples && done <= total)
        && (progress.TimeUs is null || long.TryParse(progress.TimeUs, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long time)
            && time >= 0 && time <= AnalysisResultCodec.MaximumSourceDurationUs);
    internal bool EnterCommitting()
    {
        lock (_gate)
        {
            if (_response.CancellationRequested || _cancel.IsCancellationRequested) return false;
            _response = _response with { State = "committing", CanCancel = false,
                AnalysisProgress = _response.AnalysisProgress is null ? null : _response.AnalysisProgress with { Stage = "committing" },
                Progress = _response.Progress is null ? null : _response.Progress with { Stage = "committing" } };
            _persist(_response);
            _publish(ToSummary());
            return true;
        }
    }
    internal void Finish(ProjectSessionSummaryResponse? result, BackendErrorResponse? error = null,
        string? savedRevision = null, bool cancelled = false, BackendDiagnosticDto[]? diagnostics = null,
        ValidationJobResultDto? validationResult = null, string? failureCode = null, ExportJobResultDto? exportResult = null, BackendJobReportReferenceDto? failureReportRef = null, AnalysisJobResultDto? analysisResult = null,
        BackendJobReportReferenceDto? authorCompilationResult = null)
    {
        lock (_gate)
        {
            _response = _response with {
            State = cancelled ? "cancelled" : error is null ? "succeeded" : "failed",
            CanCancel = false, Result = result, ProjectSessionId = result?.ProjectSessionId ?? _response.ProjectSessionId,
            SavedProjectRevision = savedRevision, CurrentProjectRevision = result?.ProjectRevision,
            IsDirty = result?.IsDirty, Error = error, ExpiresAtUtc = _time.GetUtcNow() + _retention, Diagnostics = diagnostics,
            ValidationResult = validationResult, FailureCode = failureCode, ExportResult = exportResult, FailureReportRef = failureReportRef, AnalysisResult = analysisResult,
            AuthorCompilationResult = authorCompilationResult,
            AnalysisProgress = _response.AnalysisProgress is null ? null : _response.AnalysisProgress with { Stage = cancelled ? "cancelled" : error is null ? "completed" : "failed" },
            Progress = _response.Progress is null ? null : _response.Progress with { Stage = cancelled ? "cancelled" : error is null ? "completed" : "failed" },
            };
            try { _persist(_response); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ResourceStoreException)
            {
                _response = _response with { State = "unknown", Error = new(BackendErrorCode.InternalError,
                    "任务终态无法记录，请核对实际工程入口；已经发布的工程不会回滚。") };
            }
            _publish(ToSummary());
        }
    }
    internal BackendOperationResult<JobResponse> Cancel()
    {
        JobResponse response;
        lock (_gate)
        {
            if (BackendJobLedger.IsTerminal(_response.State)) return BackendOperationResult<JobResponse>.Success(Read());
            if (!_response.CanCancel) return BackendOperationResult<JobResponse>.Rejected(new(BackendErrorCode.OperationNotCancellable,
                "任务已进入不可撤回的提交阶段，请查询最终结果。"));
            _response = _response with { CancellationRequested = true };
            _publish(ToSummary());
            response = _response;
        }
        _cancel.Cancel();
        return BackendOperationResult<JobResponse>.Success(response);
    }
    public void Dispose() => _cancel.Dispose();
}
