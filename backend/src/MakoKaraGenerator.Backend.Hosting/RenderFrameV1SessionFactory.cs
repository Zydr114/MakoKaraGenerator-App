using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>同进程 render-frame-v1 的输出预算。</summary>
/// <remarks>
/// 预算在调用实际渲染会话之前检查目标尺寸，因此不允许请求先分配一块
/// 超出边界的像素缓冲区。返回的 <see cref="RenderFrame"/> 仍由调用者拥有。
/// </remarks>
public sealed record RenderFrameV1Limits
{
    /// <summary>创建输出预算。</summary>
    public RenderFrameV1Limits(int maxWidth, int maxHeight, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        MaxBytes = maxBytes;
    }

    /// <summary>默认的预览尺寸上限。</summary>
    public static RenderFrameV1Limits Default { get; } = new(1920, 1080, 64L * 1024 * 1024);

    /// <summary>最大宽度（像素）。</summary>
    public int MaxWidth { get; }

    /// <summary>最大高度（像素）。</summary>
    public int MaxHeight { get; }

    /// <summary>最大 BGRA 像素字节数。</summary>
    public long MaxBytes { get; }

    internal void Validate(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.PixelWidth <= 0 || target.PixelHeight <= 0
            || target.PixelWidth > MaxWidth || target.PixelHeight > MaxHeight)
            throw new PreparedFrameException("RenderFrameV1SizeExceeded", "渲染帧尺寸超过 render-frame-v1 预算。");

        long bytes;
        try { bytes = checked((long)target.PixelWidth * target.PixelHeight * 4); }
        catch (OverflowException)
        { throw new PreparedFrameException("RenderFrameV1SizeExceeded", "渲染帧像素字节数超出可表示范围。"); }
        if (bytes > MaxBytes)
            throw new PreparedFrameException("RenderFrameV1BudgetExceeded", "渲染帧像素预算不足。");
    }
}

/// <summary>
/// 将真实 Runtime.Hosting 会话接入 Backend.Hosting 的 render-frame-v1 同进程接缝。
/// </summary>
/// <remarks>
/// <para>
/// 传入的工厂可以是 <c>ProjectRenderSessionFactory</c>，也可以是后续的受管理进程代理。
/// 适配器只固定不可变快照、检查目标预算并维持中立会话合同，不改变底层渲染实现。
/// </para>
/// <para>
/// 创建会话时固定 <see cref="ProjectSnapshot"/> 引用；每个返回帧由调用者负责释放。
/// 取消会同时连接工厂调用令牌、RenderRequest 令牌和本次调用令牌；若取消或校验失败发生在
/// 底层帧返回之后，适配器会立即释放该帧，避免池缓冲区泄漏。
/// </para>
/// </remarks>
public sealed class RenderFrameV1SessionFactory : IRenderFrameSessionFactory
{
    private readonly IRenderFrameSessionFactory _inner;
    private readonly RenderFrameV1Limits _limits;

    /// <summary>创建同进程 render-frame-v1 适配器。</summary>
    public RenderFrameV1SessionFactory(IRenderFrameSessionFactory inner, RenderFrameV1Limits? limits = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _limits = limits ?? RenderFrameV1Limits.Default;
    }

    /// <summary>建立绑定到固定快照的像素会话。</summary>
    public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(CreateCoreAsync(snapshot, cancellationToken));
    }

    private async Task<IRenderFrameSession> CreateCoreAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IRenderFrameSession? session = null;
        try
        {
            session = await _inner.CreateAsync(snapshot, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new Session(snapshot, session, _limits);
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Session(ProjectSnapshot snapshot, IRenderFrameSession inner, RenderFrameV1Limits limits) : IRenderFrameSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!ReferenceEquals(request.Project, snapshot))
                throw new PreparedFrameException("RenderFrameV1InputsMismatch", "渲染请求不属于固定的项目快照。");

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, request.CancellationToken);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RenderTarget target = request.ResolveTarget();
                limits.Validate(target);
                linked.Token.ThrowIfCancellationRequested();

                RenderFrame? frame = null;
                try
                {
                    frame = await inner.RenderAsync(request with { CancellationToken = linked.Token }, linked.Token)
                        .ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    ValidateFrame(frame, request, target, limits);
                    RenderFrame owned = frame;
                    frame = null;
                    return owned;
                }
                catch
                {
                    frame?.Dispose();
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private static void ValidateFrame(RenderFrame frame, RenderRequest request, RenderTarget target, RenderFrameV1Limits limits)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Target != target || frame.TimeUs != request.TimeUs
                || frame.Format != PixelFormat.Bgra8888Premultiplied
                || frame.PixelMemory.Length != checked(frame.Stride * frame.Height))
                throw new PreparedFrameException("RenderFrameV1OutputMismatch", "渲染会话返回的帧不符合 render-frame-v1 输入。");
            limits.Validate(frame.Target);
        }
    }
}
