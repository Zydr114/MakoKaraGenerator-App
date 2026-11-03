using System.Reflection;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task Service_dispose_retries_a_pool_quarantined_by_the_first_drain_failure()
    {
        await using Fixture fixture = await Fixture.Create();
        bool fail = true;
        bool block = false;
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new GpuWorkerSparePool<WorkerProcessProxy>("fixture",
            () => new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1))),
            (_, _) => Task.CompletedTask,
            async worker =>
            {
                if (fail) throw new IOException("first owned drain is unconfirmed");
                if (block) { retryEntered.TrySetResult(); await allowDrain.Task; }
                await worker.DisposeAsync();
            },
            _ => true);
        var lease = await owned.AcquireAsync();
        var pool = (GpuWorkerPool)RuntimeHelpers.GetUninitializedObject(typeof(GpuWorkerPool));
        typeof(GpuWorkerPool).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pool, owned);
        typeof(PreviewBackendService).GetField("_gpuWorkerPool", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, pool);
        var registry = new GpuPreviewStreamRegistry();
        typeof(PreviewBackendService).GetField("_gpuStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, registry);
        GpuStreamWorkerOpened opened = new("stream", "1", "1", 64, 64, 3, 49152, 123, 0, 0, 0, 0, "device");
        var worker = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [lease.Worker, lease, new StaticWorkerSessionHandle(Guid.NewGuid(), "hash"), "unused", "1", "unused", "secret", opened], null)!;
        typeof(GpuStreamWorkerSession).GetField("_disposeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(worker, Task.FromException(new IOException("original stream close fault")));
        var state = new GpuPreviewStreamState("stream", "1", "1", "ready", fixture.Source, new(64, 64),
            PreviewAlphaMode.Straight, "vulkan", "1", "1", 3, 49152);
        using IDisposable reservation = registry.TryReserve(fixture.Client.ClientId, "consumer")!;
        var retention = new CountingRetention();
        Assert.True(registry.Publish(reservation, state, new(fixture.Source, null!, null!, retention), worker));
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Service.DisposeAsync().AsTask());
        Assert.True(owned.Read().Quarantined > 0);
        Assert.True(owned.Read().Total > 0);
        Assert.Equal(0, retention.Released);
        fail = false;
        block = true;
        Task retry = fixture.Service.DisposeAsync().AsTask();
        await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(retry.IsCompleted);
        Assert.False(worker.TerminationConfirmed);
        Assert.Equal(0, retention.Released);
        allowDrain.TrySetResult();
        await retry;
        Assert.Equal(0, owned.Read().Total);
        Assert.True(worker.TerminationConfirmed);
        Assert.Equal(1, retention.Released);
        await lease.DisposeAsync();
    }

    private sealed class CountingRetention : IDisposable
    {
        public int Released { get; private set; }
        public void Dispose() => Released++;
    }
}
