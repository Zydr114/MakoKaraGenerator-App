using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class GpuWorkerDeviceAvailabilityTests
{
    [Fact]
    public async Task Gpu_configuration_reuses_registered_device_without_starting_helper()
    {
        using var configuration = new BackendRenderBackendConfiguration(new WorkerProcessOptions("missing-worker"), "cpu");
        int calls = 0;
        configuration.ConfigureGpuStreamDevice(_ => { calls++; return Task.FromResult(new RenderBackendDeviceInfoDto("fixture", "device", "driver", "1.3")); });
        Assert.Null(await configuration.VerifyAsync("gpu"));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => configuration.ConfigureGpuStreamDevice(_ => throw new InvalidOperationException("duplicate configuration")));
    }

    [Fact]
    public async Task Failed_existing_device_proof_reports_unavailable_without_fallback_helper()
    {
        using var configuration = new BackendRenderBackendConfiguration(new WorkerProcessOptions("missing-worker"), "cpu");
        int calls = 0;
        configuration.ConfigureGpuStreamDevice(_ => { calls++; throw new InvalidOperationException("no live matching device"); });
        var failure = await configuration.VerifyAsync("gpu");
        Assert.NotNull(failure);
        Assert.Equal(BackendErrorCode.TaskUnavailable, failure!.Code);
        Assert.Equal(1, calls);
    }
}
