using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

internal static class RuntimeTestPlatformHelpers
{
    internal static long GetStartIdentityUtc(int pid)
    {
        using Process process = Process.GetProcessById(pid);
        return process.StartTime.ToFileTimeUtc();
    }

    internal static bool IsProcessAlive(int pid, long? startIdentityUtc = null)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)))
                return false;
            if (startIdentityUtc is null) return true;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            return startIdentityUtc is null || process.StartTime.ToFileTimeUtc() == startIdentityUtc.Value;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static ulong ReadCpuTicks(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return ulong.Parse(fields[11], CultureInfo.InvariantCulture) + ulong.Parse(fields[12], CultureInfo.InvariantCulture);
        }

        using Process process = Process.GetProcessById(pid);
        return (ulong)(process.TotalProcessorTime.Ticks / 100_000L);
    }

    internal static bool IdentityStillExists(ProcessIdentity identity)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string stat = File.ReadAllText("/proc/" + identity.ProcessId.ToString(CultureInfo.InvariantCulture) + "/stat");
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return ulong.Parse(fields[19], CultureInfo.InvariantCulture) == identity.StartTicks
                    && int.Parse(fields[2], CultureInfo.InvariantCulture) == identity.ProcessGroupId;
            }
            catch (IOException)
            {
                return false;
            }
        }

        return IsProcessAlive(identity.ProcessId, checked((long)identity.StartTicks));
    }
}
