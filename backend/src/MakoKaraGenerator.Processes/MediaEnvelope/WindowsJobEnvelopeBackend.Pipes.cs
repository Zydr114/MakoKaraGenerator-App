using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

public sealed partial class WindowsJobEnvelopeBackend
{
    [SupportedOSPlatform("windows")]
    private sealed class OwnedPipe(NamedPipeServerStream parent, SafeFileHandle child, uint quota, Action progress) : Stream
    {
        private const uint PipeQuota = 4096;
        private const uint FirstPipeInstance = 0x00080000;
        private const uint Overlapped = 0x40000000;
        private const uint RejectRemoteClients = 0x00000008;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint SecurityAnonymous = 0x00100000;

        internal SafeFileHandle Child => child;
        internal uint Quota => quota;
        internal bool Eof { get; private set; }

        internal static async ValueTask<OwnedPipe> OpenAsync(bool writing, Action progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string name = @"\\.\pipe\mako-media-" + Guid.NewGuid().ToString("N");
            PipeDirection direction = writing ? PipeDirection.Out : PipeDirection.In;
            SafePipeHandle? server = WindowsNative.CreateNamedPipeW(name, (uint)direction | Overlapped | FirstPipeInstance,
                RejectRemoteClients, 1, writing ? PipeQuota : 0, writing ? 0 : PipeQuota, 0, 0);
            NamedPipeServerStream? parent = null;
            SafeFileHandle? child = null;
            try
            {
                if (server.IsInvalid) throw PipeUnavailable();
                parent = new(direction, isAsync: true, isConnected: false, server);
                SafePipeHandle serverHandle = server;
                server = null;
                child = WindowsNative.CreateFileW(name, writing ? GenericRead : GenericWrite, 0, 0, OpenExisting, SecurityAnonymous, 0);
                if (child.IsInvalid || !WindowsNative.GetNamedPipeClientProcessId(serverHandle, out uint peer) || peer != Environment.ProcessId
                    || !WindowsNative.GetNamedPipeServerProcessId(child, out peer) || peer != Environment.ProcessId)
                    throw PipeUnavailable();
                await parent.WaitForConnectionAsync(token).ConfigureAwait(false);
                if (!WindowsNative.GetNamedPipeInfo(writing ? child : serverHandle, out uint flags, out uint output, out uint input, out uint instances)
                    || (flags & ~RejectRemoteClients) != (writing ? 0u : 1u) || instances != 1
                    || (writing ? output : input) != PipeQuota || (writing ? input : output) != 0)
                    throw PipeUnavailable();
                OwnedPipe pipe = new(parent, child, PipeQuota, progress);
                parent = null;
                child = null;
                return pipe;
            }
            finally
            {
                server?.Dispose();
                parent?.Dispose();
                child?.Dispose();
            }
        }

        private static EnvelopeException PipeUnavailable() => new(EnvelopeFailure.CapacityUnsupported, "windows-envelope-pipe-unverified");

        internal void CloseChild() => child.Dispose();

        public override bool CanRead => parent.CanRead;
        public override bool CanWrite => parent.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => parent.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => parent.FlushAsync(cancellationToken);

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
            int count = await parent.ReadAsync(buffer[..Math.Min(buffer.Length, (int)PipeQuota)], cancellationToken).ConfigureAwait(false);
            if (count != 0) progress();
            else if (!buffer.IsEmpty) Eof = true;
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, (int)PipeQuota);
                await parent.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                progress();
                buffer = buffer[count..];
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                parent.Dispose();
                CloseChild();
            }
            base.Dispose(disposing);
        }
    }

    private static partial class WindowsNative
    {
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, nint security);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition,
            uint flags, nint template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeInfo(SafeHandle pipe, out uint flags, out uint output, out uint input, out uint maxInstances);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeServerProcessId(SafeFileHandle pipe, out uint processId);
    }
}
