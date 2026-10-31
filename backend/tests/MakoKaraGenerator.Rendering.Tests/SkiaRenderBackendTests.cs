using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MakoKaraGenerator.Rendering.Skia;
using SharpVk;
using SkiaSharp;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SkiaRenderBackendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("cpu")]
    [InlineData("CPU")]
    public void BackendParsingDefaultsToCpu(string? value) =>
        Assert.Equal(SkiaRenderBackend.Cpu, SkiaRenderBackendOptions.Parse(value).Backend);

    [Theory]
    [InlineData("vulkan")]
    [InlineData("VULKAN")]
    public void BackendParsingSelectsVulkanExplicitly(string value) =>
        Assert.Equal(SkiaRenderBackend.Vulkan, SkiaRenderBackendOptions.Parse(value).Backend);

    [Theory]
    [InlineData("gpu")]
    [InlineData("opengl")]
    [InlineData("vulkan ")]
    public void BackendParsingRejectsUnknownValues(string value) =>
        Assert.Contains(value, Assert.Throws<InvalidOperationException>(() => SkiaRenderBackendOptions.Parse(value)).Message,
            StringComparison.Ordinal);

    [Fact]
    public void FactoryRejectsUnknownBackendEnum() =>
        Assert.Throws<InvalidOperationException>(() => SkiaRenderDeviceFactory.Create(new((SkiaRenderBackend)999)));

    [Fact]
    public void GpuSelectionPrefersDiscreteThenIntegratedThenVirtualAndExcludesSoftware()
    {
        VulkanSkiaRenderDevice.DeviceCandidate[] candidates =
        [
            new("llvmpipe", PhysicalDeviceType.Cpu, 0),
            new("virtual", PhysicalDeviceType.VirtualGpu, 0),
            new("integrated", PhysicalDeviceType.IntegratedGpu, 2),
            new("discrete", PhysicalDeviceType.DiscreteGpu, 1),
            new("other", PhysicalDeviceType.Other, 0),
        ];

        Assert.Equal(3, VulkanSkiaRenderDevice.SelectGpuIndex(candidates));
        Assert.Equal(2, VulkanSkiaRenderDevice.SelectGpuIndex(candidates[..3]));
        Assert.Equal(1, VulkanSkiaRenderDevice.SelectGpuIndex(candidates[..2]));
    }

    [Theory]
    [InlineData(PhysicalDeviceType.Cpu)]
    [InlineData(PhysicalDeviceType.Other)]
    public void GpuSelectionRejectsSoftwareAndUnknownPhysicalTypes(PhysicalDeviceType type)
    {
        var error = Assert.Throws<InvalidOperationException>(() => VulkanSkiaRenderDevice.SelectGpuIndex(
            [new("llvmpipe", type, 0)]));

        Assert.Contains("llvmpipe", error.Message, StringComparison.Ordinal);
        Assert.Contains(type.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("CPU/软件", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuSelectionSkipsDevicesWithoutGraphicsQueuesAndKeepsStablePreference()
    {
        Assert.Equal(1, VulkanSkiaRenderDevice.SelectGpuIndex(
        [
            new("compute-only", PhysicalDeviceType.DiscreteGpu, -1),
            new("first integrated", PhysicalDeviceType.IntegratedGpu, 0),
            new("second integrated", PhysicalDeviceType.IntegratedGpu, 1),
        ]));

        var error = Assert.Throws<InvalidOperationException>(() => VulkanSkiaRenderDevice.SelectGpuIndex(
            [new("compute-only", PhysicalDeviceType.DiscreteGpu, -1)]));
        Assert.Contains("graphicsQueueFamily=-1", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => VulkanSkiaRenderDevice.SelectGpuIndex([]));
    }

    [Fact]
    public void DriverDiagnosticsPreserveVendorDefinedBitsAndApiDiagnosticsDecodeVersion()
    {
        Assert.Equal("raw:0xDEADBEEF", VulkanSkiaRenderDevice.FormatDriverVersion(0xdeadbeefu));
        Assert.Equal("1.4.354", VulkanSkiaRenderDevice.FormatApiVersion(new SharpVk.Version(1, 4, 354)));
        Assert.Equal("1.4.354 (variant 1)", VulkanSkiaRenderDevice.FormatApiVersion(0x20404162u));
    }

    [Fact]
    public void CleanupReleasesEveryOwnerAfterWaitIdleAndContextFailures()
    {
        List<string> released = [];
        var waitFailure = new InvalidOperationException("device lost during WaitIdle");
        var contextFailure = new InvalidOperationException("context disposal failed");

        Exception? actual = SkiaNativeResourceCleanup.Run(null,
            () => { released.Add("wait"); throw waitFailure; },
            () => { released.Add("context"); throw contextFailure; },
            () => released.Add("backendContext"),
            () => released.Add("extensions"),
            () => released.Add("device"),
            () => released.Add("instance"),
            () => released.Add("nativeLibrary"));

        Assert.Equal(["wait", "context", "backendContext", "extensions", "device", "instance", "nativeLibrary"], released);
        Assert.Same(waitFailure, actual);
        Assert.NotNull(actual);
        Assert.Same(contextFailure, Assert.Single(Assert.IsType<Exception[]>(
            actual.Data[SkiaNativeResourceCleanup.SecondaryFailuresKey])));
    }

    [Fact]
    public void CleanupPreservesInitializationFailureWhileReleasingPartialResources()
    {
        var initializeFailure = new InvalidOperationException("Ganesh initialization failed");
        var cleanupFailure = new InvalidOperationException("device disposal failed");
        bool libraryReleased = false;

        Exception? actual = SkiaNativeResourceCleanup.Run(initializeFailure,
            () => throw cleanupFailure,
            () => libraryReleased = true);

        Assert.True(libraryReleased);
        Assert.Same(initializeFailure, actual);
        Assert.NotNull(actual);
        Assert.Same(cleanupFailure, Assert.Single(Assert.IsType<Exception[]>(
            actual.Data[SkiaNativeResourceCleanup.SecondaryFailuresKey])));
        Assert.Null(SkiaNativeResourceCleanup.Run(null, () => { }));
    }

    [Fact]
    public void NativeResolverInstallationSupportsConcurrentCallers() =>
        Parallel.For(0, 32, _ => SharpVkNativeLoader.EnsureInstalled());

    [Fact]
    public void CpuFactoryCreatesRasterSurfaceAndPreservesBgraAndPremultipliedAlpha()
    {
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        Assert.Equal("cpu", device.Info.Backend);
        VerifySurface(device, gpu: false);
        device.Dispose();
        device.Dispose();
        Assert.Throws<ObjectDisposedException>(() => device.CreateSurface(new SKImageInfo(1, 1)));
    }

    [Fact]
    public void VulkanFactoryCreatesTextureBackedSurfacesAndReadsRepeatedFramesWhenEnabled()
    {
        if (!GpuTestsEnabled()) return;
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        Assert.Equal("vulkan", device.Info.Backend);
        Assert.StartsWith("raw:0x", device.Info.Driver, StringComparison.Ordinal);
        VerifySurface(device, gpu: true);
        device.Dispose();
        device.Dispose();
        Assert.Throws<ObjectDisposedException>(() => device.CreateSurface(new SKImageInfo(1, 1)));
    }

    [Fact]
    public void ConcurrentVulkanDeviceCreationUsesIndependentTextureBackedContextsWhenEnabled()
    {
        if (!GpuTestsEnabled()) return;
        Parallel.For(0, 4, _ =>
        {
            using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
            VerifySurface(device, gpu: true);
        });
    }

    private void VerifySurface(ISkiaRenderDevice device, bool gpu)
    {
        var info = new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = device.CreateSurface(info);
        if (gpu)
        {
            Assert.NotNull(surface.Context);
            Assert.Equal(GRBackend.Vulkan, surface.Context.Backend);
        }
        else Assert.Null(surface.Context);

        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.Red };
        surface.Canvas.DrawRect(0, 0, 4, 4, paint);
        device.FlushAndSubmit(surface);
        using (SKImage image = surface.Snapshot())
        {
            Assert.Equal(gpu, image.IsTextureBacked);
            AssertPixels(ReadPixels(device, surface, null, info), 0, 0, 0, 255, 255);
            AssertPixels(ReadPixels(device, null, image, info), 0, 0, 0, 255, 255);
            AssertPixels(ReadPixels(device, null, image, info), 7, 0, 0, 0, 0);
            using SKImage? raster = device.RasterizeImage(image);
            Assert.NotNull(raster);
            Assert.False(raster.IsTextureBacked);
        }

        surface.Canvas.Clear(SKColors.Transparent);
        paint.Color = new SKColor(200, 100, 50, 128);
        surface.Canvas.DrawRect(0, 0, 4, 4, paint);
        device.FlushAndSubmit(surface);
        byte[] premultiplied = ReadPixels(device, surface, null, info);
        AssertPixels(premultiplied, 0, 25, 50, 100, 128);
        AssertPixels(premultiplied, 7, 0, 0, 0, 0);
        output.WriteLine($"{device.Info}, surface={info.Width}x{info.Height}, textureBacked={gpu}, " +
            $"readbackSha256={Convert.ToHexString(SHA256.HashData(premultiplied))}");
    }

    private static byte[] ReadPixels(ISkiaRenderDevice device, SKSurface? surface, SKImage? image, SKImageInfo info)
    {
        byte[] pixels = new byte[info.BytesSize];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.True(surface is not null
                ? device.ReadPixels(surface, info, pin.AddrOfPinnedObject(), info.RowBytes)
                : device.ReadPixels(image!, info, pin.AddrOfPinnedObject(), info.RowBytes));
        }
        finally { pin.Free(); }
        return pixels;
    }

    private static void AssertPixels(byte[] pixels, int pixelIndex, byte blue, byte green, byte red, byte alpha)
    {
        int offset = pixelIndex * 4;
        Assert.InRange((int)pixels[offset], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.InRange((int)pixels[offset + 1], Math.Max(0, green - 1), Math.Min(255, green + 1));
        Assert.InRange((int)pixels[offset + 2], Math.Max(0, red - 1), Math.Min(255, red + 1));
        Assert.Equal(alpha, pixels[offset + 3]);
    }

    private static bool GpuTestsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE"), "1", StringComparison.Ordinal);
}
