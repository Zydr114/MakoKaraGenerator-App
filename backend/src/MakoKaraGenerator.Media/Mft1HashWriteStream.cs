using System.Security.Cryptography;

namespace MakoKaraGenerator.Media;

/// <summary>Forward-only section-limited hash sink. It never owns the caller's target.</summary>
internal sealed class Mft1HashWriteStream(Stream target, IncrementalHash fullHash) : Stream
{
    private long _limit, _sectionCount;
    private IncrementalHash? _sectionHash;
    private int _writing;
    internal long Count { get; private set; }

    internal void BeginSection(long bytes, IncrementalHash? sectionHash = null)
    {
        Mft1Wire.Require(Volatile.Read(ref _writing) == 0, "Section copy left a pending write operation.");
        _limit = bytes; _sectionCount = 0; _sectionHash = sectionHash;
    }

    internal void EndSection() => Mft1Wire.Require(Volatile.Read(ref _writing) == 0 && _sectionCount == _limit,
        "Section copy did not emit its exact canonical byte length.");

    private void Enter(int length)
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw Mft1Wire.Invalid("Concurrent writes into a canonical section are not permitted.");
        if (length > _limit - _sectionCount)
        {
            Volatile.Write(ref _writing, 0);
            throw Mft1Wire.Invalid("Section copy exceeds its exact canonical byte length.");
        }
    }

    private void Record(ReadOnlySpan<byte> buffer)
    {
        fullHash.AppendData(buffer); _sectionHash?.AppendData(buffer);
        _sectionCount = checked(_sectionCount + buffer.Length); Count = checked(Count + buffer.Length);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Enter(buffer.Length);
        try { target.Write(buffer); Record(buffer); }
        finally { Volatile.Write(ref _writing, 0); }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Enter(buffer.Length);
        try
        {
            await target.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); Record(buffer.Span);
        }
        finally { Volatile.Write(ref _writing, 0); }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => target.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { base.Dispose(disposing); /* The caller retains target ownership. */ }
}
