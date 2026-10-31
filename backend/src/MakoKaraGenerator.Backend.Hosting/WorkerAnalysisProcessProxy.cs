using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    internal async Task<WorkerMessage> InvokeAnalysisTaskAsync(WorkerMessage request, TimeSpan timeout,
        IProgress<BackendAnalysisProgressDto>? progress, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(timeout));
        WorkerProtocol.Validate(request);
        bool capabilities = request.Kind == WorkerAnalysisContract.CapabilitiesKind;
        if (!capabilities && request.Kind != WorkerAnalysisContract.RequestKind) throw Invalid("分析请求kind无效。");
        var input = WorkerAnalysisContract.DecodeInput(request.Payload, capabilities);
        WorkerAnalysisContract.ValidateCorrelation(request, request);
        if (request.TaskId != input.TaskId.ToString("N") || request.SourceSha256 != input.Metadata.Audio?.Sha256)
            throw Invalid("分析请求与固定授权身份不一致。");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        try { return await InvokeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new RenderSessionFailureException("WorkerAnalysisTimedOut", "离线音频分析超过任务期限。", innerException: failure); }

        async Task<WorkerMessage> InvokeAsync()
        {
            await _callGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            lock (_lifecycleGate) { _activeCallCancellation = call; }
            Task<WorkerMessage>? read = null; bool matchedCancellation = false;
            long sequence = 0, completed = 0; int stage = -1;
            try
            {
                await StartAsync(call.Token).ConfigureAwait(false);
                if (!_childReapingEnabled || _ownedGroup is null)
                    throw new WorkerProtocolException("离线分析需要实际私有Worker进程组与子进程回收能力。", "WorkerAnalysisUnavailable");
                await WriteAsync(request, call.Token).ConfigureAwait(false);
                read = ReadLoopAsync();
                bool cancellationWon = await Task.WhenAny(read, Task.Delay(Timeout.InfiniteTimeSpan, call.Token)).ConfigureAwait(false) != read;
                if (cancellationWon && !await SendCancellationAndWaitAsync(request, read).ConfigureAwait(false))
                    throw new OperationCanceledException(call.Token);
                WorkerMessage outcome = await read.ConfigureAwait(false);
                if (outcome.Cancelled && !call.IsCancellationRequested) throw Invalid("未请求取消时返回取消终态。");
                matchedCancellation = outcome.Cancelled;
                if (cancellationWon || call.IsCancellationRequested || outcome.Cancelled) throw new OperationCanceledException(call.Token);
                if (request.Kind == WorkerAnalysisContract.RequestKind)
                {
                    if (stage != 4 || completed <= 0 || outcome.ProgressSequence != sequence) throw Invalid("缺少已排水的真实prepared进度。");
                    var result = AnalysisResultCodec.Decode(outcome.Payload, request.SourceSha256!);
                    if (result.SampleCount != completed) throw Invalid("完整结果与真实进度采样数不同。");
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
                    WorkerMessage message = await ReadRequiredAsync(CancellationToken.None).ConfigureAwait(false);
                    WorkerAnalysisContract.ValidateCorrelation(request, message);
                    if (message.Kind == WorkerAnalysisContract.ProgressKind)
                    {
                        if (request.Kind != WorkerAnalysisContract.RequestKind) throw Invalid("能力探测不能返回分析进度。");
                        stage = WorkerAnalysisContract.ValidateProgress(message, sequence, completed, stage);
                        sequence = message.ProgressSequence!.Value;
                        completed = long.Parse(message.AnalysisProgress!.CompletedSamples, CultureInfo.InvariantCulture);
                        if (!call.IsCancellationRequested) progress?.Report(message.AnalysisProgress);
                        continue;
                    }
                    if (message.Kind == "error")
                    {
                        if (message.Payload.Length != 0 || message.OutputSha256 is not null || message.AnalysisProgress is not null
                            || message.ProgressSequence is not null || message.Cancelled || string.IsNullOrWhiteSpace(message.ErrorCode))
                            throw Invalid("分析错误回执形状无效。");
                        throw new WorkerProtocolException("Worker离线分析未完成。", message.ErrorCode);
                    }
                    string expected = request.Kind == WorkerAnalysisContract.CapabilitiesKind
                        ? WorkerAnalysisContract.CapabilitiesResultKind : WorkerAnalysisContract.ResultKind;
                    if (message.Kind != expected && !(message.Cancelled && message.Kind == WorkerAnalysisContract.ResultKind)
                        || message.AnalysisProgress is not null || message.Error is not null || message.ErrorCode is not null
                        || message.Cancelled && (message.Payload.Length != 0 || message.OutputSha256 is not null)
                        || !message.Cancelled && (message.OutputSha256 is null || message.ProgressSequence is null || message.ProgressSequence < sequence))
                        throw Invalid("分析唯一终态形状或哈希无效。");
                    return message;
                }
            }
        }
    }
    private static WorkerProtocolException Invalid(string message) => new(message, "WorkerAnalysisProtocolInvalid");
}
