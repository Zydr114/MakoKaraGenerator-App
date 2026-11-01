using System.Reflection;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task Drained_failure_preserves_diagnostics_across_reads_and_can_close_and_reopen_quota()
    {
        await using Fixture fixture = await Fixture.Create();
        string client = fixture.Client.ClientId;
        GpuPreviewStreamRegistry registry = new();
        typeof(PreviewBackendService).GetField("_gpuStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, registry);
        WorkerProcessProxy proxy = new(new WorkerProcessOptions("unused", [], TimeSpan.FromSeconds(1)));
        GpuStreamWorkerOpened opened = new("stream", "1", "1", 64, 64, 3, 49152, 123, 0, 0, 0, 0, "device");
        GpuStreamWorkerSession worker = (GpuStreamWorkerSession)Activator.CreateInstance(typeof(GpuStreamWorkerSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [proxy, new StaticWorkerSessionHandle(Guid.NewGuid(), "hash"), "unused", "1", "unused", "secret", opened], null)!;
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
}
