using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpVk;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>Skia 后端的显式设备选择。默认值保持既有 CPU 光栅行为。</summary>
internal enum SkiaRenderBackend
{
    Cpu,
    Vulkan,
}

internal sealed record SkiaRenderBackendOptions(
    SkiaRenderBackend Backend = SkiaRenderBackend.Cpu,
    bool EnableGpuPreview = false,
    PreviewTransportKind? PreviewTransport = null)
{
    public static SkiaRenderBackendOptions FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable("MAKO_RENDER_BACKEND"));

    internal static SkiaRenderBackendOptions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("cpu", StringComparison.OrdinalIgnoreCase))
            return new(SkiaRenderBackend.Cpu);
        if (value.Equals("vulkan", StringComparison.OrdinalIgnoreCase))
            return new(SkiaRenderBackend.Vulkan);
        throw new InvalidOperationException($"未知渲染后端 '{value}'；允许值为 cpu 或 vulkan。");
    }
}

internal sealed record SkiaRenderDeviceInfo(string Backend, string? Vendor, string? Device, string? Driver, string? ApiVersion)
{
    public override string ToString() => string.Join(", ", new[]
    {
        $"backend={Backend}", Vendor is null ? null : $"vendor={Vendor}", Device is null ? null : $"device={Device}",
        Driver is null ? null : $"driver={Driver}", ApiVersion is null ? null : $"api={ApiVersion}",
    }.Where(value => value is not null));
}

/// <summary>共享给主画布和所有图中间表面的 Skia 设备。</summary>
internal interface ISkiaRenderDevice : IDisposable
{
    object SynchronizationRoot { get; }
    SkiaRenderDeviceInfo Info { get; }
    PreviewDeviceIdentity PreviewIdentity => new(Info.Backend, Info.Device ?? Info.Backend);
    SKSurface CreateSurface(SKImageInfo imageInfo);
    void FlushAndSubmit(SKSurface surface);
    /// <summary>提交本表面已记录的绘制，不等待 GPU 完成。同设备后续绘制按提交顺序执行，读回函数各自先同步再读。</summary>
    /// <remarks>未覆盖的实现（如测试包装）保守地退回 <see cref="FlushAndSubmit"/>。</remarks>
    void SubmitWithoutWait(SKSurface surface) => FlushAndSubmit(surface);
    SKImage? RasterizeImage(SKImage image);
    SKImage? CreateTextureImage(SKImage image, bool mipmapped)
        => throw new NotSupportedException("当前设备不支持真实纹理及 mip 上传。");
    bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes);
    bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes);
    // Caller must own the snapshot and know that this device created it.
    bool ReadOwnedImagePixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
        => ReadPixels(image, imageInfo, destination, rowBytes);
}

internal static class SkiaReadbackTelemetry
{
    internal static void Record(bool image, SKImageInfo info)
    {
        if (!PerformanceTelemetry.HasFrame) return;
        long pixels = (long)Math.Max(0, info.Width) * Math.Max(0, info.Height);
        int bytesPerPixel = Math.Max(0, info.BytesPerPixel);
        long bytes = bytesPerPixel == 0 ? 0 : pixels > long.MaxValue / bytesPerPixel ? long.MaxValue : pixels * bytesPerPixel;
        PerformanceTelemetry.RecordReadback(image, bytes);
    }
}

internal static class SkiaRenderDeviceFactory
{
    public static ISkiaRenderDevice Create(SkiaRenderBackendOptions? options = null)
    {
        options ??= SkiaRenderBackendOptions.FromEnvironment();
        return options.Backend switch
        {
            SkiaRenderBackend.Cpu => new CpuSkiaRenderDevice(),
            SkiaRenderBackend.Vulkan => VulkanSkiaRenderDevice.Create(options.EnableGpuPreview, options.PreviewTransport),
            _ => throw new InvalidOperationException($"不支持的 Skia 渲染后端：{options.Backend}。"),
        };
    }
}

