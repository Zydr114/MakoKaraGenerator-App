using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MakoKaraGenerator.Processes;

/// <summary>Auditable Windows process identity captured from a held <see cref="SafeProcessHandle"/>.</summary>
public readonly record struct WindowsProcessIdentity(int ProcessId, long CreationTimeFileTime);

/// <summary>Controlled Windows process launched atomically into a private Job Object.</summary>
public sealed class WindowsControlledProcess
{
    internal WindowsControlledProcess(
        WindowsJobObjectGuard guard,
        Process managedProcess,
        Stream standardInput,
        Stream standardOutput,
        StreamReader standardError)
    {
        Guard = guard;
        ManagedProcess = managedProcess;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>Private Windows Job Object guard owning the process tree.</summary>
    public WindowsJobObjectGuard Guard { get; }
    /// <summary>Managed <see cref="Process"/> handle bound to the live child PID.</summary>
    public Process ManagedProcess { get; }
    /// <summary>Parent write end of child standard input.</summary>
    public Stream StandardInput { get; }
    /// <summary>Parent read end of child standard output.</summary>
    public Stream StandardOutput { get; }
    /// <summary>Parent reader for child standard error.</summary>
    public StreamReader StandardError { get; }
}

/// <summary>
/// Windows x64 Job Object process guard using <c>CreateProcessW</c> with
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> and <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>
/// for pre-start atomic enrollment and concurrent pipe deadlock prevention.
/// </summary>
public sealed partial class WindowsJobObjectGuard : IWorkerProcessGuard
{
    private readonly SafeJobHandle _jobHandle;
    private readonly SafeProcessHandle _processHandle;
    private int _disposed;

    private WindowsJobObjectGuard(SafeJobHandle jobHandle, SafeProcessHandle processHandle, WindowsProcessIdentity leader)
    {
        _jobHandle = jobHandle;
        _processHandle = processHandle;
        Leader = leader;
    }

    /// <summary>Whether the Windows x64 Job Object guard is supported on this host.</summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Captured leader process identity.</summary>
    public WindowsProcessIdentity Leader { get; }

    /// <inheritdoc/>
    public int LeaderProcessId => Leader.ProcessId;

    /// <summary>Verifies that the current process is inside a Job Object with KILL_ON_JOB_CLOSE and without breakaway.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsCurrentProcessGuarded()
    {
        if (!IsSupported) return false;
        IntPtr current = Win32.GetCurrentProcess();
        if (!Win32.IsProcessInJob(current, IntPtr.Zero, out int inJob) || inJob == 0) return false;
        unsafe
        {
            Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
            if (!Win32.QueryInformationJobObject(IntPtr.Zero, Win32.JobObjectExtendedLimitInformation,
                &limits, (uint)sizeof(Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION), out _))
                return false;
            uint flags = limits.BasicLimitInformation.LimitFlags;
            return (flags & Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) != 0
                && (flags & (Win32.JOB_OBJECT_LIMIT_BREAKAWAY_OK | Win32.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK)) == 0;
        }
    }

    /// <summary>
    /// Starts a child process atomically enrolled in a new private Job Object with <c>KILL_ON_JOB_CLOSE</c>
    /// (and no breakaway), whitelisting only the 3 redirected stdio handles via <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static WindowsControlledProcess StartProcess(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.FileName);
        if (!IsSupported) throw new PlatformNotSupportedException("WindowsJobObjectGuard requires Windows x64.");

        SafeJobHandle? jobHandle = null;
        SafeProcessHandle? processHandle = null;
        SafeThreadHandle? threadHandle = null;
        SafeFileHandle? stdinRead = null, stdinWrite = null;
        SafeFileHandle? stdoutRead = null, stdoutWrite = null;
        SafeFileHandle? stderrRead = null, stderrWrite = null;
        Process? managedProcess = null;
        bool transferred = false;

