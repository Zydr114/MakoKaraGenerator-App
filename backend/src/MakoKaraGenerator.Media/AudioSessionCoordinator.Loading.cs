using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Media;

public sealed partial class AudioSessionCoordinator
{
    /// <summary>先准备导入候选，再在相同工程会话内提交领域命令并安装；准备失败保留原播放。</summary>
    /// <param name="path">新音频来源。</param>
    /// <param name="commit">调用方的同步领域提交；拒绝时返回空，成功返回实际采用工程。</param>
    /// <param name="cancellationToken">导入取消令牌。</param>
    /// <returns>领域与运行时均已采用时为真。</returns>
    public async Task<bool> ImportAsync(string path, Func<Project?> commit, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        CancelRequest();
        _restoration = Task.CompletedTask;
        AudioSessionState priorState = Playback is not null ? AudioSessionState.Ready
            : Target is null ? AudioSessionState.Empty : AudioSessionState.Unavailable;
        Exception? priorFailure = Failure;
        using Request request = BeginRequest(cancellationToken);
        LoadingPath = path; Failure = null; State = AudioSessionState.Loading;
        Changed?.Invoke(this, EventArgs.Empty);
        Prepared? prepared = null;
        bool installed = false;
        try
        {
            prepared = await PrepareAsync(path, request).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared is null || !IsCurrent(request)) return false;
            Project? applied = commit();
            // 领域提交已经发生；只拒绝换会话／换目标，不把迟到取消误作未提交。
            if (applied is null || !IsCurrentIdentity(request)) return false;
            AudioSourceIdentity? next = AudioSourceIdentity.FromProject(applied);
            if (next?.CanonicalPath != AudioSourceIdentity.NormalizePath(path)) return false;
            ReleasePlayback();
            if (Target != next) _sessionGeneration++;
            Target = next; _sourcePath = applied.AudioPath;
            DisplayName = applied.AudioMedia?.OriginalName ?? Path.GetFileName(applied.AudioPath);
            var creation = prepared.TakeCreation();
            installed = true;
            Install(prepared.Loaded, creation);
            return true;
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCurrent(request)) Failure = error;
            return false;
        }
        finally
        {
            try
            {
                ReleasePrepared(prepared, request);
                if (ReferenceEquals(_request, request) && !installed)
                {
                    State = priorState; LoadingPath = null;
                    if (request.Cancellation.IsCancellationRequested || Failure is null) Failure = priorFailure;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            finally { EndRequest(request); }
        }
    }

    private async Task RestoreTargetAsync(CancellationToken token)
    {
        using Request request = BeginRequest(token);
        string path = _sourcePath!;
        LoadingPath = path; Failure = null; State = AudioSessionState.Loading;
        Changed?.Invoke(this, EventArgs.Empty);
        Prepared? prepared = null;
        try
        {
            prepared = await PrepareAsync(path, request).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (prepared is null || !IsCurrent(request)) return;
            Install(prepared.Loaded, prepared.TakeCreation());
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_request, request)) MarkUnavailable(new OperationCanceledException(token));
        }
        catch (Exception error) when (IsLoadError(error))
        {
            if (IsCurrent(request)) MarkUnavailable(error);
        }
        finally
        {
            try
            {
                ReleasePrepared(prepared, request);
                if (ReferenceEquals(_request, request) && State == AudioSessionState.Loading)
                    MarkUnavailable(new OperationCanceledException(request.Cancellation.Token));
            }
            finally { EndRequest(request); }
        }
    }

    private async Task<Prepared?> PrepareAsync(string path, Request request)
    {
        LoadedWaveform? loaded = await _decode(path, request.Cancellation.Token).ConfigureAwait(true);
        if (!IsCurrent(request)) return null;
        if (loaded is null || loaded.Peaks.DurationUs <= 0) throw new InvalidDataException("该来源没有可用音频流。");
        AudioPlaybackCreation creation = _createPlayback(loaded.Info.SampleRate);
        var prepared = new Prepared(loaded, creation);
        try
        {
            await creation.Playback.LoadAsync(path, loaded.Peaks.DurationUs, request.Cancellation.Token).ConfigureAwait(true);
            if (IsCurrent(request)) return prepared;
            ReleasePrepared(prepared, request);
            return null;
        }
        catch (Exception error)
        {
            try { prepared.Dispose(); }
            catch (Exception cleanup) when (IsLoadError(cleanup)) { error.Data["AudioCandidateCleanupFailure"] = cleanup; }
            throw;
        }
    }

    private void ReleasePrepared(Prepared? prepared, Request request)
    {
        try { prepared?.Dispose(); }
        catch (Exception error) when (IsLoadError(error))
        {
            if (IsCurrentIdentity(request)) CleanupFailure = error;
            else System.Diagnostics.Trace.TraceError("过期音频候选释放失败：{0}", error);
        }
    }

    private void MarkUnavailable(Exception error)
    {
        State = AudioSessionState.Unavailable; LoadingPath = null; Failure = error;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsLoadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException;

    private sealed class Prepared(LoadedWaveform loaded, AudioPlaybackCreation creation) : IDisposable
    {
        private AudioPlaybackCreation? _creation = creation;
        public LoadedWaveform Loaded { get; } = loaded;
        public AudioPlaybackCreation TakeCreation()
        {
            AudioPlaybackCreation result = _creation ?? throw new InvalidOperationException("准备的音频已经交接。");
            _creation = null;
            return result;
        }
        public void Dispose() { AudioPlaybackCreation? value = _creation; _creation = null; value?.Playback.Dispose(); }
    }
}
