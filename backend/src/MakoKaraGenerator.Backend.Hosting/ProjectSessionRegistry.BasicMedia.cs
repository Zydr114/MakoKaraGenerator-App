using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    internal Func<Task>? BasicAudioPreparationCheckpoint { get; set; }

    private async Task<ProjectCommandExecutionResult> ExecuteBasicAudioCommandAsync(string clientId,
        ProjectSession session, ProjectCommandRequest request)
    {
        var captured = await session.CaptureBasicAudioAsync(request).ConfigureAwait(false);
        if (captured.Error is { } captureError) return ProjectCommandExecutionResult.Rejected(captureError);
        ProjectSession.BasicAudioCapture capture = captured.Capture!;
        ProjectSession.AudioHistoryState? state = null;
        IDisposable? pin = null;
        AudioPreparationOperation? operation = null;
        try
        {
            StoredAsset? audio = null;
            if (!capture.Clear)
            {
                string assetId = ((ReplaceProjectAudioAssetCommandDto)request.Command).AssetId!;
                pin = _resources.AcquireReference(clientId, assetId);
                audio = _resources.GetAuthorized(clientId, assetId);
                if (audio.ContentType.StartsWith("text/", StringComparison.Ordinal))
                    return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.UnsupportedMediaType, "音轨必须是固定媒体资源。", "command.assetId"));
                VerifyFixedAudioSource(clientId, audio);
                if (!capture.NoOp)
                {
                    operation = ReserveAudioPreparation(clientId, CancellationToken.None);
                    if (operation is not null) await operation.WaitAsync().ConfigureAwait(false);
                    CancellationToken token = operation?.Token ?? CancellationToken.None;
                    LoadedWaveform? waveform = operation is not null
                        ? await PrepareManagedAudioAsync(audio, token).ConfigureAwait(false)
                        : await (_loadAudio ?? ((path, cancellation) => AudioWaveformLoader.CreateForSelfContainedFiles()
                            .LoadAsync(path, cancellationToken: cancellation)))(audio.StoragePath, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (waveform is null || waveform.Peaks.DurationUs <= 0)
                        return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.NoAudioStream, "固定资源没有可准备的首音轨。", "command.assetId"));
                    VerifyFixedAudioSource(clientId, audio, token);
                    WaveformCache.Binding? binding = _waveforms.TryRetain(waveform, audio.Sha256, WorkerAudioPreparationContract.Profile);
                    state = new(audio.AssetId, audio.StoragePath, audio.Sha256, audio.Size, waveform.Peaks.DurationUs,
                        pin, binding, "cacheBudgetExceeded");
                    pin = null;
                }
            }
            if (BasicAudioPreparationCheckpoint is { } checkpoint) await checkpoint().ConfigureAwait(false);
            operation?.Token.ThrowIfCancellationRequested();
            if (audio is not null) VerifyFixedAudioSource(clientId, audio, operation?.Token ?? CancellationToken.None);
            // Commit assumes ownership even on rejection, so every candidate is released once.
            var transfer = state; state = null;
            return await session.CommitBasicAudioAsync(request, capture, audio, transfer).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (operation is { TimedOut: true })
        { return ProjectCommandExecutionResult.Rejected(AudioPreparationFailure(AudioPreparationOperation.TimeoutFailure(error))); }
        catch (OperationCanceledException)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "音频准备已取消，工程保持原状。", "command")); }
        catch (ResourceStoreException error)
        { return ProjectCommandExecutionResult.Rejected(new(error.Code, error.Message, "command.assetId")); }
        catch (Exception error) when (error is WorkerProtocolException or MakoKaraGenerator.Rendering.RenderSessionFailureException)
        { return ProjectCommandExecutionResult.Rejected(AudioPreparationFailure(error)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.AudioPreparationFailed, "固定音频无法完整准备，工程保持原状。", "command.assetId")); }
        finally { try { state?.Dispose(); } finally { try { pin?.Dispose(); } finally { operation?.Dispose(); } } }
    }
}