        try
        {
            jobHandle = Win32.CreateJobObjectW(IntPtr.Zero, null);
            if (jobHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObjectW failed.");

            unsafe
            {
                Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
                limits.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!Win32.SetInformationJobObject(jobHandle, Win32.JobObjectExtendedLimitInformation,
                    &limits, (uint)sizeof(Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject failed.");
            }

            CreateNonInheritablePipe(out stdinRead, out stdinWrite);
            CreateNonInheritablePipe(out stdoutRead, out stdoutWrite);
            CreateNonInheritablePipe(out stderrRead, out stderrWrite);

            SetInheritable(stdinRead);
            SetInheritable(stdoutWrite);
            SetInheritable(stderrWrite);

            string commandLine = BuildCommandLine(startInfo);
            char[] envBlock = BuildUnicodeEnvironmentBlock(startInfo);
            string? workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;

            (processHandle, threadHandle, int pid) = CreateSuspendedProcess(jobHandle,
                [stdinRead, stdoutWrite, stderrWrite], null, commandLine, envBlock, workingDirectory);

            // Close child-side pipe ends in the parent immediately so EOF works reliably.
            stdinRead.Dispose(); stdinRead = null;
            stdoutWrite.Dispose(); stdoutWrite = null;
            stderrWrite.Dispose(); stderrWrite = null;

            bool procRefAdded = false, jobCheckRefAdded = false;
            int inJob;
            try
            {
                processHandle.DangerousAddRef(ref procRefAdded);
                jobHandle.DangerousAddRef(ref jobCheckRefAdded);
                if (!Win32.IsProcessInJob(processHandle.DangerousGetHandle(), jobHandle.DangerousGetHandle(), out inJob) || inJob == 0)
                    throw new IOException("Started process was not enrolled in the private Windows Job Object.");
            }
            finally
            {
                if (jobCheckRefAdded) jobHandle.DangerousRelease();
                if (procRefAdded) processHandle.DangerousRelease();
            }

            long creationTime = ReadCreationTimeFileTime(processHandle);
            managedProcess = Process.GetProcessById(pid);

            if (Win32.ResumeThread(threadHandle) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread failed.");
            threadHandle.Dispose(); threadHandle = null;

            FileStream stdinStream = new(stdinWrite, FileAccess.Write, 4096, isAsync: false);
            stdinWrite = null; // owned by stdinStream
            FileStream stdoutStream = new(stdoutRead, FileAccess.Read, 4096, isAsync: false);
            stdoutRead = null; // owned by stdoutStream
            FileStream stderrStream = new(stderrRead, FileAccess.Read, 4096, isAsync: false);
            stderrRead = null; // owned by stderrReader
            StreamReader stderrReader = new(stderrStream, Encoding.UTF8);

            WindowsJobObjectGuard guard = new(jobHandle, processHandle, new WindowsProcessIdentity(pid, creationTime));
            WindowsControlledProcess controlled = new(guard, managedProcess, stdinStream, stdoutStream, stderrReader);
            transferred = true;
            return controlled;
        }
        finally
        {
            if (!transferred)
            {
                managedProcess?.Dispose();
                stdinRead?.Dispose(); stdinWrite?.Dispose();
                stdoutRead?.Dispose(); stdoutWrite?.Dispose();
                stderrRead?.Dispose(); stderrWrite?.Dispose();
                threadHandle?.Dispose();
                if (jobHandle is { IsInvalid: false })
                {
                    _ = Win32.TerminateJobObject(jobHandle, 1);
                    jobHandle.Dispose();
                }
                processHandle?.Dispose();
            }
        }
    }

    /// <summary>Verifies that our held <see cref="SafeProcessHandle"/> still matches the recorded <see cref="Leader"/> identity.</summary>
    public void VerifyIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int actualPid = Win32.GetProcessId(_processHandle);
        if (actualPid != Leader.ProcessId)
            throw new IOException("Private Worker process identity PID mismatch.");
        long actualCreation = ReadCreationTimeFileTime(_processHandle);
        if (actualCreation != Leader.CreationTimeFileTime)
            throw new IOException("Private Worker process identity creation time mismatch.");
    }

    /// <summary>Queries the leader process exit code if it has already terminated.</summary>
    public bool TryGetLeaderExitCode(out int exitCode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Win32.GetExitCodeProcess(_processHandle, out uint raw))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed.");
        if (raw == Win32.STILL_ACTIVE && Win32.WaitForSingleObject(_processHandle, 0) != Win32.WAIT_OBJECT_0)
        {
            exitCode = 0;
            return false;
        }
        exitCode = unchecked((int)raw);
        return true;
    }

    /// <summary>Queries the number of currently active processes inside this Job Object.</summary>
    public int ObserveActiveProcessCount()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        unsafe
        {
            Win32.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info = default;
            if (!Win32.QueryInformationJobObject(_jobHandle, Win32.JobObjectBasicAccountingInformation,
                &info, (uint)sizeof(Win32.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject(Accounting) failed.");
            return checked((int)info.ActiveProcesses);
        }
    }

    /// <summary>Queries the live PIDs currently inside this Job Object.</summary>
    public IReadOnlyList<int> ObserveProcessIds()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int capacity = 64;
        while (true)
        {
            uint byteLength = checked((uint)(16 + capacity * IntPtr.Size));
            IntPtr buffer = Marshal.AllocHGlobal((nint)byteLength);
            try
            {
                if (!Win32.QueryInformationJobObject(_jobHandle, Win32.JobObjectBasicProcessIdList,
                    buffer, byteLength, out _))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == Win32.ERROR_MORE_DATA)
                    {
                        capacity *= 2;
                        continue;
                    }
                    throw new Win32Exception(error, "QueryInformationJobObject(ProcessIdList) failed.");
                }
                int assigned = Marshal.ReadInt32(buffer, 0);
                int returned = Marshal.ReadInt32(buffer, 4);
                if (assigned > returned && returned == capacity)
                {
                    capacity = assigned + 16;
                    continue;
                }
                int[] pids = new int[returned];
                int offset = 8;
                for (int i = 0; i < returned; i++)
                {
                    pids[i] = checked((int)(nint)Marshal.ReadIntPtr(buffer, offset + i * IntPtr.Size));
                }
                return pids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <inheritdoc/>
    public async Task KillAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        VerifyIdentity();
        if (!Win32.TerminateJobObject(_jobHandle, 1))
            throw new IOException("Private Windows Job Object termination failed.", new Win32Exception(Marshal.GetLastPInvokeError()));

        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint leaderWait = Win32.WaitForSingleObject(_processHandle, 0);
            int active = ObserveActiveProcessCount();
            if (leaderWait == Win32.WAIT_OBJECT_0 && active == 0) return;
            if (watch.Elapsed >= timeout)
                throw new IOException("Private Windows Job Object descendants did not drain before the deadline.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _jobHandle.Dispose();
        _processHandle.Dispose();
        return ValueTask.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    internal static long ReadCreationTimeFileTime(SafeProcessHandle handle)
    {
        if (!Win32.GetProcessTimes(handle, out long creationTime, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetProcessTimes failed.");
        return creationTime;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateNonInheritablePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle)
    {
        Win32.SECURITY_ATTRIBUTES security = new()
        {
            nLength = (uint)Marshal.SizeOf<Win32.SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 0,
        };
        if (!Win32.CreatePipe(out readHandle, out writeHandle, ref security, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed.");
    }

    [SupportedOSPlatform("windows")]
    internal static void SetInheritable(SafeFileHandle handle)
    {
        if (!Win32.SetHandleInformation(handle, Win32.HANDLE_FLAG_INHERIT, Win32.HANDLE_FLAG_INHERIT))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed.");
    }

    internal static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        StringBuilder sb = new();
        AppendArgument(sb, startInfo.FileName);
        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (string arg in startInfo.ArgumentList)
            {
                sb.Append(' ');
                AppendArgument(sb, arg);
            }
        }
        else if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            sb.Append(' ');
            sb.Append(startInfo.Arguments);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (argument.Length != 0 && !NeedsQuoting(argument))
        {
            sb.Append(argument);
            return;
        }
        sb.Append('"');
        int index = 0;
        while (index < argument.Length)
        {
            int backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }
            if (index == argument.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (argument[index] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(argument[index]);
            }
            index++;
        }
        sb.Append('"');
    }

    private static bool NeedsQuoting(string argument)
    {
        foreach (char ch in argument)
        {
            if (ch is ' ' or '\t' or '\n' or '\v' or '"') return true;
        }
        return false;
    }

    private static char[] BuildUnicodeEnvironmentBlock(ProcessStartInfo startInfo)
    {
        List<KeyValuePair<string, string>> entries = [];
        foreach (KeyValuePair<string, string?> pair in startInfo.Environment)
        {
            if (!string.IsNullOrEmpty(pair.Key) && pair.Value is not null)
                entries.Add(new(pair.Key, pair.Value));
        }
        entries.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        StringBuilder sb = new();
        foreach ((string key, string value) in entries)
        {
            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
            sb.Append('\0');
        }
        sb.Append('\0');
        if (sb.Length == 1) sb.Append('\0');
        char[] chars = new char[sb.Length];
        sb.CopyTo(0, chars, 0, sb.Length);
        return chars;
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => Win32.CloseHandle(handle);
    }

    internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle() : base(ownsHandle: true) { }
        public SafeThreadHandle(IntPtr preexistingHandle, bool ownsHandle) : base(ownsHandle)
            => SetHandle(preexistingHandle);
        protected override bool ReleaseHandle() => Win32.CloseHandle(handle);
    }

    internal static partial class Win32
    {
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        internal const uint CREATE_NO_WINDOW = 0x08000000;
        internal const uint CREATE_SUSPENDED = 0x00000004;
        internal const nuint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
        internal const nuint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;
        internal const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        internal const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        internal const uint HANDLE_FLAG_INHERIT = 0x00000001;
        internal const uint STARTF_USESHOWWINDOW = 0x00000001;
        internal const uint STARTF_USESTDHANDLES = 0x00000100;
        internal const ushort SW_HIDE = 0;
        internal const int JobObjectBasicAccountingInformation = 1;
        internal const int JobObjectBasicProcessIdList = 3;
        internal const int JobObjectExtendedLimitInformation = 9;
        internal const int ERROR_MORE_DATA = 234;
        internal const uint WAIT_OBJECT_0 = 0;
        internal const uint STILL_ACTIVE = 259;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SECURITY_ATTRIBUTES
        {
            internal uint nLength;
            internal IntPtr lpSecurityDescriptor;
            internal int bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOW
        {
            internal uint cb;
            internal IntPtr lpReserved;
            internal IntPtr lpDesktop;
            internal IntPtr lpTitle;
            internal uint dwX;
            internal uint dwY;
            internal uint dwXSize;
            internal uint dwYSize;
            internal uint dwXCountChars;
            internal uint dwYCountChars;
            internal uint dwFillAttribute;
            internal uint dwFlags;
            internal ushort wShowWindow;
            internal ushort cbReserved2;
            internal IntPtr lpReserved2;
            internal IntPtr hStdInput;
            internal IntPtr hStdOutput;
            internal IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOEXW
        {
            internal STARTUPINFOW StartupInfo;
            internal IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            internal IntPtr hProcess;
            internal IntPtr hThread;
            internal uint dwProcessId;
            internal uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize;
            internal nuint MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            internal JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            internal IO_COUNTERS IoInfo;
            internal nuint ProcessMemoryLimit;
            internal nuint JobMemoryLimit;
            internal nuint PeakProcessMemoryUsed;
            internal nuint PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            internal long TotalUserTime;
            internal long TotalKernelTime;
            internal long ThisPeriodTotalUserTime;
            internal long ThisPeriodTotalKernelTime;
            internal uint TotalPageFaultCount;
            internal uint TotalProcesses;
            internal uint ActiveProcesses;
            internal uint TotalTerminatedProcesses;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out int result);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool SetInformationJobObject(SafeJobHandle hJob, int jobObjectInfoClass, void* lpJobObjectInfo, uint cbJobObjectInfoLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool QueryInformationJobObject(SafeJobHandle hJob, int jobObjectInfoClass, void* lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool QueryInformationJobObject(IntPtr hJob, int jobObjectInfoClass, void* lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryInformationJobObject(SafeJobHandle hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref nuint lpSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, nuint attribute, IntPtr lpValue, nuint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [LibraryImport("kernel32.dll")]
        internal static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool CreateProcessW(
            string? lpApplicationName,
            char* lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            int bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            char* lpCurrentDirectory,
            ref STARTUPINFOEXW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint ResumeThread(SafeThreadHandle hThread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int GetProcessId(SafeProcessHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr hObject);
    }
}