internal sealed class CpuSkiaRenderDevice : ISkiaRenderDevice
{
    private bool _disposed;
    public object SynchronizationRoot { get; } = new();

    public SkiaRenderDeviceInfo Info { get; } = new("cpu", null, "Skia raster", null, null);
    public PreviewDeviceIdentity PreviewIdentity { get; } = new(PreviewWireConventions.ApiCpu, "Skia raster");

    public SKSurface CreateSurface(SKImageInfo imageInfo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SKSurface.Create(imageInfo) ?? throw new InvalidOperationException("无法创建 CPU Skia 表面。");
    }

    public void FlushAndSubmit(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        surface.Flush();
    }

    public SKImage? RasterizeImage(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return image.ToRasterImage(true);
    }

    public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);
        SkiaReadbackTelemetry.Record(false, imageInfo);
        return surface.ReadPixels(imageInfo, destination, rowBytes, 0, 0);
    }

    public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);
        SkiaReadbackTelemetry.Record(true, imageInfo);
        return image.ReadPixels(imageInfo, destination, rowBytes, 0, 0);
    }

    public void Dispose() => _disposed = true;
}

/// <summary>Skia Ganesh Vulkan device backed by one SharpVk instance/device/queue.</summary>
internal sealed class VulkanSkiaRenderDevice : ISkiaRenderDevice
{
    private static int _deviceInitializations, _contextInitializations;
    private readonly string _initializationId = Guid.NewGuid().ToString("N");
    internal string InitializationId => _initializationId;
    internal static int DeviceInitializations => Volatile.Read(ref _deviceInitializations);
    internal static int ContextInitializations => Volatile.Read(ref _contextInitializations);
    private readonly Instance _instance;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly GRVkExtensions _extensions;
    private readonly GRSharpVkBackendContext _backendContext;
    private readonly GRContext _context;
    private readonly IntPtr _vulkanLibrary;
    private readonly GRVkGetProcedureAddressDelegate _nativeProcedure;
    private readonly GRSharpVkGetProcedureAddressDelegate _sharpProcedure;
    private bool _disposed;
    private readonly List<VulkanGpuFramePool> _gpuPools = [];
    private VulkanGpuFramePool? _currentGpuPool;
    private readonly bool _gpuPreviewEnabled;
    private readonly bool _dmaBufExportEnabled;
    private bool _gpuPreviewFailed;
    private ulong _gpuPoolGeneration, _gpuSequence;
    private readonly ulong _gpuProducerEpoch = CreateNonZeroRandomEpoch();
    private readonly uint _queueFamily;
    private readonly uint _vendorId;
    private readonly uint _deviceId;
    public object SynchronizationRoot { get; } = new();
    internal bool SupportsGpuFrames => _gpuPreviewEnabled && !_gpuPreviewFailed && !_disposed;
    internal bool SupportsDmaBufExport => _dmaBufExportEnabled && !_gpuPreviewFailed && !_disposed;
    internal GRContext GrContext => _context;
    internal uint QueueFamilyIndex => _queueFamily;
    public PreviewDeviceIdentity PreviewIdentity => new(PreviewWireConventions.ApiVulkan, Info.Device ?? "Vulkan GPU", _vendorId, _deviceId);
    // Caller must hold SynchronizationRoot through drawing and CopyGpuFrame.
    internal ulong NextGpuFrameSequence => checked(_gpuSequence + 1);

    private static ulong CreateNonZeroRandomEpoch()
    {
        ulong value = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        return value == 0 ? 1UL : value;
    }

