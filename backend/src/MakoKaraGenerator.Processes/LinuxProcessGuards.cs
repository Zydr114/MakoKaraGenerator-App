using System.Runtime.InteropServices;

namespace MakoKaraGenerator.Processes;

/// <summary>Linux x64 process isolation and inherited per-file write limits.</summary>
public static class LinuxProcessGuards
{
    /// <summary>Whether the native guard layout is supported on this host.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Create a private group before the Worker accepts input or starts children.</summary>
    public static bool InitializeCurrentProcessGroup()
    {
        if (!IsSupported) return false;
        int pid = Environment.ProcessId;
        return (Native.GetProcessGroup() == pid || Native.SetProcessGroup(0, 0) == 0) && Native.GetProcessGroup() == pid;
    }

    /// <summary>Set both RLIMIT_FSIZE limits; subsequently started FFmpeg processes inherit them.</summary>
    public static bool ApplyCurrentFileSizeLimit(long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!IsSupported || Native.GetResourceLimit(1, out Native.ResourceLimit previous) != 0
            || (ulong)maximumBytes > previous.Hard) return false;
        Native.ResourceLimit limit = new() { Soft = (ulong)maximumBytes, Hard = (ulong)maximumBytes };
        return Native.SetResourceLimit(1, in limit) == 0
            && Native.GetResourceLimit(1, out Native.ResourceLimit actual) == 0
            && actual.Soft == (ulong)maximumBytes && actual.Hard == (ulong)maximumBytes;
    }

    /// <summary>Allow this host to reap orphan descendants; callers only reap their registered private groups.</summary>
    public static bool TryEnableChildReaping() => IsSupported && Native.SetChildSubreaper(36, 1, 0, 0, 0) == 0;
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResourceLimit { internal ulong Soft; internal ulong Hard; }
    [LibraryImport("libc", EntryPoint = "getpgrp")]
    internal static partial int GetProcessGroup();
    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    internal static partial int SetProcessGroup(int pid, int group);
    [LibraryImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    internal static partial int GetResourceLimit(int resource, out ResourceLimit limit);
    [LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    internal static partial int SetResourceLimit(int resource, in ResourceLimit limit);
    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    internal static partial int SetChildSubreaper(int option, ulong value, ulong third, ulong fourth, ulong fifth);
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int signal);
    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitPid(int pid, out int status, int options);
}
