using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class GpuPreviewStreamRegistryTests
{
    [Fact]
    public void ReservationsEnforceClientConsumerAndHostLimitsUntilReleased()
    {
        GpuPreviewStreamRegistry registry = new();
        using IDisposable first = registry.TryReserve("a", "consumer-a")!;
        Assert.NotNull(first);
        Assert.Null(registry.TryReserve("a", "consumer-b"));
        Assert.Null(registry.TryReserve("b", "consumer-a"));
        IDisposable second = registry.TryReserve("b", "consumer-b")!;
        Assert.NotNull(second);
        Assert.Null(registry.TryReserve("c", "consumer-c"));
        second.Dispose();
        second.Dispose();
        using IDisposable replacement = registry.TryReserve("c", "consumer-c")!;
        Assert.NotNull(replacement);
        Assert.Null(registry.TryReserve("b", "consumer-b"));
    }

    [Fact]
    public void ResolveEffectiveTransportHonorsExplicitRulesWithoutSilentFallback()
    {
        Assert.Equal(PreviewTransportKind.Shm, GpuStreamWorkerSession.ResolveEffectiveTransport("cpu", transport: null, envOverride: ""));
        Assert.Equal(PreviewTransportKind.Shm, GpuStreamWorkerSession.ResolveEffectiveTransport("cpu", transport: null, envOverride: "shm"));
        Assert.Equal(PreviewTransportKind.Shm, GpuStreamWorkerSession.ResolveEffectiveTransport("vulkan", transport: null, envOverride: "shm"));

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(PreviewTransportKind.DmaBuf, GpuStreamWorkerSession.ResolveEffectiveTransport("vulkan", transport: null, envOverride: ""));
            Assert.Equal(PreviewTransportKind.DmaBuf, GpuStreamWorkerSession.ResolveEffectiveTransport("vulkan", transport: null, envOverride: "dma-buf"));
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.Equal(PreviewTransportKind.Shm, GpuStreamWorkerSession.ResolveEffectiveTransport("vulkan", transport: null, envOverride: ""));
            Assert.Throws<PlatformNotSupportedException>(() =>
                GpuStreamWorkerSession.ResolveEffectiveTransport("vulkan", transport: null, envOverride: "dma-buf"));
            Assert.Throws<PlatformNotSupportedException>(() =>
                GpuStreamWorkerSession.ResolveEffectiveTransport("vulkan", transport: PreviewTransportKind.DmaBuf, envOverride: "shm"));
        }

        GpuPreviewException cpuDmaBuf = Assert.Throws<GpuPreviewException>(() =>
            GpuStreamWorkerSession.ResolveEffectiveTransport("cpu", transport: null, envOverride: "dma-buf"));
        Assert.Equal("PreviewTransportUnsupported", cpuDmaBuf.Code);

        GpuPreviewException explicitCpuDmaBuf = Assert.Throws<GpuPreviewException>(() =>
            GpuStreamWorkerSession.ResolveEffectiveTransport("cpu", transport: PreviewTransportKind.DmaBuf, envOverride: "shm"));
        Assert.Equal("PreviewTransportUnsupported", explicitCpuDmaBuf.Code);

        // 连续预览流 Worker 允许显式 gpu 或 cpu，但拒绝隐式 null 渲染后端。
        new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1)) { RenderBackend = "gpu", EnableGpuPreview = true }.Validate();
        new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1)) { RenderBackend = "cpu", EnableGpuPreview = true }.Validate();
        Assert.Throws<ArgumentException>(() =>
            new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1)) { RenderBackend = null, EnableGpuPreview = true }.Validate());
    }
}
