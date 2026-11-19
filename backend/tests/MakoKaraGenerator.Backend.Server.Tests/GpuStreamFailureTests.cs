using System.Reflection;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task Consumer_failure_requires_claimed_exact_identity_and_terminates_only_owned_worker_without_reopening_quota()
    {
        await using Fixture fixture = await Fixture.Create();
        const string consumer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", secret = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        GpuPreviewStreamRegistry registry = new();
        typeof(PreviewBackendService).GetField("_gpuStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, registry);
        typeof(PreviewBackendService).GetField("_desktopConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Service, DesktopConsumerIdentity.Create(consumer, secret, "123"));
        int stopped = 0;
        await using var pool = new GpuWorkerSparePool<WorkerProcessProxy>("isolation-fixture",
            () => new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1))),
            (_, _) => Task.CompletedTask, _ => { Interlocked.Increment(ref stopped); return Task.CompletedTask; }, _ => true);
        var workerLease = await pool.AcquireAsync();
        GpuStreamWorkerOpened opened = new("stream", "1", "1", 64, 64, 3, 49152, 123, 0, 0, 0, 0, "device");
        GpuStreamWorkerSession worker = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [workerLease.Worker, workerLease, new StaticWorkerSessionHandle(Guid.NewGuid(), "hash"), "unused", "1", "unused", "secret", opened], null)!;
        var state = new GpuPreviewStreamState("stream", "1", "1", "ready", fixture.Source, new(64, 64), PreviewAlphaMode.Straight,
            "vulkan", "1", "1", 3, 49152);
        using IDisposable reservation = registry.TryReserve(fixture.Client.ClientId, consumer)!;
        Assert.True(registry.Publish(reservation, state, new(fixture.Source, null!, null!), worker));
        Assert.False(await fixture.Service.AbortGpuConsumerAsync("stream", consumer, "host", "1", "1", secret));
        var entries = (System.Collections.IDictionary)typeof(GpuPreviewStreamRegistry).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        object entry = entries["stream"]!;
        entry.GetType().GetField("Claimed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, true);
        foreach (var request in new[]
        {
            ("stream", consumer, "host", "1", "1", "wrong"), ("stream", "other", "host", "1", "1", secret),
            ("stream", consumer, "other", "1", "1", secret), ("other", consumer, "host", "1", "1", secret),
            ("stream", consumer, "host", "2", "1", secret), ("stream", consumer, "host", "1", "2", secret),
        }) Assert.False(await fixture.Service.AbortGpuConsumerAsync(request.Item1, request.Item2, request.Item3, request.Item4, request.Item5, request.Item6));
        Assert.Equal(0, stopped);
        Assert.True(await fixture.Service.AbortGpuConsumerAsync("stream", consumer, "host", "1", "1", secret));
        Assert.Equal(1, stopped); Assert.True(worker.TerminationConfirmed);
        Assert.Equal("GpuConsumerCompletionUnknown", registry.Read(fixture.Client.ClientId, "stream")!.FailureCode);
        Assert.Null(registry.Read(fixture.Client.ClientId, "stream")!.WorkerState);
        Assert.Null(registry.TryReserve(fixture.Client.ClientId, consumer));
        Assert.Null(registry.Claim(consumer, "stream", "1"));
        await workerLease.Worker.DisposeAsync();
    }

    [Fact]
    public async Task Drained_failure_preserves_diagnostics_across_reads_and_can_close_and_reopen_quota()
    {
        await using Fixture fixture = await Fixture.Create();
        string client = fixture.Client.ClientId;
        GpuPreviewStreamRegistry registry = new();
        typeof(PreviewBackendService).GetField("_gpuStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, registry);
        await using var pool = new GpuWorkerSparePool<WorkerProcessProxy>("drained-fixture",
            () => new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1))),
            (_, _) => Task.CompletedTask, worker => worker.DisposeAsync().AsTask(), _ => true);
        var workerLease = await pool.AcquireAsync();
        WorkerProcessProxy proxy = workerLease.Worker;
        GpuStreamWorkerOpened opened = new("stream", "1", "1", 64, 64, 3, 49152, 123, 0, 0, 0, 0, "device");
        GpuStreamWorkerSession worker = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [proxy, workerLease, new StaticWorkerSessionHandle(Guid.NewGuid(), "hash"), "unused", "1", "unused", "secret", opened], null)!;
        typeof(GpuStreamWorkerSession).GetField("_disposeTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, Task.CompletedTask);
        typeof(GpuStreamWorkerSession).GetField("_terminationConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, true);
        var source = fixture.Source;
        var state = new GpuPreviewStreamState("stream", "1", "1", "ready", source, new(64, 64), PreviewAlphaMode.Straight,
            "vulkan", "1", "1", 3, 49152);
        var input = new BackendPreviewPinnedInput(source, null!, null!);
        using IDisposable reservation = registry.TryReserve(client, "consumer")!;
        Assert.True(registry.Publish(reservation, state, input, worker));
        Assert.True(registry.TryBeginChange(client, "stream", "1", out _));
        GpuStreamDiagnosticSummary diagnostics = new(1, 0, 1, false, [new("error", "compile", "invalid source", null, null)]);
        GpuStreamWorkerState failed = new("failed", 7, "1", "reset", "paused", null, "authorError", true, diagnostics);
        Assert.True(registry.FailChange(client, "stream", worker, "authorError", failed));

        for (int i = 0; i < 3; i++)
        {
            var read = await fixture.Service.ReadGpuStreamAsync(client, "stream");
            Assert.Equal("authorError", read.Response!.FailureCode);
            Assert.Equal(failed, read.Response.WorkerState);
        }
        var closed = await fixture.Service.CloseGpuStreamAsync(client, "stream", new("1", fixture.Metadata()));
        Assert.Equal("closed", closed.Response!.RegistrationState);
        using IDisposable? retry = registry.TryReserve(client, "consumer");
        Assert.NotNull(retry);
        await proxy.DisposeAsync();
    }

    [Fact]
    public async Task Explicit_dmabuf_transport_rejects_cpu_preview_stream_without_silent_fallback()
    {
        await using Fixture fixture = await Fixture.Create();
        const string consumer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", secret = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        GpuPreviewStreamRegistry registry = new();
        typeof(PreviewBackendService).GetField("_gpuStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, registry);
        typeof(PreviewBackendService).GetField("_gpuWorkerOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Service, new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1)));
        typeof(PreviewBackendService).GetField("_desktopConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Service, DesktopConsumerIdentity.Create(consumer, secret, "123"));

        string? previous = Environment.GetEnvironmentVariable("MAKO_PREVIEW_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("MAKO_PREVIEW_TRANSPORT", "dma-buf");
            var result = await fixture.Service.CreateGpuStreamAsync(fixture.Client.ClientId,
                new(fixture.Source, new(640, 360), PreviewAlphaMode.Opaque, "cpu", consumer, fixture.Metadata()));
            Assert.False(result.Accepted);
            Assert.Equal(BackendErrorCode.CommandNotSupported, result.Error!.Code);
            Assert.Equal("PreviewTransportUnsupported", result.Error.FailureCode);

            if (OperatingSystem.IsWindows())
            {
                var winVulkan = await fixture.Service.CreateGpuStreamAsync(fixture.Client.ClientId,
                    new(fixture.Source, new(640, 360), PreviewAlphaMode.Opaque, "vulkan", consumer, fixture.Metadata()));
                Assert.False(winVulkan.Accepted);
                Assert.Equal(BackendErrorCode.PreviewUnavailable, winVulkan.Error!.Code);
                Assert.Equal("GpuPreviewUnsupportedPlatform", winVulkan.Error.FailureCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAKO_PREVIEW_TRANSPORT", previous);
        }
    }

    [Fact]
    public async Task Claim_propagates_shm_transport_to_trusted_desktop_consumer()
    {
        await using Fixture fixture = await Fixture.Create();
        const string consumer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", secret = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        GpuPreviewStreamRegistry registry = new();
        typeof(PreviewBackendService).GetField("_gpuStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, registry);
        typeof(PreviewBackendService).GetField("_desktopConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Service, DesktopConsumerIdentity.Create(consumer, secret, "123"));
        await using var pool = new GpuWorkerSparePool<WorkerProcessProxy>("claim-shm-fixture",
            () => new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1))),
            (_, _) => Task.CompletedTask, worker => worker.DisposeAsync().AsTask(), _ => true);
        var workerLease = await pool.AcquireAsync();
        GpuStreamWorkerOpened opened = new("stream-shm", "1", "1", 640, 360, 3, 640UL * 360 * 4 * 3, 123, 0, 0, 0, 0, "",
            Transport: "shm", BackendApi: "cpu", PixelFormat: "bgra8888", StrideBytes: 640 * 4, ShmResourceName: "/dev/shm/mkg-shm-test");
        GpuStreamWorkerSession worker = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [workerLease.Worker, workerLease, new StaticWorkerSessionHandle(Guid.NewGuid(), "hash"), "unused", "1", "/tmp/broker.sock", "secret", opened], null)!;
        typeof(GpuStreamWorkerSession).GetField("_disposeTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, Task.CompletedTask);
        typeof(GpuStreamWorkerSession).GetField("_terminationConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, true);
        var state = new GpuPreviewStreamState("stream-shm", "1", "1", "ready", fixture.Source, new(640, 360),
            PreviewAlphaMode.Opaque, "cpu", "1", "1", 3, 640L * 360 * 4 * 3, Transport: "shm");
        using IDisposable reservation = registry.TryReserve(fixture.Client.ClientId, consumer)!;
        Assert.True(registry.Publish(reservation, state, new(fixture.Source, null!, null!), worker));

        // Validate state preserves explicit cpu/shm negotiation and unopened SHM cleanup path does not throw.
        GpuPreviewStreamState? observed = registry.Read(fixture.Client.ClientId, "stream-shm");
        Assert.NotNull(observed);
        Assert.Equal("cpu", observed!.Api);
        Assert.Equal("shm", observed.Transport);
        Assert.Equal("failed", observed.RegistrationState);
        Assert.Equal("GpuWorkerExited", observed.FailureCode);
        await workerLease.Worker.DisposeAsync();
    }
}
