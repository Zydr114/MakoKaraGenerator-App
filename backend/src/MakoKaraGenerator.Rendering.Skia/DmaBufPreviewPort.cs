using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>将既有 Linux Vulkan DMA-BUF 零拷贝池封装为双阶段 IPreviewPort 实现。</summary>
internal sealed class DmaBufPreviewPort : IPreviewPort
{
    private readonly VulkanGpuFramePool _pool;
    private readonly GRContext _context;
    private readonly uint _queueFamily;
    private readonly Func<ulong> _nextSequence;
    private bool _disposed;

    internal DmaBufPreviewPort(
        VulkanGpuFramePool pool,
        GRContext context,
        uint queueFamily,
        Func<ulong> nextSequence)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _queueFamily = queueFamily;
        _nextSequence = nextSequence ?? throw new ArgumentNullException(nameof(nextSequence));

        GpuFramePoolInfo info = _pool.Info;
        PoolInfo = new PreviewPoolInfo(
            PreviewTransportKind.DmaBuf,
            PreviewWireConventions.ApiVulkan,
            info.ProducerEpoch,
            info.PoolGeneration,
            info.Width,
            info.Height,
            PreviewPixelFormat.Bgra8888,
            checked((uint)(info.Width * 4)),
            info.SlotCount,
            info.AllocationBytes,
            PreviewDeviceIdentity.FromGpuDevice(info.Device));
    }

    public PreviewTransportKind TransportKind => PreviewTransportKind.DmaBuf;
    public string BackendApi => PreviewWireConventions.ApiVulkan;
    public PreviewPoolInfo PoolInfo { get; }
    internal VulkanGpuFramePool UnderlyingPool => _pool;

    public bool HasFreeSlot
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pool.HasFreeSlot;
        }
    }

    public bool IsDrained
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pool.IsDrained;
        }
    }

    public IPreviewFrameTarget BeginFrame(ISkiaRenderDevice device, SKImageInfo surfaceInfo, RenderTarget renderTarget)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderTarget);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (device is not VulkanSkiaRenderDevice)
            throw new GpuPreviewException("PreviewTransportUnsupported", "DMA-BUF preview port requires a Vulkan Skia render device.");
        if (renderTarget.PixelWidth != PoolInfo.Width || renderTarget.PixelHeight != PoolInfo.Height ||
            surfaceInfo.Width != PoolInfo.Width || surfaceInfo.Height != PoolInfo.Height)
            throw new GpuPreviewException("GpuPreviewTargetNotInitialized", "DMA-BUF target dimensions do not match initialized pool.");
        if (surfaceInfo.ColorType != SKColorType.Bgra8888 || surfaceInfo.AlphaType != SKAlphaType.Premul)
            throw new GpuPreviewException("PreviewUnsupportedPixelFormat", "DMA-BUF preview port requires Bgra8888 Premul surface.");

        GpuPreviewNative.Lease reserved = _pool.AcquireReserved(_nextSequence());
        SKSurface? surface = null;
        try
        {
            surface = device.CreateSurface(surfaceInfo);
            return new FrameTarget(this, surface, reserved);
        }
        catch
        {
            surface?.Dispose();
            _pool.CancelReserved(reserved);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.Dispose();
    }

    internal static PreviewFrameLease AdaptGpuLease(GpuFrameLease gpuLease) => new LeaseAdapter(gpuLease);

    private sealed class FrameTarget(
        DmaBufPreviewPort owner,
        SKSurface surface,
        GpuPreviewNative.Lease reserved) : IPreviewFrameTarget
    {
        private bool _committed;
        private bool _disposed;

        public SKSurface Surface => _disposed || _committed
            ? throw new ObjectDisposedException(nameof(FrameTarget))
            : surface;

        public ulong FrameSequence => reserved.Token.FrameSequence;

        public PreviewFrameLease Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("DMA-BUF frame target has already been committed.");
            GpuFrameLease gpuLease = owner._pool.CommitReserved(reserved, surface, owner._context, owner._queueFamily);
            _committed = true;
            surface.Dispose();
            return new LeaseAdapter(gpuLease);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            surface.Dispose();
            if (!_committed)
            {
                owner._pool.CancelReserved(reserved);
            }
        }
    }

    private sealed class LeaseAdapter(GpuFrameLease inner) : PreviewFrameLease(PreviewFrameToken.FromGpuToken(inner.Token))
    {
        public override PreviewFrameDelivery ExportForDelivery()
        {
            GpuFrameExport exported = inner.ExportForDelivery();
            GpuFrameDescriptor d = exported.Descriptor;
            var descriptor = new PreviewFrameDescriptor(
                PreviewTransportKind.DmaBuf,
                PreviewWireConventions.ApiVulkan,
                PreviewFrameToken.FromGpuToken(d.Token),
                d.Width,
                d.Height,
                PreviewPixelFormat.Bgra8888,
                d.PlaneStride,
                d.PlaneOffset,
                d.AllocationSize,
                d.AllocationSize,
                PreviewDeviceIdentity.FromGpuDevice(d.Device),
                new PreviewFrameCounters(d.GpuBlits, d.CpuPixelReadbacks, d.QueueIdleWaits),
                DmaBufModifier: d.Modifier,
                QueriedSubresourceBytes: d.QueriedSubresourceSize);
            return new PreviewFrameDelivery(descriptor, exported.FileDescriptor);
        }

        public override void ReleaseAfterConsumerCompletion(PreviewFrameToken token)
        {
            ArgumentNullException.ThrowIfNull(token);
            inner.ReleaseAfterConsumerCompletion(token.ToGpuToken());
        }

        public override void Quarantine() => inner.Quarantine();
        public override void Dispose() => inner.Dispose();
    }
}
