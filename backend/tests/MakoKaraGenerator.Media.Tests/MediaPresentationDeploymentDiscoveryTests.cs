using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary><see cref="MediaPresentationDeploymentDiscovery"/> 的取消与有界读取测试。</summary>
public sealed class MediaPresentationDeploymentDiscoveryTests
{
    [Fact]
    public async Task ReadLoaderClosure取消时立即终止进程树且不泄漏子进程()
    {
        if (!OperatingSystem.IsLinux()) return;

        using TemporaryDirectory directory = new();
        string pidFile = directory.FilePath("loader.pid");
        string script = CreateExecutableScript(directory, "slow-ldd", $$"""
            import os, subprocess, sys, time
            child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
            with open({{JsonSerializer.Serialize(pidFile)}}, "w", encoding="utf-8") as handle:
                handle.write(f"{os.getpid()}\n{child.pid}\n")
            time.sleep(30)
            """);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource cancel = new();
        Task<string[]> task = MediaPresentationDeploymentDiscovery.ReadLoaderClosureAsync(
            "/bin/true",
            cancel.Token,
            loaderExecutable: script,
            timeout: TimeSpan.FromSeconds(30));

        while (!File.Exists(pidFile) || File.ReadAllLines(pidFile).Length < 2)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, deadline.Token);
        }

        int[] pids = File.ReadAllLines(pidFile)
            .Select(line => int.Parse(line, CultureInfo.InvariantCulture))
            .ToArray();

        cancel.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);

        foreach (int pid in pids)
        {
            await WaitUntilProcessGoneAsync(pid, TimeSpan.FromSeconds(2), deadline.Token);
            Assert.False(IsProcessAlive(pid), $"取消后仍残留进程 {pid}。");
        }
    }

    private static async Task WaitUntilProcessGoneAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(timeout);
        while (IsProcessAlive(pid) && !waitCts.IsCancellationRequested)
        {
            try { await Task.Delay(20, waitCts.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
        }
    }

    [Fact]
    public async Task ReadLoaderClosure遇到超大输出时按上限拒绝并终止进程()
    {
        if (!OperatingSystem.IsLinux()) return;

        using TemporaryDirectory directory = new();
        string script = CreateExecutableScript(directory, "flood-ldd", """
            import sys
            chunk = "\tliba.so => /usr/lib/liba.so (0x00001000)\n" * 64
            while True:
                sys.stdout.write(chunk)
                sys.stdout.flush()
            """);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        MediaAbException error = await Assert.ThrowsAsync<MediaAbException>(
            () => MediaPresentationDeploymentDiscovery.ReadLoaderClosureAsync(
                "/bin/true",
                deadline.Token,
                loaderExecutable: script,
                timeout: TimeSpan.FromSeconds(20),
                maxOutputCharacters: 1024).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));

        Assert.Equal(MediaAbFailure.ProfileUnsupported, error.Failure);
        Assert.Contains("size limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateExecutableScript(TemporaryDirectory directory, string name, string pythonBody)
    {
        string path = directory.FilePath(name);
        File.WriteAllText(path, "#!/usr/bin/env python3\n" + pythonBody.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
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
