using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Linux-x64 cgroup-v2 backend using direct sd-bus and four owned nonblocking UNIX pipes.</summary>
/// <remarks>Construction performs no capability probe. Open/start verify actual platform controls and exact pipe sizes.</remarks>
public sealed partial class LinuxSystemdEnvelopeBackend : IEnvelopeBackend
{
    static LinuxSystemdEnvelopeBackend()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxSystemdEnvelopeBackend).Assembly,
            static (name, _, _) => name == "mkg_media_envelope"
                ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "media-tools", "libmkg_media_envelope.so")) : 0);
    }
    /// <inheritdoc />
    public async ValueTask<IEnvelopeSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-platform-unavailable");
        try
        {
            return await Task.Run<IEnvelopeSession>(OpenSession).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-bridge-unavailable"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-transport-unavailable"); }
    }

    private static unsafe Session OpenSession()
    {
        if (Native.AbiSize() != sizeof(NativeSnapshot))
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-bridge-abi-invalid");
        int[] descriptors = [-1, -1, -1, -1];
        nint context = 0;
        long backing;
        fixed (int* fds = descriptors) Check(Native.Open(out context, (nint)fds, out backing));
        try { return new Session(context, descriptors, backing); }
        catch
        {
            Native.Close(context);
            foreach (int fd in descriptors) if (fd >= 0) _ = Native.CloseFd(fd);
            throw;
        }
    }

    private static void Check(int result)
    {
        if (result < 0)
            throw new EnvelopeException(result == -116 ? EnvelopeFailure.OwnershipLost : EnvelopeFailure.CapacityUnsupported,
                result == -116 ? "native-envelope-identity-stale" : "native-envelope-systemd-or-controller-unavailable");
    }

    private sealed class Session : IEnvelopeSession
    {
        private nint _context;
        private readonly SemaphoreSlim _control = new(1, 1);
        private long _progress;
        public Stream Input { get; }
        public Stream Output { get; }
        public Stream Metadata { get; }
        public Stream Diagnostics { get; }
        public long PipeBackingBytes { get; }
        public long ProgressVersion => Interlocked.Read(ref _progress);
        public bool StartAttempted { get; private set; }

        internal Session(nint context, int[] descriptors, long backing)
        {
            _context = context;
            PipeBackingBytes = backing;
            OwnedPipeStream? input = null, output = null, diagnostics = null, metadata = null;
            try
            {
                input = TakePipe(descriptors, 0, true);
                output = TakePipe(descriptors, 1, false);
                diagnostics = TakePipe(descriptors, 2, false);
                metadata = TakePipe(descriptors, 3, false);
                Input = input; Output = output; Diagnostics = diagnostics; Metadata = metadata;
            }
            catch
            {
                input?.Dispose(); output?.Dispose(); diagnostics?.Dispose(); metadata?.Dispose();
                throw;
            }
        }

        private OwnedPipeStream TakePipe(int[] descriptors, int index, bool writing)
        {
            var handle = new SafeFileHandle(descriptors[index], ownsHandle: true);
            descriptors[index] = -1; // SafeHandle now owns it; no raw-descriptor double close.
            try { return new OwnedPipeStream(handle, writing, () => Interlocked.Increment(ref _progress)); }
            catch { handle.Dispose(); throw; }
        }

        private async ValueTask<T> CallAsync<T>(Func<T> action, CancellationToken token)
        {
            await _control.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_context == 0, this);
                // Do not cancel/abandon an in-flight manager call: a late accepted
                // StartTransientUnit must retain its bus, pipes and reconciliation state.
                return await Task.Run(action).ConfigureAwait(false);
            }
            finally { _control.Release(); }
        }

        public async ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken cancellationToken)
        {
            await CallAsync(() =>
            {
                StartAttempted = true;
                using var args = new Utf8Argv(start.Command.Arguments);
                Check(Native.Start(_context, start.Nonce, start.Command.GateExecutable, start.Command.Executable,
                    args.Pointer, checked((uint)start.Command.Arguments.Length), start.NativeLimitBytes));
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken)
            => CallAsync(() => { Check(Native.Snapshot(_context, out NativeSnapshot value)); return value.ToValue(); }, cancellationToken);

        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken cancellationToken)
            => CallAsync(() => { int result = Native.Absent(_context); Check(result); return result == 1; }, cancellationToken);

        public async ValueTask ReleaseExecGateAsync(CancellationToken cancellationToken)
        {
            await Input.WriteAsync(new byte[] { 0xa7 }, cancellationToken).ConfigureAwait(false);
            await Input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
        {
            await CallAsync(() =>
            {
                Check(Native.Snapshot(_context, out NativeSnapshot current));
                OwnedNativeEnvelope.VerifyIdentity(current.ToValue(), identity);
                Check(Native.Kill(_context, identity.InvocationId, identity.CgroupDevice, identity.CgroupInode));
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
        {
            await CallAsync(() =>
            {
                Check(Native.Snapshot(_context, out NativeSnapshot current));
                EnvelopeSnapshot value = current.ToValue();
                OwnedNativeEnvelope.VerifyIdentity(value, identity);
                if (!value.Terminal || value.CgroupPopulated || !(value.CgroupExists || value.CgroupRemoved))
                    throw new EnvelopeException(EnvelopeFailure.DrainFailed, "native-unit-not-terminal");
                Check(Native.Unref(_context, identity.InvocationId, identity.CgroupDevice, identity.CgroupInode));
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _control.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_context == 0) return;
                Input.Dispose(); Output.Dispose(); Metadata.Dispose(); Diagnostics.Dispose();
                Native.Close(_context); _context = 0;
            }
            finally { _control.Release(); }
        }
    }

    // A FileStream's async-handle assumptions are deliberately avoided for raw
    // anonymous UNIX pipes. Each operation pins its caller memory only while a
    // bounded poll/read/write executes; cancellation never abandons that Task.
    private sealed class OwnedPipeStream(SafeFileHandle handle, bool writing, Action progress) : Stream
    {
        public override bool CanRead => !writing && !handle.IsClosed;
        public override bool CanSeek => false;
        public override bool CanWrite => writing && !handle.IsClosed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        public override Task FlushAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (writing) throw new NotSupportedException();
            if (buffer.Length == 0) return 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(handle.IsClosed, this);
                int read = await Task.Run(() => IoOnce(buffer, false)).ConfigureAwait(false);
                if (read is -11 or -4) continue;
                if (read < 0) throw new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-owned-pipe-read-failed");
                if (read != 0) progress();
                return read;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!writing) throw new NotSupportedException();
            while (buffer.Length != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(handle.IsClosed, this);
                ReadOnlyMemory<byte> remaining = buffer;
                int written = await Task.Run(() => IoOnce(MemoryMarshal.AsMemory(remaining), true)).ConfigureAwait(false);
                if (written is -11 or -4) continue;
                if (written <= 0) throw new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-owned-pipe-write-failed");
                progress(); buffer = buffer[written..];
            }
        }

        private unsafe int IoOnce(Memory<byte> buffer, bool write)
        {
            bool retained = false;
            try
            {
                handle.DangerousAddRef(ref retained);
                using MemoryHandle pin = buffer.Pin();
                return Native.PipeIo(checked((int)handle.DangerousGetHandle()), (nint)pin.Pointer,
                    checked((uint)buffer.Length), write ? 1 : 0, 50);
            }
            finally { if (retained) handle.DangerousRelease(); }
        }

        protected override void Dispose(bool disposing)
        { if (disposing) handle.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Utf8Argv : IDisposable
    {
        private readonly nint[] _strings;
        public nint Pointer { get; }
        public Utf8Argv(string[] args)
        {
            _strings = new nint[args.Length];
            try
            {
                for (int i = 0; i < args.Length; i++) _strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                Pointer = Marshal.AllocCoTaskMem(IntPtr.Size * (args.Length + 1));
                for (int i = 0; i < _strings.Length; i++) Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, _strings[i]);
                Marshal.WriteIntPtr(Pointer, _strings.Length * IntPtr.Size, 0);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        { foreach (nint pointer in _strings) if (pointer != 0) Marshal.FreeCoTaskMem(pointer); if (Pointer != 0) Marshal.FreeCoTaskMem(Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeSnapshot
    {
        public fixed byte _unit[256];
        public fixed byte _path[512];
        public fixed byte _invocation[33];
        public fixed byte _description[128];
        public fixed byte _cgroup[2048];
        public fixed byte _type[32];
        public fixed byte _killMode[32];
        public fixed byte _restart[32];
        public fixed byte _oomPolicy[32];
        public fixed byte _active[32];
        public fixed byte _result[64];
        public ulong _startTicks, _device, _inode, _memoryMax, _effectiveMax, _swapMax, _kernelMax, _kernelSwap, _oomKills;
        public uint _mainPid;
        public int _mainInGroup, _cgExists, _cgPopulated, _cgRemoved, _oomGroup, _accounting, _delegate, _execCode, _execStatus, _killAvailable, _controlsComplete;
        internal EnvelopeSnapshot ToValue()
        {
            fixed (byte* unit = _unit, path = _path, invocation = _invocation, description = _description, cgroup = _cgroup,
                type = _type, kill = _killMode, restart = _restart, oom = _oomPolicy, active = _active, result = _result)
                return new(Text(unit, 256), Text(path, 512), Text(invocation, 33), Text(description, 128), Text(cgroup, 2048),
                    _mainPid, _startTicks, _device, _inode, _mainInGroup != 0, _cgExists != 0, _cgPopulated != 0, _cgRemoved != 0,
                    _memoryMax, _effectiveMax, _swapMax, _kernelMax, _kernelSwap, _oomGroup != 0, _accounting != 0, _delegate != 0,
                    Text(type, 32), Text(kill, 32), Text(restart, 32), Text(oom, 32), Text(active, 32), Text(result, 64),
                    _execCode, _execStatus, _oomKills, _killAvailable != 0, _controlsComplete != 0);
        }
        private static string Text(byte* value, int maximum)
        {
            var bytes = new ReadOnlySpan<byte>(value, maximum);
            int length = bytes.IndexOf((byte)0);
            if (length < 0) throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-bridge-abi-invalid");
            return Encoding.UTF8.GetString(bytes[..length]);
        }
    }

    private static partial class Native
    {
        private const string Library = "mkg_media_envelope";
        [LibraryImport(Library, EntryPoint = "mkg_envelope_abi_size")]
        internal static partial int AbiSize();
        [LibraryImport(Library, EntryPoint = "mkg_envelope_open")]
        internal static partial int Open(out nint context, nint descriptors, out long backing);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_start", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Start(nint context, string nonce, string gate, string tool, nint args, uint count, ulong limit);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_snapshot")]
        internal static partial int Snapshot(nint context, out NativeSnapshot snapshot);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_start_absent")]
        internal static partial int Absent(nint context);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_kill", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Kill(nint context, string invocation, ulong device, ulong inode);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_unref", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Unref(nint context, string invocation, ulong device, ulong inode);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_close")]
        internal static partial void Close(nint context);
        [LibraryImport(Library, EntryPoint = "mkg_pipe_io")]
        internal static partial int PipeIo(int fd, nint buffer, uint count, int writing, int timeoutMilliseconds);
        [LibraryImport("libc", EntryPoint = "close")]
        internal static partial int CloseFd(int fd);
    }
}
