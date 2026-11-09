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

    [Fact]
    public void Worker设定64MiB的gen0预算_运维显式设置时沿用()
    {
        var start = new ProcessStartInfo();
        start.Environment.Remove("DOTNET_GCgen0size");
        start.Environment.Remove("COMPlus_GCgen0size");
        WorkerProcessProxy.ApplyWorkerGcBudget(start);
        Assert.Equal("0x4000000", start.Environment["DOTNET_GCgen0size"]);

        foreach (string key in new[] { "DOTNET_GCgen0size", "COMPlus_GCgen0size" })
        {
            var configured = new ProcessStartInfo();
            configured.Environment.Remove("DOTNET_GCgen0size");
            configured.Environment.Remove("COMPlus_GCgen0size");
            configured.Environment[key] = "0x8000000";
            WorkerProcessProxy.ApplyWorkerGcBudget(configured);
            Assert.Equal("0x8000000", configured.Environment[key]);
            Assert.Equal(key == "DOTNET_GCgen0size", configured.Environment.ContainsKey("DOTNET_GCgen0size"));
        }
    }
}
