using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>仅把Host固定音频授权送入独立Worker，不在Hosting运行分析或解码。</summary>
public sealed class WorkerBackendAnalysisExecutor : IBackendAnalysisExecutor
{
    private readonly WorkerProcessOptions _options;
    private readonly string _ffmpeg, _ffprobe;
    private readonly TimeSpan _timeout;
    public WorkerBackendAnalysisExecutor(string workerPath, string ffmpegPath, string ffprobePath, TimeSpan? operationTimeout = null)
        : this(new WorkerProcessOptions(workerPath), ffmpegPath, ffprobePath, operationTimeout) { }
    public WorkerBackendAnalysisExecutor(WorkerProcessOptions options, string ffmpegPath, string ffprobePath, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _options = options with { ExportMediaFileSizeLimitBytes = null };
        _ffmpeg = ffmpegPath; _ffprobe = ffprobePath; _timeout = operationTimeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        WorkerAnalysisContract.ValidateMetadata(Metadata(null), capabilities: true);
    }
    public void ValidateInput(BackendAudioAnalysisGrant grant)
    { ArgumentNullException.ThrowIfNull(grant); _ = WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), Metadata(grant)); }
    private WorkerAnalysisMetadata Metadata(BackendAudioAnalysisGrant? grant) => new(grant is null || grant.AudioProfile == BackendAudioInputProfiles.Pcm16Wave
            ? WorkerAnalysisContract.Profile : WorkerAnalysisContract.CompressedProfile,
        grant is null ? null : new(grant.Path, grant.Size, grant.Sha256, grant.AudioProfile), _ffmpeg, _ffprobe);
    public async Task<BackendAnalysisCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        byte[] payload = WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), Metadata(null), capabilities: true);
        try
        {
            WorkerMessage message = await InvokeAsync(payload, true, null, cancellationToken).ConfigureAwait(false);
            var result = WorkerAnalysisContract.DecodeJson<WorkerAnalysisCapabilitiesDto>(message.Payload, 4096);
            if (!result.GuardAvailable || !result.ToolStartupVerified && string.IsNullOrWhiteSpace(result.FailureCode)
                || result.ToolStartupVerified && result.FailureCode is not null)
                throw new RenderSessionFailureException("WorkerAnalysisProtocolInvalid", "分析能力回执无效。");
            if (result.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                || !Exact(result.AudioProfiles, [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                    BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg])
                || !Exact(result.SourceProfiles, [BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource]))
                return new(false, false, "WorkerAnalysisProfileMismatch", [], []);
            return new(result.GuardAvailable, result.ToolStartupVerified, result.FailureCode, result.AudioProfiles, result.SourceProfiles);
        }
        catch (RenderSessionFailureException failure) { return new(false, false, failure.Code, [], []); }
        catch (WorkerProtocolException failure) { return new(false, false, failure.Code, [], []); }
    }
    private static bool Exact(string[]? actual, string[] expected) => actual is not null
        && actual.Length == expected.Length && actual.Distinct(StringComparer.Ordinal).Count() == expected.Length
        && actual.All(value => expected.Contains(value, StringComparer.Ordinal));
    public async Task<byte[]> ExecuteAsync(BackendAudioAnalysisGrant grant, IProgress<BackendAnalysisProgressDto>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(grant);
        byte[] payload = WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), Metadata(grant));
        WorkerMessage result = await InvokeAsync(payload, false, progress, cancellationToken).ConfigureAwait(false);
        _ = AnalysisResultCodec.Decode(result.Payload, grant.Sha256);
        return result.Payload;
    }
    private async Task<WorkerMessage> InvokeAsync(byte[] payload, bool capabilities, IProgress<BackendAnalysisProgressDto>? progress, CancellationToken token)
    {
        var input = WorkerAnalysisContract.DecodeInput(payload, capabilities);
        WorkerMessage request = new(capabilities ? WorkerAnalysisContract.CapabilitiesKind : WorkerAnalysisContract.RequestKind,
            Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            taskId: input.TaskId.ToString("N"), sourceSha256: input.Metadata.Audio?.Sha256);
        var proxy = new WorkerProcessProxy(_options); Exception? primary = null;
        try { return await proxy.InvokeAnalysisTaskAsync(request, _timeout, progress, token).ConfigureAwait(false); }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is WorkerProtocolException protocol)
                throw new RenderSessionFailureException(protocol.Code, "独立离线音频分析未完成。", innerException: failure);
            if (failure is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerAnalysisUnavailable", "独立音频分析Worker不可用。", innerException: failure);
            if (failure is IOException)
                throw new RenderSessionFailureException("WorkerSessionConflict", "独立音频分析Worker连接提前结束。", innerException: failure);
            throw;
        }
        finally
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerCleanupFailure"] = cleanup; }
        }
    }
}
