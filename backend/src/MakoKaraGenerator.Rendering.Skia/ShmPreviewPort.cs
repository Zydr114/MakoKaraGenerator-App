using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>平台无关的三槽共享内存预览端口（支持 Vulkan Ganesh 与 CPU 光栅设备）。</summary>
internal sealed class ShmPreviewPort : IPreviewPort
{
    private enum SlotState
    {
        Free,
        Writing,
        Ready,
        Exported,
    }

    private sealed class Slot
    {
        public SlotState State = SlotState.Free;
        public ulong Epoch;
        public PreviewFrameToken? CurrentToken;
    }

    private readonly object _gate;
    private readonly Action _failStream;
    private readonly Func<ulong> _nextSequence;
    private readonly Slot[] _slots = [new(), new(), new()];
    private readonly ulong _slotSizeBytes;
    private IShmPoolBacking? _backing;
    private uint _cursor;
    private bool _quarantined;

    internal ShmPreviewPort(
        object synchronizationRoot,
        PreviewDeviceIdentity deviceIdentity,
        RenderTarget target,
        PreviewPixelFormat pixelFormat,
        uint requestedStrideBytes,
        ulong producerEpoch,
        ulong poolGeneration,
        ulong maximumAllocationBytes,
        string? shmToken,
        Action failStream,
        Func<ulong> nextSequence,
        IShmPoolBacking? customBacking = null)
    {
        ArgumentNullException.ThrowIfNull(synchronizationRoot);
        ArgumentNullException.ThrowIfNull(deviceIdentity);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(failStream);
        ArgumentNullException.ThrowIfNull(nextSequence);

        if (target.PixelWidth is < 64 or > 2560 || target.PixelHeight is < 64 or > 1440)
            throw new GpuPreviewException("GpuPreviewSizeUnsupported", "Shared preview output is limited to 64..2560 x 64..1440.");
        if (producerEpoch == 0 || poolGeneration == 0)
            throw new GpuPreviewException("PreviewPoolInvalidGeneration", "ProducerEpoch and PoolGeneration must be positive.");

        uint minStride = checked((uint)(target.PixelWidth * 4));
        uint stride = requestedStrideBytes == 0 ? minStride : requestedStrideBytes;
        if (stride < minStride || stride % 4 != 0)
            throw new GpuPreviewException("PreviewInvalidStride", $"SHM 预览行步长非法：{stride}（要求 >= {minStride} 且 4 字节对齐）。");

        ulong slotBytes = checked((ulong)stride * (ulong)target.PixelHeight);
        ulong totalBytes = checked(slotBytes * 3UL);
        if (maximumAllocationBytes == 0 || totalBytes > maximumAllocationBytes || totalBytes > 64UL * 1024 * 1024)
            throw new GpuPreviewException("GpuPreviewOutOfMemory", $"SHM 三槽共享池需 {totalBytes} 字节，超出预算上限 {maximumAllocationBytes} 字节。");

        _gate = synchronizationRoot;
        _failStream = failStream;
        _nextSequence = nextSequence;
        _slotSizeBytes = slotBytes;
        Target = target;
        BackendApi = PreviewWireConventions.NormalizeBackendApi(deviceIdentity.BackendApi);

        if (customBacking is not null)
        {
            if (customBacking.TotalBytes < totalBytes)
                throw new ArgumentException("Custom SHM backing is smaller than required 3-slot pool size.", nameof(customBacking));
            _backing = customBacking;
        }
        else
        {
            _backing = ShmPoolBackingFactory.Create(totalBytes, shmToken);
        }

        PoolInfo = new PreviewPoolInfo(
            PreviewTransportKind.Shm,
            BackendApi,
            producerEpoch,
            poolGeneration,
            target.PixelWidth,
            target.PixelHeight,
            pixelFormat,
            stride,
            SlotCount: 3,
            AllocationBytes: totalBytes,
            Device: deviceIdentity,
            ShmResourceName: _backing.ResourceName);
    }

    public PreviewTransportKind TransportKind => PreviewTransportKind.Shm;
    public string BackendApi { get; }
    public PreviewPoolInfo PoolInfo { get; }
    internal RenderTarget Target { get; }

    public bool HasFreeSlot
    {
        get
        {
            lock (_gate)
            {
                EnsureOpen();
                return !_quarantined && _slots.Any(slot => slot.State == SlotState.Free);
            }
        }
    }

