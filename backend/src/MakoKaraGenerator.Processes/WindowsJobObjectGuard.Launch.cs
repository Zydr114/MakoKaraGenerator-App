using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MakoKaraGenerator.Processes;

public sealed partial class WindowsJobObjectGuard
{
    [SupportedOSPlatform("windows")]
    internal static unsafe (SafeProcessHandle Process, SafeThreadHandle Thread, int Pid) CreateSuspendedProcess(
        SafeJobHandle job, ReadOnlySpan<SafeFileHandle> inherited, string? executable, string commandLine,
        char[] environment, string? workingDirectory)
    {
        if (inherited.Length is not (3 or 4)) throw new ArgumentException("Expected three stdio handles and optional metadata.", nameof(inherited));
        nuint size = 0;
        Win32.InitializeProcThreadAttributeList(0, 2, 0, ref size);
        if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint attributes = Marshal.AllocHGlobal((nint)size);
        bool initialized = false, jobHeld = false;
        int held = 0;
        try
        {
            if (!Win32.InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            initialized = true;
            job.DangerousAddRef(ref jobHeld);
            nint jobValue = job.DangerousGetHandle();
            if (!Win32.UpdateProcThreadAttribute(attributes, 0, Win32.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                (nint)(&jobValue), (nuint)sizeof(nint), 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            nint* handles = stackalloc nint[inherited.Length];
            foreach (SafeFileHandle child in inherited)
            {
                bool referenced = false;
                child.DangerousAddRef(ref referenced);
                handles[held++] = child.DangerousGetHandle();
            }
            if (!Win32.UpdateProcThreadAttribute(attributes, 0, Win32.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                (nint)handles, (nuint)(inherited.Length * sizeof(nint)), 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            Win32.STARTUPINFOEXW startup = default;
            startup.StartupInfo.cb = (uint)sizeof(Win32.STARTUPINFOEXW);
            startup.StartupInfo.dwFlags = Win32.STARTF_USESTDHANDLES | Win32.STARTF_USESHOWWINDOW;
            startup.StartupInfo.wShowWindow = Win32.SW_HIDE;
            startup.StartupInfo.hStdInput = handles[0];
            startup.StartupInfo.hStdOutput = handles[1];
            startup.StartupInfo.hStdError = handles[2];
            startup.lpAttributeList = attributes;
            char[] cmdBuffer = new char[commandLine.Length + 1];
            commandLine.CopyTo(0, cmdBuffer, 0, commandLine.Length);
            fixed (char* cmd = cmdBuffer, env = environment, cwd = workingDirectory)
            {
                const uint flags = Win32.EXTENDED_STARTUPINFO_PRESENT | Win32.CREATE_UNICODE_ENVIRONMENT
                    | Win32.CREATE_NO_WINDOW | Win32.CREATE_SUSPENDED;
                if (!Win32.CreateProcessW(executable, cmd, 0, 0, 1, flags, (nint)env, cwd, ref startup, out var result))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                return (new(result.hProcess, ownsHandle: true), new(result.hThread, ownsHandle: true), unchecked((int)result.dwProcessId));
            }
        }
        finally
        {
            if (initialized) Win32.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            while (held > 0) inherited[--held].DangerousRelease();
            if (jobHeld) job.DangerousRelease();
        }
    }
}
