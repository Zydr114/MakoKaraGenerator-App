using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ShmPreviewPortTests
{
    [Fact]
    public void 传输选择器严格拒绝非法值与Cpu零拷贝静默降级()
    {
        Assert.Equal(PreviewTransportKind.Shm, PreviewTransportSelector.Resolve("cpu", explicitTransport: null, envOverride: ""));
        Assert.Equal(PreviewTransportKind.Shm, PreviewTransportSelector.Resolve("vulkan", PreviewTransportKind.Shm, envOverride: ""));
        Assert.Equal(PreviewTransportKind.Shm, PreviewTransportSelector.Resolve("vulkan", explicitTransport: null, envOverride: "shm"));

        var invalid = Assert.Throws<GpuPreviewException>(() =>
            PreviewTransportSelector.Resolve("vulkan", explicitTransport: null, envOverride: "auto"));
        Assert.Equal("PreviewTransportInvalid", invalid.Code);

        var cpuDmaBuf = Assert.Throws<GpuPreviewException>(() =>
            PreviewTransportSelector.Resolve("cpu", PreviewTransportKind.DmaBuf));
        Assert.Equal("PreviewTransportUnsupported", cpuDmaBuf.Code);
    }

    [Fact]
    public void ShmCommit仅调用单次ReadPixels且不前置调用FlushAndSubmit()
    {
        using var inner = new CpuSkiaRenderDevice();
        using var spy = new SpyRenderDevice(inner);
        var target = new RenderTarget { PixelWidth = 64, PixelHeight = 64 };
        ulong seq = 0;
        using var backing = new UnmanagedMemoryShmBacking(64 * 64 * 4 * 3);
        using var port = new ShmPreviewPort(
            spy.SynchronizationRoot,
            spy.PreviewIdentity,
            target,
            PreviewPixelFormat.Bgra8888,
            requestedStrideBytes: 0,
            producerEpoch: 1,
            poolGeneration: 1,
            maximumAllocationBytes: 1024 * 1024,
            shmToken: null,
            failStream: () => { },
            nextSequence: () => ++seq,
            customBacking: backing);

        var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (IPreviewFrameTarget frameTarget = port.BeginFrame(spy, info, target))
        {
            frameTarget.Surface.Canvas.Clear(new SKColor(200, 100, 50, 255));
            using PreviewFrameLease lease = frameTarget.Commit();
            Assert.Equal(0, spy.FlushAndSubmitCalls);
            Assert.Equal(1, spy.SurfaceReadPixelsCalls);

            using PreviewFrameDelivery delivery = lease.ExportForDelivery();
            Assert.Null(delivery.AuxiliaryHandle);
            Assert.Equal(1UL, delivery.Descriptor.Counters.CpuPixelReadbacks);
            Assert.Equal(0UL, delivery.Descriptor.Counters.GpuBlits);
            lease.ReleaseAfterConsumerCompletion(lease.Token);
        }

        Assert.True(port.IsDrained);
    }

    [Fact]
    public void 未提交异常释放时自动取消槽位并保持池可排空()
    {
        using var device = new CpuSkiaRenderDevice();
        var target = new RenderTarget { PixelWidth = 64, PixelHeight = 64 };
        ulong seq = 0;
        bool streamFailed = false;
        using var backing = new UnmanagedMemoryShmBacking(64 * 64 * 4 * 3);
        using var port = new ShmPreviewPort(
            device.SynchronizationRoot,
            device.PreviewIdentity,
            target,
            PreviewPixelFormat.Bgra8888,
            requestedStrideBytes: 0,
            producerEpoch: 7,
            poolGeneration: 1,
            maximumAllocationBytes: 1024 * 1024,
            shmToken: null,
            failStream: () => streamFailed = true,
            nextSequence: () => ++seq,
            customBacking: backing);

        var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (IPreviewFrameTarget uncommitted = port.BeginFrame(device, info, target))
        {
            Assert.Equal(1UL, uncommitted.FrameSequence);
            // 模拟绘制中途抛出异常：不调用 Commit()，直接退出 using 作用域。
        }

        Assert.False(streamFailed);
        Assert.True(port.IsDrained);
        Assert.True(port.HasFreeSlot);

        // 三槽背压：连续占用 3 个槽位后第 4 次请求返回 GpuPreviewNoFreeSlot，归还后恢复。
        var leases = new List<PreviewFrameLease>();
        for (int i = 0; i < 3; i++)
        {
            using IPreviewFrameTarget t = port.BeginFrame(device, info, target);
            leases.Add(t.Commit());
        }

        Assert.False(port.HasFreeSlot);
        var full = Assert.Throws<GpuPreviewException>(() => port.BeginFrame(device, info, target));
        Assert.Equal("GpuPreviewNoFreeSlot", full.Code);

        foreach (PreviewFrameLease lease in leases)
        {
            using PreviewFrameDelivery d = lease.ExportForDelivery();
            lease.ReleaseAfterConsumerCompletion(lease.Token);
            lease.Dispose();
        }

        Assert.True(port.IsDrained);
    }

    [Theory]
    [InlineData(PreviewPixelFormat.Bgra8888, 0u)]
    [InlineData(PreviewPixelFormat.Rgba8888, 320u)]
    public void Cpu合成图案在双像素格式与非紧凑步长下字节精确(PreviewPixelFormat format, uint customStride)
    {
        using var device = new CpuSkiaRenderDevice();
        using var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
        var target = new RenderTarget { PixelWidth = 64, PixelHeight = 64 };
        ulong seq = 0;
        uint expectedStride = customStride == 0 ? 256u : customStride;
        using var backing = new UnmanagedMemoryShmBacking((ulong)expectedStride * 64UL * 3UL);
        using var port = new ShmPreviewPort(
            device.SynchronizationRoot,
            device.PreviewIdentity,
            target,
            format,
            requestedStrideBytes: customStride,
            producerEpoch: 1,
            poolGeneration: 1,
            maximumAllocationBytes: 2 * 1024 * 1024,
            shmToken: null,
            failStream: () => { },
            nextSequence: () => ++seq,
            customBacking: backing);

        using PreviewRenderFrame frame = renderer.RenderSyntheticToPreview(target, 1000, port, format);
        Assert.True(frame.Timings.RenderDurationUs >= 0);
        Assert.True(frame.Timings.PortCaptureDurationUs >= 0);

        using PreviewFrameDelivery delivery = frame.Lease.ExportForDelivery();
        Assert.Equal(format, delivery.Descriptor.PixelFormat);
        Assert.Equal(expectedStride, delivery.Descriptor.StrideBytes);

        byte[] raw = new byte[delivery.Descriptor.SlotSizeBytes];
        Marshal.Copy(
            checked((IntPtr)((long)backing.BasePointer + (long)delivery.Descriptor.SlotOffsetBytes)),
            raw,
            0,
            raw.Length);

        (byte R, byte G, byte B, byte A) Sample(int px, int py)
        {
            int idx = py * (int)expectedStride + px * 4;
            return format == PreviewPixelFormat.Bgra8888
                ? (raw[idx + 2], raw[idx + 1], raw[idx], raw[idx + 3])
                : (raw[idx], raw[idx + 1], raw[idx + 2], raw[idx + 3]);
        }

        // 上下左右严格不对称四角纯色（Top-Down 行序）
        Assert.Equal((255, 0, 0, 255), Sample(4, 10));       // 左上红
        Assert.Equal((0, 255, 0, 255), Sample(60, 10));      // 右上绿
        Assert.Equal((0, 0, 255, 255), Sample(4, 60));       // 左下蓝
        Assert.Equal((255, 255, 255, 255), Sample(60, 60));  // 右下白

        // 预乘半透明紫 [128, 0, 128, 128] 与全透明 [0, 0, 0, 0]
        var premul = Sample(24, 50);
        Assert.InRange(Math.Abs(premul.R - 128), 0, 1);
        Assert.Equal(0, premul.G);
        Assert.InRange(Math.Abs(premul.B - 128), 0, 1);
        Assert.Equal(128, premul.A);
        Assert.Equal((0, 0, 0, 0), Sample(40, 50));

        frame.Lease.ReleaseAfterConsumerCompletion(frame.Lease.Token);
    }

    [Fact]
    public void LinuxTmpfs后备创建0600文件且可幂等兜底清理()
    {
        if (!OperatingSystem.IsLinux()) return;

        string token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        string resourcePath;
        using (LinuxPosixShmBacking backing = LinuxPosixShmBacking.Create(12288, token))
        {
            resourcePath = backing.ResourceName;
            Assert.True(File.Exists(resourcePath));
            UnixFileMode mode = File.GetUnixFileMode(resourcePath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            Assert.Equal(12288L, new FileInfo(resourcePath).Length);
        }

        Assert.False(File.Exists(resourcePath));
        // 再次调用未打开清理兜底须安全幂等。
        LinuxPosixShmBacking.TryCleanupUnopened(token);
    }

    [Fact]
    public void Linux未打开清理兜底仅删除当前用户0600普通文件并拒绝非0600或符号链接()
    {
        if (!OperatingSystem.IsLinux()) return;

        string ownToken = "aa11223344556677889900aabbccddeeff";
        string ownPath = Path.Combine("/dev/shm", "mkg-shm-" + ownToken);
        string looseToken = "bb11223344556677889900aabbccddeeff";
        string loosePath = Path.Combine("/dev/shm", "mkg-shm-" + looseToken);
        string linkToken = "cc11223344556677889900aabbccddeeff";
        string linkPath = Path.Combine("/dev/shm", "mkg-shm-" + linkToken);
        try
        {
            File.WriteAllBytes(ownPath, [1, 2, 3, 4]);
            File.SetUnixFileMode(ownPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            LinuxPosixShmBacking.TryCleanupUnopened(ownToken);
            Assert.False(File.Exists(ownPath));

            File.WriteAllBytes(loosePath, [1, 2, 3, 4]);
            File.SetUnixFileMode(loosePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            LinuxPosixShmBacking.TryCleanupUnopened(looseToken);
            Assert.True(File.Exists(loosePath));

            File.CreateSymbolicLink(linkPath, loosePath);
            LinuxPosixShmBacking.TryCleanupUnopened(linkToken);
            Assert.True(File.Exists(linkPath));
        }
        finally
        {
            if (File.Exists(linkPath)) File.Delete(linkPath);
            if (File.Exists(loosePath)) File.Delete(loosePath);
            if (File.Exists(ownPath)) File.Delete(ownPath);
        }
    }

    [Fact]
    public void WindowsLocalShm后备创建独占命名段并严格配对释放()
    {
        if (!OperatingSystem.IsWindows()) return;

        string token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        string expectedName = "Local\\mkg-shm-" + token;

        using (WindowsShmBacking backing = WindowsShmBacking.Create(12288, token))
        {
            Assert.Equal(expectedName, backing.ResourceName);
            Assert.Equal(12288UL, backing.TotalBytes);

            // 禁止复用已存在的同名共享内存对象
            GpuPreviewException dup = Assert.Throws<GpuPreviewException>(() => ShmPoolBackingFactory.Create(12288, token));
            Assert.Equal("PreviewShmAlreadyExists", dup.Code);

            // 写入测试载荷并通过独立只读映射核对跨视图可见性
            byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44];
            Marshal.Copy(payload, 0, backing.BasePointer, payload.Length);
            Thread.MemoryBarrier();

            IntPtr roSection = Win32TestOpenFileMappingW(0x0004u, 0, expectedName);
            Assert.NotEqual(IntPtr.Zero, roSection);
            try
            {
                IntPtr roView = Win32TestMapViewOfFile(roSection, 0x0004u, 0, 0, 12288);
                Assert.NotEqual(IntPtr.Zero, roView);
                try
                {
                    byte[] observed = new byte[payload.Length];
                    Marshal.Copy(roView, observed, 0, observed.Length);
                    Assert.Equal(payload, observed);
                }
                finally
                {
                    Assert.NotEqual(0, Win32TestUnmapViewOfFile(roView));
                }
            }
            finally
            {
                Assert.NotEqual(0, Win32TestCloseHandle(roSection));
            }
        }

        // Dispose 后 section 句柄与视图均已释放，内核应立即回收命名段
        IntPtr afterDispose = Win32TestOpenFileMappingW(0x0004u, 0, expectedName);
        if (afterDispose != IntPtr.Zero)
        {
            _ = Win32TestCloseHandle(afterDispose);
        }

        Assert.Equal(IntPtr.Zero, afterDispose);
    }

    [Fact]
    [Trait("Category", "GpuSmoke")]
    public void VulkanShm模式免除Drm导出强耦合且可绘制合成图案()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        // GPU smoke is opt-in because VulkanSkiaRenderDevice explicitly rejects software Vulkan (llvmpipe)
        // and normal rendering tests must remain runnable on GPU-less CI runners.
        if (!string.Equals(Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE"), "1", StringComparison.Ordinal)) return;

        using var vulkan = VulkanSkiaRenderDevice.Create(enableGpuPreview: true, previewTransport: PreviewTransportKind.Shm);
        Assert.False(vulkan.SupportsDmaBufExport);
        Assert.True(vulkan.SupportsGpuFrames);

        using var manager = new PreviewPortSessionManager(vulkan);
        var target = new RenderTarget { PixelWidth = 128, PixelHeight = 128 };
        PreviewPoolInfo pool = manager.InitializeOutput(
            target,
            maximumAllocationBytes: 16 * 1024 * 1024,
            new PreviewOutputOptions(PreviewTransportKind.Shm, PreviewPixelFormat.Rgba8888));
        Assert.Equal(PreviewTransportKind.Shm, pool.TransportKind);
        Assert.Equal("vulkan", pool.BackendApi);
        Assert.Equal(PreviewPixelFormat.Rgba8888, pool.PixelFormat);
        Assert.NotNull(pool.ShmResourceName);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("Local\\mkg-shm-", pool.ShmResourceName, StringComparison.Ordinal);
        }

        using var renderer = new SkiaRenderPipeline(null, null, null, null, false, vulkan, false);
        IPreviewPort port = manager.RequireCurrentPort(target);
        using PreviewRenderFrame frame = renderer.RenderSyntheticToPreview(target, 0, port);
        using PreviewFrameDelivery delivery = frame.Lease.ExportForDelivery();
        Assert.Equal(1UL, delivery.Descriptor.Counters.CpuPixelReadbacks);
        frame.Lease.ReleaseAfterConsumerCompletion(frame.Lease.Token);
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr Win32TestOpenFileMappingW(uint dwDesiredAccess, int bInheritHandle, string lpName);

    [DllImport("kernel32.dll", EntryPoint = "MapViewOfFile", SetLastError = true)]
    private static extern IntPtr Win32TestMapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", EntryPoint = "UnmapViewOfFile", SetLastError = true)]
    private static extern int Win32TestUnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern int Win32TestCloseHandle(IntPtr hObject);

    private sealed class SpyRenderDevice(ISkiaRenderDevice inner) : ISkiaRenderDevice
    {
        public int FlushAndSubmitCalls { get; private set; }
        public int SurfaceReadPixelsCalls { get; private set; }
        public object SynchronizationRoot => inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => inner.Info;
        public PreviewDeviceIdentity PreviewIdentity => inner.PreviewIdentity;
        public SKSurface CreateSurface(SKImageInfo imageInfo) => inner.CreateSurface(imageInfo);
        public void FlushAndSubmit(SKSurface surface)
        {
            FlushAndSubmitCalls++;
            inner.FlushAndSubmit(surface);
        }

        public SKImage? RasterizeImage(SKImage image) => inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
        {
            SurfaceReadPixelsCalls++;
            return inner.ReadPixels(surface, imageInfo, destination, rowBytes);
        }

        public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes) =>
            inner.ReadPixels(image, imageInfo, destination, rowBytes);

        public void Dispose() => inner.Dispose();
    }
}
