using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    /// <summary>执行一个固定静态帧计划；进度流与唯一终态均核对固定身份和来源。</summary>
    public async Task<ValidationReportDto> ValidateStaticProjectAsync(ReadOnlyMemory<byte> snapshotBytes, int framesPerOutput,
        TimeSpan timeout, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        => await ValidateProjectAsync(snapshotBytes, framesPerOutput, timeout, progress,
            StaticProjectSnapshotCodec.Profile, cancellationToken).ConfigureAwait(false);

    public Task<ValidationReportDto> ValidateImageProjectAsync(ReadOnlyMemory<byte> snapshotBytes, int framesPerOutput,
        TimeSpan timeout, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        => ValidateProjectCoreAsync(snapshotBytes, framesPerOutput, timeout, progress,
            ProjectImageValidationWorkerContract.ValidationProfile, true, cancellationToken);

    public Task<ValidationReportDto> ValidateRuntimeProjectAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
        TimeSpan timeout, IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        => ValidateProjectCoreAsync(sourceBytes, framesPerOutput, timeout, progress, RuntimeValidationWorkerContract.Profile, false, cancellationToken);

    public async Task<ValidationReportDto> ValidateProjectAsync(ReadOnlyMemory<byte> snapshotBytes, int framesPerOutput,
        TimeSpan timeout, IProgress<BackendJobProgressDto>? progress, string profile, CancellationToken cancellationToken = default)
        => await ValidateProjectCoreAsync(snapshotBytes, framesPerOutput, timeout, progress, profile, false, cancellationToken).ConfigureAwait(false);

    private async Task<ValidationReportDto> ValidateProjectCoreAsync(ReadOnlyMemory<byte> snapshotBytes, int framesPerOutput,
        TimeSpan timeout, IProgress<BackendJobProgressDto>? progress, string profile, bool imageValidation, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        bool image = imageValidation || profile == ProjectImageValidationWorkerContract.ValidationProfile;
        bool runtime = profile == RuntimeValidationWorkerContract.Profile;
        byte[] payload = runtime ? RuntimeValidationWorkerContract.EncodeInput(Guid.NewGuid(), framesPerOutput, snapshotBytes.Span)
            : image
            ? ProjectImageValidationWorkerContract.EncodeInput(Guid.NewGuid(), framesPerOutput, snapshotBytes.Span)
            : WorkerValidationContract.EncodeInput(Guid.NewGuid(), framesPerOutput, snapshotBytes.Span,
                profile == PackageProjectSnapshotCodec.Profile);
        WorkerValidationInput input = runtime ? RuntimeValidationWorkerContract.DecodeInput(payload) : image ? ProjectImageValidationWorkerContract.DecodeInput(payload) : WorkerValidationContract.DecodeInput(payload);
        var composition = runtime ? ProjectRuntimeInputCodec.ReadComposition(input.SnapshotBytes)
            : (image
                ? ProjectImagePreviewSnapshotCodec.Decode(input.SnapshotBytes).Snapshot
                : input.Profile == PackageProjectSnapshotCodec.Profile
                    ? PackageProjectSnapshotCodec.Decode(input.SnapshotBytes)
                    : StaticProjectSnapshotCodec.Decode(input.SnapshotBytes)).Composition;
        WorkerValidationContract.ValidateGeometryAndTiming(composition.CanvasWidth, composition.CanvasHeight, composition.OutputDurationUs,
            composition.FrameRate.Numerator, composition.FrameRate.Denominator, framesPerOutput);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var prepared = await PrepareValidationScriptInputAsync().ConfigureAwait(false);
        payload = runtime ? RuntimeValidationWorkerContract.EncodeInput(input.TaskId, framesPerOutput, prepared.Envelope)
            : image ? ProjectImageValidationWorkerContract.EncodeInput(input.TaskId, framesPerOutput, prepared.Envelope)
            : WorkerValidationContract.EncodeInput(input.TaskId, framesPerOutput, prepared.Envelope, profile == PackageProjectSnapshotCodec.Profile);
        input = runtime ? RuntimeValidationWorkerContract.DecodeInput(payload) : image
            ? ProjectImageValidationWorkerContract.DecodeInput(payload) : WorkerValidationContract.DecodeInput(payload);
        string progressKind = runtime ? RuntimeValidationWorkerContract.ProgressKind : image ? ProjectImageValidationWorkerContract.ProgressKind : WorkerValidationContract.ProgressKind;
        string resultKind = runtime ? RuntimeValidationWorkerContract.ResultKind : image ? ProjectImageValidationWorkerContract.ResultKind : WorkerValidationContract.ResultKind;
        string requestKind = runtime ? RuntimeValidationWorkerContract.RequestKind : image ? ProjectImageValidationWorkerContract.RequestKind : WorkerValidationContract.RequestKind;
        WorkerMessage request = new(requestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload),
            payload, sourceSha256: input.SourceSha256, taskId: input.TaskId.ToString("N"), validationFramesPerOutput: framesPerOutput);
        // Package validation has a different source codec and must prove the actual
        // Worker contains the matching package runtime before sending the validation
        // payload.  An older Worker may still accept the generic validation kind and
        // only fail while decoding the package snapshot, which is too late and makes
        // capability negotiation misleading.
        if (profile == PackageProjectSnapshotCodec.Profile)
        {
            try { await GetPackageValidationCapabilitiesAsync(timeout, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new RenderSessionFailureException("WorkerTaskTimedOut", "固定帧计划 Worker 验证超过任务期限。", innerException: failure); }
        }
        if (image)
        {
            try { await GetImageValidationCapabilitiesAsync(timeout, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new RenderSessionFailureException("WorkerTaskTimedOut", "图片背景验证超过任务期限。", innerException: failure); }
        }
        if (runtime)
        {
            try { await GetRuntimeValidationCapabilitiesAsync(timeout, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new RenderSessionFailureException("WorkerTaskTimedOut", "统一Runtime验证超过任务期限。", innerException: failure); }
        }
        try
        {
            var report = await InvokeValidationAsync().ConfigureAwait(false);
            prepared.Complete(); return report;
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new RenderSessionFailureException("WorkerTaskTimedOut", "固定帧计划 Worker 验证超过任务期限。", innerException: failure); }

        async Task<PreparedScriptCall> PrepareValidationScriptInputAsync()
        {
            try { return await PrepareScriptCallAsync(snapshotBytes, deadline.Token, image: image).ConfigureAwait(false); }
            catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new RenderSessionFailureException("WorkerTaskTimedOut", "固定帧计划 Worker 验证超过任务期限。", innerException: failure); }
        }

        async Task<ValidationReportDto> InvokeValidationAsync()
        {
            await _callGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            lock (_lifecycleGate) { _activeCallCancellation = call; }
            Task<WorkerMessage>? read = null;
            Process? invocationProcess = null;
            Stream? invocationInput = null;
            Stream? invocationOutput = null;
            bool matchedCancellation = false;
            long sequence = 0; int completed = 0;
            try
            {
                await StartAsync(call.Token).ConfigureAwait(false);
                bool bindInvocation = profile == PackageProjectSnapshotCodec.Profile || image || runtime;
                if (bindInvocation)
                {
                    await _lifecycleGate.WaitAsync(call.Token).ConfigureAwait(false);
                    try
                    {
                        Process? verified = runtime ? _runtimeValidationVerifiedProcess : image ? _imageValidationVerifiedProcess : _packageValidationVerifiedProcess;
                        if (!_childReapingEnabled || _ownedGroup is null || _process is null || _process.HasExited
                            || !ReferenceEquals(_process, verified))
                            throw new WorkerProtocolException(image ? "图片验证 Worker 实际进程未通过能力验证。" : "包验证 Worker 实际进程未通过能力验证。",
                                image ? "WorkerImageValidationUnavailable" : "WorkerPackageUnavailable");
                        invocationProcess = _process;
                        invocationInput = _input;
                        invocationOutput = _output;
                        if (invocationInput is null || invocationOutput is null)
                            throw new WorkerProtocolException(image ? "图片验证 Worker 管道未就绪。" : "包验证 Worker 管道未就绪。",
                                image ? "WorkerImageValidationUnavailable" : "WorkerPackageUnavailable");
                        await WriteAsync(invocationInput, request, call.Token).ConfigureAwait(false);
                    }
                    finally { _lifecycleGate.Release(); }
                }
                else await WriteAsync(request, call.Token).ConfigureAwait(false);
                read = ReadLoopAsync(invocationOutput);
                Task canceled = Task.Delay(Timeout.InfiniteTimeSpan, call.Token);
                bool cancellationWon = await Task.WhenAny(read, canceled).ConfigureAwait(false) != read;
                if (cancellationWon)
                {
                    if (!await SendCancellationAndWaitAsync(request, read, invocationInput).ConfigureAwait(false))
                        throw new OperationCanceledException(call.Token);
                }
                WorkerMessage outcome = await read.ConfigureAwait(false);
                if (outcome.Cancelled && !call.IsCancellationRequested)
                    throw new WorkerProtocolException("Worker 在没有任务取消请求时返回取消终态。");
                matchedCancellation = outcome.Cancelled;
                if (cancellationWon || call.IsCancellationRequested || outcome.Cancelled) throw new OperationCanceledException(call.Token);
                if (bindInvocation)
                {
                    await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (!ReferenceEquals(invocationProcess, _process) || invocationProcess?.HasExited == true)
                            throw new WorkerProtocolException(image ? "图片验证 Worker 回执的实际进程代次已改变。" : "包验证 Worker 回执的实际进程代次已改变。",
                                image ? "WorkerImageValidationUnavailable" : "WorkerPackageUnavailable");
                    }
                    finally { _lifecycleGate.Release(); }
                }
                ValidationReportDto result = WorkerValidationContract.DecodeReport(outcome.Payload, framesPerOutput);
                WorkerValidationContract.ValidateReportTimes(result,
                    (long)(framesPerOutput - 1) * 1_000_000 * composition.FrameRate.Denominator / composition.FrameRate.Numerator);
                if (result.OpaqueFrames + result.TransparentFrames < completed)
                    throw new WorkerProtocolException("验证终态计数少于已公布的真实完成帧数。");
                if (result.Succeeded && (outcome.ProgressSequence != WorkerValidationContract.MaximumProgressSequence(framesPerOutput)
                    || completed != framesPerOutput * 2 || sequence != outcome.ProgressSequence))
                    throw new WorkerProtocolException("成功验证缺少正式帧计划的最终进度序号。");
                return result;
            }
            catch (OperationCanceledException) when (matchedCancellation) { throw; }
            catch (Exception primary)
            {
                await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    try { await StopProcessAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
                }
                finally { _lifecycleGate.Release(); }
                if (read is not null)
                {
                    try { await read.ConfigureAwait(false); }
                    catch (Exception failure) when (failure is IOException or WorkerProtocolException or ObjectDisposedException or OperationCanceledException) { }
                }
                throw;
            }
            finally
            {
                call.Cancel();
                lock (_lifecycleGate) { _activeCallCancellation = null; }
                _callGate.Release();
            }

            async Task<WorkerMessage> ReadLoopAsync(Stream? output)
            {
                while (true)
                {
                    WorkerMessage message = output is null
                        ? await ReadRequiredAsync(CancellationToken.None).ConfigureAwait(false)
                        : await ReadRequiredFromStreamAsync(output, CancellationToken.None).ConfigureAwait(false);
                    WorkerValidationContract.ValidateCorrelation(request, message);
                    if (message.Kind == progressKind)
                    {
                        WorkerValidationContract.ValidateProgress(request, message with { Kind = WorkerValidationContract.ProgressKind }, sequence, completed,
                            composition.FrameRate.Numerator, composition.FrameRate.Denominator);
                        sequence = message.ProgressSequence!.Value; completed = message.ValidationProgress!.CompletedFrames;
                        if (!call.IsCancellationRequested) progress?.Report(message.ValidationProgress);
                        continue;
                    }
                    if (message.Kind == "error")
                    {
                        if (message.Payload.Length != 0 || message.OutputSha256 is not null || message.Cancelled
                            || string.IsNullOrWhiteSpace(message.ErrorCode) || message.ValidationProgress is not null)
                            throw new WorkerProtocolException("验证失败回执形状无效。");
                        throw new WorkerProtocolException(message.Error ?? "Worker 验证失败。", message.ErrorCode ?? "WorkerExecutionFailed", message.Diagnostics);
                    }
                    if (message.Kind != resultKind || message.ValidationProgress is not null || message.Diagnostics is not null
                        || message.Error is not null || message.Cancelled && (message.Payload.Length != 0 || message.OutputSha256 is not null)
                        || !message.Cancelled && (message.OutputSha256 is null || message.ProgressSequence is null || message.ProgressSequence < sequence
                            || message.ProgressSequence > WorkerValidationContract.MaximumProgressSequence(framesPerOutput)))
                        throw new WorkerProtocolException("验证唯一终态形状或进度序号无效。");
                    return message;
                }
            }
        }
    }
}
