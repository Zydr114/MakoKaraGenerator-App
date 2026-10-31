using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private readonly object _audioPreparationGate = new();
    private readonly CancellationTokenSource _audioPreparationStop = new();
    private readonly HashSet<AudioPreparationOperation> _audioPreparationOperations = [];
    private readonly BackendTaskScheduler _audioPreparationScheduler = new(new(1, 1, 1, 4, 4));
    private IBackendAudioPreparationExecutor? _audioPreparationExecutor;
    private bool _audioPreparationConfigured;
    private bool _audioPreparationStopping;
    private int _audioPreparationTimeoutSeconds = 60;
    private BackendAudioPreparationCapabilities _audioPreparationCapabilities = new(false, false, "WorkerAudioPreparationUnavailable");

    /// <summary>可信Server组合显式选择Worker-only准备；禁用时不回退Host native解码。</summary>
    public void ConfigureAudioPreparation(IBackendAudioPreparationExecutor? executor,
        BackendAudioPreparationCapabilities? capabilities = null, int operationTimeoutSeconds = 60)
    {
        if (operationTimeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(operationTimeoutSeconds));
        lock (_audioPreparationGate)
        {
            if (_audioPreparationConfigured || _audioPreparationStopping || _audioPreparationStop.IsCancellationRequested
                || _audioPreparationOperations.Count != 0 || Volatile.Read(ref _stopping) != 0)
                throw new InvalidOperationException("音频准备组合只能在接受操作前配置一次。");
            _audioPreparationConfigured = true;
            _audioPreparationExecutor = executor;
            _audioPreparationCapabilities = capabilities ?? new(false, false, "WorkerAudioPreparationUnavailable");
            _audioPreparationTimeoutSeconds = operationTimeoutSeconds;
        }
    }

    /// <summary>实际Worker能力与有限完整波形预算；配置事实不表示任意原媒体都可准备。</summary>
    public BackendAudioPreparationCapabilitiesDto AudioPreparationCapabilities => new(
        _audioPreparationConfigured && _audioPreparationExecutor is not null && _audioPreparationCapabilities.GuardAvailable
            && _audioPreparationCapabilities.ToolStartupVerified,
        _audioPreparationExecutor is not null && _audioPreparationCapabilities.GuardAvailable && _audioPreparationCapabilities.ToolStartupVerified
            ? null : _audioPreparationCapabilities.FailureCode ?? "WorkerAudioPreparationUnavailable",
        WorkerAudioPreparationContract.Profile, ["wav", "mp3", "flac", "aac", "mov", "ogg"],
        WorkerAudioPreparationContract.MaximumResultBytes, WorkerAudioPreparationContract.MaximumBaseBuckets,
        WorkerAudioPreparationContract.BaseBucketSamples, _audioPreparationTimeoutSeconds, 2, 1, 1, 4, 4);

    private AudioPreparationOperation? ReserveAudioPreparation(string clientId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_audioPreparationGate)
        {
            if (!_audioPreparationConfigured) return null; // Explicit trusted in-process default remains native.
            if (_audioPreparationStopping || Volatile.Read(ref _stopping) != 0 || _audioPreparationStop.IsCancellationRequested)
                throw new ResourceStoreException(BackendErrorCode.SessionClosing, "服务正在停止音频准备。");
            if (!AudioPreparationCapabilities.Enabled)
                throw new ResourceStoreException(BackendErrorCode.TaskUnavailable, "受管理音频准备Worker不可用，不回退本机解码。");
            AudioPreparationOperation operation = new(this, _audioPreparationTimeoutSeconds, token, _audioPreparationStop.Token);
            BackendTaskScheduler.Reservation? reservation = _audioPreparationScheduler.TryReserve(clientId, "audio-preparation");
            if (reservation is null)
            {
                operation.Dispose();
                throw new ResourceStoreException(BackendErrorCode.RequestQuotaExceeded, "音频准备排队配额已达到上限。");
            }
            operation.Reservation = reservation;
            _audioPreparationOperations.Add(operation);
            return operation;
        }
    }

    private Task<LoadedWaveform?> PrepareManagedAudioAsync(StoredAsset audio, CancellationToken token)
    {
        BackendAudioPreparationGrant grant = new(audio.StoragePath, audio.Size, audio.Sha256);
        _audioPreparationExecutor!.ValidateInput(grant);
        return _audioPreparationExecutor.ExecuteAsync(grant, token);
    }

    /// <summary>停止新的Worker准备并取消、等待所有已拥有操作；Server须在等待HTTP回执前调用。</summary>
    public async Task StopAudioPreparationAsync()
    {
        if (_audioPreparationConfigured)
        {
            lock (_jobGate)
                foreach (BackendJob job in _jobs.Values.Where(job => job.Read().Kind is "open" or "recover")) job.Cancel();
        }
        Task[] completion;
        lock (_audioPreparationGate)
        {
            // Stopping admission precedes cancellation and the receipt drain.
            _audioPreparationStopping = true;
            completion = [.. _audioPreparationOperations.Select(operation => operation.Completion.Task)];
        }
        await _audioPreparationStop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(completion).ConfigureAwait(false);
    }

    private static BackendErrorResponse AudioPreparationFailure(Exception error) => error switch
    {
        ResourceStoreException resource => new(resource.Code, resource.Message, resource.Field),
        RenderSessionFailureException failure => new(BackendErrorCode.TaskExecutionFailed, "受管理音频准备未完成。",
            FailureCode: failure.Code),
        WorkerProtocolException failure => new(BackendErrorCode.TaskExecutionFailed, "受管理音频准备未完成。",
            FailureCode: failure.Code),
        Application.ProjectWriteLeaseConflictException => new(BackendErrorCode.WriteLeaseConflict, "工程目录已经有写者。"),
        _ => new(BackendErrorCode.ProjectLoadFailed, "持久操作失败，原工程与原会话保留。"),
    };

    private sealed class AudioPreparationOperation : IDisposable
    {
        private readonly ProjectSessionRegistry _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenRegistration _caller, _stop, _expired;
        private int _cause;
        private int _disposed;
        internal BackendTaskScheduler.Reservation? Reservation;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token => _cancellation.Token;
        internal bool TimedOut => Volatile.Read(ref _cause) == 2;
        internal AudioPreparationOperation(ProjectSessionRegistry owner, int seconds, CancellationToken caller, CancellationToken stop)
        {
            _owner = owner;
            _deadline = new(TimeSpan.FromSeconds(seconds));
            _caller = caller.Register(() => Cancel(1));
            _stop = stop.Register(() => Cancel(1));
            _expired = _deadline.Token.Register(() => Cancel(2));
        }
        private void Cancel(int cause)
        {
            Interlocked.CompareExchange(ref _cause, cause, 0);
            _cancellation.Cancel();
        }
        internal Task WaitAsync() => Reservation!.WaitAsync(Token);
        internal static RenderSessionFailureException TimeoutFailure(OperationCanceledException primary) =>
            new("WorkerAudioPreparationTimedOut", "受管理音频准备超过操作期限。", innerException: primary);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _caller.Dispose(); _stop.Dispose(); _expired.Dispose(); _deadline.Dispose(); _cancellation.Dispose();
            Reservation?.Dispose();
            lock (_owner._audioPreparationGate) _owner._audioPreparationOperations.Remove(this);
            Completion.TrySetResult();
        }
    }
}
