using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerFontCatalogTests
{
    [Fact]
    public async Task 实际Worker目录和整形成功响应均按result信封读取并停止排水()
    {
        string worker = Environment.GetEnvironmentVariable("MAKO_TEST_FONT_WORKER_PATH")
            ?? WorkerBinaryLocator.LocateBackendWorker();
        Assert.True(File.Exists(worker), $"Build the matching Worker or configure the published Worker path: {worker}");
        await using WorkerProcessProxy proxy = new(new WorkerProcessOptions(worker));
        FontListResponse fonts = await proxy.ListFontsAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(fonts.Families);
        Assert.Equal(FontWorkerContract.Environment, fonts.Environment);
        FontInspectResponse inspection = await proxy.InspectFontAsync(new("Mako 歌詞", fonts.Families[0]), TimeSpan.FromSeconds(10));
        Assert.True(inspection.RequestedAvailable);
        Assert.Equal(FontWorkerContract.Environment, inspection.Environment);
        Assert.NotEmpty(inspection.ResolvedFamily);
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
    }
}