    public bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                EnsureOpen();
                return IsDrainedCore();
            }
        }
    }

    public IPreviewFrameTarget BeginFrame(ISkiaRenderDevice device, SKImageInfo surfaceInfo, RenderTarget renderTarget)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderTarget);

        if (renderTarget.PixelWidth != Target.PixelWidth || renderTarget.PixelHeight != Target.PixelHeight ||
            surfaceInfo.Width != Target.PixelWidth || surfaceInfo.Height != Target.PixelHeight)
        {
            throw new GpuPreviewException("GpuPreviewTargetNotInitialized", "SHM 帧请求尺寸与已分配共享池不匹配。");
        }

        if (surfaceInfo.AlphaType != SKAlphaType.Premul)
        {
            throw new GpuPreviewException("PreviewUnsupportedAlphaType", $"SHM 预览端口要求预乘 Alpha（Premul），收到 '{surfaceInfo.AlphaType}'。");
        }

        PreviewPixelFormat actualFormat = PreviewTransportSelector.FromSkColorType(surfaceInfo.ColorType);
        if (actualFormat != PoolInfo.PixelFormat)
        {
            throw new GpuPreviewException(
                "PreviewUnsupportedPixelFormat",
                $"SHM 表面像素格式 '{actualFormat}' 与池协商格式 '{PoolInfo.PixelFormat}' 不一致。");
        }

        lock (_gate)
        {
            EnsureOpen();
            EnsureNotQuarantined();

            int foundSlot = -1;
            for (uint offset = 0; offset < 3; offset++)
            {
                uint candidate = (_cursor + offset) % 3;
                if (_slots[candidate].State == SlotState.Free)
                {
                    foundSlot = (int)candidate;
                    break;
                }
            }

            if (foundSlot < 0)
            {
                throw new GpuPreviewException("GpuPreviewNoFreeSlot", "SHM 三槽共享池暂无空闲槽位。");
            }

            Slot slot = _slots[foundSlot];
            if (slot.Epoch == ulong.MaxValue)
            {
                QuarantineCore();
                throw new GpuPreviewException("GpuPreviewPoolQuarantined", "SHM slot epoch exhausted.");
            }

            SKSurface surface = device.CreateSurface(surfaceInfo);
            try
            {
                ulong frameSeq = _nextSequence();
                ulong slotEpoch = checked(++slot.Epoch);
                var token = new PreviewFrameToken(
                    PoolInfo.ProducerEpoch,
                    PoolInfo.PoolGeneration,
                    frameSeq,
                    slotEpoch,
                    (uint)foundSlot);
                slot.State = SlotState.Writing;
                slot.CurrentToken = token;
                _cursor = ((uint)foundSlot + 1) % 3;
                return new FrameTarget(this, device, surface, surfaceInfo, token);
            }
            catch
            {
                surface.Dispose();
                throw;
            }
        }
    }

    private Lease CommitSlot(ISkiaRenderDevice device, SKSurface surface, SKImageInfo surfaceInfo, PreviewFrameToken token)
    {
        IntPtr destination;
        lock (_gate)
        {
            EnsureOpen();
            EnsureNotQuarantined();
            Slot slot = FindMatchingSlot(token, SlotState.Writing);
            ulong byteOffset = checked((ulong)token.SlotId * _slotSizeBytes);
            destination = checked((IntPtr)((long)_backing!.BasePointer + (long)byteOffset));
            _ = slot;
        }

        // 严禁前置调用 device.FlushAndSubmit(surface)：
        // Skia Ganesh 的 surface.ReadPixels 内部已自动合并 Flush、Staging 拷贝与单次 Fence 等待。
        bool ok;
        try
        {
            ok = device.ReadPixels(surface, surfaceInfo, destination, checked((int)PoolInfo.StrideBytes));
        }
        catch (Exception error)
        {
            lock (_gate) QuarantineCore();
            throw new GpuPreviewException("PreviewReadbackFailed", $"SHM 端口单次 ReadPixels 异常：{error.Message}");
        }

        if (!ok)
        {
            lock (_gate) QuarantineCore();
            throw new GpuPreviewException("PreviewReadbackFailed", $"Skia {device.Info.Backend} 表面向 SHM 槽位 ReadPixels 返回失败。");
        }

        // 确保像素写入对其他进程只读视图全局可见后再发布 FrameReady 消息。
        Thread.MemoryBarrier();

        lock (_gate)
        {
            EnsureOpen();
            EnsureNotQuarantined();
            Slot slot = FindMatchingSlot(token, SlotState.Writing);
            slot.State = SlotState.Ready;
            return new Lease(this, token);
        }
    }

    private void CancelUncommittedSlot(PreviewFrameToken token)
    {
        lock (_gate)
        {
            if (_backing is null || _quarantined) return;
            Slot slot = _slots[token.SlotId];
            if (slot.State == SlotState.Writing && slot.CurrentToken == token)
            {
                slot.State = SlotState.Free;
                slot.CurrentToken = null;
            }
        }
    }

    private PreviewFrameDelivery ExportSlot(PreviewFrameToken token)
    {
        lock (_gate)
        {
            EnsureOpen();
            EnsureNotQuarantined();
            Slot slot = FindMatchingSlot(token, SlotState.Ready);
            ulong offsetBytes = checked((ulong)token.SlotId * _slotSizeBytes);
            var counters = new PreviewFrameCounters(
                GpuBlits: 0,
                CpuPixelReadbacks: 1,
                QueueIdleWaits: BackendApi == PreviewWireConventions.ApiVulkan ? 1UL : 0UL);
            var descriptor = new PreviewFrameDescriptor(
                PreviewTransportKind.Shm,
                BackendApi,
                token,
                PoolInfo.Width,
                PoolInfo.Height,
                PoolInfo.PixelFormat,
                PoolInfo.StrideBytes,
                offsetBytes,
                _slotSizeBytes,
                PoolInfo.AllocationBytes,
                PoolInfo.Device,
                counters);
            slot.State = SlotState.Exported;
            return new PreviewFrameDelivery(descriptor, auxiliaryHandle: null);
        }
    }

    private void ReleaseExportedSlot(PreviewFrameToken expected, PreviewFrameToken actual)
    {
        lock (_gate)
        {
            EnsureOpen();
            if (expected != actual)
                throw new GpuPreviewException("GpuPreviewStaleLease", "Consumer completion token does not match the SHM lease.");
            Slot slot = FindMatchingSlot(expected, SlotState.Exported);
            slot.State = SlotState.Free;
            slot.CurrentToken = null;
        }
    }

    private void DisposeReadyOrExportedLease(PreviewFrameToken token, bool wasExported)
    {
        lock (_gate)
        {
            if (_backing is null) return;
            if (wasExported)
            {
                QuarantineCore();
                return;
            }

            if (_quarantined) return;
            Slot slot = _slots[token.SlotId];
            if (slot.State == SlotState.Ready && slot.CurrentToken == token)
            {
                slot.State = SlotState.Free;
                slot.CurrentToken = null;
            }
        }
    }

    private Slot FindMatchingSlot(PreviewFrameToken token, SlotState expectedState)
    {
        if (token.SlotId >= _slots.Length)
            throw new GpuPreviewException("GpuPreviewStaleLease", "SHM slot index out of range.");
        Slot slot = _slots[token.SlotId];
        if (slot.State != expectedState || slot.CurrentToken != token)
            throw new GpuPreviewException("GpuPreviewStaleLease", $"SHM lease state mismatch (expected {expectedState}, actual {slot.State}).");
        return slot;
    }

    private bool IsDrainedCore() =>
        !_quarantined && _slots.All(slot => slot.State == SlotState.Free);

    private void QuarantineCore()
    {
        _quarantined = true;
        _failStream();
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_backing is null, this);

    private void EnsureNotQuarantined()
    {
        if (_quarantined)
            throw new GpuPreviewException("GpuPreviewPoolQuarantined", "SHM preview pool has been quarantined and cannot issue new frames.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_backing is null) return;
            bool drained = IsDrainedCore();
            IShmPoolBacking backing = _backing;
            _backing = null;
            backing.Dispose();
            if (!drained)
            {
                _failStream();
                throw new GpuPreviewException("GpuPreviewNotDrained", "SHM preview pool was disposed while consumer leases remained unreturned.");
            }
        }
    }

    private sealed class FrameTarget(
        ShmPreviewPort owner,
        ISkiaRenderDevice device,
        SKSurface surface,
        SKImageInfo surfaceInfo,
        PreviewFrameToken token) : IPreviewFrameTarget
    {
        private bool _committed;
        private bool _disposed;

        public SKSurface Surface => _disposed || _committed
            ? throw new ObjectDisposedException(nameof(FrameTarget))
            : surface;

        public ulong FrameSequence => token.FrameSequence;

        public PreviewFrameLease Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Preview frame target has already been committed.");
            PreviewFrameLease lease = owner.CommitSlot(device, surface, surfaceInfo, token);
            _committed = true;
            surface.Dispose();
            return lease;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            surface.Dispose();
            if (!_committed)
            {
                owner.CancelUncommittedSlot(token);
            }
        }
    }

    private sealed class Lease(ShmPreviewPort owner, PreviewFrameToken token) : PreviewFrameLease(token)
    {
        private bool _exported;
        private bool _released;
        private bool _disposed;

        public override PreviewFrameDelivery ExportForDelivery()
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _released, this);
                if (_exported)
                    throw new GpuPreviewException("GpuPreviewLeaseAlreadyExported", "A preview frame lease may be exported only once.");
                _exported = true;
                try
                {
                    return owner.ExportSlot(Token);
                }
                catch
                {
                    owner.QuarantineCore();
                    throw;
                }
            }
        }

        public override void ReleaseAfterConsumerCompletion(PreviewFrameToken completedToken)
        {
            ArgumentNullException.ThrowIfNull(completedToken);
            lock (owner._gate)
            {
                if (!_exported || _released)
                    throw new GpuPreviewException("GpuPreviewStaleLease", "Consumer completion does not match an outstanding exported SHM lease.");
                owner.ReleaseExportedSlot(Token, completedToken);
                _released = true;
            }
        }

        public override void Quarantine()
        {
            lock (owner._gate) owner.QuarantineCore();
        }

        public override void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed || _released) return;
                _disposed = true;
                owner.DisposeReadyOrExportedLease(Token, _exported);
            }
        }
    }
}
