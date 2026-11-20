using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MakoKaraGenerator.Processes.Tests;

internal static partial class Program
{
    private static readonly string[] FfmpegArguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi",
        "-i", "color=c=black:s=16x16:r=1", "-t", "3", "-f", "null", "-"];

    internal static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "--owned-child") return 64;
        switch (args[1])
        {
            case "file-size" when args.Length == 3:
                const long maximum = 64 * 1024 * 1024;
                if (!LinuxProcessGuards.InitializeCurrentProcessGroup() || !LinuxProcessGuards.ApplyCurrentFileSizeLimit(maximum)) return 65;
                try
                {
                    using FileStream output = new(args[2], FileMode.CreateNew, FileAccess.Write);
                    byte[] buffer = new byte[1024 * 1024];
                    for (int index = 0; index < 65; index++) output.Write(buffer);
                    return 66;
                }
                catch (IOException)
                {
                    Console.WriteLine("guard-rejected-write");
                    return 0;
                }
            case "group-parent" when args.Length == 2:
                if (!LinuxProcessGuards.InitializeCurrentProcessGroup()) return 65;
                using (Process child = StartFfmpeg())
                {
                    Console.WriteLine(child.Id);
                    // The test stops this actual private FFmpeg and asks the
                    // leader to exit, reproducing a root-gone live descendant.
                    if (await Console.In.ReadLineAsync() != "exit") return 64;
                    return 0;
                }
            case "independent" when args.Length == 2:
                await Task.Delay(1000);
                return 7;
            case "win-job-parent" when args.Length == 2:
                if (!OperatingSystem.IsWindows()) return 65;
                if (!WindowsJobObjectGuard.IsCurrentProcessGuarded()) return 65;
                using (Process grandchild = StartSleeperChild())
                {
                    Console.WriteLine(grandchild.Id);
                    Console.Out.Flush();
                    string? line = await Console.In.ReadLineAsync();
                    if (line == "exit") return 0;
                    return 64;
                }
            case "win-breakaway-probe" when args.Length == 2:
                if (!OperatingSystem.IsWindows()) return 65;
                return AttemptBreakawayChild() ? 66 : 0;
            case "win-pipe-echo" when args.Length == 3:
                Console.WriteLine(args[2]);
                Console.Out.Flush();
                return 0;
            case "win-sleeper" when args.Length == 2:
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "win-media" when OperatingSystem.IsWindows():
                return await RunMediaChildAsync(args[2..]);
            default:
                return 64;
        }
    }

    private static Process StartFfmpeg()
    {
        ProcessStartInfo info = new("ffmpeg") { UseShellExecute = false };
        foreach (string argument in FfmpegArguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Owned FFmpeg failed to start.");
    }

    private static Process StartSleeperChild()
    {
        ProcessStartInfo info = new("dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--owned-child");
        info.ArgumentList.Add("win-sleeper");
        return Process.Start(info) ?? throw new IOException("Owned sleeper child failed to start.");
    }

    [SupportedOSPlatform("windows")]
    private static bool AttemptBreakawayChild()
    {
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        const uint CREATE_NO_WINDOW = 0x08000000;
        string cmd = $"dotnet \"{Assembly.GetExecutingAssembly().Location}\" --owned-child win-sleeper";
        char[] buf = (cmd + '\0').ToCharArray();
        Win32Breakaway.STARTUPINFOW si = new() { cb = (uint)Marshal.SizeOf<Win32Breakaway.STARTUPINFOW>() };
        unsafe
        {
            fixed (char* pCmd = buf)
            {
                if (!Win32Breakaway.CreateProcessW(null, pCmd, IntPtr.Zero, IntPtr.Zero, 0,
                    CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out Win32Breakaway.PROCESS_INFORMATION pi))
                {
                    return false; // Rejected as expected because JOB_OBJECT_LIMIT_BREAKAWAY_OK is not set.
                }
                try
                {
                    Win32Breakaway.TerminateProcess(pi.hProcess, 1);
                    return true;
                }
                finally
                {
                    Win32Breakaway.CloseHandle(pi.hThread);
                    Win32Breakaway.CloseHandle(pi.hProcess);
                }
            }
        }
    }

    private static partial class Win32Breakaway
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOW
        {
            internal uint cb; internal IntPtr lpReserved; internal IntPtr lpDesktop; internal IntPtr lpTitle;
            internal uint dwX; internal uint dwY; internal uint dwXSize; internal uint dwYSize;
            internal uint dwXCountChars; internal uint dwYCountChars; internal uint dwFillAttribute; internal uint dwFlags;
            internal ushort wShowWindow; internal ushort cbReserved2; internal IntPtr lpReserved2;
            internal IntPtr hStdInput; internal IntPtr hStdOutput; internal IntPtr hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        { internal IntPtr hProcess; internal IntPtr hThread; internal uint dwProcessId; internal uint dwThreadId; }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool CreateProcessW(string? lpApplicationName, char* lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, int bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr hObject);
    }
}
