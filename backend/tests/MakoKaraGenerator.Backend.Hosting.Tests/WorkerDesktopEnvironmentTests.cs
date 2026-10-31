using System.Diagnostics;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerDesktopEnvironmentTests
{
    [Fact]
    public void DesktopCredentialsDoNotReachWorkerButOtherConfigurationRemains()
    {
        var start = new ProcessStartInfo();
        start.Environment["MAKO_DESKTOP_CONSUMER_SECRET"] = "test-secret";
        start.Environment["MAKO_DESKTOP_MAIN_PID"] = "123";
        start.Environment["MAKO_RENDER_BACKEND"] = "vulkan";
        WorkerProcessProxy.RemoveDesktopCredentials(start);
        Assert.False(start.Environment.ContainsKey("MAKO_DESKTOP_CONSUMER_SECRET"));
        Assert.False(start.Environment.ContainsKey("MAKO_DESKTOP_MAIN_PID"));
        Assert.Equal("vulkan", start.Environment["MAKO_RENDER_BACKEND"]);
    }
}
