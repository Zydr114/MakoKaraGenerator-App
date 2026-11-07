using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>表示会话在取消渲染后仍可供相同固定输入复用。</summary>
public interface IBackendPreviewCancellationRecoverable
{
    /// <summary>当前取消是否已安全处理，可以继续复用会话。</summary>
    bool CanReuseAfterCancellation { get; }
}

/// <summary>在创建失败且清理未完成时保留可重试的会话所有权。</summary>
public static class BackendPreviewFailedCreation
{
    private const string Key = "BackendPreviewFailedCreation.Session";
    /// <summary>记录仍需排水的会话。</summary>
    public static void Attach(Exception error, IRenderFrameSession? session) => error.Data[Key] = session;
    /// <summary>读取仍需排水的会话。</summary>
    public static IRenderFrameSession? Get(Exception error) => error.Data.Contains(Key) ? error.Data[Key] as IRenderFrameSession
        : error.InnerException is null ? null : Get(error.InnerException);

    internal static IRenderFrameSession Worker(WorkerProcessProxy proxy, IDisposable? retention = null) => new FailedWorkerSession(proxy, retention);

    private sealed class FailedWorkerSession(WorkerProcessProxy proxy, IDisposable? retention) : IRenderFrameSession
    {
        public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken token = default) =>
            ValueTask.FromException<RenderFrame>(new InvalidOperationException("创建失败的会话只能排水。"));
        public async ValueTask DisposeAsync()
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
            retention?.Dispose();
        }
    }
}

/// <summary>
/// 预览请求的中立输入。<see cref="PreviewGeneration"/> 表示一次连续的输入代次，
/// <see cref="SourceRevision"/> 表示工程或其他固定源的修订；播放头向前移动时可以复用同一代次。
/// </summary>
public sealed record BackendPreviewRequest
{
    /// <summary>建立固定渲染会话的工厂；工厂本身不依赖 Avalonia。</summary>
    public required IRenderFrameSessionFactory SessionFactory { get; init; }

    /// <summary>本次渲染的不可变请求。</summary>
    public required RenderRequest RenderRequest { get; init; }

    /// <summary>客户端显式维护的预览输入代次，必须为非负数。</summary>
    public required long PreviewGeneration { get; init; }

    /// <summary>固定输入的透明修订标识；协议边界不把它解释为工程对象。</summary>
    public required string SourceRevision { get; init; }

    /// <summary>可选的已验证服务请求序号；未指定时由协调器分配。</summary>
    public long? RequestSequence { get; init; }
}

/// <summary>后台预览更新的生命周期状态。</summary>
public enum BackendPreviewUpdateState
{
    /// <summary>正在准备固定会话或渲染帧。</summary>
    Preparing,
    /// <summary>当前请求产生了可交付帧。</summary>
    Ready,
    /// <summary>当前输入的渲染失败；后续同代次请求可复用该失败。</summary>
    Failed,
}

/// <summary>
/// 已脱离原生渲染对象的中立像素帧。协调器复制像素后立即释放底层 <see cref="RenderFrame"/>，
/// 所以事件订阅者不拥有会话或池缓冲区。
/// </summary>
public sealed record BackendPreviewFrame
{
    internal BackendPreviewFrame(BackendPreviewRequest request, long requestSequence, RenderFrame frame, double renderMs = 0)
    {
        Inspection = frame.Inspection;
        RenderRoundTripMs = renderMs;
        Request = request;
        RequestSequence = requestSequence;
        Pixels = frame.PixelMemory.ToArray();
        Target = frame.Target;
        Format = frame.Format;
        ActualTimeUs = frame.TimeUs;
        DiagnosticSnapshot = frame.DiagnosticSnapshot;
        ImageEffectDiagnostics = frame.ImageEffectDiagnostics;
        MaterialDiagnostics = frame.MaterialDiagnostics;
    }

    /// <summary>生成本帧的请求（包含代次与来源修订）。</summary>
    public BackendPreviewRequest Request { get; }

    /// <summary>请求开启检查时随帧返回的只读投影；未开启或生成失败时为空。</summary>
    public RenderFrameInspection? Inspection { get; }

    /// <summary>Host 侧从提交到收到帧的渲染往返耗时（毫秒），不含会话打开。</summary>
    public double RenderRoundTripMs { get; }

    /// <summary>协调器分配的单调请求序号。</summary>
    public long RequestSequence { get; }

    /// <summary>已复制的像素，长度为目标 stride 乘高度。</summary>
    public byte[] Pixels { get; }

    /// <summary>像素目标尺寸。</summary>
    public RenderTarget Target { get; }

    /// <summary>像素格式。</summary>
    public PixelFormat Format { get; }

