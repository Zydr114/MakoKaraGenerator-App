using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    public const string AuthorValidationProfile = "author-script-sample-v1";
    public Task<BackendOperationResult<JobResponse>> StartAuthorValidationAsync(string clientId, string draftId,
        AuthorValidationJobRequest request, CancellationToken cancellationToken = default) =>
        RunAuthorMutationAsync(clientId, draftId, request.Metadata, "POST /api/v1/drafts/validation-jobs",
            request with { Metadata = null }, document => StartAuthorValidationCoreAsync(clientId, document, request, cancellationToken), cancellationToken);

    private async Task<BackendOperationResult<JobResponse>> StartAuthorValidationCoreAsync(string clientId, AuthorDraftDocument document,
        AuthorValidationJobRequest request, CancellationToken cancellationToken)
    {
        if (request.RenderBackend is not (null or "cpu" or "gpu"))
            return BackendOperationResult<JobResponse>.Rejected(InvalidRenderBackend());
        if (_validationExecutor is null)
            return AuthorReject<JobResponse>(BackendErrorCode.TaskUnavailable, "作者验证Worker未部署固定Runtime验证能力。", "worker");
        var selection = await SelectTaskRenderBackendAsync(_validationExecutor, request.RenderBackend, cancellationToken).ConfigureAwait(false);
        if (!selection.Accepted) return BackendOperationResult<JobResponse>.Rejected(selection.Error!);
        if (selection.Response!.Executor is not IBackendRuntimeValidationExecutor { RuntimeValidationAvailable: true } runtimeExecutor)
            return AuthorReject<JobResponse>(BackendErrorCode.TaskUnavailable, "作者验证Worker未部署固定Runtime验证能力。", "worker");
        request = request with { RenderBackend = selection.Response.RenderBackend };
        if (_validationOptions is null || _validationReports is null || _taskScheduler is null)
            return AuthorReject<JobResponse>(BackendErrorCode.TaskUnavailable, "正式验证任务尚未配置。", "validation");
        if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
            return AuthorReject<JobResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
        if (request.SampleId is not (TemplatePreviewLimits.SingleLineSampleId or TemplatePreviewLimits.MultiLineSampleId))
            return AuthorReject<JobResponse>(BackendErrorCode.InvalidRequest, "作者验证样本版本不受支持。", "sampleId");
        try
        {
            AuthorDraftConfigurationDocument saved = document.Configuration ?? DefaultConfiguration(document);
            EffectPackageContents package = await LoadPackageAsync(document.DraftId, saved.SourceRevision,
                _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            ScriptConfiguration configuration = BuildConfiguration(package, saved);
            TemplateMaterialSlot slot = ParseMaterialSlot(request.MaterialSlot, configuration);
            bool multiLine = request.SampleId == TemplatePreviewLimits.MultiLineSampleId;
            TemplateSampleTarget[] targets = ScriptSampleBuilder.Targets(configuration, slot,
                request.SampleId == TemplatePreviewLimits.MultiLineSampleId);
            TemplateSampleTarget? target = targets.FirstOrDefault(item => item.Key == request.TargetKey);
            if (target is null) return AuthorReject<JobResponse>(BackendErrorCode.InvalidRequest, "作者验证目标不属于固定样本。", "targetKey");
            AuthorValidationJobInputDto authorInput = new(document.DraftId, document.DraftRevision,
                saved.SourceRevision, request.SampleId, target.Key, request.MaterialSlot, request.RenderBackend);
            BackendTaskScheduler.Reservation? reservation = _taskScheduler.TryReserve(clientId, "author-draft:" + document.DraftId);
            if (reservation is null) return AuthorReject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "验证任务排队配额已满。", "draftId");
            AuthorValidationWork? work = null;
            try
            {
                work = await AuthorValidationWork.PrepareAsync(runtimeExecutor, new(configuration, target, slot, multiLine),
                    reservation, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                ProjectSnapshot sampleSnapshot = work.Input.Snapshot;
                ValidateValidationPlan(sampleSnapshot, ScriptSampleValidator.FramesPerOutput);
                if (work.Input.SourceBytes.Length > RuntimeValidationWorkerContract.MaximumInputBytes)
                    return AuthorReject<JobResponse>(BackendErrorCode.ResourceTooLarge, "作者固定输入超过正式验证封包预算。", "source");
                BackendJobInputDto input = new("authorDraft", document.DraftId, saved.SourceRevision, AuthorValidationProfile,
                    WorkerProtocol.ComputeSha256(work.Input.SourceBytes.Span), ScriptSampleValidator.FramesPerOutput,
                    sampleSnapshot.Composition.CanvasWidth, sampleSnapshot.Composition.CanvasHeight,
                    sampleSnapshot.Composition.FrameRate.Numerator, sampleSnapshot.Composition.FrameRate.Denominator,
                    sampleSnapshot.Composition.OutputDurationUs!.Value.ToString(CultureInfo.InvariantCulture), RenderBackend: request.RenderBackend);
                PruneValidationReports();
                AuthorValidationWork captured = work;
                BackendOperationResult<JobResponse> accepted = StartJob(clientId, "validation", null,
                    job => RunAuthorValidationAsync(captured, job), input: input, failureMapper: ValidationFailure,
                    cancelOnShutdown: true, deferRunning: true, authorValidationInput: authorInput);
                if (accepted.Accepted) { reservation = null; work = null; }
                return accepted;
            }
            finally { if (work is not null) await work.DisposeAsync().ConfigureAwait(false); reservation?.Dispose(); }
        }
        catch (DirectoryNotFoundException) { return AuthorReject<JobResponse>(BackendErrorCode.ResourceNotFound, "作者配置固定源码不存在。", "sourceRevision"); }
        catch (EffectPackageException error) { return AuthorReject<JobResponse>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (TemplateSampleException error) { return AuthorReject<JobResponse>(BackendErrorCode.InvalidRequest, "作者试样选区不受支持。", "targetKey", error.Code); }
        catch (ArgumentException error) { return AuthorReject<JobResponse>(BackendErrorCode.InvalidRequest, error.Message, "configuration", "AuthorConfigurationInvalid"); }
        catch (WorkerProtocolException error) { return AuthorReject<JobResponse>(BackendErrorCode.InvalidRequest, error.Message, "sampleId", error.Code); }
        catch (AuthorSamplePreparationException error) { return BackendOperationResult<JobResponse>.Rejected(error.Error); }
    }

    private static TemplateMaterialSlot ParseMaterialSlot(string? value, ScriptConfiguration configuration)
    {
        if (configuration.Kind != ScriptConfigurationKind.Material)
        {
            if (value is not null) throw new ArgumentException("效果配置不得提供材质槽位。", nameof(value));
            return TemplateMaterialSlot.UnsungFill;
        }
        return value switch
        {
            "unsungFill" => TemplateMaterialSlot.UnsungFill,
            "sungFill" => TemplateMaterialSlot.SungFill,
            "stroke" => TemplateMaterialSlot.Stroke,
            _ => throw new ArgumentException("材质配置必须提供合法材质槽位。", nameof(value)),
        };
    }

    private async Task RunAuthorValidationAsync(AuthorValidationWork work, BackendJob job)
    {
        try
        {
            await work.Reservation!.WaitAsync(job.Cancellation).ConfigureAwait(false);
            lock (_jobGate)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.SetState("running", true);
                job.ReportProgress(new("validating", 0, ScriptSampleValidator.TotalFrames));
            }
            ValidationReportDto report = await work.ValidateAsync(new InlineValidationProgress(job), job.Cancellation).ConfigureAwait(false);
            string? backend = job.Read().Input?.RenderBackend;
            if (report.RenderBackend is not null && report.RenderBackend != backend)
                throw new ValidationTaskException("ValidationReportInvalid", "作者验证报告渲染后端与固定任务不一致。");
            report = report with { RenderBackend = backend };
            byte[] bytes = _validationReports!.Encode(report, ScriptSampleValidator.FramesPerOutput);
            job.Cancellation.ThrowIfCancellationRequested();
            lock (_jobGate)
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                BackendJobReportReferenceDto reference = _validationReports.Publish(job.Id, bytes);
                job.Finish(null, report.Succeeded ? null : new(BackendErrorCode.ValidationFailed, "作者固定试样正式验证未通过。"),
                    validationResult: new(report.Succeeded, report.OpaqueFrames, report.TransparentFrames, reference),
                    failureCode: report.Succeeded ? null : "ValidationFailed");
            }
        }
        finally { await work.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class AuthorValidationWork : IAsyncDisposable
    {
        private AuthorValidationWork(IBackendRuntimeValidationExecutor executor, BackendPreparedProjectRuntimeInput input,
            BackendTaskScheduler.Reservation reservation)
        { Executor = executor; Input = input; Reservation = reservation; }
        internal BackendPreparedProjectRuntimeInput Input { get; }
        private IBackendRuntimeValidationExecutor Executor { get; }
        internal BackendTaskScheduler.Reservation? Reservation;
        internal Task<ValidationReportDto> ValidateAsync(IProgress<BackendJobProgressDto> progress, CancellationToken token) =>
            Executor.ExecuteRuntimeAsync(Input.SourceBytes, ScriptSampleValidator.FramesPerOutput, progress, token);
        internal static async Task<AuthorValidationWork> PrepareAsync(IBackendRuntimeValidationExecutor executor,
            ScriptSampleRequest request, BackendTaskScheduler.Reservation reservation, CancellationToken token)
        {
            BackendPreparedProjectRuntimeInput input = await AuthorSampleRuntimeInput.PrepareAsync(request, token).ConfigureAwait(false);
            return new(executor, input, reservation);
        }
        public async ValueTask DisposeAsync()
        {
            try { Input.Dispose(); }
            finally { Interlocked.Exchange(ref Reservation, null)?.Dispose(); }
        }
    }
}
