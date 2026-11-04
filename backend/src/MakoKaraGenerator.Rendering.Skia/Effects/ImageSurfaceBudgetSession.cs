using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>实际密度下受管中间像素存储的活动量和历史峰值；不代表完整 native RSS。</summary>
/// <param name="ActiveSurfaces">去重后的活动存储数，包含显式临时表面。</param>
/// <param name="EstimatedBytes">活动 BGRA 像素存储估算。</param>
/// <param name="PeakSurfaces">活动存储数峰值。</param>
/// <param name="PeakBytes">估算字节峰值。</param>
public sealed record ImageSurfaceUsage(int ActiveSurfaces, long EstimatedBytes, int PeakSurfaces, long PeakBytes);

/// <summary>中间表面超限；申请发生前报告当前量、申请量和固定限制。</summary>
public sealed class ImageSurfaceBudgetException : Exception
{
    internal ImageSurfaceBudgetException(string operation, int currentSurfaces, int additionalSurfaces, int maximumSurfaces,
        long currentBytes, long additionalBytes, long maximumBytes)
        : base($"{operation}：活动表面 当前 {currentSurfaces} + 申请 {additionalSurfaces} / {maximumSurfaces}；会话字节 当前 {currentBytes} + 申请 {additionalBytes} / {maximumBytes}。")
    { Operation = operation; CurrentSurfaces = currentSurfaces; AdditionalSurfaces = additionalSurfaces; MaximumSurfaces = maximumSurfaces;
        CurrentBytes = currentBytes; AdditionalBytes = additionalBytes; MaximumBytes = maximumBytes; }
    /// <summary>稳定诊断代码。</summary>
    public string Code { get; } = "ImageSurfaceBudgetExceeded";
    /// <summary>触发申请的生产操作。</summary>
    public string Operation { get; }
    /// <summary>本帧当前活动存储数。</summary>
    public int CurrentSurfaces { get; }
    /// <summary>本帧新增存储数。</summary>
    public int AdditionalSurfaces { get; }
    /// <summary>每帧固定活动限制。</summary>
    public int MaximumSurfaces { get; }
    /// <summary>会话当前估算字节。</summary>
    public long CurrentBytes { get; }
    /// <summary>会话新增估算字节。</summary>
    public long AdditionalBytes { get; }
    /// <summary>会话固定估算字节限制。</summary>
    public long MaximumBytes { get; }
}

