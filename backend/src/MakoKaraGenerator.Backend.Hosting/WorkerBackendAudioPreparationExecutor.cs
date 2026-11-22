using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>只经独立Worker执行原native源采样率完整波形准备。</summary>
public sealed class WorkerBackendAudioPreparationExecutor : IBackendAudioPreparationExecutor
{
    private readonly WorkerProcessOptions _options;
    private readonly string _ffmpeg, _ffprobe;
    private readonly TimeSpan _timeout;
    public WorkerBackendAudioPreparationExecutor(WorkerProcessOptions options, string ffmpegPath, string ffprobePath,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _options = options with { ExportMediaFileSizeLimitBytes = null, CancellationGracePeriod = TimeSpan.FromSeconds(2) };
        _ffmpeg = ffmpegPath; _ffprobe = ffprobePath; _timeout = operationTimeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _ = WorkerAudioPreparationContract.EncodeInput(Guid.NewGuid(), Metadata(null), capabilities: true);
    }
    private WorkerAudioPreparationMetadata Metadata(BackendAudioPreparationGrant? grant) => new(
        WorkerAudioPreparationContract.Profile, grant is null ? null : new(grant.Path, grant.Size, grant.Sha256), _ffmpeg, _ffprobe);
    public void ValidateInput(BackendAudioPreparationGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _ = WorkerAudioPreparationContract.EncodeInput(Guid.NewGuid(), Metadata(grant));
    }
    public Task<BackendAudioPreparationCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
        => ProbeAsync(null, cancellationToken);

    public async Task<BackendAudioPreparationCapabilities> ProbeAsync(WorkerProcessProxy? sharedProxy, CancellationToken cancellationToken = default)
    {
        try
        {
            WorkerMessage message = await InvokeAsync(null, true, cancellationToken, sharedProxy).ConfigureAwait(false);
            WorkerAudioPreparationCapabilities result = WorkerAudioPreparationContract.DecodeCapabilities(message.Payload);
            string[] families = ["wav", "mp3", "flac", "aac", "mov", "ogg"];
            if (result.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity || result.Profile != WorkerAudioPreparationContract.Profile
                || result.Families is null || result.Families.Length != families.Length
                || result.Families.Distinct(StringComparer.Ordinal).Count() != families.Length
                || result.Families.Any(value => !families.Contains(value, StringComparer.Ordinal))
                || result.MaximumBaseBuckets != WorkerAudioPreparationContract.MaximumBaseBuckets
                || result.BaseBucketSamples != WorkerAudioPreparationContract.BaseBucketSamples
                || result.MaximumResultBytes != WorkerAudioPreparationContract.MaximumResultBytes)
                return new(false, false, "WorkerAudioPreparationProfileMismatch");
            if (result.ToolStartupVerified && (!result.GuardAvailable || result.FailureCode is not null)
                || !result.ToolStartupVerified && string.IsNullOrWhiteSpace(result.FailureCode))
                return new(false, false, "WorkerAudioPreparationProtocolInvalid");
            return new(result.GuardAvailable, result.ToolStartupVerified, result.FailureCode);
        }
        catch (RenderSessionFailureException failure) { return new(false, false, failure.Code); }
    }
    public async Task<LoadedWaveform?> ExecuteAsync(BackendAudioPreparationGrant grant, CancellationToken cancellationToken = default)
    {
        ValidateInput(grant);
        WorkerMessage message;
        try { message = await InvokeAsync(grant, false, cancellationToken).ConfigureAwait(false); }
        catch (RenderSessionFailureException failure) when (failure.Code == "WorkerAudioPreparationNoAudioStream") { return null; }
        catch (RenderSessionFailureException failure) when (failure.Code == "WorkerAudioPreparationInputRejected")
        { throw new IOException("音频资源探测或解码失败。"); }
        WorkerAudioPreparationResult result = WorkerAudioPreparationContract.DecodeResult(message.Payload, grant.Sha256);
        return new(new(result.Info.DurationUs, result.Info.SampleRate, result.Info.Channels, result.Info.CodecName, result.Info.ContainerName),
            WaveformPeakPyramid.FromBaseBuckets(result.Info.SampleRate, result.SampleCount, result.BaseBucketSamples, result.Minimums, result.Maximums));
    }
    private async Task<WorkerMessage> InvokeAsync(BackendAudioPreparationGrant? grant, bool capabilities, CancellationToken token,
        WorkerProcessProxy? sharedProxy = null)
    {
        Guid id = Guid.NewGuid(); byte[] payload = WorkerAudioPreparationContract.EncodeInput(id, Metadata(grant), capabilities);
        WorkerMessage request = new(capabilities ? WorkerAudioPreparationContract.CapabilitiesKind : WorkerAudioPreparationContract.RequestKind,
            Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            taskId: id.ToString("N"), sourceSha256: grant?.Sha256);
        WorkerProcessProxy proxy = sharedProxy ?? new(_options); Exception? primary = null;
        try { return await proxy.InvokeAudioPreparationTaskAsync(request, _timeout, token).ConfigureAwait(false); }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is WorkerProtocolException protocol)
                throw new RenderSessionFailureException(protocol.Code == "WorkerProtocolInvalid" ? "WorkerAudioPreparationProtocolInvalid" : protocol.Code,
                    "独立音频准备未完成。", innerException: failure);
            if (failure is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerAudioPreparationUnavailable", "独立音频准备Worker不可用。", innerException: failure);
            if (failure is IOException)
                throw new RenderSessionFailureException("WorkerAudioPreparationTransportFailed", "独立音频准备Worker连接提前结束。", innerException: failure);
            throw;
        }
        finally
        {
            if (sharedProxy is null)
            {
                try { await proxy.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            }
        }
    }
}
