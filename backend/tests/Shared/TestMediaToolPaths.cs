using System.Collections.Concurrent;

namespace MakoKaraGenerator.TestSupport;

internal static class TestMediaToolPaths
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    internal static string Ffmpeg => ResolveMediaTool("MAKO_BACKEND_FFMPEG_PATH", "ffmpeg");
    internal static string Ffprobe => ResolveMediaTool("MAKO_BACKEND_FFPROBE_PATH", "ffprobe");
    internal static string Python => Cache.GetOrAdd("python", static _ => ResolvePython());

    internal static string ResolveMediaTool(string envVariable, string toolName)
        => Cache.GetOrAdd(envVariable + ":" + toolName, _ => ResolveMediaToolCore(envVariable, toolName));

    private static string ResolveMediaToolCore(string envVariable, string toolName)
    {
        string? configured = Environment.GetEnvironmentVariable(envVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            return configured;

        string fileName = toolName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

        if (TryFindInPath(Environment.GetEnvironmentVariable("PATH"), fileName, skipWindowsApps: false, out string? inProcessPath))
            return inProcessPath!;

        if (OperatingSystem.IsWindows())
        {
            if (TryFindInPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User), fileName, skipWindowsApps: false, out string? inUserPath))
                return inUserPath!;

            if (TryFindInPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine), fileName, skipWindowsApps: false, out string? inMachinePath))
                return inMachinePath!;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                string winGetLinks = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", fileName);
                if (File.Exists(winGetLinks)) return winGetLinks;

                string winGetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(winGetPackages))
                {
                    foreach (string packageDir in Directory.EnumerateDirectories(winGetPackages, "Gyan.FFmpeg*"))
                    {
                        foreach (string candidate in Directory.EnumerateFiles(packageDir, fileName, SearchOption.AllDirectories))
                            return candidate;
                    }
                }
            }
        }
        else
        {
            string unixDefault = Path.Combine("/usr/bin", fileName);
            if (File.Exists(unixDefault)) return unixDefault;
        }

        throw new FileNotFoundException($"Unable to locate required test tool '{fileName}'.", fileName);
    }

    private static string ResolvePython()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/usr/bin/python3") ? "/usr/bin/python3" : "python3";
        }

        if (TryFindInPath(Environment.GetEnvironmentVariable("PATH"), "python.exe", skipWindowsApps: true, out string? inPath))
            return inPath!;
        if (TryFindInPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User), "python.exe", skipWindowsApps: true, out string? inUserPath))
            return inUserPath!;
        if (TryFindInPath(Environment.GetEnvironmentVariable("PATH"), "python3.exe", skipWindowsApps: true, out string? inPath3))
            return inPath3!;

        return "python";
    }

    private static bool TryFindInPath(string? pathValue, string fileName, bool skipWindowsApps, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(pathValue)) return false;

        foreach (string raw in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = raw.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            if (skipWindowsApps && directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                resolved = candidate;
                return true;
            }
        }
        return false;
    }
}
