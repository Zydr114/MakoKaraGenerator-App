using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

internal static class HostingTestPlatformHelpers
{
    internal static string ContractFfmpegPath => OperatingSystem.IsWindows() ? @"C:\usr\bin\ffmpeg.exe" : "/usr/bin/ffmpeg";
    internal static string ContractFfprobePath => OperatingSystem.IsWindows() ? @"C:\usr\bin\ffprobe.exe" : "/usr/bin/ffprobe";

    internal static string CrossPlatformRootedPath(string unixPath) => OperatingSystem.IsWindows()
        ? Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, unixPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)))
        : unixPath;

    internal static WorkerProcessOptions PythonOptions(string scriptPath, IEnumerable<string> arguments, TimeSpan? cancellationGracePeriod = null)
        => new(TestMediaToolPaths.Python, ["-u", scriptPath, .. arguments], cancellationGracePeriod ?? TimeSpan.FromSeconds(5));

    internal static bool IsSameProcess(int pid, long startIdentity)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited && process.StartTime.ToFileTimeUtc() == startIdentity;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[0] != "Z" && long.Parse(fields[19], CultureInfo.InvariantCulture) == startIdentity;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", linkPath, targetPath])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new IOException("无法启动 cmd.exe 创建目录联接。");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string stderr = process.StandardError.ReadToEnd();
            throw new IOException($"创建目录联接失败（退出码 {process.ExitCode}）：{stderr}");
        }
    }
}
