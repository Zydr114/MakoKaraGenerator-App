using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerPackageRunawayIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Declare", "cancel")]
    [InlineData("Declare", "deadline")]
    [InlineData("Declare", "dispose")]
    [InlineData("Evaluate", "cancel")]
    [InlineData("Evaluate", "deadline")]
    [InlineData("Evaluate", "dispose")]
    public async Task 实际作者进入后失控仍受取消期限关闭隔离并排水真实私有组且Host可用(string stage, string action)
    {
        Assert.True(LinuxProcessGuards.IsSupported, "This bounded acceptance requires actual Linux private process groups.");
        Assert.True(LinuxProcessGuards.TryEnableChildReaping());
        PackageWorkerRuntimeFixture.AssertFixedFont();
        string nonce = Guid.NewGuid().ToString("N");
        string evidence = Path.Combine(PackageWorkerRuntimeFixture.RepositoryRoot(), "artifacts", "backend-b3g-author-runtime", "owned-runaway", stage + "-" + action + "-" + nonce);
        Directory.CreateDirectory(evidence);
        ProjectSnapshot runaway = PackageWorkerRuntimeFixture.Runaway(stage);
        ProjectSnapshot healthy = PackageWorkerRuntimeFixture.Create("scale");
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var options = new WorkerProcessOptions(PackageWorkerRuntimeFixture.WorkerApphost(), cancellationGracePeriod: TimeSpan.FromMilliseconds(150))
        {
            RenderBackend = "gpu",
            ScriptCompilationService = compiler,
        };
        var factory = new WorkerPackageRenderFrameSessionFactory(options, TimeSpan.FromSeconds(20));
        await using WorkerProcessProxy proxy = new(options);
        using CancellationTokenSource cancel = new();
        OwnedProcessGroup? group = null;
        IRenderFrameSession? session = null;
        Task? active = null;
        ProcessIdentity[] observed = [];
        int workerPid = 0;
        var host = Process.GetCurrentProcess();
        long hostStart = host.StartTime.ToUniversalTime().Ticks;
        try
        {
            if (stage == "Declare")
            {
                TimeSpan timeout = action == "deadline" ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(20);
                active = proxy.OpenPackageSessionAsync(PackageProjectSnapshotCodec.Encode(runaway), timeout, cancel.Token);
                workerPid = await WaitForWorkerPidAsync(() => proxy.ProcessId, active, TimeSpan.FromSeconds(12));
                group = await WaitForProcessGroupAsync(workerPid, active, TimeSpan.FromSeconds(5));
                await WaitForCpuAdvanceAsync(workerPid, ReadCpuTicks(workerPid), active, TimeSpan.FromSeconds(10));
            }
            else
            {
                var evalFactory = action == "deadline" ? new WorkerPackageRenderFrameSessionFactory(options, TimeSpan.FromSeconds(4)) : factory;
                session = await evalFactory.CreateAsync(runaway);
                var evalProxy = (WorkerProcessProxy)session.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Single(field => field.FieldType == typeof(WorkerProcessProxy)).GetValue(session)!;
                workerPid = Assert.IsType<int>(evalProxy.ProcessId);
                group = await WaitForProcessGroupAsync(workerPid, null, TimeSpan.FromSeconds(5));
                ulong baselineTicks = ReadCpuTicks(workerPid);
                active = session.RenderAsync(WorkerPackageSessionIntegrationTests.Request(runaway, 100_000), cancel.Token).AsTask();
                await WaitForCpuAdvanceAsync(workerPid, baselineTicks, active, TimeSpan.FromSeconds(10));
            }
            Assert.Equal(workerPid, group.Leader.ProcessId);
            observed = [.. group.ObserveMembers()];
            await File.WriteAllTextAsync(Path.Combine(evidence, "observed-identities.json"), System.Text.Json.JsonSerializer.Serialize(observed));
            Assert.False(active.IsCompleted, "The real author loop must still be running after CPU advance was observed.");
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
                if (stage == "Declare") await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
                else await session!.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
                failure = await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(6)));
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            }
            else
            {
                failure = await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(25)));
                if (stage == "Declare")
                {
                    Assert.IsAssignableFrom<OperationCanceledException>(failure);
                    Assert.False(cancel.IsCancellationRequested);
                }
                else
                {
                    Assert.Equal("WorkerPackageTimedOut", failure switch
                    {
                        WorkerProtocolException protocol => protocol.Code,
                        RenderSessionFailureException render => render.Code,
                        _ => failure?.GetType().Name,
                    });
                    Assert.False(failure is OperationCanceledException);
                }
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
            if (stage == "Declare" && action == "cancel")
            {
                StaticWorkerSessionHandle reopened = await proxy.OpenPackageSessionAsync(PackageProjectSnapshotCodec.Encode(healthy), TimeSpan.FromSeconds(20));
                Assert.NotEqual(workerPid, proxy.ProcessId);
                WorkerMessage frame = await proxy.RenderPackageSessionAsync(new(reopened.SessionId, reopened.SourceSha256, 320, 180, 100_000, false, 1), TimeSpan.FromSeconds(20));
                Assert.NotEmpty(frame.Payload);
                await proxy.ClosePackageSessionAsync(reopened, 2, TimeSpan.FromSeconds(20)); await proxy.StopAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "verified-terminal.json"), System.Text.Json.JsonSerializer.Serialize(new
            { stage, action, nonce, hostPid = Environment.ProcessId, entered = true, observed, allObservedIdentitiesGone = true, parallelHealthyFrame = true, recoveredHealthyFrame = true }));
            output.WriteLine($"Actual {stage}/{action} pure-v2 runaway confirmed; Host={Environment.ProcessId}; drained={string.Join(',', observed.Select(value => value.ToString()))}; useful parallel Host operation and new Worker recovery passed.");
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

    private static async Task<int> WaitForWorkerPidAsync(Func<int?> readPid, Task active, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (readPid() is { } pid) return pid;
            if (active.IsCompleted)
            {
                await active;
                Assert.Fail("Actual author invocation finished before Worker PID became active.");
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Worker PID was not observed before the fixture deadline.");
    }

    private static async Task<OwnedProcessGroup> WaitForProcessGroupAsync(int pid, Task? active, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (OwnedProcessGroup.TryCapture(process) is { } group) return group;
            }
            catch (ArgumentException) { }
            if (active?.IsCompleted == true)
            {
                await active;
                Assert.Fail("Actual author invocation finished before Worker private process group became ready.");
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Worker private process group was not captured before the fixture deadline.");
    }

    private static async Task WaitForCpuAdvanceAsync(int pid, ulong baselineTicks, Task active, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (ReadCpuTicks(pid) >= baselineTicks + 10) return;
            if (active.IsCompleted)
            {
                await active;
                Assert.Fail("Actual author invocation finished before CPU runaway ticks advanced.");
            }
            await Task.Delay(15);
        }
        throw new TimeoutException("Worker CPU ticks did not advance before the fixture deadline.");
    }

    private static ulong ReadCpuTicks(int pid)
    {
        string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ulong.Parse(fields[11], CultureInfo.InvariantCulture) + ulong.Parse(fields[12], CultureInfo.InvariantCulture);
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
