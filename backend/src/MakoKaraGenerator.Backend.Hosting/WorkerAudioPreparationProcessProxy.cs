using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    internal async Task<WorkerMessage> InvokeAudioPreparationTaskAsync(WorkerMessage request, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        bool capabilities = request.Kind == WorkerAudioPreparationContract.CapabilitiesKind;
        if (!capabilities && request.Kind != WorkerAudioPreparationContract.RequestKind)
            throw new WorkerProtocolException("音频准备请求kind无效。", "WorkerAudioPreparationProtocolInvalid");
        WorkerAudioPreparationInput input = WorkerAudioPreparationContract.DecodeInput(request.Payload, capabilities);
        WorkerAudioPreparationContract.ValidateCorrelation(request, request);
        if (request.TaskId != input.TaskId.ToString("N") || request.SourceSha256 != input.Metadata.Audio?.Sha256)
            throw new WorkerProtocolException("音频准备请求与固定授权身份不同。", "WorkerAudioPreparationProtocolInvalid");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await InvokeMessageAsync(request, deadline.Token, expectedKind: capabilities
                    ? WorkerAudioPreparationContract.CapabilitiesResultKind : WorkerAudioPreparationContract.ResultKind,
                validateMessage: ValidateMessage, requireAudioPreparationGuards: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new RenderSessionFailureException("WorkerAudioPreparationTimedOut", "受管理音频准备超过操作期限。", innerException: error); }

        void ValidateMessage(WorkerMessage message)
        {
            WorkerAudioPreparationContract.ValidateCorrelation(request, message);
            if (message.AnalysisProgress is not null || message.ProgressSequence is not null || message.ExportProgress is not null
                || message.Diagnostics is not null || message.ExportDiagnostics is not null || message.ExportDiagnosticsFailureCode is not null)
                throw Invalid();
            if (message.Kind == "error")
            {
                if (message.Payload.Length != 0 || message.OutputSha256 is not null || message.Cancelled || string.IsNullOrWhiteSpace(message.ErrorCode))
                    throw Invalid();
                return;
            }
            if (message.Error is not null || message.ErrorCode is not null
                || message.Cancelled && (message.Payload.Length != 0 || message.OutputSha256 is not null)
                || !message.Cancelled && message.OutputSha256 is null)
                throw Invalid();
            if (!message.Cancelled)
            {
                if (capabilities) _ = WorkerAudioPreparationContract.DecodeCapabilities(message.Payload);
                else _ = WorkerAudioPreparationContract.DecodeResult(message.Payload, request.SourceSha256!);
            }
        }
        static WorkerProtocolException Invalid() => new("音频准备唯一终态形状无效。", "WorkerAudioPreparationProtocolInvalid");
    }
}
