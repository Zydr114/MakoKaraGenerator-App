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
}