/// <summary>会话中间表面总账；线程安全，帧关闭不释放调用者仍持有的产物。</summary>
public sealed class ImageSurfaceBudgetSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, Allocation> _images = [];
    private int _surfaces, _peakSurfaces;
    private long _bytes, _peakBytes;
    private bool _disposed;

    /// <summary>固定会话限值；活动数量按帧、估算字节按整个会话检查。</summary>
    public ImageSurfaceBudgetSession(GraphExecutionBudget? limits = null)
    {
        Limits = limits ?? GraphExecutionBudget.Default;
        if (Limits.MaximumSurfaces <= 0 || Limits.MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "活动表面和会话字节限制必须大于零。");
    }
    /// <summary>不可变限值；调整限值应创建新的固定会话。</summary>
    public GraphExecutionBudget Limits { get; }
    /// <summary>去重后的会话活动存储和峰值；关闭后仍可检查未释放产物。</summary>
    public ImageSurfaceUsage Usage { get { lock (_gate) return new(_surfaces, _bytes, _peakSurfaces, _peakBytes); } }
    /// <summary>开始一张帧账；宿主应让同一帧的捕获、实例图和合成都使用它。</summary>
    public ImageSurfaceBudgetFrame BeginFrame()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return new(this); } }
    /// <summary>保留固定包图片的会话字节账；逐帧借用相同图像只新增帧活动量，不重复计字节。</summary>
    public IDisposable HoldFixedImages(IEnumerable<SkiaGraphSurface> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        using var frame = BeginFrame();
        return frame.BorrowInputs(images);
    }

    internal ImageSurfaceReservation Reserve(ImageSurfaceBudgetFrame frame, int count, long bytes, string operation)
    {
        if (count <= 0 || bytes <= 0) throw new ArgumentOutOfRangeException(nameof(count), "像素存储申请必须非空。");
        bool reclaim;
        lock (_gate)
        {
            Validate(frame); reclaim = !Fits(frame, count, bytes);
            if (!reclaim) return Allocate(frame, count, bytes);
        }
        frame.ReclaimRetained();
        lock (_gate)
        {
            Validate(frame);
            if (!Fits(frame, count, bytes)) throw BudgetError(operation, frame, count, bytes);
            return Allocate(frame, count, bytes);
        }
    }
    internal void CheckAdditional(ImageSurfaceBudgetFrame frame, int count, long bytes, string operation, nint protectedImage = default)
    {
        if (count < 0 || bytes < 0) throw new ArgumentOutOfRangeException(nameof(count));
        bool reclaim;
        lock (_gate) { Validate(frame); reclaim = !Fits(frame, count, bytes); }
        if (reclaim) frame.ReclaimRetained(protectedImage);
        lock (_gate)
        {
            Validate(frame);
            if (!Fits(frame, count, bytes)) throw BudgetError(operation, frame, count, bytes);
        }
    }
    private void Validate(ImageSurfaceBudgetFrame frame)
    {
        if (!ReferenceEquals(frame.Session, this)) throw new InvalidOperationException("帧账不属于本会话，不能跨会话借用存储。");
        ObjectDisposedException.ThrowIf(_disposed, this); ObjectDisposedException.ThrowIf(frame.Closed, frame);
    }
    private bool Fits(ImageSurfaceBudgetFrame frame, int count, long bytes)
        => count <= Limits.MaximumSurfaces - frame.Surfaces && bytes <= Limits.MaximumBytes - _bytes;
    private ImageSurfaceBudgetException BudgetError(string operation, ImageSurfaceBudgetFrame frame, int count, long bytes)
        => new(operation, frame.Surfaces, count, Limits.MaximumSurfaces, _bytes, bytes, Limits.MaximumBytes);
    private ImageSurfaceReservation Allocate(ImageSurfaceBudgetFrame frame, int count, long bytes)
    {
        var allocation = new Allocation(count, bytes); allocation.Claims.Add(frame, 1);
        _surfaces = checked(_surfaces + count); _bytes = checked(_bytes + bytes); UpdatePeak();
        Add(frame, allocation); return new(this, allocation, frame);
    }
    internal ImageSurfaceReservation Borrow(ImageSurfaceBudgetFrame frame, nint image, long bytes)
    {
        if (image == nint.Zero || bytes <= 0) throw new ArgumentException("借用图像必须仍存活且有明确像素存储。");
        lock (_gate)
        {
            if (TryBorrow(frame, image, bytes, out _) is { } reservation) return reservation;
        }
        // 回收会释放图像及预留，不能持有会话锁；借用目标在回收期间受到保护。
        frame.ReclaimRetained(image);
        lock (_gate)
        {
            if (TryBorrow(frame, image, bytes, out long additionalBytes) is { } reservation) return reservation;
            throw BudgetError("BorrowGraphInput", frame, 1, additionalBytes);
        }
    }
    // 由 _gate 保护查询、准入、登记和绑定，多个调用者首次借用同一图像也只创建一笔存储账。
    private ImageSurfaceReservation? TryBorrow(ImageSurfaceBudgetFrame frame, nint image, long bytes, out long additionalBytes)
    {
        Validate(frame);
        _images.TryGetValue(image, out var allocation);
        additionalBytes = allocation is null ? bytes : 0;
        if (allocation is not null)
        {
            if (allocation.Bytes != bytes || allocation.Count != 1)
                throw new InvalidOperationException("相同图像句柄的存储大小不一致。");
            if (allocation.Claims.TryGetValue(frame, out int references))
            {
                allocation.Claims[frame] = checked(references + 1);
                return new(this, allocation, frame);
            }
        }
        if (!Fits(frame, 1, additionalBytes)) return null;
        if (allocation is not null)
        {
            Add(frame, allocation); allocation.Claims[frame] = 1;
            return new(this, allocation, frame);
        }
        var reservation = Allocate(frame, 1, bytes);
        try { reservation.BindImage(image); return reservation; }
        catch { reservation.Dispose(); throw; }
    }
    internal void Bind(Allocation allocation, nint image)
    {
        lock (_gate)
        {
            if (image == nint.Zero || allocation.Handle != nint.Zero || allocation.Claims.Count == 0 || _images.ContainsKey(image))
                throw new InvalidOperationException("新分配的图像必须唯一地关联仍生效的预留。");
            _images.Add(image, allocation); allocation.Handle = image;
        }
    }
    internal void Release(Allocation allocation, ImageSurfaceBudgetFrame frame)
    {
        lock (_gate)
        {
            int references = allocation.Claims[frame];
            if (references > 1) { allocation.Claims[frame] = references - 1; return; }
            allocation.Claims.Remove(frame); frame.Surfaces -= allocation.Count; frame.Bytes -= allocation.Bytes;
            if (allocation.Claims.Count != 0) return;
            if (allocation.Handle != nint.Zero) _images.Remove(allocation.Handle);
            _surfaces -= allocation.Count; _bytes -= allocation.Bytes;
        }
    }
    private static void Add(ImageSurfaceBudgetFrame frame, Allocation allocation)
    {
        frame.Surfaces = checked(frame.Surfaces + allocation.Count); frame.Bytes = checked(frame.Bytes + allocation.Bytes);
        frame.PeakSurfaces = Math.Max(frame.PeakSurfaces, frame.Surfaces); frame.PeakBytes = Math.Max(frame.PeakBytes, frame.Bytes);
    }
    private void UpdatePeak() { _peakSurfaces = Math.Max(_peakSurfaces, _surfaces); _peakBytes = Math.Max(_peakBytes, _bytes); }
    internal ImageSurfaceUsage FrameUsage(ImageSurfaceBudgetFrame frame)
    { lock (_gate) return new(frame.Surfaces, frame.Bytes, frame.PeakSurfaces, frame.PeakBytes); }
    internal void Close(ImageSurfaceBudgetFrame frame) { lock (_gate) frame.Closed = true; }
    /// <summary>拒绝后续申请；现存产物仍由原调用方释放，释放时照常减账。</summary>
    public void Dispose() { lock (_gate) _disposed = true; }

    internal sealed class Allocation(int count, long bytes)
    {
        internal int Count { get; } = count;
        internal long Bytes { get; } = bytes;
        internal nint Handle { get; set; }
        internal Dictionary<ImageSurfaceBudgetFrame, int> Claims { get; } = [];
    }
}

