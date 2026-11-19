using System.Reflection;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;

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

    [Fact]
    public async Task WorkerDisposeConfirmsTerminationEvenWhenProtocolFailsAndDirectoryLocked()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "makokara-test-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string lockFile = Path.Combine(tempDir, "locked.bin");
        using FileStream locked = new(lockFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await using var pool = new GpuWorkerSparePool<WorkerProcessProxy>("dispose-tolerant-fixture",
                () => new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1))),
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => true);
            var workerLease = await pool.AcquireAsync();
            GpuStreamWorkerOpened opened = new("stream-lock", "1", "1", 64, 64, 3, 49152, 123, 0, 0, 0, 0, "device");
            GpuStreamWorkerSession worker = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [workerLease.Worker, workerLease, new StaticWorkerSessionHandle(Guid.NewGuid(), "hash"), tempDir, "1", "unused", "secret", opened], null)!;

            // 协议关闭会因未启动进程而失败，Windows 文件句柄也会锁定 tempDir，但进程确认退出后仍应成功完成 DisposeAsync。
            await worker.DisposeAsync();
            await worker.DisposeAsync();
            Assert.True(worker.TerminationConfirmed);
            await workerLease.Worker.DisposeAsync();
        }
        finally
        {
            locked.Dispose();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ListAndClientStreamsForProjectExposeAndFilterQuarantineAccurately()
    {
        GpuPreviewStreamRegistry registry = new();
        PreviewSourceDto projectOne = new("project", "proj-1", "1");
        PreviewSourceDto projectTwo = new("project", "proj-2", "1");

        await using var pool = new GpuWorkerSparePool<WorkerProcessProxy>("registry-list-fixture",
            () => new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1))),
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => true);
        var lease1 = await pool.AcquireAsync();
        var lease2 = await pool.AcquireAsync();
        GpuStreamWorkerSession w1 = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [lease1.Worker, lease1, new StaticWorkerSessionHandle(Guid.NewGuid(), "h1"), "unused", "1", "u1", "s1",
                new GpuStreamWorkerOpened("s1", "1", "1", 64, 64, 3, 49152, 1001, 0, 0, 0, 0, "dev")], null)!;
        GpuStreamWorkerSession w2 = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [lease2.Worker, lease2, new StaticWorkerSessionHandle(Guid.NewGuid(), "h2"), "unused", "1", "u2", "s2",
                new GpuStreamWorkerOpened("s2", "1", "1", 64, 64, 3, 49152, 1002, 0, 0, 0, 0, "dev")], null)!;

        try
        {
            using IDisposable r1 = registry.TryReserve("c1", "cons-1")!;
            using IDisposable r2 = registry.TryReserve("c2", "cons-2")!;
            Assert.True(registry.Publish(r1, new("s1", "1", "1", "ready", projectOne, new(64, 64), PreviewAlphaMode.Opaque, "cpu", "1", "1", 3, 49152, Transport: "shm"), new(projectOne, null!, null!), w1));
            Assert.True(registry.Publish(r2, new("s2", "1", "1", "ready", projectTwo, new(64, 64), PreviewAlphaMode.Opaque, "cpu", "1", "1", 3, 49152, Transport: "shm"), new(projectTwo, null!, null!), w2));

            GpuPreviewStreamState[] c1List = registry.List("c1");
            Assert.Single(c1List);
            Assert.Equal("s1", c1List[0].StreamId);
            Assert.Null(c1List[0].Quarantined);
            Assert.Single(registry.ClientStreamsForProject("c1", "proj-1"));
            Assert.Empty(registry.ClientStreamsForProject("c1", "proj-2"));

            var entries = (System.Collections.IDictionary)typeof(GpuPreviewStreamRegistry)
                .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
            entries["s1"]!.GetType().GetField("Claimed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entries["s1"], true);
            Assert.NotNull(registry.QuarantineConsumerFailure("cons-1", "s1", "1", "1"));
            Assert.True(registry.FailChange("c1", "s1", w1, "GpuConsumerCompletionUnknown"));
            Assert.True(registry.IsQuarantined("c1", "s1"));
            Assert.Empty(registry.ClientStreamsForProject("c1", "proj-1"));
            Assert.True(registry.List("c1")[0].Quarantined);
        }
        finally
        {
            await lease1.Worker.DisposeAsync();
            await lease2.Worker.DisposeAsync();
        }
    }
}
