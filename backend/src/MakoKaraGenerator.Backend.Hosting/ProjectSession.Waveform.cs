using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private WaveformCache.Binding? _waveform;
    private string? _waveformSourceSha256;
    private long _waveformSourceBytes;
    private string? _waveformAssetId;
    private string _waveformUnavailable = "noAudio";

    // Called only during unpublished candidate construction, after source verification.
    internal void BindWaveform(WaveformCache cache, LoadedWaveform prepared, StoredAsset audio)
    {
        _waveformAssetId = audio.AssetId;
        _waveformSourceSha256 = audio.Sha256;
        _waveformSourceBytes = audio.Size;
        _waveform = cache.TryRetain(prepared, audio.Sha256, WorkerAudioPreparationContract.Profile);
        _waveformUnavailable = "cacheBudgetExceeded";
    }

    private bool WaveformAudioMatches()
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _waveformAssetId is not null && AudioAssetId == _waveformAssetId
            && PreparedAudioSourcePath is not null && string.Equals(Workspace.Project.AudioPath, PreparedAudioSourcePath, comparison)
            && (Workspace.Project.AudioMedia is not { } declaration
                || declaration.Hash == _waveformSourceSha256 && declaration.ByteLength == _waveformSourceBytes);
    }

    internal async Task<BackendOperationResult<ProjectWaveformResponse>> ReadWaveformAsync(CancellationToken token)
    {
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsClosed) return BackendOperationResult<ProjectWaveformResponse>.Rejected(WaveformSessionMissing());
            if (_waveformAssetId is not null && !WaveformAudioMatches())
                return BackendOperationResult<ProjectWaveformResponse>.Success(new(ProjectSessionId, false, "audioIdentityChanged"));
            ProjectWaveformResponse response = _waveform?.Metadata(ProjectSessionId)
                ?? new(ProjectSessionId, false, _waveformUnavailable);
            return BackendOperationResult<ProjectWaveformResponse>.Success(response);
        }
        finally { Writer.Release(); }
    }

    internal async Task<BackendOperationResult<WaveformWindow>> ReadWaveformWindowAsync(
        string waveformId, int level, int start, int count, CancellationToken token)
    {
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsClosed) return BackendOperationResult<WaveformWindow>.Rejected(WaveformSessionMissing());
            if (_waveform is null || !WaveformAudioMatches())
                return BackendOperationResult<WaveformWindow>.Rejected(new(BackendErrorCode.ResourceNotFound, "当前波形不可用。", "waveformId"));
            return _waveform.Read(waveformId, level, start, count);
        }
        finally { Writer.Release(); }
    }

    private static BackendErrorResponse WaveformSessionMissing() => new(BackendErrorCode.SessionNotFound, "工程会话不存在。");
}

public sealed partial class ProjectSessionRegistry
{
    public Task<BackendOperationResult<ProjectWaveformResponse>> ReadWaveformAsync(
        string clientId, string sessionId, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session) ? session.ReadWaveformAsync(cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectWaveformResponse>.Rejected(MissingSessionError(sessionId)));

    public Task<BackendOperationResult<WaveformWindow>> ReadWaveformWindowAsync(
        string clientId, string sessionId, string waveformId, int level, int start, int count, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session) ? session.ReadWaveformWindowAsync(waveformId, level, start, count, cancellationToken)
            : Task.FromResult(BackendOperationResult<WaveformWindow>.Rejected(MissingSessionError(sessionId)));
}