    private VulkanSkiaRenderDevice(Instance instance, PhysicalDevice physicalDevice, Device device, Queue queue,
        GRVkExtensions extensions, GRSharpVkBackendContext backendContext, GRContext context, IntPtr vulkanLibrary,
        GRVkGetProcedureAddressDelegate nativeProcedure, GRSharpVkGetProcedureAddressDelegate sharpProcedure,
        SkiaRenderDeviceInfo info, uint queueFamily, uint vendorId, uint deviceId, bool gpuPreviewEnabled, bool dmaBufExportEnabled)
    {
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;
        _queue = queue;
        _extensions = extensions;
        _backendContext = backendContext;
        _context = context;
        _vulkanLibrary = vulkanLibrary;
        _nativeProcedure = nativeProcedure;
        _sharpProcedure = sharpProcedure;
        Info = info;
        _queueFamily = queueFamily;
        _vendorId = vendorId;
        _deviceId = deviceId;
        _gpuPreviewEnabled = gpuPreviewEnabled;
        _dmaBufExportEnabled = dmaBufExportEnabled;
    }

    public SkiaRenderDeviceInfo Info { get; }

    public static VulkanSkiaRenderDevice Create(bool enableGpuPreview = false, PreviewTransportKind? previewTransport = null)
    {
        SharpVkNativeLoader.EnsureInstalled();
        bool enableDmaBufExport = enableGpuPreview &&
            PreviewTransportSelector.Resolve(PreviewWireConventions.ApiVulkan, previewTransport) == PreviewTransportKind.DmaBuf;
        Instance? instance = null;
        Device? device = null;
        GRVkExtensions? extensions = null;
        GRSharpVkBackendContext? backendContext = null;
        GRContext? context = null;
        IntPtr vulkanLibrary = IntPtr.Zero;
        try
        {
            instance = Instance.Create(null, null, null,
                new ApplicationInfo { ApplicationName = "MakoKaraGenerator", ApplicationVersion = new SharpVk.Version(1, 0, 0),
                    EngineName = "MakoKaraGenerator", EngineVersion = new SharpVk.Version(1, 0, 0), ApiVersion = new SharpVk.Version(1, enableDmaBufExport ? 2 : 0, 0) },
                null, null, null);
            PhysicalDevice[] physicalDevices = instance.EnumeratePhysicalDevices();
            var candidates = physicalDevices.Select(value =>
            {
                PhysicalDeviceProperties properties = value.GetProperties();
                int graphicsQueueFamily = Array.FindIndex(value.GetQueueFamilyProperties(),
                    family => family.QueueCount > 0 && family.QueueFlags.HasFlag(QueueFlags.Graphics));
                return new DeviceCandidate(properties.DeviceName, properties.DeviceType, graphicsQueueFamily);
            }).ToArray();
            int selectedIndex = SelectGpuIndex(candidates);
            PhysicalDevice physical = physicalDevices[selectedIndex];
            int family = candidates[selectedIndex].GraphicsQueueFamily;
            string[] deviceExtensions = [];
            if (enableDmaBufExport)
            {
                if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new GpuPreviewException("GpuPreviewUnsupportedPlatform", "GPU shared DMA-BUF preview requires Linux x64.");
                if ((uint)physical.GetProperties().ApiVersion < 0x00402000)
                    throw new GpuPreviewException("GpuPreviewUnsupportedVulkanVersion", "GPU shared DMA-BUF preview requires Vulkan 1.2.");
                deviceExtensions = ["VK_KHR_external_memory_fd", "VK_EXT_external_memory_dma_buf", "VK_EXT_image_drm_format_modifier", "VK_EXT_queue_family_foreign", "VK_EXT_physical_device_drm"];
                var available = physical.EnumerateDeviceExtensionProperties(null).Select(value => value.ExtensionName).ToHashSet(StringComparer.Ordinal);
                if (deviceExtensions.Any(name => !available.Contains(name)))
                    throw new GpuPreviewException("GpuPreviewMissingExtensions", "Vulkan shared preview extensions are unavailable on the selected physical device.");
                if (GpuPreviewNative.AbiVersion() != GpuPreviewNative.Version)
                    throw new GpuPreviewException("GpuPreviewNativeAbiMismatch", "GPU preview native ABI does not match this renderer.");
                _ = SkiaGpuBridge.FlushSurface(IntPtr.Zero, IntPtr.Zero, 0, 0);
                _ = SkiaGpuBridge.GetImageInfo(IntPtr.Zero, out _);
            }
            DeviceQueueCreateInfo[] queues = [new() { QueueFamilyIndex = (uint)family, QueuePriorities = [1f] }];
            device = physical.CreateDevice(queues,
                null, deviceExtensions.Length == 0 ? null : deviceExtensions, null, null, null, null, null);
            Interlocked.Increment(ref _deviceInitializations);
            Queue queue = device.GetQueue((uint)family, 0);
            Instance stableInstance = instance;
            Device stableDevice = device;
            vulkanLibrary = NativeLibrary.Load(GetVulkanLibraryName());
            var globalGetInstanceProcAddress = Marshal.GetDelegateForFunctionPointer<VkGetInstanceProcAddrDelegate>(
                NativeLibrary.GetExport(vulkanLibrary, "vkGetInstanceProcAddr"));
            GRVkGetProcedureAddressDelegate getRawProc = new((name, vkInstance, vkDevice) =>
                vkDevice != IntPtr.Zero ? stableDevice.GetProcedureAddress(name)
                : vkInstance != IntPtr.Zero ? stableInstance.GetProcedureAddress(name)
                : GlobalProcedure(globalGetInstanceProcAddress, name));
            GRSharpVkGetProcedureAddressDelegate getSharpProc = new((name, vkInstance, vkDevice) =>
            {
                IntPtr rawInstance = vkInstance is null ? IntPtr.Zero : checked((IntPtr)(long)vkInstance.RawHandle.ToUInt64());
                IntPtr rawDevice = vkDevice is null ? IntPtr.Zero : checked((IntPtr)(long)vkDevice.RawHandle.ToUInt64());
                return getRawProc(name, rawInstance, rawDevice);
            });
            extensions = GRVkExtensions.Create(
                getRawProc,
                (IntPtr)instance.RawHandle.ToUInt64(), (IntPtr)physical.RawHandle.ToUInt64(), Array.Empty<string>(), deviceExtensions);
            var properties = physical.GetProperties();
            backendContext = new GRSharpVkBackendContext
            {
                VkInstance = instance,
                VkPhysicalDevice = physical,
                VkDevice = device,
                VkQueue = queue,
                GraphicsQueueIndex = (uint)family,
                MaxAPIVersion = enableDmaBufExport ? 0x00402000u : 0x00400000u,
                Extensions = extensions,
                GetProcedureAddress = getSharpProc,
            };
            context = GRContext.CreateVulkan(backendContext)
                ?? throw new InvalidOperationException("Skia Ganesh 无法基于 Vulkan device 创建 GRContext。");
            Interlocked.Increment(ref _contextInitializations);
            var info = new SkiaRenderDeviceInfo("vulkan", $"0x{properties.VendorID:X8}", properties.DeviceName,
                FormatDriverVersion(properties.DriverVersion), FormatApiVersion(properties.ApiVersion));
            var result = new VulkanSkiaRenderDevice(instance, physical, device, queue, extensions, backendContext, context,
                vulkanLibrary, getRawProc, getSharpProc, info, (uint)family, properties.VendorID, properties.DeviceID,
                enableGpuPreview, enableDmaBufExport);
            instance = null; device = null; extensions = null; context = null;
            backendContext = null; vulkanLibrary = IntPtr.Zero;
            return result;

            static IntPtr GlobalProcedure(VkGetInstanceProcAddrDelegate global, string name)
            {
                IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(name);
                try { return global(IntPtr.Zero, utf8); }
                finally { Marshal.FreeCoTaskMem(utf8); }
            }
        }
        catch (Exception error)
        {
            SkiaNativeResourceCleanup.Run(error,
                () => device?.WaitIdle(),
                () => context?.Dispose(),
                () => backendContext?.Dispose(),
                () => extensions?.Dispose(),
                () => device?.Dispose(),
                () => instance?.Dispose(),
                () => { if (vulkanLibrary != IntPtr.Zero) NativeLibrary.Free(vulkanLibrary); });
            throw new InvalidOperationException($"Vulkan Skia 渲染设备初始化失败；已拒绝回落 CPU：{error.Message}", error);
        }
    }

