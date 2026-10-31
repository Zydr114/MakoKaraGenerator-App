using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendExportJobTests
{
    [Fact]
    public async Task 运行排队和保留期内终态任务保护身份过期回收不删ledger报告正式产物()
    {
        await using Fixture fixture = await Fixture.Create();
        string session = await fixture.Project("a");
        JobResponse running = (await fixture.Registry.StartValidationAsync("a", session, new("0", 3, fixture.Meta()))).Response!;
        await fixture.Validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        JobResponse queued = (await fixture.Registry.StartExportAsync("a", session, new("0", Metadata: fixture.Meta()))).Response!;
        Assert.Equal("queued", fixture.Registry.ReadJob("a", queued.JobId).Response!.State);
        Assert.False(await fixture.Registry.TryReclaimClientAsync("a"));
        fixture.Registry.CancelJob("a", running.JobId);
        await fixture.Terminal("a", running.JobId);
        (await fixture.Exporter.Next()).Release.TrySetResult();
        JobResponse exported = await fixture.Terminal("a", queued.JobId);
        Assert.Equal("succeeded", exported.State);
        JobResponse close = (await fixture.Registry.StartCloseAsync("a", session,
            new("0", "discard", Metadata: fixture.Meta()))).Response!;
        Assert.Equal("succeeded", (await fixture.Terminal("a", close.JobId)).State);
        Assert.False(fixture.Registry.TryGet("a", session, out _));
        Assert.False(await fixture.Registry.TryReclaimClientAsync("a"));
        Dictionary<string, string> persistent = Directory.EnumerateFiles(fixture.State, "*", SearchOption.AllDirectories)
            .Where(path => path.Contains(Path.DirectorySeparatorChar + "jobs" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || path.Contains(Path.DirectorySeparatorChar + "export-artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
        Assert.Contains(persistent.Keys, path => path.EndsWith(".json", StringComparison.Ordinal));
        Assert.Contains(persistent.Keys, path => path.EndsWith(".mp4", StringComparison.Ordinal));
        fixture.Clock.Advance(3601);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        while (!await fixture.Registry.TryReclaimClientAsync("a"))
            await Task.Delay(10, timeout.Token);
        Assert.Equal(BackendErrorCode.JobNotFound, fixture.Registry.ReadJob("a", queued.JobId).Error!.Code);
        foreach ((string path, string hash) in persistent)
        {
            Assert.True(File.Exists(path), path);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }
}