    /// <summary>渲染后端实际返回的时间点。</summary>
    public long ActualTimeUs { get; }

    /// <summary>与本帧一起捕获的作者／运行诊断。</summary>
    public RuntimeDiagnosticSnapshot DiagnosticSnapshot { get; }

    /// <summary>透明效果诊断。</summary>
    public ImmutableArray<RenderImageEffectDiagnostic> ImageEffectDiagnostics { get; }

    /// <summary>材质诊断。</summary>
    public ImmutableArray<RenderMaterialDiagnostic> MaterialDiagnostics { get; }
}

/// <summary>预览状态通知；事件回调在协调器内部锁释放之后执行。</summary>
public sealed record BackendPreviewUpdate(
    BackendPreviewRequest Request,
    long RequestSequence,
    BackendPreviewUpdateState State,
    BackendPreviewFrame? Frame = null,
    string? Error = null,
    RuntimeDiagnosticSnapshot? FailureDiagnostics = null,
    string? FailureCode = null,
    ImmutableArray<RenderSourceDiagnostic> FailureSourceDiagnostics = default,
    ImmutableArray<RenderImageEffectDiagnostic> FailureImageEffectDiagnostics = default,
    ImmutableArray<RenderMaterialDiagnostic> FailureMaterialDiagnostics = default);

/// <summary>
/// 中立预览协调器：每个实例最多有一个执行中的请求和一个最新待处理请求。
/// 代次／来源修订改变时取消在途工作；正常播放只替换待处理时间，允许当前帧完成。
/// </summary>
public sealed class BackendPreviewCoordinator : IAsyncDisposable
{
    private sealed record Work(BackendPreviewRequest Request, long Sequence);

    private readonly object _gate = new();
    private Work? _latest;
    private Work? _pending;
    private IRenderFrameSession? _session;
    private bool _sessionNeedsDispose;
    private Exception? _sessionDisposalFailurePrimary;
    private BackendPreviewRequest? _sessionRequest;
    private CancellationTokenSource? _cancellation;
    private Task? _runner;
    private long _nextSequence;
    private bool _running;
    private bool _disposed;
    private bool _releaseRequested;
    private Task? _disposeTask;
    private string? _failure;
    private string? _failureCode;
    private RuntimeDiagnosticSnapshot? _failureDiagnostics;
    private ImmutableArray<RenderSourceDiagnostic> _failureSourceDiagnostics = [];
    private ImmutableArray<RenderImageEffectDiagnostic> _failureImageEffectDiagnostics = [];
    private ImmutableArray<RenderMaterialDiagnostic> _failureMaterialDiagnostics = [];

    /// <summary>向当前订阅者发布状态；回调不得依赖协调器内部锁。</summary>
    public event Action<BackendPreviewUpdate>? Updated;

