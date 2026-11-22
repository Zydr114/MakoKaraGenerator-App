using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class BackendServerControlTests
{
    [Fact]
    public void ResolveTool按环境变量PATH及WindowsExe扩展正确解析()
    {
        MethodInfo resolve = typeof(PreviewTransportTests).Assembly.GetType("MakoKaraGenerator.Backend.Server.BackendApplicationHost", false) is null
            ? typeof(LoopbackSecurity).Assembly.GetType("MakoKaraGenerator.Backend.Server.ExportWorkerConfiguration", throwOnError: true)!
                .GetMethod("ResolveTool", BindingFlags.Static | BindingFlags.NonPublic)!
            : throw new InvalidOperationException();

        string? originalFfmpeg = Environment.GetEnvironmentVariable("MAKO_BACKEND_FFMPEG_PATH");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        using TemporaryDirectory temp = new();
        try
        {
            string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            string explicitDir = temp.FilePath("explicit");
            Directory.CreateDirectory(explicitDir);
            string explicitTool = Path.Combine(explicitDir, exeName);
            File.WriteAllBytes(explicitTool, [1]);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(explicitTool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Environment.SetEnvironmentVariable("MAKO_BACKEND_FFMPEG_PATH", explicitTool);
            Assert.Equal(explicitTool, (string?)resolve.Invoke(null, ["MAKO_BACKEND_FFMPEG_PATH", "ffmpeg"]));

            Environment.SetEnvironmentVariable("MAKO_BACKEND_FFMPEG_PATH", Path.Combine(explicitDir, "missing.exe"));
            Assert.Null((string?)resolve.Invoke(null, ["MAKO_BACKEND_FFMPEG_PATH", "ffmpeg"]));

            Environment.SetEnvironmentVariable("MAKO_BACKEND_FFMPEG_PATH", null);
            string pathDir = temp.FilePath("path-bin");
            Directory.CreateDirectory(pathDir);
            string pathTool = Path.Combine(pathDir, exeName);
            File.WriteAllBytes(pathTool, [1]);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(pathTool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", pathDir);
            Assert.Equal(pathTool, (string?)resolve.Invoke(null, ["MAKO_BACKEND_FFMPEG_PATH", "ffmpeg"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAKO_BACKEND_FFMPEG_PATH", originalFfmpeg);
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 启用Stdin受控关闭协议后SHUTDOWN或EOF优雅停止服务(bool closeStdinDirectly)
    {
        string serverDll = WorkerBinaryLocator.LocateBackendServerDll();
        Assert.True(File.Exists(serverDll), serverDll);

        using TemporaryDirectory root = new();
        string projects = Path.Combine(root.Path, "projects");
        Directory.CreateDirectory(projects);
        string bootstrap = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root.Path,
        };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("MAKO_BACKEND_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.ArgumentList.Add(serverDll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["MAKO_BACKEND_RESOURCE_ROOT"] = Path.Combine(root.Path, "resources");
        start.Environment["MAKO_BACKEND_STATE_ROOT"] = Path.Combine(root.Path, "state");
        start.Environment["MAKO_BACKEND_ALLOWED_ROOTS"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["ctrl"] = projects });
        start.Environment["MAKO_BACKEND_BOOTSTRAP_TOKEN"] = bootstrap;
        start.Environment["MAKO_BACKEND_SHUTDOWN_STDIN"] = "1";
        start.Environment["MAKO_BACKEND_FFMPEG_PATH"] = TestMediaToolPaths.Ffmpeg;
        start.Environment["MAKO_BACKEND_FFPROBE_PATH"] = TestMediaToolPaths.Ffprobe;

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Backend.Server。");
        TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> lines = [];
        Task stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                lock (lines) lines.Add(line);
                if (line.StartsWith("BACKEND_READY ", StringComparison.Ordinal))
                    ready.TrySetResult(line);
            }
            ready.TrySetException(new InvalidOperationException("Backend.Server exited before BACKEND_READY."));
        });
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            string readyLine = await ready.Task.WaitAsync(deadline.Token);
            Assert.StartsWith("BACKEND_READY http://127.0.0.1:", readyLine, StringComparison.Ordinal);

            if (closeStdinDirectly)
            {
                process.StandardInput.Close();
            }
            else
            {
                await process.StandardInput.WriteLineAsync("SHUTDOWN");
                await process.StandardInput.FlushAsync(deadline.Token);
            }

            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            await stdoutTask.WaitAsync(deadline.Token);
            _ = await stderrTask.WaitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