    internal static string GetVulkanLibraryName() =>
        OperatingSystem.IsWindows() ? "vulkan-1.dll"
        : OperatingSystem.IsMacOS() ? "libvulkan.1.dylib"
        : "libvulkan.so.1";

    internal readonly record struct DeviceCandidate(string Name, PhysicalDeviceType Type, int GraphicsQueueFamily);

    public SKImage? CreateTextureImage(SKImage image, bool mipmapped)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var texture = image.ToTextureImage(_context, mipmapped, true);
            if (texture is null || texture.IsTextureBacked) return texture;
            if (!ReferenceEquals(texture, image)) texture.Dispose();
            throw new InvalidOperationException("Vulkan 上传未返回真实纹理。");
        }
    }

    internal static int SelectGpuIndex(IReadOnlyList<DeviceCandidate> candidates)
    {
        int selected = -1;
        int selectedPriority = 0;
        for (int index = 0; index < candidates.Count; index++)
        {
            DeviceCandidate candidate = candidates[index];
            int priority = candidate.Type switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };
            if (candidate.GraphicsQueueFamily < 0 || priority <= selectedPriority) continue;
            selected = index;
            selectedPriority = priority;
        }
        if (selected >= 0) return selected;

        string available = candidates.Count == 0 ? "无 physical device" : string.Join("；",
            candidates.Select(value => $"{value.Name} ({value.Type}, graphicsQueueFamily={value.GraphicsQueueFamily})"));
        throw new InvalidOperationException(
            $"Vulkan 没有带 graphics queue 的集成、独立或虚拟 GPU；CPU/软件设备不支持显式 GPU 渲染。已枚举：{available}。");
    }

    // Vulkan driverVersion has vendor-defined encoding. Preserve the exact bits rather
    // than presenting SharpVk.Version's generic decode as a vendor's semantic version.
    internal static string FormatDriverVersion(SharpVk.Version version) => $"raw:0x{(uint)version:X8}";

    internal static string FormatApiVersion(SharpVk.Version version)
    {
        uint raw = (uint)version;
        uint variant = raw >> 29;
        string value = $"{(raw >> 22) & 0x7f}.{(raw >> 12) & 0x3ff}.{raw & 0xfff}";
        return variant == 0 ? value : $"{value} (variant {variant})";
    }

    public SKSurface CreateSurface(SKImageInfo imageInfo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SKSurface.Create(_context, true, imageInfo)
            ?? throw new InvalidOperationException($"Vulkan Skia 无法创建 GPU 表面（{imageInfo.Width}×{imageInfo.Height}）。");
    }

    public void FlushAndSubmit(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var submission = PerformanceTelemetry.Measure(PerformancePhase.GpuSubmit);
        surface.Flush();
        _context.Flush();
        _context.Submit(true);
    }

    public void SubmitWithoutWait(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var submission = PerformanceTelemetry.Measure(PerformancePhase.GpuSubmit);
        surface.Flush();
        _context.Flush();
        _context.Submit(false);
    }

    public SKImage? RasterizeImage(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (PerformanceTelemetry.Measure(PerformancePhase.GpuSubmit))
        {
            _context.Flush();
            _context.Submit(true);
        }
        return image.ToRasterImage(true);
    }

    public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);
        SkiaReadbackTelemetry.Record(false, imageInfo);
        return surface.ReadPixels(imageInfo, destination, rowBytes, 0, 0);
    }

    public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);
        SkiaReadbackTelemetry.Record(true, imageInfo);
        using (PerformanceTelemetry.Measure(PerformancePhase.GpuSubmit))
        {
            _context.Flush();
            _context.Submit(true);
        }
        SKImage? raster = image.ToRasterImage(true);
        if (raster is null) return false;
        try { return raster.ReadPixels(imageInfo, destination, rowBytes, 0, 0); }
        finally { if (!ReferenceEquals(raster, image)) raster.Dispose(); }
    }

    public bool ReadOwnedImagePixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var colorSpace = image.ColorSpace;
        if (image.ColorType != SKColorType.Bgra8888 || image.AlphaType != SKAlphaType.Premul
            || colorSpace?.IsSrgb != true || imageInfo.ColorType != SKColorType.Bgra8888
            || imageInfo.AlphaType != SKAlphaType.Premul || imageInfo.ColorSpace is not null
            || imageInfo.Width != image.Width || imageInfo.Height != image.Height)
            return ReadPixels(image, imageInfo, destination, rowBytes);

        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);
        SkiaReadbackTelemetry.Record(true, imageInfo);
        using (PerformanceTelemetry.Measure(PerformancePhase.GpuSubmit))
        {
            _context.Flush();
            _context.Submit(true);
        }
        // Skia treats a null destination color space as the image's color space.
        // Explicitly matching it avoids the expensive raster intermediate without
        // changing the requested pixel encoding.
        return image.ReadPixels(imageInfo.WithColorSpace(colorSpace), destination, rowBytes, 0, 0);
    }

    public void Dispose()
    {
        lock (SynchronizationRoot) DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? error = SkiaNativeResourceCleanup.Run(null,
            () =>
            {
                Exception? poolError = SkiaNativeResourceCleanup.Run(null, [.. _gpuPools.Select<VulkanGpuFramePool, Action>(pool => pool.Dispose)]);
                _gpuPools.Clear(); _currentGpuPool = null;
                if (poolError is not null) ExceptionDispatchInfo.Capture(poolError).Throw();
            },
            _device.WaitIdle,
            _context.Dispose,
            _backendContext.Dispose,
            _extensions.Dispose,
            _device.Dispose,
            _instance.Dispose,
            () => NativeLibrary.Free(_vulkanLibrary));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    internal GpuFrameLease CopyGpuFrame(SKSurface surface, RenderTarget target)
    {
        lock (SynchronizationRoot)
        {
            EnsureGpuTargetInitialized(target);
            return _currentGpuPool!.CopyFrom(surface, _context, _queueFamily, checked(++_gpuSequence));
        }
    }

    internal VulkanGpuFramePool CreateBorrowedDmaBufPool(RenderTarget target, ulong producerEpoch, ulong generation,
        ulong maximumAllocationBytes, Action failStream)
    {
        lock (SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!SupportsDmaBufExport)
                throw new GpuPreviewException("GpuPreviewUnavailable", "This Vulkan device did not enable DMA-BUF shared export.");
            GpuPreviewNative.Context context = new()
            {
                Size = (uint)Marshal.SizeOf<GpuPreviewNative.Context>(), Version = 1,
                Instance = _instance.RawHandle.ToUInt64(), PhysicalDevice = _physicalDevice.RawHandle.ToUInt64(),
                Device = _device.RawHandle.ToUInt64(), Queue = _queue.RawHandle.ToUInt64(),
                QueueFamily = _queueFamily, EnabledExtensions = GpuPreviewNative.RequiredExtensions,
            };
            return new VulkanGpuFramePool(SynchronizationRoot, context, target, producerEpoch, generation, maximumAllocationBytes, () =>
            {
                _gpuPreviewFailed = true;
                failStream();
            });
        }
    }

    internal void EnsureGpuTargetInitialized(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!SupportsDmaBufExport) throw new GpuPreviewException("GpuPreviewUnavailable", "This session did not enable GPU shared output or its stream has failed.");
            if (_currentGpuPool is null || _currentGpuPool.Target.PixelWidth != target.PixelWidth || _currentGpuPool.Target.PixelHeight != target.PixelHeight)
                throw new GpuPreviewException("GpuPreviewTargetNotInitialized", "Reserve the session budget and initialize this exact GPU target before rendering.");
        }
    }

    internal GpuFramePoolInfo InitializeGpuOutput(RenderTarget target, ulong maximumAllocationBytes)
    {
        lock (SynchronizationRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!SupportsDmaBufExport) throw new GpuPreviewException("GpuPreviewUnavailable", "This session did not enable GPU shared output or its stream has failed.");
            if (target.PixelWidth is < 64 or > 2560 || target.PixelHeight is < 64 or > 1440)
                throw new GpuPreviewException("GpuPreviewSizeUnsupported", "Shared GPU output is limited to 64..2560 x 64..1440.");
            if (maximumAllocationBytes is 0 or > 192ul * 1024 * 1024)
                throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "The reserved shared GPU session budget must be at most 192 MiB.");
            if (_currentGpuPool is null || _currentGpuPool.Target.PixelWidth != target.PixelWidth || _currentGpuPool.Target.PixelHeight != target.PixelHeight)
            {
                foreach (var retired in _gpuPools.Where(pool => pool != _currentGpuPool && pool.IsDrained).ToArray())
                { retired.Dispose(); _gpuPools.Remove(retired); }
                if (_currentGpuPool is not null && _currentGpuPool.IsDrained)
                { _currentGpuPool.Dispose(); _gpuPools.Remove(_currentGpuPool); _currentGpuPool = null; }
                ulong retained = _gpuPools.Aggregate(0ul, (total, pool) => checked(total + pool.AllocationBytes));
                if (retained >= maximumAllocationBytes || _gpuPools.Count >= 2)
                    throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "Outstanding GPU pool generations exceed the shared allocation budget.");
                GpuPreviewNative.Context context = new()
                {
                    Size = (uint)Marshal.SizeOf<GpuPreviewNative.Context>(), Version = 1,
                    Instance = _instance.RawHandle.ToUInt64(), PhysicalDevice = _physicalDevice.RawHandle.ToUInt64(),
                    Device = _device.RawHandle.ToUInt64(), Queue = _queue.RawHandle.ToUInt64(),
                    QueueFamily = _queueFamily, EnabledExtensions = GpuPreviewNative.RequiredExtensions,
                };
                var created = new VulkanGpuFramePool(SynchronizationRoot, context, target, _gpuProducerEpoch,
                    checked(++_gpuPoolGeneration), maximumAllocationBytes - retained, () => _gpuPreviewFailed = true);
                _gpuPools.Add(created); _currentGpuPool = created;
            }
            if (_gpuPools.Aggregate(0ul, (total, pool) => checked(total + pool.AllocationBytes)) > maximumAllocationBytes)
                throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "Existing GPU pool generations exceed the newly reserved session budget.");
            return _currentGpuPool.Info with { RetainedAllocationBytes = _gpuPools.Aggregate(0ul, (total, pool) => checked(total + pool.AllocationBytes)) };
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VkGetInstanceProcAddrDelegate(IntPtr instance, IntPtr name);
}

