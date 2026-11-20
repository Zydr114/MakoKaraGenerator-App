using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using JobNative = MakoKaraGenerator.Processes.WindowsJobObjectGuard.Win32;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Memory limit packets received from the envelope's private completion port.</summary>
[Flags]
public enum WindowsJobMemoryNotifications
{
    /// <summary>No limit packet was observed; this does not prove that all allocations succeeded.</summary>
    None = 0,
    /// <summary>JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT (9).</summary>
    ProcessMemoryLimit = 1,
    /// <summary>JOB_OBJECT_MSG_JOB_MEMORY_LIMIT (10).</summary>
    JobMemoryLimit = 2,
}

/// <summary>Path-free Windows Job Object diagnostics. Peak commit is never limit-hit evidence.</summary>
/// <param name="Notifications">Observed memory-limit packets only.</param>
/// <param name="PeakProcessCommitBytes">Largest observed process commit in the Job Object.</param>
/// <param name="PeakJobCommitBytes">Peak aggregate Job Object commit.</param>
public readonly record struct WindowsJobMemoryDiagnostics(WindowsJobMemoryNotifications Notifications,
    ulong PeakProcessCommitBytes, ulong PeakJobCommitBytes);

/// <summary>Windows x64 media backend using a 128 MiB private Job Object and four binary pipes.</summary>
/// <remarks>Use <see cref="WindowsJobObjectGuard.IsSupported"/> for the platform check. Admission verifies actual controls.
/// Windows Job Object memory notifications are best-effort; absence is not proof that a commit was never denied.</remarks>
public sealed partial class WindowsJobEnvelopeBackend : IEnvelopeBackend
{
    private const ulong MemoryLimit = 128 * 1024 * 1024;
    private const uint ProcessMemoryLimit = 0x100;
    private const uint JobMemoryLimit = 0x200;
    private const uint RequiredFlags = ProcessMemoryLimit | JobMemoryLimit | JobNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    private const nuint CompletionKey = 1;

