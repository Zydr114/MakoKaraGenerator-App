using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

public sealed partial class LinuxSystemdEnvelopeBackend : IEnvelopeObserverBackend
{
    /// <inheritdoc />
    public async ValueTask<EnvelopeObserverAttachResult> AttachAsync(EnvelopeIdentity expectedIdentity,
        ulong expectedLimitBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnvelopeSnapshotValidator.ValidateObserverExpected(expectedIdentity, expectedLimitBytes);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-platform-unavailable");
        EnvelopeObserver observer;
        try
        {
            // Open has no Ref or start side effect, and its result is joined.
            observer = await Task.Run(() => OpenObserver(expectedIdentity, expectedLimitBytes)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-bridge-unavailable"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-transport-unavailable"); }
        // All outcomes from here carry the real owner, including cancellation
        // requested while Open was being joined or a later Ref reply timeout.
        return await observer.AttachOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static unsafe EnvelopeObserver OpenObserver(EnvelopeIdentity expectedIdentity, ulong limit)
    {
        if (Native.AbiSize() != sizeof(NativeSnapshot) || Native.ObserverIdentityAbiSize() != sizeof(NativeObserverIdentity))
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-bridge-abi-invalid");
        NativeObserverIdentity expected = NativeObserverIdentity.FromValue(expectedIdentity);
        CheckObserver(Native.ObserverOpen(out nint context, in expected, limit));
        try { return new EnvelopeObserver(expectedIdentity, limit, new ObserverNativeContext(context)); }
        catch
        {
            // Open never Ref'd. This close cannot consume a Worker's AddRef.
            CheckObserver(Native.ObserverClose(context));
            throw;
        }
    }

    private static void CheckObserver(int result)
    {
        if (result >= 0) return;
        throw result switch
        {
            -16 => new EnvelopeException(EnvelopeFailure.DrainFailed, "native-observer-reference-unresolved"),
            -2 => new EnvelopeException(EnvelopeFailure.OwnershipLost, "native-observer-unit-unavailable"),
            -116 => new EnvelopeException(EnvelopeFailure.OwnershipLost, "native-envelope-identity-stale"),
            _ => new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-systemd-or-controller-unavailable"),
        };
    }

    private sealed class ObserverNativeContext(nint context) : IEnvelopeObserverNativeContext
    {
        private nint _context = context;
        public EnvelopeSnapshot Attach()
        {
            CheckObserver(Native.ObserverAttach(_context, out NativeSnapshot value));
            return value.ToValue();
        }
        public EnvelopeSnapshot Observe()
        {
            CheckObserver(Native.ObserverSnapshot(_context, out NativeSnapshot value));
            return value.ToValue();
        }
        public void KillVerifiedCgroup() => CheckObserver(Native.ObserverKill(_context));
        public void UnrefOwnReference() => CheckObserver(Native.ObserverUnref(_context));
        public void Close()
        {
            CheckObserver(Native.ObserverClose(_context));
            _context = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeObserverIdentity
    {
        public fixed byte _nonce[33];
        public fixed byte _unit[256];
        public fixed byte _path[512];
        public fixed byte _invocation[33];
        public fixed byte _group[2048];
        public uint _mainPid;
        public ulong _startTicks, _device, _inode;

        internal static NativeObserverIdentity FromValue(EnvelopeIdentity value)
        {
            NativeObserverIdentity result = default;
            Write(result._nonce, 33, value.Nonce);
            Write(result._unit, 256, value.UnitName);
            Write(result._path, 512, value.UnitPath);
            Write(result._invocation, 33, value.InvocationId);
            Write(result._group, 2048, value.ControlGroup);
            result._mainPid = value.MainPid;
            result._startTicks = value.StartTicks;
            result._device = value.CgroupDevice;
            result._inode = value.CgroupInode;
            return result;
        }

        private static void Write(byte* target, int capacity, string value)
        {
            var destination = new Span<byte>(target, capacity);
            int length = Encoding.UTF8.GetBytes(value.AsSpan(), destination);
            if (length >= capacity)
                throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "native-observer-expected-invalid");
            destination[length] = 0;
        }
    }

    private static partial class Native
    {
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_identity_abi_size")]
        internal static partial int ObserverIdentityAbiSize();
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_open")]
        internal static partial int ObserverOpen(out nint context, in NativeObserverIdentity expected, ulong limit);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_attach")]
        internal static partial int ObserverAttach(nint context, out NativeSnapshot value);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_snapshot")]
        internal static partial int ObserverSnapshot(nint context, out NativeSnapshot value);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_kill")]
        internal static partial int ObserverKill(nint context);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_unref")]
        internal static partial int ObserverUnref(nint context);
        [LibraryImport(Library, EntryPoint = "mkg_envelope_observer_close")]
        internal static partial int ObserverClose(nint context);
    }
}
