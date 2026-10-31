using System.Runtime.InteropServices;

namespace MakoKaraGenerator.Rendering.Skia;

internal static class GpuPreviewNative
{
    internal const string Library = "mako_gpu_preview";
    internal const uint Version = 1;
    internal const uint RequiredExtensions = 31;
    internal enum Status { Ok, WouldBlock, NoFreeSlot, Unsupported, InvalidArgument, StaleLease, Quarantined, NotDrained, DeviceLost, IoError, OutOfMemory, InternalError }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct Error
    {
        internal uint Size, Version;
        internal int Status, VkResult;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Message;
        internal static Error Create() => new() { Size = (uint)Marshal.SizeOf<Error>(), Version = 1, Message = string.Empty };
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Context
    {
        internal uint Size, Version;
        internal ulong Instance, PhysicalDevice, Device, Queue;
        internal uint QueueFamily, EnabledExtensions;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PoolConfig
    {
        internal uint Size, Version, Width, Height, SlotCount, Reserved;
        internal ulong ProducerEpoch, PoolGeneration, MaxAllocationBytes;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct PoolInfo
    {
        internal uint Size, Version, Width, Height, SlotCount, Format;
        internal ulong AllocationBytes, Modifier;
        internal uint VendorId, DeviceId, DrmRenderMajor, DrmRenderMinor;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] DeviceUuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string DeviceName;
        internal static PoolInfo Create() => new() { Size = (uint)Marshal.SizeOf<PoolInfo>(), Version = 1, DeviceUuid = new byte[16], DeviceName = string.Empty };
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Lease
    {
        internal uint Size, Version;
        internal ulong ProducerEpoch, PoolGeneration, FrameSequence, SlotEpoch;
        internal uint SlotId, Reserved;
        internal static Lease Create() => new() { Size = (uint)Marshal.SizeOf<Lease>(), Version = 1 };
        internal readonly GpuFrameToken Token => new(ProducerEpoch, PoolGeneration, FrameSequence, SlotEpoch, SlotId);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Source
    {
        internal uint Size, Version;
        internal ulong Image;
        internal uint Width, Height, Format, Tiling, Usage, SampleCount, Layout, QueueFamily, Origin, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Plane { internal uint FdIndex, Stride; internal ulong Offset, Size; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Descriptor
    {
        internal uint Size, Version;
        internal Lease Lease;
        internal uint Width, Height, Format, Fourcc, PlaneCount, Origin, Layout, QueueFamily;
        internal ulong Modifier, AllocationBytes, QueriedSubresourceBytes, GpuBlits, CpuPixelReadbacks, QueueIdleWaits;
        internal Plane Plane0, Plane1, Plane2, Plane3;
        internal static Descriptor Create() => new() { Size = (uint)Marshal.SizeOf<Descriptor>(), Version = 1 };
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PoolState
    {
        internal uint Size, Version, FreeSlots, ReservedSlots, ReadySlots, ExportedSlots, Quarantined, Reserved;
        internal ulong AllocationBytes, GpuBlits, CpuPixelReadbacks, QueueIdleWaits;
        internal static PoolState Create() => new() { Size = (uint)Marshal.SizeOf<PoolState>(), Version = 1 };
    }
    internal static void Check(Status status, Error error, string operation)
    {
        if (status != Status.Ok) throw new GpuPreviewException("GpuPreview" + status, $"{operation}: {error.Message} (Vulkan {error.VkResult})");
    }
    [DllImport(Library, EntryPoint = "mkg_gpu_abi_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint AbiVersion();
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_create_borrowed", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status Create(in Context context, in PoolConfig config, out IntPtr pool, ref PoolInfo info, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_try_acquire", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status Acquire(IntPtr pool, ulong sequence, ref Lease lease, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_blit", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status Blit(IntPtr pool, in Lease lease, in Source source, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_export", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status Export(IntPtr pool, in Lease lease, ref Descriptor descriptor, out int fd, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_cancel_unexported", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status Cancel(IntPtr pool, in Lease lease, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_consumer_released", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status ConsumerReleased(IntPtr pool, in Lease lease, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_quarantine", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status Quarantine(IntPtr pool, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_read_state", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status ReadState(IntPtr pool, ref PoolState state, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_destroy_drained", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status DestroyDrained(IntPtr pool, ref Error error);
    [DllImport(Library, EntryPoint = "mkg_gpu_pool_abandon_local", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status AbandonLocal(IntPtr pool, out uint wasDrained, ref Error error);
}
