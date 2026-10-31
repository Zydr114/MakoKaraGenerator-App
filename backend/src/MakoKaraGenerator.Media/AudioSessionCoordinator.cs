using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Media;

/// <summary>工程音频运行时的准备状态。</summary>
public enum AudioSessionState
{
    /// <summary>目标工程没有音频。</summary>
    Empty,
    /// <summary>正在准备目标或导入候选；导入时旧会话仍然可用。</summary>
    Loading,
    /// <summary>目标播放器与波形已经接入。</summary>
    Ready,
    /// <summary>目标有音频，但恢复失败；保留领域数据并允许重试。</summary>
    Unavailable,
}

/// <summary>拥有当前播放器、波形和加载请求，按工程会话及来源身份接受异步结果。</summary>
/// <remarks>
/// 操作由同一调用方上下文串行发起，异步恢复继续回到该上下文；不引用GUI框架。
/// 工程历史只保存领域数据。换工程或换音频立即摘除不匹配会话，导入则先准备候选。
/// </remarks>
public sealed partial class AudioSessionCoordinator : IDisposable
{
    private readonly Func<string, CancellationToken, Task<LoadedWaveform?>> _decode;
    private readonly Func<int, AudioPlaybackCreation> _createPlayback;
    private long _sessionGeneration;
    private Request? _request;
    private bool _disposed;
    private bool _restoring;
    private string? _sourcePath;
    private Task _restoration = Task.CompletedTask;