    /// <inheritdoc />
    public async ValueTask<IEnvelopeSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsJobObjectGuard.IsSupported)
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-platform-unavailable");

        Session? session = null;
        try
        {
            session = new Session();
            await session.OpenPipesAsync(cancellationToken).ConfigureAwait(false);
            IEnvelopeSession result = session;
            session = null;
            return result;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or NotSupportedException
            or EntryPointNotFoundException or DllNotFoundException)
        { throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-transport-unavailable"); }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class Session : EnvelopeRunOwner, IEnvelopeSession, IHandleEnvelopeSession
    {
        private readonly OwnedPipe[] _pipes = new OwnedPipe[4];
        private WindowsJobObjectGuard.SafeJobHandle? _job;
        private SafeFileHandle? _port;
        private SafeProcessHandle? _process;
        private WindowsJobObjectGuard.SafeThreadHandle? _thread;
        private int _launchedPid;
        private WindowsProcessIdentity? _identity;
        private WindowsJobMemoryNotifications _notifications;
        private long _progress;
        private bool _admitted, _drained, _disposed;

        public Stream Input => _pipes[0];
        public Stream Output => _pipes[1];
        public Stream Diagnostics => _pipes[2];
        public Stream Metadata => _pipes[3];
        public long PipeBackingBytes { get; private set; }
        public long ProgressVersion => Interlocked.Read(ref _progress);
        public bool StartAttempted { get; private set; }

        internal async ValueTask OpenPipesAsync(CancellationToken token)
        {
            for (int index = 0; index < _pipes.Length; index++)
            {
                OwnedPipe pipe = await OwnedPipe.OpenAsync(index == 0, () => Interlocked.Increment(ref _progress), token)
                    .ConfigureAwait(false);
                _pipes[index] = pipe;
                PipeBackingBytes += pipe.Quota;
            }
        }

        public ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (StartAttempted) throw new InvalidOperationException("The Windows media envelope is single use.");
            start.Command.Validate();
            if (start.NativeLimitBytes != MemoryLimit || !string.Equals(start.Command.GateExecutable,
                start.Command.Executable, StringComparison.Ordinal))
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-profile-unavailable");
            StartAttempted = true;

            try
            {
                CreateControls();
                var startInfo = new ProcessStartInfo(start.Command.Executable);
                foreach (string argument in start.Command.Arguments) startInfo.ArgumentList.Add(argument);
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!Path.IsPathFullyQualified(windows))
                    throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-system-root-unavailable");
                string metadata = unchecked((nuint)_pipes[3].Child.DangerousGetHandle()).ToString(CultureInfo.InvariantCulture);
                char[] environment = $"MAKO_MEDIA_METADATA_HANDLE={metadata}\0SystemRoot={windows}\0\0".ToCharArray();
                string commandLine = WindowsJobObjectGuard.BuildCommandLine(startInfo);
                string? workingDirectory = Path.GetDirectoryName(start.Command.Executable);
                cancellationToken.ThrowIfCancellationRequested();
                SafeFileHandle[] inherited = [_pipes[0].Child, _pipes[1].Child, _pipes[2].Child, _pipes[3].Child];
                foreach (SafeFileHandle handle in inherited) WindowsJobObjectGuard.SetInheritable(handle);
                (_process, _thread, _launchedPid) = WindowsJobObjectGuard.CreateSuspendedProcess(_job!, inherited,
                    start.Command.Executable, commandLine, environment, workingDirectory);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
            { throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-held-start-failed"); }
            finally
            {
                foreach (OwnedPipe pipe in _pipes) pipe.CloseChild();
            }
            return ValueTask.CompletedTask;
        }

        private unsafe void CreateControls()
        {
            _job = JobNative.CreateJobObjectW(0, null);
            _port = WindowsNative.CreateIoCompletionPort(-1, 0, 0, 1);
            if (_job.IsInvalid || _port.IsInvalid)
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-job-or-port-unavailable");

            JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
            limits.BasicLimitInformation.LimitFlags = RequiredFlags;
            limits.ProcessMemoryLimit = (nuint)MemoryLimit;
            limits.JobMemoryLimit = (nuint)MemoryLimit;
            if (!JobNative.SetInformationJobObject(_job, JobNative.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-job-limits-unavailable");

            WindowsNative.CompletionPortAssociation association = new(CompletionKey, _port.DangerousGetHandle());
            if (!JobNative.SetInformationJobObject(_job, WindowsNative.AssociateCompletionPortInformation, &association,
                (uint)sizeof(WindowsNative.CompletionPortAssociation)))
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-port-association-failed");
        }

        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_process is null);
        }

        public EnvelopeRunOwner CaptureOwner(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_process is null || _job is null || _thread is null)
                throw OwnerLost();
            WindowsProcessIdentity actual = ReadIdentity();
            if (actual.ProcessId != _launchedPid || _identity is { } expected && actual != expected || !IsInJob())
                throw OwnerLost();
            _identity = actual;
            return this;
        }

        internal override ValueTask ValidateControlsAsync(ulong limit, CancellationToken token)
        {
            VerifyHeldControls(limit, token);
            ReadNotifications(token);
            if (_notifications != WindowsJobMemoryNotifications.None)
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-held-memory-limit");
            _admitted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseExecGateAsync(CancellationToken cancellationToken)
        {
            if (!_admitted) throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-admission-required");
            VerifyHeldControls(MemoryLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (JobNative.ResumeThread(_thread!) != 1)
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-resume-failed");
            _thread!.Dispose();
            _thread = null;
            return ValueTask.CompletedTask;
        }

        private void VerifyHeldControls(ulong limit, CancellationToken token)
        {
            if (VerifyProcess(token) || _thread is null)
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-leader-not-held");
            var limits = ReadLimits(EnvelopeFailure.CapacityUnsupported);
            if (limits.BasicLimitInformation.LimitFlags != RequiredFlags || limits.ProcessMemoryLimit != limit || limits.JobMemoryLimit != limit
                || ReadActiveProcesses() != 1)
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "windows-envelope-controls-unverified");
        }

        internal override ValueTask<EnvelopeRunState> ObserveOwnedAsync(CancellationToken token) => ValueTask.FromResult(ObserveOwned(token));

        private EnvelopeRunState ObserveOwned(CancellationToken token)
        {
            bool terminal = VerifyProcess(token);
            bool populated = ReadActiveProcesses() != 0;
            int exitCode = 0;
            if (terminal)
            {
                if (!JobNative.GetExitCodeProcess(_process!, out uint result)) throw OwnerLost();
                exitCode = unchecked((int)result);
            }
            ReadNotifications(token);
            var limits = ReadLimits(EnvelopeFailure.OwnershipLost);
            WindowsJobMemoryDiagnostics memory = new(_notifications, limits.PeakProcessMemoryUsed, limits.PeakJobMemoryUsed);
            return new(terminal, populated, true, memory.Notifications != WindowsJobMemoryNotifications.None,
                terminal ? exitCode == 0 ? "success" : "exit-code" : "running", terminal, exitCode, memory);
        }

        internal override ValueTask KillOwnedAsync(CancellationToken token)
        {
            VerifyProcess(token);
            if (!JobNative.TerminateJobObject(_job!, 1))
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "windows-envelope-job-termination-failed");
            return ValueTask.CompletedTask;
        }

        internal override async ValueTask<EnvelopeRunState> ReleaseOwnedAsync(EnvelopeRunState last, CancellationToken token)
        {
            if (last.ExitCode != 0 && !last.MemoryLimitHit)
                await Task.Delay(TimeSpan.FromMilliseconds(40), token).ConfigureAwait(false);
            EnvelopeRunState verified = ObserveOwned(token);
            if (!verified.Terminal || verified.Populated || !_pipes[1].Eof || !_pipes[2].Eof || !_pipes[3].Eof)
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "windows-envelope-not-drained");
            _drained = true;
            return verified;
        }

        private WindowsProcessIdentity ReadIdentity()
        {
            int pid = JobNative.GetProcessId(_process!);
            try
            {
                long creation = WindowsJobObjectGuard.ReadCreationTimeFileTime(_process!);
                return pid > 0 && creation > 0 ? new(pid, creation) : throw OwnerLost();
            }
            catch (Win32Exception) { throw OwnerLost(); }
        }

        private bool VerifyProcess(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_identity is null || _job is null || _job.IsClosed || _job.IsInvalid || ReadIdentity() != _identity)
                throw OwnerLost();
            if (HasExited()) return true;
            if (IsInJob()) return false;
            if (HasExited()) return true;
            throw OwnerLost();
        }

        private bool HasExited() => JobNative.WaitForSingleObject(_process!, 0) switch
        {
            JobNative.WAIT_OBJECT_0 => true,
            WindowsNative.WaitTimeout => false,
            _ => throw OwnerLost(),
        };

        private bool IsInJob() => WindowsNative.IsProcessInJob(_process!, _job!, out int inJob) && inJob != 0;

        private unsafe uint ReadActiveProcesses()
        {
            JobNative.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info = default;
            if (!JobNative.QueryInformationJobObject(_job!, JobNative.JobObjectBasicAccountingInformation, &info,
                (uint)sizeof(JobNative.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), out _))
                throw OwnerLost();
            return info.ActiveProcesses;
        }

        private unsafe JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION ReadLimits(EnvelopeFailure failure)
        {
            JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
            if (!JobNative.QueryInformationJobObject(_job!, JobNative.JobObjectExtendedLimitInformation, &limits,
                (uint)sizeof(JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION), out _))
                throw new EnvelopeException(failure, "windows-envelope-job-query-failed");
            return limits;
        }

        private void ReadNotifications(CancellationToken token)
        {
            for (int index = 0; index < 256; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!WindowsNative.GetQueuedCompletionStatus(_port!, out uint message, out nuint key, out nint payload, 0))
                {
                    if (Marshal.GetLastPInvokeError() == WindowsNative.WaitTimeout && payload == 0) return;
                    throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "windows-envelope-port-read-failed");
                }
                if (key != CompletionKey)
                    throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "windows-envelope-port-key-unverified");
                switch (message)
                {
                    case 9: _notifications |= WindowsJobMemoryNotifications.ProcessMemoryLimit; break;
                    case 10: _notifications |= WindowsJobMemoryNotifications.JobMemoryLimit; break;
                }
            }
            throw new EnvelopeException(EnvelopeFailure.DrainFailed, "windows-envelope-port-packet-limit");
        }

        private static EnvelopeException OwnerLost() => new(EnvelopeFailure.OwnershipLost, "windows-envelope-owner-unverified");

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (_process is not null && !_drained)
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "windows-envelope-dispose-before-drain");
            _disposed = true;
            foreach (OwnedPipe? pipe in _pipes) pipe?.Dispose();
            _thread?.Dispose();
            _process?.Dispose();
            _job?.Dispose();
            _port?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static partial class WindowsNative
    {
        internal const int AssociateCompletionPortInformation = 7;
        internal const int WaitTimeout = 258;

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct CompletionPortAssociation(nuint CompletionKey, nint CompletionPort);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeFileHandle CreateIoCompletionPort(nint fileHandle, nint existingPort, nuint key, uint concurrency);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out nuint key, out nint data, uint timeoutMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsProcessInJob(SafeProcessHandle process, WindowsJobObjectGuard.SafeJobHandle job, out int inJob);
    }
}
