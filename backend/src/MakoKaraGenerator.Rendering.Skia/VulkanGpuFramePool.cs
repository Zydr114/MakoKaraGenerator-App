using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

internal sealed class VulkanGpuFramePool : IDisposable
{
    private readonly object _gate;
    private readonly Action _failStream;
    private IntPtr _pool;
    private readonly GpuPreviewNative.PoolInfo _info;

    internal VulkanGpuFramePool(object gate, GpuPreviewNative.Context context, RenderTarget target,
        ulong producerEpoch, ulong generation, ulong maximumAllocation, Action failStream)
    {
        _gate = gate;
        _failStream = failStream;
        GpuPreviewNative.PoolConfig config = new()
        {
            Size = (uint)Marshal.SizeOf<GpuPreviewNative.PoolConfig>(), Version = 1,
            Width = checked((uint)target.PixelWidth), Height = checked((uint)target.PixelHeight), SlotCount = 3,
            ProducerEpoch = producerEpoch, PoolGeneration = generation, MaxAllocationBytes = maximumAllocation,
        };
        var error = GpuPreviewNative.Error.Create();
        var info = GpuPreviewNative.PoolInfo.Create();
        GpuPreviewNative.Check(GpuPreviewNative.Create(context, config, out _pool, ref info, ref error), error, "create shared GPU pool");
        _info = info;
        Target = target;
        Info = new(producerEpoch, generation, target.PixelWidth, target.PixelHeight, info.SlotCount, info.AllocationBytes,
            new(info.DeviceName, info.VendorId, info.DeviceId, info.DrmRenderMajor, info.DrmRenderMinor, Convert.ToHexStringLower(info.DeviceUuid)));
    }

