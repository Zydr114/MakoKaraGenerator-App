using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>跨设备统一的连续预览池代际与预算管理器（覆盖 CPU·SHM、GPU·SHM 与 GPU·DMA-BUF）。</summary>
internal sealed class PreviewPortSessionManager : IDisposable
{
    private readonly ISkiaRenderDevice _device;
    private readonly List<IPreviewPort> _ports = [];
    private readonly ulong _producerEpoch = CreateNonZeroRandomUInt64();
    private IPreviewPort? _currentPort;
    private ulong _poolGeneration;
    private ulong _frameSequence;
    private bool _failed;
    private bool _disposed;

    internal PreviewPortSessionManager(ISkiaRenderDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    internal bool IsHealthy => !_disposed && !_failed;

    internal IPreviewPort RequireCurrentPort(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_device.SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed)
                throw new GpuPreviewException("GpuPreviewUnavailable", "Preview output stream has been quarantined after an earlier failure.");
            if (_currentPort is null || _currentPort.PoolInfo.Width != target.PixelWidth || _currentPort.PoolInfo.Height != target.PixelHeight)
                throw new GpuPreviewException("GpuPreviewTargetNotInitialized", "Initialize the preview output pool for this exact target size before rendering.");
            return _currentPort;
        }
    }

    internal PreviewPoolInfo InitializeOutput(RenderTarget target, ulong maximumAllocationBytes, PreviewOutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new PreviewOutputOptions();

        lock (_device.SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed)
                throw new GpuPreviewException("GpuPreviewUnavailable", "Preview output session has failed and cannot allocate replacement pools.");
            if (target.PixelWidth is < 64 or > 2560 || target.PixelHeight is < 64 or > 1440)
                throw new GpuPreviewException("GpuPreviewSizeUnsupported", "Shared preview output is limited to 64..2560 x 64..1440.");
            if (maximumAllocationBytes is 0 or > 192UL * 1024 * 1024)
                throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "The reserved preview session budget must be within 1..201326592 bytes.");

            PreviewTransportKind transport = PreviewTransportSelector.Resolve(_device.Info.Backend, options.Transport);
            if (transport == PreviewTransportKind.DmaBuf && options.PixelFormat != PreviewPixelFormat.Bgra8888)
            {
                throw new GpuPreviewException("PreviewUnsupportedPixelFormat", "DMA-BUF preview transport only supports bgra8888.");
            }

            uint expectedStride = options.StrideBytes == 0 ? checked((uint)(target.PixelWidth * 4)) : options.StrideBytes;
            bool matchesExisting = _currentPort is not null
                && _currentPort.TransportKind == transport
                && _currentPort.PoolInfo.Width == target.PixelWidth
                && _currentPort.PoolInfo.Height == target.PixelHeight
                && _currentPort.PoolInfo.PixelFormat == options.PixelFormat
                && (transport == PreviewTransportKind.DmaBuf || _currentPort.PoolInfo.StrideBytes == expectedStride);

            if (!matchesExisting)
            {
                foreach (IPreviewPort retired in _ports.Where(port => port != _currentPort && port.IsDrained).ToArray())
                {
                    retired.Dispose();
                    _ports.Remove(retired);
                }

                if (_currentPort is { IsDrained: true })
                {
                    _currentPort.Dispose();
                    _ports.Remove(_currentPort);
                    _currentPort = null;
                }

                ulong retained = _ports.Aggregate(0UL, (sum, port) => checked(sum + port.PoolInfo.AllocationBytes));
                if (retained >= maximumAllocationBytes || _ports.Count >= 2)
                {
                    throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "Outstanding preview pool generations exceed the session budget.");
                }

                ulong remainingBudget = maximumAllocationBytes - retained;
                ulong nextGeneration = checked(++_poolGeneration);
                IPreviewPort created = transport switch
                {
                    PreviewTransportKind.DmaBuf => CreateDmaBufPort(target, _producerEpoch, nextGeneration, remainingBudget),
                    PreviewTransportKind.Shm => new ShmPreviewPort(
                        _device.SynchronizationRoot,
                        _device.PreviewIdentity,
                        target,
                        options.PixelFormat,
                        options.StrideBytes,
                        _producerEpoch,
                        nextGeneration,
                        remainingBudget,
                        options.ShmToken,
                        () => _failed = true,
                        () => checked(++_frameSequence)),
                    _ => throw new ArgumentOutOfRangeException(nameof(options), transport, "Unsupported preview transport."),
                };

                _ports.Add(created);
                _currentPort = created;
            }

            ulong totalRetained = _ports.Aggregate(0UL, (sum, port) => checked(sum + port.PoolInfo.AllocationBytes));
            if (totalRetained > maximumAllocationBytes)
            {
                throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "Existing preview pool generations exceed the newly reserved session budget.");
            }

            return _currentPort!.PoolInfo with { RetainedAllocationBytes = totalRetained };
        }
    }

    private DmaBufPreviewPort CreateDmaBufPort(RenderTarget target, ulong producerEpoch, ulong generation, ulong remainingBudget)
    {
        if (_device is not VulkanSkiaRenderDevice { SupportsDmaBufExport: true } vulkan)
        {
            throw new GpuPreviewException(
                "PreviewTransportUnsupported",
                "DMA-BUF preview port requires a Vulkan device initialized with DMA-BUF export extensions.");
        }

        VulkanGpuFramePool pool = vulkan.CreateBorrowedDmaBufPool(target, producerEpoch, generation, remainingBudget, () => _failed = true);
        return new DmaBufPreviewPort(pool, vulkan.GrContext, vulkan.QueueFamilyIndex, () => checked(++_frameSequence));
    }

    public void Dispose()
    {
        lock (_device.SynchronizationRoot)
        {
            if (_disposed) return;
            _disposed = true;
            Exception? error = SkiaNativeResourceCleanup.Run(null, [.. _ports.Select<IPreviewPort, Action>(port => port.Dispose)]);
            _ports.Clear();
            _currentPort = null;
            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }

    private static ulong CreateNonZeroRandomUInt64()
    {
        ulong value = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        return value == 0 ? 1UL : value;
    }
}
