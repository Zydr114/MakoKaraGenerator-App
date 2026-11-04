using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    internal async Task<WorkerMessage> InvokeExportTaskAsync(WorkerMessage request, int totalFrames, TimeSpan timeout,
        IProgress<BackendJobProgressDto>? progress, CancellationToken cancellationToken, bool imageExport = false)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        WorkerProtocol.Validate(request);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Process? packageVerifiedProcess = null;
        try
        {
            using var prepared = request.Kind is WorkerExportContract.RequestKind or ProjectImageExportWorkerContract.RequestKind
                ? await PrepareExportScriptInputAsync().ConfigureAwait(false) : null;
            var result = await InvokeAsync().ConfigureAwait(false);
            prepared?.Complete(); return result;
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new RenderSessionFailureException("WorkerExportTimedOut", imageExport ? "图片背景导出超过任务期限。" : "静态导出超过任务期限。", innerException: failure); }

        async Task<PreparedScriptCall> PrepareExportScriptInputAsync()
        {
            byte[] payload;
            PreparedScriptCall inputLease;
            if (imageExport)
            {
                var input = ProjectImageExportWorkerContract.DecodeInput(request.Payload);
                inputLease = await PrepareScriptCallAsync(input.SnapshotBytes, deadline.Token, image: true).ConfigureAwait(false);
                try { payload = ProjectImageExportWorkerContract.EncodeInput(input.TaskId, input.Metadata, inputLease.Envelope); }
                catch { inputLease.Dispose(); throw; }
            }
            else
            {
                var input = WorkerExportContract.DecodeInput(request.Payload, false);
                inputLease = await PrepareScriptCallAsync(input.SnapshotBytes, deadline.Token).ConfigureAwait(false);
                try { payload = WorkerExportContract.EncodeInput(input.TaskId, input.Metadata, inputLease.Envelope); }
                catch { inputLease.Dispose(); throw; }
            }
            try
            {
                if (inputLease.HasPackages)
                {
                    PackageRenderWorkerCapabilities capabilities = imageExport
                        ? (await GetImagePreviewCapabilitiesAsync(TimeSpan.FromSeconds(20), deadline.Token).ConfigureAwait(false)).PackageCapabilities
                        : await GetRuntimeInputCapabilitiesAsync(TimeSpan.FromSeconds(20), deadline.Token).ConfigureAwait(false);
                    inputLease.RequirePackageProfiles(capabilities);
                    packageVerifiedProcess = imageExport ? _imagePreviewVerifiedProcess : _runtimeInputVerifiedProcess;
                    if (packageVerifiedProcess is null)
                        throw new WorkerProtocolException("导出包能力证明对应的实际 Worker 已释放。", "WorkerPackageProfileMismatch");
                }
            }
            catch { inputLease.Dispose(); throw; }
            string sourceHash = imageExport ? ProjectImageExportWorkerContract.DecodeInput(payload).SourceSha256
                : WorkerExportContract.DecodeInput(payload, false).SourceSha256;
            request = request with { Payload = payload, InputSha256 = WorkerProtocol.ComputeSha256(payload), SourceSha256 = sourceHash };
            return inputLease;
        }

        async Task<WorkerMessage> InvokeAsync()
        {
            await _callGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            lock (_lifecycleGate) { _activeCallCancellation = call; }
            Task<WorkerMessage>? read = null; bool matchedCancellation = false;
            Process? invocationProcess = null;
            Stream? invocationOutput = null;
            long sequence = 0; int completed = 0, stage = -1;
            try
            {
                await StartAsync(call.Token).ConfigureAwait(false); RequireExportGuards();
                await _lifecycleGate.WaitAsync(call.Token).ConfigureAwait(false);
                try
                {
                    if (packageVerifiedProcess is not null && !ReferenceEquals(_process, packageVerifiedProcess))
                        throw new WorkerProtocolException("导出实际 Worker 代次与包能力证明不一致。", "WorkerPackageProfileMismatch");
                    if (_process is null || _process.HasExited || imageExport && request.Kind == ProjectImageExportWorkerContract.RequestKind
                        && !ReferenceEquals(_process, _imageExportVerifiedProcess))
                        throw new WorkerProtocolException("图片导出实际 Worker 代次没有通过独立能力协商。", "WorkerImageExportUnavailable");
                    invocationProcess = _process; invocationOutput = _output;
                    await WriteAsync(request, call.Token).ConfigureAwait(false);
                }
                finally { _lifecycleGate.Release(); }
                read = ReadLoopAsync();
                Task canceled = Task.Delay(Timeout.InfiniteTimeSpan, call.Token);
                bool cancellationWon = await Task.WhenAny(read, canceled).ConfigureAwait(false) != read;
                if (cancellationWon)
                {
                    if (!await SendCancellationAndWaitAsync(request, read).ConfigureAwait(false))
                        throw new OperationCanceledException(call.Token);
                }
                WorkerMessage outcome = await read.ConfigureAwait(false);
                if (outcome.Cancelled && !call.IsCancellationRequested) throw new WorkerProtocolException("Worker 无请求返回取消终态。", "WorkerExportProtocolInvalid");
                matchedCancellation = outcome.Cancelled;
                if (cancellationWon || call.IsCancellationRequested || outcome.Cancelled) throw new OperationCanceledException(call.Token);
                if (request.Kind == (imageExport ? ProjectImageExportWorkerContract.RequestKind : WorkerExportContract.RequestKind)
                    && (stage != 6 || completed != totalFrames || outcome.ProgressSequence != sequence))
                    throw new WorkerProtocolException("导出终态缺少排水后的实际prepared进度。", "WorkerExportProtocolInvalid");
                if (imageExport && request.Kind == ProjectImageExportWorkerContract.CapabilitiesKind)
                {
                    ProjectImageExportWorkerContract.ValidateCapabilities(ProjectImageExportWorkerContract.DecodeCapabilities(outcome.Payload));
                    await _lifecycleGate.WaitAsync(call.Token).ConfigureAwait(false);
                    try
                    {
                        if (!ReferenceEquals(invocationProcess, _process) || invocationProcess.HasExited)
                            throw new WorkerProtocolException("图片导出实际 Worker 代次已改变。", "WorkerImageExportUnavailable");
                        _imageExportVerifiedProcess = invocationProcess;
                    }
                    finally { _lifecycleGate.Release(); }
                }
                return outcome;
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
            finally { call.Cancel(); lock (_lifecycleGate) { _activeCallCancellation = null; } _callGate.Release(); }

            async Task<WorkerMessage> ReadLoopAsync()
            {
                while (true)
                {
                    WorkerMessage message = await ReadRequiredFromStreamAsync(invocationOutput!, CancellationToken.None).ConfigureAwait(false);
                    WorkerExportContract.ValidateCorrelation(request, message);
                    if (message.Kind == (imageExport ? ProjectImageExportWorkerContract.ProgressKind : WorkerExportContract.ProgressKind))
                    {
                        BackendJobProgressDto? value = message.ExportProgress;
                        int nextStage = value?.Stage switch { "validating" => 0, "probing" => 1, "rendering" => 2,
                            "muxing" => 3, "verifying" => 4, "preparing-result" => 5, "prepared" => 6, _ => -1 };
                        if (request.Kind != (imageExport ? ProjectImageExportWorkerContract.RequestKind : WorkerExportContract.RequestKind)
                            || value is null || message.ProgressSequence is null
                            || message.ProgressSequence <= sequence || message.ProgressSequence > (long)totalFrames + 16 || nextStage < stage || nextStage < 0
                            || value.CompletedFrames < completed || value.CompletedFrames > totalFrames || value.CompletedFrames < 0
                            || (nextStage < 2 ? value.CompletedFrames != 0 || value.TotalFrames != 0 : value.TotalFrames != totalFrames)
                            || nextStage >= 3 && value.CompletedFrames != totalFrames || value.TimeUs is not null || value.Transparent is not null
                            || message.Payload.Length != 0 || message.OutputSha256 is not null || message.Cancelled || message.Error is not null
                            || message.ErrorCode is not null || message.Diagnostics is not null)
                            throw new WorkerProtocolException("导出进度阶段、来源、计数或序号无效。", "WorkerExportProtocolInvalid");
                        stage = nextStage; sequence = message.ProgressSequence.Value; completed = value.CompletedFrames;
                        if (!call.IsCancellationRequested) progress?.Report(value);
                        continue;
                    }
                    if (message.Kind == "error")
                    {
                        if (message.Payload.Length != 0 || message.OutputSha256 is not null || message.Cancelled || message.ExportProgress is not null
                            || message.ProgressSequence is not null || string.IsNullOrWhiteSpace(message.ErrorCode))
                            throw new WorkerProtocolException("导出错误回执形状无效。", "WorkerExportProtocolInvalid");
                        if (message.Diagnostics is not null) throw new WorkerProtocolException("导出不能混用预览诊断字段。", "WorkerExportProtocolInvalid");
                        var failure = new WorkerProtocolException("Worker 静态导出失败。", message.ErrorCode ?? "WorkerExportFailed", message.ExportDiagnostics);
                        if (message.ExportDiagnosticsFailureCode is { } secondary) failure.Data["WorkerExportDiagnosticsFailureCode"] = secondary;
                        throw failure;
                    }
                    string expected = request.Kind == (imageExport ? ProjectImageExportWorkerContract.CapabilitiesKind : WorkerExportContract.CapabilitiesKind)
                        ? (imageExport ? ProjectImageExportWorkerContract.CapabilitiesResultKind : WorkerExportContract.CapabilitiesResultKind)
                        : (imageExport ? ProjectImageExportWorkerContract.ResultKind : WorkerExportContract.ResultKind);
                    if (message.Kind != expected && !(message.Cancelled && message.Kind == (imageExport ? ProjectImageExportWorkerContract.ResultKind : WorkerExportContract.ResultKind))
                        || message.ExportProgress is not null || message.Diagnostics is not null || message.Error is not null || message.ErrorCode is not null
                        || message.Cancelled && (message.Payload.Length != 0 || message.OutputSha256 is not null)
                        || !message.Cancelled && (message.OutputSha256 is null || message.ProgressSequence is null || message.ProgressSequence < sequence))
                        throw new WorkerProtocolException("导出终态形状无效。", "WorkerExportProtocolInvalid");
                    return message;
                }
            }
        }
    }
}