    internal RenderTarget Target { get; }
    internal GpuFramePoolInfo Info { get; }
    internal ulong AllocationBytes => _info.AllocationBytes;
    internal bool HasFreeSlot
    {
        get
        {
            lock (_gate)
            {
                EnsureOpen();
                var state = GpuPreviewNative.PoolState.Create();
                var error = GpuPreviewNative.Error.Create();
                GpuPreviewNative.Check(GpuPreviewNative.ReadState(_pool, ref state, ref error), error, "read shared GPU pool state");
                return state.Quarantined == 0 && state.FreeSlots > 0;
            }
        }
    }
    internal bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                EnsureOpen();
                var state = GpuPreviewNative.PoolState.Create();
                var error = GpuPreviewNative.Error.Create();
                GpuPreviewNative.Check(GpuPreviewNative.ReadState(_pool, ref state, ref error), error, "read shared GPU pool state");
                return state.Quarantined == 0 && state.FreeSlots == 3 && state.ReservedSlots == 0 && state.ReadySlots == 0 && state.ExportedSlots == 0;
            }
        }
    }

    internal GpuPreviewNative.Lease AcquireReserved(ulong sequence)
    {
        lock (_gate)
        {
            EnsureOpen();
            var lease = GpuPreviewNative.Lease.Create();
            var error = GpuPreviewNative.Error.Create();
            GpuPreviewNative.Check(GpuPreviewNative.Acquire(_pool, sequence, ref lease, ref error), error, "reserve shared GPU slot");
            return lease;
        }
    }

    internal GpuFrameLease CommitReserved(GpuPreviewNative.Lease lease, SKSurface surface, GRContext context, uint queueFamily)
    {
        lock (_gate)
        {
            EnsureOpen();
            // 包含共享纹理提交、复制与本机同步等待；这是宿主墙钟，不是 GPU 时间戳。
            using var submission = PerformanceTelemetry.Measure(PerformancePhase.GpuSubmit);
            var error = GpuPreviewNative.Error.Create();
            try
            {
                if (!SkiaGpuBridge.FlushSurface(context.Handle, surface.Handle, 6, uint.MaxValue))
                    throw new GpuPreviewException("GpuPreviewSubmitFailed", "Skia GPU source flush/submit did not complete.");
                if (!SkiaGpuBridge.GetImageInfo(surface.Handle, out GRVkImageInfo image) || image.Image == 0 ||
                    image.SampleCount != 1 || image.ImageLayout != 6 || image.CurrentQueueFamily != queueFamily ||
                    image.Format != 44 || image.ImageTiling != 0 || (image.ImageUsageFlags & 1) == 0)
                    throw new GpuPreviewException("GpuPreviewSourceStateInvalid", "Skia GPU source is not a single-sample BGRA transfer source on this graphics queue.");
                if (!SkiaGpuBridge.Submit(context.Handle, true))
                    throw new GpuPreviewException("GpuPreviewSubmitFailed", "Skia backend image query submission did not complete.");
                GpuPreviewNative.Source source = new()
                {
                    Size = (uint)Marshal.SizeOf<GpuPreviewNative.Source>(), Version = 1, Image = image.Image,
                    Width = checked((uint)Target.PixelWidth), Height = checked((uint)Target.PixelHeight), Format = image.Format,
                    Tiling = image.ImageTiling, Usage = image.ImageUsageFlags, SampleCount = image.SampleCount,
                    Layout = image.ImageLayout, QueueFamily = image.CurrentQueueFamily, Origin = 1,
                };
                GpuPreviewNative.Check(GpuPreviewNative.Blit(_pool, lease, source, ref error), error, "one GPU blit to shared slot");
                return new Lease(this, lease);
            }
            catch
            {
                CancelReservedCore(lease);
                throw;
            }
        }
    }

    internal void CancelReserved(GpuPreviewNative.Lease lease)
    {
        lock (_gate)
        {
            if (_pool == IntPtr.Zero) return;
            CancelReservedCore(lease);
        }
    }

    private void CancelReservedCore(GpuPreviewNative.Lease lease)
    {
        var cancelError = GpuPreviewNative.Error.Create();
        var cancelled = GpuPreviewNative.Cancel(_pool, lease, ref cancelError);
        if (cancelled != GpuPreviewNative.Status.Ok) QuarantineCore();
    }

    private GpuFrameExport Export(GpuPreviewNative.Lease lease)
    {
        lock (_gate)
        {
            EnsureOpen();
            var descriptor = GpuPreviewNative.Descriptor.Create();
            var error = GpuPreviewNative.Error.Create();
            GpuPreviewNative.Check(GpuPreviewNative.Export(_pool, lease, ref descriptor, out int fd, ref error), error, "export shared GPU slot");
            var owned = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            try
            {
                if (owned.IsInvalid || descriptor.Lease.Token != lease.Token || descriptor.PlaneCount != 1 ||
                    descriptor.Plane0.FdIndex != 0 || descriptor.Format != 44 || descriptor.Fourcc != 0x34325241 || descriptor.Origin != 0 ||
                    descriptor.Width != _info.Width || descriptor.Height != _info.Height || descriptor.Modifier != _info.Modifier ||
                    descriptor.AllocationBytes == 0 || descriptor.Plane0.Stride < descriptor.Width * 4ul ||
                    descriptor.Plane0.Offset >= descriptor.AllocationBytes || descriptor.Plane0.Size > descriptor.AllocationBytes - descriptor.Plane0.Offset ||
                    (descriptor.Height - 1ul) * descriptor.Plane0.Stride + descriptor.Width * 4ul > descriptor.Plane0.Size ||
                    descriptor.Layout != 1 || descriptor.QueueFamily != uint.MaxValue - 2 || descriptor.CpuPixelReadbacks != 0)
                    throw new GpuPreviewException("GpuPreviewDescriptorInvalid", "Native GPU export descriptor does not match the exact exported lease.");
                var identity = new GpuDeviceIdentity(_info.DeviceName, _info.VendorId, _info.DeviceId,
                    _info.DrmRenderMajor, _info.DrmRenderMinor, Convert.ToHexStringLower(_info.DeviceUuid));
                return new(new(lease.Token, checked((int)descriptor.Width), checked((int)descriptor.Height),
                    descriptor.Plane0.Stride, descriptor.Plane0.Offset, descriptor.AllocationBytes,
                    descriptor.QueriedSubresourceBytes, descriptor.Modifier, identity, descriptor.GpuBlits,
                    descriptor.CpuPixelReadbacks, descriptor.QueueIdleWaits), owned);
            }
            catch { owned.Dispose(); QuarantineCore(); throw; }
        }
    }

    private void QuarantineCore()
    {
        _failStream();
        if (_pool == IntPtr.Zero) return;
        var error = GpuPreviewNative.Error.Create();
        GpuPreviewNative.Check(GpuPreviewNative.Quarantine(_pool, ref error), error, "quarantine shared GPU pool");
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_pool == IntPtr.Zero, this);
    public void Dispose()
    {
        lock (_gate)
        {
            if (_pool == IntPtr.Zero) return;
            var error = GpuPreviewNative.Error.Create();
            var status = GpuPreviewNative.DestroyDrained(_pool, ref error);
            if (status == GpuPreviewNative.Status.Ok) { _pool = IntPtr.Zero; return; }
            _failStream();
            GpuPreviewNative.Check(GpuPreviewNative.AbandonLocal(_pool, out uint wasDrained, ref error), error, "abandon producer-local GPU pool");
            _pool = IntPtr.Zero;
            if (wasDrained == 0) throw new GpuPreviewException("GpuPreviewNotDrained", "Producer-local GPU objects were released after own-queue completion; consumer drain was not proved.");
        }
    }

    private sealed class Lease(VulkanGpuFramePool owner, GpuPreviewNative.Lease nativeLease) : GpuFrameLease(nativeLease.Token)
    {
        private bool _exported, _released, _disposed;
        public override GpuFrameExport ExportForDelivery()
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _released, this);
                if (_exported) throw new GpuPreviewException("GpuPreviewLeaseAlreadyExported", "A shared GPU lease may be exported only once.");
                // The native call transitions before exposing its FD. Any later failure
                // is uncertain delivery and must never cancel an exported allocation.
                _exported = true;
                try { return owner.Export(nativeLease); }
                catch { owner.QuarantineCore(); throw; }
            }
        }
        public override void ReleaseAfterConsumerCompletion(GpuFrameToken token)
        {
            ArgumentNullException.ThrowIfNull(token);
            lock (owner._gate)
            {
                owner.EnsureOpen();
                if (token != Token || !_exported || _released)
                    throw new GpuPreviewException("GpuPreviewStaleLease", "Consumer completion does not match an outstanding exported GPU lease.");
                var error = GpuPreviewNative.Error.Create();
                GpuPreviewNative.Check(GpuPreviewNative.ConsumerReleased(owner._pool, nativeLease, ref error), error, "release completed GPU consumer lease");
                _released = true;
            }
        }
        public override void Quarantine() { lock (owner._gate) owner.QuarantineCore(); }
        public override void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed || _released) return;
                _disposed = true;
                if (owner._pool == IntPtr.Zero) return;
                if (_exported) { owner.QuarantineCore(); return; }
                var error = GpuPreviewNative.Error.Create();
                GpuPreviewNative.Check(GpuPreviewNative.Cancel(owner._pool, nativeLease, ref error), error, "cancel unpublished shared GPU lease");
            }
        }
    }
}

internal static class SkiaGpuBridge
{
    [DllImport("libSkiaSharp", EntryPoint = "gr_mako_direct_context_flush_surface_vk_state", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool FlushSurface(IntPtr context, IntPtr surface, uint layout, uint family);
    [DllImport("libSkiaSharp", EntryPoint = "sk_mako_surface_get_vk_image_info", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool GetImageInfo(IntPtr surface, out GRVkImageInfo info);
    [DllImport("libSkiaSharp", EntryPoint = "gr_direct_context_submit", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Submit(IntPtr context, [MarshalAs(UnmanagedType.I1)] bool syncCpu);
}
