using System.Text;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class GpuWorkerWarmupContractTests
{
    private static GpuWorkerWarmupIdentity Identity => new(1, new('a', 64), 2, new('b', 64), "fixture-engine");
    private static GpuWorkerWarmupResult Result => new(Identity, Guid.NewGuid().ToString("N"), 123,
        "vulkan", true, 1024, [new("fixture-runtime, Version=1.0.0.0", Guid.NewGuid().ToString("N"))],
        Guid.NewGuid().ToString("N"), 1, 1);

    [Fact]
    public void Full_preparation_round_trips_exact_private_identity()
    {
        Assert.Equal(Identity, GpuWorkerWarmupContract.DecodeRequest(GpuWorkerWarmupContract.EncodeRequest(Identity)));
        var original = Result;
        var decoded = GpuWorkerWarmupContract.DecodeResult(GpuWorkerWarmupContract.EncodeResult(original), Identity);
        Assert.Equal(original.Identity, decoded.Identity);
        Assert.Equal(original.PreparationId, decoded.PreparationId);
        Assert.Equal(original.InitializationId, decoded.InitializationId);
        Assert.Equal(original.RuntimeAssemblies, decoded.RuntimeAssemblies);
    }

    [Fact]
    public void Changed_reference_sdk_or_engine_cannot_reuse_preparation()
    {
        var bytes = GpuWorkerWarmupContract.EncodeResult(Result);
        foreach (var changed in new[] { Identity with { ReferenceIdentity = new('c', 64) },
                     Identity with { ReferenceCount = 3 }, Identity with { SdkIdentity = new('c', 64) },
                     Identity with { EngineIdentity = "different-engine" } })
            Assert.Throws<WorkerProtocolException>(() => GpuWorkerWarmupContract.DecodeResult(bytes, changed));
    }

    [Fact]
    public void Partial_or_reinitialized_preparation_is_rejected()
    {
        var original = Result;
        foreach (var invalid in new[] { original with { WorkerPid = 0 }, original with { OriginalDeviceRetained = false },
                     original with { ReferenceBytes = 0 }, original with { RuntimeAssemblies = [] },
                     original with { RuntimeAssemblies = [null!] },
                     original with { DeviceInitializations = 2 }, original with { ContextInitializations = 2 },
                     original with { InitializationId = "wrong" }, original with { Api = "raster" } })
            Assert.Throws<WorkerProtocolException>(() => GpuWorkerWarmupContract.EncodeResult(invalid));
    }

    [Fact]
    public void Duplicate_identity_and_unknown_fields_are_rejected()
    {
        string valid = Encoding.UTF8.GetString(GpuWorkerWarmupContract.EncodeRequest(Identity));
        foreach (string invalid in new[] { valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
                     valid[..^1] + ",\"unexpected\":true}" })
            Assert.Throws<WorkerProtocolException>(() => GpuWorkerWarmupContract.DecodeRequest(Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    [Trait("Category", "RequiresVulkan")]
    public async Task Windows_job_guarded_worker_completes_gpu_warmup_and_device_proof()
    {
        if (!OperatingSystem.IsWindows()) return;
        string workerExe = WorkerBinaryLocator.LocateBackendWorker();
        Assert.True(File.Exists(workerExe), $"Missing built Worker binary: {workerExe}");

        WorkerProcessOptions options = new(workerExe, [], TimeSpan.FromSeconds(5))
        {
            RenderBackend = "gpu",
            EnableGpuPreview = true,
        };
        await using WorkerProcessProxy proxy = new(options);
        GpuWorkerWarmupResult warmed = await proxy.WarmGpuAsync();
        Assert.Equal("vulkan", warmed.Api);
        Assert.True(warmed.OriginalDeviceRetained);
        Assert.Equal(1, warmed.DeviceInitializations);
        Assert.Equal(1, warmed.ContextInitializations);
        Assert.NotNull(proxy.GpuWarmupProcessIdentity);
        Assert.Equal(proxy.ProcessId, proxy.GpuWarmupProcessIdentity!.ProcessId);

        RenderBackendDeviceInfoDto proof = await proxy.ReadGpuDeviceProofAsync();
        Assert.False(string.IsNullOrWhiteSpace(proof.Device));
        await proxy.StopAsync();
    }
}