    /// <summary>使用注入的解码和播放适配器建立协调器。</summary>
    public AudioSessionCoordinator(Func<string, CancellationToken, Task<LoadedWaveform?>> decode,
        Func<int, AudioPlaybackCreation> createPlayback)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(createPlayback);
        _decode = decode; _createPlayback = createPlayback;
    }

    /// <summary>状态已经在调用方上下文完成变更。</summary>
    public event EventHandler? Changed;
    /// <summary>当前目标来源；项目会话代次由协调器另行持有。</summary>
    public AudioSourceIdentity? Target { get; private set; }
    /// <summary>工程会话或目标音频身份切换的代次；普通编辑不增加。</summary>
    public long Generation => _sessionGeneration;
    /// <summary>当前实际播放器，供适配诊断；协调器独占释放责任，播放控制通过协调器命令。</summary>
    public IAudioPlayback? Playback { get; private set; }
    /// <summary>与实际播放器属于同一来源的峰值数据。</summary>
    public WaveformPeakPyramid? Waveform { get; private set; }
    /// <summary>已接入来源的流信息。</summary>
    public AudioStreamInfo? Info { get; private set; }
    /// <summary>当前准备状态。</summary>
    public AudioSessionState State { get; private set; }
    /// <summary>最近一次准备错误；导入错误不会清除原可用会话。</summary>
    public Exception? Failure { get; private set; }
    /// <summary>最近一次播放器或候选释放错误；不把已采用的目标反向改回旧来源。</summary>
    public Exception? CleanupFailure { get; private set; }
    /// <summary>后端静音回退原因。</summary>
    public string? FallbackReason { get; private set; }
    /// <summary>目标的显示名称，不参与身份比较。</summary>
    public string? DisplayName { get; private set; }
    /// <summary>正在准备的来源；导入时可与当前目标不同。</summary>
    public string? LoadingPath { get; private set; }

    /// <summary>领域数据采用目标音频；新工程必须增加会话代次，即使来源路径相同。</summary>
    /// <param name="project">已经被领域历史采用的工程。</param>
    /// <param name="newSession">是否切换整个工程。</param>
    /// <param name="restore">是否立即异步恢复；预先准备的新工程使用显式交接。</param>
    /// <returns>本次目标恢复任务；普通编辑返回既有任务。</returns>
    public Task SelectProject(Project project, bool newSession = false, bool restore = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioSourceIdentity? next = AudioSourceIdentity.FromProject(project);
        string? display = project.AudioMedia?.OriginalName ?? Path.GetFileName(project.AudioPath);
        bool displayChanged = DisplayName != display;
        DisplayName = display;
        if (!newSession && Target == next)
        {
            if (displayChanged) Changed?.Invoke(this, EventArgs.Empty);
            return _restoration;
        }
        _sessionGeneration++;
        long generation = _sessionGeneration;
        CancelRequest();
        ReleasePlayback();
        Target = next; _sourcePath = project.AudioPath;
        Failure = null; LoadingPath = _sourcePath;
        State = next is null ? AudioSessionState.Empty : AudioSessionState.Loading;
        _restoration = Task.CompletedTask;
        Changed?.Invoke(this, EventArgs.Empty);
        if (restore && next is not null && generation == _sessionGeneration) StartRestore(CancellationToken.None);
        return _restoration;
    }

    /// <summary>等待当前目标恢复；不会提交工程命令或自动重试失败目标。</summary>
    /// <param name="project">当前目标工程。</param>
    /// <param name="cancellationToken">取消本次等待；新发起准备同时采用该取消令牌。</param>
    public Task RestoreAsync(Project project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectProject(project, restore: false);
        if (Target is not null && Playback is null && !_restoring && State == AudioSessionState.Loading)
            StartRestore(cancellationToken);
        return _restoration.WaitAsync(cancellationToken);
    }

    /// <summary>明确重试不可用的目标音频，保持工程与历史。</summary>
    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Target is not null && State == AudioSessionState.Unavailable) StartRestore(cancellationToken);
        return _restoration.WaitAsync(cancellationToken);
    }

    /// <summary>接管已经准备好的新工程播放资源；不会再次解码或修改领域历史。</summary>
    /// <param name="project">当前工程。</param>
    /// <param name="loaded">已经准备的波形。</param>
    /// <param name="creation">被转交的播放器及回退信息。</param>
    public void AdoptPrepared(Project project, LoadedWaveform loaded, AudioPlaybackCreation creation)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(creation);
        try
        {
            SelectProject(project, restore: false);
            if (Target is null) throw new InvalidOperationException("准备的工程没有音频来源。");
            CancelRequest();
            ReleasePlayback();
        }
        catch { creation.Playback.Dispose(); throw; }
        Install(loaded, creation);
    }

    /// <summary>立即取消请求并清空运行时；忽略取消的旧准备仍由其任务在返回后释放。</summary>
    public void Detach()
    {
        if (_disposed) return;
        _sessionGeneration++;
        CancelRequest();
        ReleasePlayback();
        Target = null; _sourcePath = null; DisplayName = null;
        LoadingPath = null; Failure = null; State = AudioSessionState.Empty;
        _restoration = Task.CompletedTask;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重复关闭幂等，释放实际播放器并使迟到结果失效。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
    }

    private void StartRestore(CancellationToken token)
    {
        CancelRequest();
        _restoring = true;
        _restoration = RestoreTargetAsync(token);
    }

    private void Install(LoadedWaveform loaded, AudioPlaybackCreation creation)
    {
        Playback = creation.Playback; Waveform = loaded.Peaks; Info = loaded.Info;
        AttachPlayback(creation.Playback);
        FallbackReason = creation.FallbackReason; LoadingPath = null; Failure = null;
        State = AudioSessionState.Ready;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleasePlayback()
    {
        IAudioPlayback? previous = Playback;
        DetachPlayback(previous);
        Playback = null; Waveform = null; Info = null; FallbackReason = null;
        if (previous is null) return;
        CleanupFailure = null;
        try { previous.Stop(); }
        catch (Exception error) when (IsLoadError(error)) { CleanupFailure = error; }
        finally
        {
            try { previous.Dispose(); }
            catch (Exception error) when (IsLoadError(error))
            {
                CleanupFailure = CleanupFailure is { } stopError ? new AggregateException(stopError, error) : error;
            }
        }
    }

    private void CancelRequest()
    {
        Request? previous = _request;
        _request = null;
        _restoring = false;
        previous?.Cancellation.Cancel();
    }

    private bool IsCurrentIdentity(Request request) => !_disposed && ReferenceEquals(request, _request)
        && request.Session == _sessionGeneration && request.Target == Target;

    private bool IsCurrent(Request request) => IsCurrentIdentity(request) && !request.Cancellation.IsCancellationRequested;

    private Request BeginRequest(CancellationToken token)
    {
        var request = new Request(_sessionGeneration, Target, CancellationTokenSource.CreateLinkedTokenSource(token));
        _request = request;
        return request;
    }

    private void EndRequest(Request request)
    {
        if (!ReferenceEquals(_request, request)) return;
        _request = null; _restoring = false;
    }

    private sealed record Request(long Session, AudioSourceIdentity? Target, CancellationTokenSource Cancellation) : IDisposable
    {
        public void Dispose() => Cancellation.Dispose();
    }
}
