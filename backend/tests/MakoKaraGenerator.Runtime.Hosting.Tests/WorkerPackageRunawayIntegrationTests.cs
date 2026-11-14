using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerPackageRunawayIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Initialize", "cancel", true)]
    [InlineData("Initialize", "deadline", false)]
    [InlineData("Initialize", "dispose", false)]
    [InlineData("Evaluate", "cancel", false)]
    [InlineData("Evaluate", "deadline", false)]
    [InlineData("Evaluate", "dispose", false)]
    public async Task 实际作者进入后失控仍受取消期限关闭隔离并排水真实私有组且Host可用(string stage, string action, bool ffmpeg)
    {
        Assert.True(LinuxProcessGuards.IsSupported, "This bounded acceptance requires actual Linux private process groups.");
        Assert.True(LinuxProcessGuards.TryEnableChildReaping());
        PackageWorkerRuntimeFixture.AssertFixedFont();
        string nonce = Guid.NewGuid().ToString("N");
        string evidence = Path.Combine(PackageWorkerRuntimeFixture.RepositoryRoot(), "artifacts", "backend-b3g-author-runtime", "owned-runaway", stage + "-" + action + "-" + nonce);
        Directory.CreateDirectory(evidence);
        string marker = Path.Combine(evidence, "actual-entered");
        ProjectSnapshot runaway = PackageWorkerRuntimeFixture.Runaway(stage, marker, nonce, ffmpeg);
        ProjectSnapshot healthy = PackageWorkerRuntimeFixture.Create("scale");
        var options = new WorkerProcessOptions(PackageWorkerRuntimeFixture.WorkerApphost(), cancellationGracePeriod: TimeSpan.FromMilliseconds(150)) { RenderBackend = "gpu" };
        var factory = new WorkerPackageRenderFrameSessionFactory(options, TimeSpan.FromSeconds(20));
        await using WorkerProcessProxy proxy = new(options);
        using CancellationTokenSource cancel = new();
        OwnedProcessGroup? group = null;
        IRenderFrameSession? session = null;
        Task? active = null;
        ProcessIdentity[] observed = [];
        var host = Process.GetCurrentProcess();
        long hostStart = host.StartTime.ToUniversalTime().Ticks;
        try
        {
            if (stage == "Initialize")
            {
                active = action == "deadline" ? factory.CreateAsync(runaway, cancel.Token).AsTask()
                    : proxy.OpenPackageSessionAsync(PackageProjectSnapshotCodec.Encode(runaway), TimeSpan.FromSeconds(20), cancel.Token);
                int readyPid = int.Parse(await WaitForFileAsync(marker + ".ready", active, TimeSpan.FromSeconds(12)), CultureInfo.InvariantCulture);
                using Process ready = Process.GetProcessById(readyPid);
                group = OwnedProcessGroup.TryCapture(ready); Assert.NotNull(group);
            }
            else
            {
                session = await factory.CreateAsync(runaway);
                int readyPid = int.Parse(await WaitForFileAsync(marker + ".ready", active, TimeSpan.FromSeconds(10)), CultureInfo.InvariantCulture);
                using Process ready = Process.GetProcessById(readyPid);
                group = OwnedProcessGroup.TryCapture(ready); Assert.NotNull(group);
                active = session.RenderAsync(WorkerPackageSessionIntegrationTests.Request(runaway, 100_000), cancel.Token).AsTask();
            }
            string[] entered = (await WaitForFileAsync(marker, active, TimeSpan.FromSeconds(12))).Split('|');
            Assert.Equal(4, entered.Length); Assert.Equal(stage, entered[2]); Assert.Equal(nonce, entered[3]);
            int workerPid = int.Parse(entered[0], CultureInfo.InvariantCulture), childPid = int.Parse(entered[1], CultureInfo.InvariantCulture);
            using (Process process = Process.GetProcessById(workerPid)) group ??= OwnedProcessGroup.TryCapture(process);
            Assert.NotNull(group); Assert.Equal(workerPid, group.Leader.ProcessId);
            observed = [.. group.ObserveMembers()];
            await File.WriteAllTextAsync(Path.Combine(evidence, "observed-identities.json"), System.Text.Json.JsonSerializer.Serialize(observed));
            ProcessIdentity child = Assert.Single(observed, value => value.ProcessId == childPid);
            Assert.Equal(group.Leader.ProcessGroupId, child.ProcessGroupId); Assert.True(child.StartTicks >= group.Leader.StartTicks);
            Assert.False(active.IsCompleted, "The real author loop must still be running after its marker was observed.");
            if (ffmpeg)
            {
                string command = await File.ReadAllTextAsync("/proc/" + childPid.ToString(CultureInfo.InvariantCulture) + "/cmdline");
                Assert.Contains("ffmpeg", command, StringComparison.Ordinal); Assert.Contains("anullsrc", command, StringComparison.Ordinal);
            }
            // The Host must perform useful work while the author's CPU loop is
            // still live in a different Worker, before any termination action.
            await AssertHealthyAsync(factory, healthy);
            Assert.False(active.IsCompleted, "The runaway terminated before the selected lifecycle action.");
            Exception? failure;
            if (action == "cancel")
            {
                cancel.Cancel(); failure = await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(6)));
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            }
            else if (action == "dispose")
            {
                if (stage == "Initialize") await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
                else await session!.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
                failure = await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(6)));
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            }
            else
            {
                failure = await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(25)));
                Assert.Equal("WorkerPackageTimedOut", failure switch
                {
                    WorkerProtocolException protocol => protocol.Code,
                    RenderSessionFailureException render => render.Code,
                    _ => failure?.GetType().Name,
                });
                Assert.False(failure is OperationCanceledException);
            }
            Assert.Empty(group.ObserveMembers());
            foreach (ProcessIdentity identity in observed)
                Assert.False(IdentityStillExists(identity), "Observed owned identity was not drained: " + identity);
            if (session is not null)
            {
                Exception? stale = await Record.ExceptionAsync(() => session.RenderAsync(WorkerPackageSessionIntegrationTests.Request(runaway, 200_000)).AsTask());
                if (action == "dispose") Assert.IsType<ObjectDisposedException>(stale);
                else Assert.Equal("WorkerPackageSessionFailed", Assert.IsType<PreparedFrameException>(stale).Code);
            }
            Assert.False(host.HasExited); Assert.Equal(hostStart, host.StartTime.ToUniversalTime().Ticks);
            await AssertHealthyAsync(factory, healthy);
            if (stage == "Initialize" && action == "cancel")
            {
                StaticWorkerSessionHandle reopened = await proxy.OpenPackageSessionAsync(PackageProjectSnapshotCodec.Encode(healthy), TimeSpan.FromSeconds(20));
                Assert.NotEqual(workerPid, proxy.ProcessId);
                WorkerMessage frame = await proxy.RenderPackageSessionAsync(new(reopened.SessionId, reopened.SourceSha256, 320, 180, 100_000, false, 1), TimeSpan.FromSeconds(20));
                Assert.NotEmpty(frame.Payload);
                await proxy.ClosePackageSessionAsync(reopened, 2, TimeSpan.FromSeconds(20)); await proxy.StopAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "verified-terminal.json"), System.Text.Json.JsonSerializer.Serialize(new
            { stage, action, ffmpeg, nonce, hostPid = Environment.ProcessId, entered = true, observed, allObservedIdentitiesGone = true, parallelHealthyFrame = true, recoveredHealthyFrame = true }));
            output.WriteLine($"Actual {stage}/{action}/{(ffmpeg ? "ffmpeg" : "sleep")} marker confirmed; Host={Environment.ProcessId}; drained={string.Join(',', observed.Select(value => value.ToString()))}; useful parallel Host operation and new Worker recovery passed.");
        }
        finally
        {
            // A failing assertion must also clean the exact captured test group.
            // Never find or kill a process by a name or a guessed PID alone.
            cancel.Cancel();
            if (group is not null) await group.KillAndDrainAsync(TimeSpan.FromSeconds(3));
            if (active is not null) { try { await active.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { } }
            if (session is not null) await session.DisposeAsync();
            host.Dispose();
        }
    }

    private static async Task AssertHealthyAsync(WorkerPackageRenderFrameSessionFactory factory, ProjectSnapshot healthy)
    {
        await using IRenderFrameSession session = await factory.CreateAsync(healthy).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
        using RenderFrame frame = await session.RenderAsync(WorkerPackageSessionIntegrationTests.Request(healthy, 100_000)).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(320 * 180 * 4, frame.PixelMemory.Length);
        Assert.False(frame.DiagnosticSnapshot.HasError);
        Assert.Empty(frame.DiagnosticSnapshot.ReadAll());
    }

    private static async Task<string> WaitForFileAsync(string path, Task? active, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                string text = await File.ReadAllTextAsync(path);
                if (text.Length > 0) return text;
            }
            if (active?.IsCompleted == true)
            {
                await active;
                Assert.Fail("Actual author invocation finished without its entered marker.");
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Actual author entered marker was not observed before the fixture deadline.");
    }

    private static bool IdentityStillExists(ProcessIdentity identity)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + identity.ProcessId.ToString(CultureInfo.InvariantCulture) + "/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return ulong.Parse(fields[19], CultureInfo.InvariantCulture) == identity.StartTicks
                && int.Parse(fields[2], CultureInfo.InvariantCulture) == identity.ProcessGroupId;
        }
        catch (IOException) { return false; }
    }
}
