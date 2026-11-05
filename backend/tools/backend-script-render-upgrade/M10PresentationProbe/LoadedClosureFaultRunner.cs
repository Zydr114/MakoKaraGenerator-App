using System.Buffers.Binary;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.Presentation.Probe;

/// <summary>Explicit protocol fault injection over a real owned runner; never a fake helper or acceptance oracle.</summary>
internal sealed class LoadedClosureFaultRunner(MediaEnvelopeRunner real) : IMediaPresentationNativeRunner
{
    public long NativeLimitBytes => real.NativeLimitBytes;
    internal bool Injected { get; private set; }

    public Task RunAsync(IMediaRetainableFileLease source, IMediaWorkingSetBudget ownerBudget,
        EnvelopeCommand command, long managedPeakBytes,
        Func<IEnvelopeSession, DecodeOperationBudget, CancellationToken, Task> consume, CancellationToken cancellationToken)
        => real.RunAsync(source, ownerBudget, command, checked(managedPeakBytes + 32 * 1024),
            async (session, operation, token) =>
            {
                using var reservation = operation.ReserveManaged(32 * 1024);
                using var metadata = new FaultMetadataStream(session.Metadata);
                var view = new SessionView(session, metadata);
                try { await consume(view, operation, token).ConfigureAwait(false); }
                finally { Injected |= metadata.Injected; }
            }, cancellationToken);

    private sealed class SessionView(IEnvelopeSession realSession, Stream metadata) : IEnvelopeSession
    {
        public Stream Input => realSession.Input;
        public Stream Output => realSession.Output;
        public Stream Metadata => metadata;
        public Stream Diagnostics => realSession.Diagnostics;
        public long PipeBackingBytes => realSession.PipeBackingBytes;
        public long ProgressVersion => realSession.ProgressVersion;
        public bool StartAttempted => realSession.StartAttempted;
        public ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken cancellationToken)
            => realSession.StartHeldAsync(start, cancellationToken);
        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken)
            => realSession.ObserveAsync(cancellationToken);
        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken cancellationToken)
            => realSession.ConfirmStartAbsentAsync(cancellationToken);
        public ValueTask ReleaseExecGateAsync(CancellationToken cancellationToken)
            => realSession.ReleaseExecGateAsync(cancellationToken);
        public ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
            => realSession.KillVerifiedCgroupAsync(identity, cancellationToken);
        public ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
            => realSession.UnrefVerifiedAsync(identity, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The real owner disposes its real session.
    }

    private sealed class FaultMetadataStream(Stream realMetadata) : Stream
    {
        private readonly byte[] _record = new byte[16 + 16 * 1024];
        private int _offset, _length;
        private long _frames;
        internal bool Injected { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            if (_offset == _length)
            {
                int first = await realMetadata.ReadAsync(_record.AsMemory(0, 16), cancellationToken).ConfigureAwait(false);
                if (first == 0) return 0;
                await realMetadata.ReadExactlyAsync(_record.AsMemory(first, 16 - first), cancellationToken).ConfigureAwait(false);
                if (!_record.AsSpan(0, 8).SequenceEqual("MKGPRE1\0"u8)) throw new ProbeFailure("probe-fault-wire-magic");
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(_record.AsSpan(8));
                uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(_record.AsSpan(12));
                if (bytes > 16 * 1024) throw new ProbeFailure("probe-fault-record-capacity");
                await realMetadata.ReadExactlyAsync(_record.AsMemory(16, (int)bytes), cancellationToken).ConfigureAwait(false);
                if (type == 4 && _frames > 0 && !Injected)
                {
                    if (bytes < 44) throw new ProbeFailure("probe-fault-loaded-record-truncated");
                    // One SHA256 bit in the first actual loaded-file record after the first native frame.
                    // The real loaded path/length and every other byte remain unchanged.
                    _record[16 + 8] ^= 1;
                    Injected = true;
                }
                if (type == 2) _frames++;
                _length = 16 + (int)bytes; _offset = 0;
            }
            int count = Math.Min(buffer.Length, _length - _offset);
            _record.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