internal static class SharpVkNativeLoader
{
    private static readonly Lazy<bool> Installation = new(InstallResolver, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void EnsureInstalled() => _ = Installation.Value;

    private static bool InstallResolver()
    {
        if (!OperatingSystem.IsLinux())
            return true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Instance).Assembly, static (name, assembly, searchPath) =>
                name == "libdl.so" ? NativeLibrary.Load("libdl.so.2") : IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            // A host may have installed an equivalent resolver before this backend is loaded.
        }
        return true;
    }
}

internal static class SkiaNativeResourceCleanup
{
    internal const string SecondaryFailuresKey = "VulkanCleanupFailures";

    // All native owners must be released even after WaitIdle/device loss or a failed
    // partial initialization. Retain the original failure and its stack for callers.
    internal static Exception? Run(Exception? primaryFailure, params Action[] release)
    {
        List<Exception>? secondaryFailures = null;
        foreach (Action step in release)
        {
            try { step(); }
            catch (Exception error)
            {
                if (primaryFailure is null) primaryFailure = error;
                else (secondaryFailures ??= []).Add(error);
            }
        }
        if (secondaryFailures is not null)
            primaryFailure!.Data[SecondaryFailuresKey] = secondaryFailures.ToArray();
        return primaryFailure;
    }
}
