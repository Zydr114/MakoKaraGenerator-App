using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MakoKaraGenerator.Processes.Tests;

public sealed class LinuxProcessGuardTests
{
    [Fact]
    public async Task 独立子进程实际应用硬文件限额且测试宿主仍可写入()
    {
        if (!LinuxProcessGuards.IsSupported) return;
        string directory = CreateOwnedDirectory();
        try
        {
            string media = Path.Combine(directory, "guarded.bin");
            using Process process = StartChild("file-size", media);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            long bytes = new FileInfo(media).Length;
            Assert.Equal(64 * 1024 * 1024, bytes);
            Assert.NotEqual(66, process.ExitCode);
            // Only the child lowered RLIMIT_FSIZE; this write and final TRX
            // remain under the test host's original limits.
            await File.WriteAllTextAsync(Path.Combine(directory, "host-alive.txt"), "host limits unchanged");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task 原组长退出后仍回收实际FFmpeg且不抢无关Dotnet进程状态()
    {
        if (!LinuxProcessGuards.IsSupported) return;
        Assert.True(LinuxProcessGuards.TryEnableChildReaping());
        using Process unrelated = StartChild("independent");
        using Process parent = StartChild("group-parent");
        OwnedProcessGroup? group = null;
        try
        {
            string line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("No child identity.");
            int child = int.Parse(line, CultureInfo.InvariantCulture);
            group = OwnedProcessGroup.TryCapture(parent);
            Assert.NotNull(group);
            ProcessIdentity owned = Assert.Single(group.ObserveMembers(), value => value.ProcessId == child);
            Assert.Equal(parent.Id, owned.ProcessGroupId);
            Assert.Equal(0, TestNative.Kill(child, 19));
            await parent.StandardInput.WriteLineAsync("exit");
            await parent.StandardInput.FlushAsync();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(group.ObserveMembers(), value => value == owned);
            await group.KillAndDrainAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(group.ObserveMembers());
            await unrelated.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(7, unrelated.ExitCode);
        }
        finally
        {
            if (group is not null) await group.KillAndDrainAsync(TimeSpan.FromSeconds(3));
            else if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(); }
            if (!unrelated.HasExited) { unrelated.Kill(); await unrelated.WaitForExitAsync(); }
        }
    }

    private static Process StartChild(string mode, string? path = null)
    {
        ProcessStartInfo info = new("dotnet")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--owned-child"); info.ArgumentList.Add(mode);
        if (path is not null) info.ArgumentList.Add(path);
        return Process.Start(info) ?? throw new IOException("Owned test child failed to start.");
    }

    private static string CreateOwnedDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "src"))) current = current.Parent;
        if (current is null) throw new IOException("Repository root not found.");
        string path = Path.Combine(current.FullName, "artifacts", "backend-b4b-processes", "child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}

internal static partial class TestNative
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int signal);
}
