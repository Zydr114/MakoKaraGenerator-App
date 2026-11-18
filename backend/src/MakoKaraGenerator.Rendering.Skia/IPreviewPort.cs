using System.Runtime.InteropServices;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>双阶段连续预览输出端口；公共契约完全独立于底层操作系统内核机制。</summary>
internal interface IPreviewPort : IDisposable
{
    PreviewTransportKind TransportKind { get; }
    string BackendApi { get; }
    PreviewPoolInfo PoolInfo { get; }
    bool HasFreeSlot { get; }
    bool IsDrained { get; }
    IPreviewFrameTarget BeginFrame(ISkiaRenderDevice device, SKImageInfo surfaceInfo, RenderTarget renderTarget);
}

/// <summary>单帧绘制目标作用域；未调用 Commit 而释放时自动取消并将槽位归还为 Free。</summary>
internal interface IPreviewFrameTarget : IDisposable
{
    SKSurface Surface { get; }
    ulong FrameSequence { get; }
    PreviewFrameLease Commit();
}

/// <summary>平台无关的共享内存后备存储接口；将 OS 共享内存原语与 ShmPreviewPort 状态机彻底解耦。</summary>
internal interface IShmPoolBacking : IDisposable
{
    string ResourceName { get; }
    IntPtr BasePointer { get; }
    ulong TotalBytes { get; }
}

/// <summary>供跨平台单元测试使用的非托管进程内对齐内存后备实现。</summary>
internal sealed unsafe class UnmanagedMemoryShmBacking : IShmPoolBacking
{
    private IntPtr _buffer;

    public UnmanagedMemoryShmBacking(ulong totalBytes, string resourceName = "memory://mkg-shm-test")
    {
        if (totalBytes is 0 or > 64UL * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        void* allocated = NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        NativeMemory.Clear(allocated, (nuint)totalBytes);
        _buffer = (IntPtr)allocated;
        ResourceName = resourceName;
        TotalBytes = totalBytes;
    }

    public string ResourceName { get; }
    public IntPtr BasePointer
    {
        get
        {
            IntPtr current = Volatile.Read(ref _buffer);
            return current == IntPtr.Zero ? throw new ObjectDisposedException(nameof(UnmanagedMemoryShmBacking)) : current;
        }
    }
    public ulong TotalBytes { get; }

    public void Dispose()
    {
        IntPtr current = Interlocked.Exchange(ref _buffer, IntPtr.Zero);
        if (current != IntPtr.Zero)
        {
            NativeMemory.AlignedFree((void*)current);
        }
    }
}

/// <summary>按当前运行平台创建共享内存后备存储（方式 B：Windows 分支显式占位）。</summary>
internal static class ShmPoolBackingFactory
{
    public static IShmPoolBacking Create(ulong totalBytes, string? token = null)
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxPosixShmBacking.Create(totalBytes, token);
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows SHM pool backing (CreateFileMappingW Local\\mkg-shm-*) is deferred under Method B.");
        }

        throw new PlatformNotSupportedException(
            $"SHM preview pool backing is not supported on platform '{RuntimeInformation.OSDescription}'.");
    }

    public static void TryCleanupUnopened(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        if (OperatingSystem.IsLinux())
        {
            LinuxPosixShmBacking.TryCleanupUnopened(token);
        }
    }
}

/// <summary>解析连续预览传输模式，严格遵守显式语义与零静默降级原则。</summary>
internal static class PreviewTransportSelector
{
    public const string EnvironmentVariableName = "MAKO_PREVIEW_TRANSPORT";

    public static PreviewTransportKind? ReadEnvironmentOverride(string? rawValue = null)
    {
        string? configured = rawValue ?? Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        return PreviewWireConventions.ParseTransport(configured.Trim());
    }

    public static PreviewTransportKind Resolve(string backendApi, PreviewTransportKind? explicitTransport = null, string? envOverride = null)
    {
        string normalizedApi = PreviewWireConventions.NormalizeBackendApi(backendApi);
        PreviewTransportKind? requested = explicitTransport ?? ReadEnvironmentOverride(envOverride);

        if (requested is { } chosen)
        {
            ValidateSupported(normalizedApi, chosen);
            return chosen;
        }

        // CPU 光栅设备无 GPU DMA-BUF 纹理，未显式指定传输时固定走跨平台 SHM。
        if (normalizedApi == PreviewWireConventions.ApiCpu)
        {
            return PreviewTransportKind.Shm;
        }

        // GPU 设备：Linux 默认保留零拷贝 DMA-BUF，Windows 及其他平台默认使用 SHM。
        return OperatingSystem.IsLinux() ? PreviewTransportKind.DmaBuf : PreviewTransportKind.Shm;
    }

    public static void ValidateSupported(string normalizedBackendApi, PreviewTransportKind transport)
    {
        if (transport == PreviewTransportKind.DmaBuf)
        {
            if (normalizedBackendApi != PreviewWireConventions.ApiVulkan)
            {
                throw new GpuPreviewException(
                    "PreviewTransportUnsupported",
                    $"DMA-BUF 零拷贝传输仅支持 vulkan 渲染设备，不支持 '{normalizedBackendApi}'；已拒绝静默降级。");
            }

            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw new GpuPreviewException(
                    "GpuPreviewUnsupportedPlatform",
                    "DMA-BUF shared preview requires Linux x64; silent fallback to SHM is prohibited.");
            }
        }
    }

    public static SKColorType ToSkColorType(PreviewPixelFormat format) => format switch
    {
        PreviewPixelFormat.Bgra8888 => SKColorType.Bgra8888,
        PreviewPixelFormat.Rgba8888 => SKColorType.Rgba8888,
        _ => throw new GpuPreviewException("PreviewUnsupportedPixelFormat", $"不支持的像素格式：{format}。"),
    };

    public static PreviewPixelFormat FromSkColorType(SKColorType colorType) => colorType switch
    {
        SKColorType.Bgra8888 => PreviewPixelFormat.Bgra8888,
        SKColorType.Rgba8888 => PreviewPixelFormat.Rgba8888,
        _ => throw new GpuPreviewException(
            "PreviewUnsupportedPixelFormat",
            $"SHM 预览端口仅支持 Bgra8888 或 Rgba8888 表面，收到 '{colorType}'。"),
    };
}