/// <summary>一帧内的共享活动账；Dispose 关闭申请入口，不提前减掉存活图像。</summary>
public sealed class ImageSurfaceBudgetFrame : IDisposable
{
    private readonly ImageSurfaceBudgetSession _session;
    private readonly object _reclaimGate = new();
    private readonly List<ReclaimableRegistration> _reclaimable = [];
    internal ImageSurfaceBudgetFrame(ImageSurfaceBudgetSession session) => _session = session;
    internal ImageSurfaceBudgetSession Session => _session;
    internal bool Closed { get; set; }
    internal int Surfaces { get; set; }
    internal long Bytes { get; set; }
    internal int PeakSurfaces { get; set; }
    internal long PeakBytes { get; set; }
    /// <summary>本帧去重活动量和历史峰值。</summary>
    public ImageSurfaceUsage Usage => _session.FrameUsage(this);
    internal ImageSurfaceReservation Reserve(int count, long bytes, string operation) => _session.Reserve(this, count, bytes, operation);
    internal void CheckAdditional(int count, long bytes, string operation) => _session.CheckAdditional(this, count, bytes, operation);
    // 仅同步对象绘制中的可选缓存使用；回调释放图像及原 reservation，不只减账。
    internal IDisposable RegisterReclaimable(nint image, Action release)
    {
        ObjectDisposedException.ThrowIf(Closed, this);
        ArgumentNullException.ThrowIfNull(release);
        var registration = new ReclaimableRegistration(this, image, release);
        lock (_reclaimGate) _reclaimable.Add(registration);
        return registration;
    }
    internal void ReclaimRetained(nint protectedImage = default)
    {
        ReclaimableRegistration[] registrations;
        lock (_reclaimGate) registrations = _reclaimable.Where(item => item.Image != protectedImage).ToArray();
        foreach (var registration in registrations) registration.Release();
    }
    private void Unregister(ReclaimableRegistration registration)
    { lock (_reclaimGate) _reclaimable.Remove(registration); }
    private sealed class ReclaimableRegistration(ImageSurfaceBudgetFrame frame, nint image, Action release) : IDisposable
    {
        private ImageSurfaceBudgetFrame? _frame = frame;
        internal nint Image { get; } = image;
        internal void Release() { Dispose(); release(); }
        public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Unregister(this);
    }
    internal ImageSurfaceReservations BorrowInputs(IEnumerable<SkiaGraphSurface> surfaces)
    {
        List<ImageSurfaceReservation> leases = [];
        try
        {
            foreach (var surface in surfaces.DistinctBy(surface => surface.Image.Handle))
                leases.Add(_session.Borrow(this, surface.Image.Handle, checked((long)surface.Image.Width * surface.Image.Height * 4)));
            return new(leases);
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    /// <summary>停止本帧的新申请；已有产物的预留保持有效。</summary>
    public void Dispose() { _session.Close(this); ReclaimRetained(); }
}

internal sealed class ImageSurfaceReservation(ImageSurfaceBudgetSession session, ImageSurfaceBudgetSession.Allocation allocation, ImageSurfaceBudgetFrame frame) : IDisposable
{
    private ImageSurfaceBudgetSession? _session = session;
    internal ImageSurfaceBudgetSession.Allocation Allocation { get; } = allocation;
    internal void BindImage(nint image) => (_session ?? throw new ObjectDisposedException(nameof(ImageSurfaceReservation))).Bind(Allocation, image);
    public void Dispose() => Interlocked.Exchange(ref _session, null)?.Release(Allocation, frame);
}
internal sealed class ImageSurfaceReservations(List<ImageSurfaceReservation> leases) : IDisposable
{
    private List<ImageSurfaceReservation>? _leases = leases;
    public void Dispose() { var owned = Interlocked.Exchange(ref _leases, null); if (owned is not null) foreach (var lease in owned) lease.Dispose(); }
}
