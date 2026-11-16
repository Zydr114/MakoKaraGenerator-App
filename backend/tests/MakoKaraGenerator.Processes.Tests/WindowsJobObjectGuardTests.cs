using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;

namespace MakoKaraGenerator.Processes.Tests;

public sealed class WindowsJobObjectGuardTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AtomicJobEnrollmentTracksGrandchildAndDrainsToZeroAfterLeaderExit()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;

        WindowsControlledProcess controlled = WindowsJobObjectGuard.StartProcess(ChildStartInfo("win-job-parent"));
        int grandchildPid = -1;
        try
        {
            using StreamReader stdout = new(controlled.StandardOutput);
            string line = await stdout.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new IOException("No grandchild PID reported.");
            grandchildPid = int.Parse(line, CultureInfo.InvariantCulture);

            IReadOnlyList<int> memberPids = controlled.Guard.ObserveProcessIds();
            Assert.Contains(controlled.Guard.LeaderProcessId, memberPids);
            Assert.Contains(grandchildPid, memberPids);
            Assert.True(controlled.Guard.ObserveActiveProcessCount() >= 2);
            controlled.Guard.VerifyIdentity();

            await using (StreamWriter stdin = new(controlled.StandardInput))
            {
                await stdin.WriteLineAsync("exit");
                await stdin.FlushAsync();
            }
            await controlled.ManagedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(controlled.Guard.TryGetLeaderExitCode(out int exitCode));
            Assert.Equal(0, exitCode);

            // Even after the leader exited, the grandchild remains inside the private Job Object.
            Assert.Contains(grandchildPid, controlled.Guard.ObserveProcessIds());

            await controlled.Guard.KillAndDrainAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, controlled.Guard.ObserveActiveProcessCount());
            Assert.Empty(controlled.Guard.ObserveProcessIds());
            Assert.False(IsProcessAlive(grandchildPid));
        }
        finally
        {
            await controlled.Guard.DisposeAsync();
            controlled.ManagedProcess.Dispose();
            if (grandchildPid > 0) Assert.False(IsProcessAlive(grandchildPid));
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task JobDisallowsBreakawayFromChildProcesses()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;

        WindowsControlledProcess controlled = WindowsJobObjectGuard.StartProcess(ChildStartInfo("win-breakaway-probe"));
        try
        {
            await controlled.ManagedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(controlled.Guard.TryGetLeaderExitCode(out int exitCode));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            await controlled.Guard.DisposeAsync();
            controlled.ManagedProcess.Dispose();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ConcurrentStartsIsolateStdioHandlesViaAttributeHandleList()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;

        List<WindowsControlledProcess> sleepers = [];
        try
        {
            // Keep multiple sleeping children alive with open stdio handles while
            // concurrently launching short-lived echo children; none may inherit each other's pipes.
            for (int i = 0; i < 4; i++)
            {
                sleepers.Add(WindowsJobObjectGuard.StartProcess(ChildStartInfo("win-sleeper")));
            }

            Task[] concurrentEchoes = Enumerable.Range(0, 8).Select(async index =>
            {
                string marker = "echo-" + index.ToString(CultureInfo.InvariantCulture);
                WindowsControlledProcess echo = WindowsJobObjectGuard.StartProcess(ChildStartInfo("win-pipe-echo", marker));
                try
                {
                    using StreamReader stdout = new(echo.StandardOutput);
                    string text = await stdout.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(marker, text.Trim());
                    await echo.ManagedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(echo.Guard.TryGetLeaderExitCode(out int exitCode));
                    Assert.Equal(0, exitCode);
                }
                finally
                {
                    await echo.Guard.KillAndDrainAsync(TimeSpan.FromSeconds(5));
                    await echo.Guard.DisposeAsync();
                    echo.ManagedProcess.Dispose();
                }
            }).ToArray();

            await Task.WhenAll(concurrentEchoes).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            foreach (WindowsControlledProcess sleeper in sleepers)
            {
                await sleeper.Guard.KillAndDrainAsync(TimeSpan.FromSeconds(5));
                await sleeper.Guard.DisposeAsync();
                sleeper.ManagedProcess.Dispose();
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposingGuardKillsOwnedProcessTreeOnJobClose()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;

        WindowsControlledProcess controlled = WindowsJobObjectGuard.StartProcess(ChildStartInfo("win-job-parent"));
        int grandchildPid = -1;
        try
        {
            using StreamReader stdout = new(controlled.StandardOutput);
            string line = await stdout.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new IOException("No grandchild PID reported.");
            grandchildPid = int.Parse(line, CultureInfo.InvariantCulture);

            int leaderPid = controlled.Guard.LeaderProcessId;
            await controlled.Guard.DisposeAsync();

            for (int i = 0; i < 100 && (IsProcessAlive(leaderPid) || IsProcessAlive(grandchildPid)); i++)
                await Task.Delay(20);
            Assert.False(IsProcessAlive(leaderPid));
            Assert.False(IsProcessAlive(grandchildPid));
        }
        finally
        {
            await controlled.Guard.DisposeAsync();
            controlled.ManagedProcess.Dispose();
        }
    }

    private static ProcessStartInfo ChildStartInfo(params string[] args)
    {
        ProcessStartInfo info = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--owned-child");
        foreach (string arg in args) info.ArgumentList.Add(arg);
        info.Environment["MAKO_JOB_GUARD_TEST_VAR"] = "verified";
        return info;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