    /// <summary>使旧输入失效，取消在途请求，并在同一执行者上释放旧会话。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest = null;
            _pending = null;
            _failure = null;
            _failureCode = null;
            _failureDiagnostics = null;
            _failureSourceDiagnostics = [];
            _failureImageEffectDiagnostics = [];
            _failureMaterialDiagnostics = [];
            _releaseRequested = true;
            _cancellation?.Cancel();
            if (!_running)
            {
                _running = true;
                _runner = Task.Run(RunAsync);
            }
        }
    }

    /// <summary>提交一个请求；相同输入与时刻的重复提交会被忽略。</summary>
    public void Submit(BackendPreviewRequest request)
    {
        Validate(request);
        lock (_gate)
        {
            if (_disposed) return;
            if (_latest is { } previous && Same(previous.Request, request)) return;

            bool inputChanged = _latest is null || !SameInput(_latest.Request, request);
            if (inputChanged)
            {
                _failure = null;
                _failureCode = null;
                _failureDiagnostics = null;
                _failureSourceDiagnostics = [];
                _failureImageEffectDiagnostics = [];
                _failureMaterialDiagnostics = [];
                _cancellation?.Cancel();
            }

            Work work = new(request, request.RequestSequence ?? checked(++_nextSequence));
            _latest = work;
            _pending = work;
            if (!_running)
            {
                _running = true;
                _runner = Task.Run(RunAsync);
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                Work? work;
                bool release;
                CancellationToken token;
                lock (_gate)
                {
                    if (_disposed || (_pending is null && !_releaseRequested)) return;
                    work = _pending;
                    _pending = null;
                    release = _releaseRequested;
                    _releaseRequested = false;
                    if (_cancellation is null || _cancellation.IsCancellationRequested)
                    {
                        _cancellation?.Dispose();
                        _cancellation = new CancellationTokenSource();
                    }
                    token = _cancellation.Token;
                }

                try
                {
                    if (_sessionNeedsDispose) await DisposeSessionAsync().ConfigureAwait(false);
                    if (release && _session is not null)
                    {
                        await DisposeSessionAsync().ConfigureAwait(false);
                    }
                    if (work is null) continue;
                    if (_session is not null && (_sessionRequest is null || !SameFixedInput(_sessionRequest, work.Request)))
                    {
                        await DisposeSessionAsync().ConfigureAwait(false);
                    }

                    token.ThrowIfCancellationRequested();
                    string? failure;
                    string? failureCode;
                    RuntimeDiagnosticSnapshot? failureDiagnostics;
                    ImmutableArray<RenderSourceDiagnostic> failureSource;
                    ImmutableArray<RenderImageEffectDiagnostic> failureImage;
                    ImmutableArray<RenderMaterialDiagnostic> failureMaterial;
                    lock (_gate) { failure = _failure; failureCode = _failureCode; failureDiagnostics = _failureDiagnostics; failureSource = _failureSourceDiagnostics; failureImage = _failureImageEffectDiagnostics; failureMaterial = _failureMaterialDiagnostics; }
                    if (failure is not null)
                    {
                        Publish(new(work.Request, work.Sequence, BackendPreviewUpdateState.Failed, Error: failure,
                            FailureDiagnostics: failureDiagnostics, FailureCode: failureCode,
                            FailureSourceDiagnostics: failureSource, FailureImageEffectDiagnostics: failureImage, FailureMaterialDiagnostics: failureMaterial));
                        continue;
                    }

                    Publish(new(work.Request, work.Sequence, BackendPreviewUpdateState.Preparing));
                    if (_session is null)
                    {
                        _session = await work.Request.SessionFactory.CreateAsync(
                            work.Request.RenderRequest.Project, token).ConfigureAwait(false);
                        _sessionRequest = work.Request;
                    }

                    token.ThrowIfCancellationRequested();
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    using RenderFrame frame = await _session.RenderAsync(work.Request.RenderRequest, token).ConfigureAwait(false);
                    double renderMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    token.ThrowIfCancellationRequested();
                    BackendPreviewFrame result = new(work.Request, work.Sequence, frame, renderMs);
                    Publish(new(work.Request, work.Sequence, BackendPreviewUpdateState.Ready, result));
                }
                catch (OperationCanceledException error) when (
                    token.IsCancellationRequested || work?.Request.RenderRequest.CancellationToken.IsCancellationRequested == true)
                {
                    if (_session is null && BackendPreviewFailedCreation.Get(error) is { } failedSession)
                    {
                        _session = failedSession;
                        _sessionRequest = work?.Request;
                        _sessionNeedsDispose = true;
                    }
                    if (_session is not null && _session is not IBackendPreviewCancellationRecoverable { CanReuseAfterCancellation: true })
                    {
                        try { await DisposeSessionAsync().ConfigureAwait(false); }
                        catch (Exception cleanup) { error.Data["PreviewCleanupFailure"] = cleanup; }
                    }
                }
                catch (Exception error)
                {
                    if (_session is null && BackendPreviewFailedCreation.Get(error) is { } failedSession)
                    {
                        _session = failedSession;
                        _sessionRequest = work?.Request;
                        _sessionNeedsDispose = true;
                        _sessionDisposalFailurePrimary = error;
                    }
                    Exception failure = error;
                    if (_sessionNeedsDispose && _sessionDisposalFailurePrimary is { } primary)
                    {
                        if (!ReferenceEquals(primary, error)) primary.Data["PreviewCleanupFailure"] = error;
                        failure = primary;
                    }
                    if (_session is not null && !_sessionNeedsDispose)
                    {
                        _sessionDisposalFailurePrimary = error;
                        try { await DisposeSessionAsync().ConfigureAwait(false); }
                        catch (Exception cleanup) { error.Data["PreviewCleanupFailure"] = cleanup; }
                    }
                    if (token.IsCancellationRequested) continue;
                    if (work is null) continue;
                    lock (_gate)
                    {
                        if (CurrentInput(work.Request))
                        {
                            _failure = failure.Message;
                            _failureCode = (failure as RenderSessionFailureException)?.Code;
                            _failureDiagnostics = (failure as RenderSessionFailureException)?.DiagnosticSnapshot;
                            _failureSourceDiagnostics = (failure as RenderSessionFailureException)?.SourceDiagnostics ?? [];
                            _failureImageEffectDiagnostics = (failure as RenderSessionFailureException)?.ImageEffectDiagnostics ?? [];
                            _failureMaterialDiagnostics = (failure as RenderSessionFailureException)?.MaterialDiagnostics ?? [];
                        }
                    }
                    Publish(new(work.Request, work.Sequence, BackendPreviewUpdateState.Failed, Error: failure.Message,
                        FailureDiagnostics: (failure as RenderSessionFailureException)?.DiagnosticSnapshot,
                        FailureCode: (failure as RenderSessionFailureException)?.Code,
                        FailureSourceDiagnostics: (failure as RenderSessionFailureException)?.SourceDiagnostics ?? [],
                        FailureImageEffectDiagnostics: (failure as RenderSessionFailureException)?.ImageEffectDiagnostics ?? [],
                        FailureMaterialDiagnostics: (failure as RenderSessionFailureException)?.MaterialDiagnostics ?? []));
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (!_disposed && (_pending is not null || _releaseRequested))
                {
                    _running = true;
                    _runner = Task.Run(RunAsync);
                }
            }
        }
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is null) return;
        _sessionNeedsDispose = true;
        await _session.DisposeAsync().ConfigureAwait(false);
        _session = null;
        _sessionRequest = null;
        _sessionNeedsDispose = false;
        _sessionDisposalFailurePrimary = null;
    }

    private void Publish(BackendPreviewUpdate update)
    {
        Action<BackendPreviewUpdate>? handler;
        lock (_gate)
        {
            if (!CanPublish(update)) return;
            handler = Updated;
        }

        // Deliberately outside _gate: subscribers may synchronously submit, replace
        // the generation, or begin disposal from this callback.
        try { handler?.Invoke(update); }
        catch { /* observer failure must not poison the rendering worker */ }
    }

    private bool CanPublish(BackendPreviewUpdate update)
    {
        if (_disposed || _latest is not { } latest || !SameInput(latest.Request, update.Request)) return false;
        if (update.RequestSequence > latest.Sequence) return false;
        if (update.State != BackendPreviewUpdateState.Ready) return true;

        long latestTime = latest.Request.RenderRequest.TimeUs;
        long frameTime = update.Frame?.ActualTimeUs ?? update.Request.RenderRequest.TimeUs;
        // Forward playback may publish an in-flight older frame. A backwards
        // result must use a new generation, otherwise reject it as stale.
        return latestTime >= frameTime;
    }

    private bool CurrentInput(BackendPreviewRequest request)
        => !_disposed && _latest is { } latest && SameInput(latest.Request, request);

    private static bool SameInput(BackendPreviewRequest left, BackendPreviewRequest right)
        => left.PreviewGeneration == right.PreviewGeneration && SameFixedInput(left, right);

    private static bool SameFixedInput(BackendPreviewRequest left, BackendPreviewRequest right)
        => ReferenceEquals(left.SessionFactory, right.SessionFactory)
            && ReferenceEquals(left.RenderRequest.Project, right.RenderRequest.Project)
            && string.Equals(left.SourceRevision, right.SourceRevision, StringComparison.Ordinal)
            && left.RenderRequest.Target == right.RenderRequest.Target
            && left.RenderRequest.Quality == right.RenderRequest.Quality
            && left.RenderRequest.TransparentBackground == right.RenderRequest.TransparentBackground
            && string.Equals(left.RenderRequest.LayoutLineId, right.RenderRequest.LayoutLineId, StringComparison.Ordinal)
            && ReferenceEquals(left.RenderRequest.PreparedFrame, right.RenderRequest.PreparedFrame);

    private static bool Same(BackendPreviewRequest left, BackendPreviewRequest right)
        => SameInput(left, right)
            && left.RequestSequence == right.RequestSequence
            && left.RenderRequest.TimeUs == right.RenderRequest.TimeUs
            && left.RenderRequest.CancellationToken == right.RenderRequest.CancellationToken;

    private static void Validate(BackendPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SessionFactory);
        ArgumentNullException.ThrowIfNull(request.RenderRequest);
        ArgumentNullException.ThrowIfNull(request.RenderRequest.Project);
        if (request.PreviewGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "PreviewGeneration 必须为非负数。");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceRevision);
        if (request.RequestSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(request));
    }

    /// <summary>停止接收请求，等待在途渲染并释放固定会话。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null && !_disposeTask.IsFaulted && !_disposeTask.IsCanceled) return new(_disposeTask);
            _disposed = true;
            _pending = null;
            _cancellation?.Cancel();
            _disposeTask = DisposeCoreAsync(_runner);
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? runner)
    {
        try
        {
            if (runner is not null) await runner.ConfigureAwait(false);
            if (_session is not null) await DisposeSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _latest = null;
                _failure = null;
                _failureCode = null;
                _failureDiagnostics = null;
                _failureSourceDiagnostics = [];
                _failureImageEffectDiagnostics = [];
                _failureMaterialDiagnostics = [];
                _runner = null;
                _cancellation?.Dispose();
                _cancellation = null;
                Updated = null;
            }
        }
    }
}
