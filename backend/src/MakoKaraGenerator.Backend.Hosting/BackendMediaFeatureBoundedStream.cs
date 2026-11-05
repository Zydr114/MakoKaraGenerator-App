using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Each seek, I/O and close shares one gate. No disposal closes a handle under an in-flight read or write.</summary>
internal sealed class BackendMediaFeatureBoundedStream(FileStream file, long maximumBytes, Action<long> chargeGrowth,
    Func<CancellationToken, ValueTask>? beforeWriteAsync = null) : Stream
{
    private readonly SemaphoreSlim _io = new(1, 1);
    private bool _closed;
    internal bool Closed => Volatile.Read(ref _closed);
    public override bool CanRead => !Closed;
    public override bool CanSeek => !Closed;
    public override bool CanWrite => !Closed;
    public override long Length => Locked(() => file.Length);
    public override long Position { get => Locked(() => file.Position); set => Seek(value, SeekOrigin.Begin); }

    private void CheckOpen() => ObjectDisposedException.ThrowIf(_closed, this);
    private long Admit(long next)
    {
        if (next < 0 || next > maximumBytes)
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Media section exceeds its admitted byte length.");
        return next;
    }
    private void BeforeWrite(int count)
    {
        long end = Admit(checked(file.Position + count));
        if (end > file.Length) chargeGrowth(end);
    }
    private T Locked<T>(Func<T> action)
    {
        _io.Wait(); try { CheckOpen(); return action(); } finally { _io.Release(); }
    }
    public override long Seek(long offset, SeekOrigin origin) => Locked(() =>
    {
        long start = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => file.Position, SeekOrigin.End => file.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        long position = Admit(checked(start + offset)); file.Position = position; return position;
    });
    public override void SetLength(long value)
    {
        _io.Wait();
        try { CheckOpen(); Admit(value); if (value > file.Length) chargeGrowth(value); file.SetLength(value); }
        finally { _io.Release(); }
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        _io.Wait(); try { CheckOpen(); return file.Read(buffer); } finally { _io.Release(); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CheckOpen(); return await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        finally { _io.Release(); }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _io.Wait(); try { CheckOpen(); BeforeWrite(buffer.Length); file.Write(buffer); } finally { _io.Release(); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckOpen(); BeforeWrite(buffer.Length);
            if (beforeWriteAsync is not null) await beforeWriteAsync(cancellationToken).ConfigureAwait(false);
            await file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally { _io.Release(); }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush()
    {
        _io.Wait(); try { CheckOpen(); file.Flush(); } finally { _io.Release(); }
    }
    internal void FlushToDisk()
    {
        _io.Wait(); try { CheckOpen(); file.Flush(flushToDisk: true); } finally { _io.Release(); }
    }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CheckOpen(); await file.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _io.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _io.Wait();
            try { if (!_closed) { _closed = true; file.Dispose(); } }
            finally { _io.Release(); }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try { if (!_closed) { _closed = true; await file.DisposeAsync().ConfigureAwait(false); } }
        finally
        {
            _io.Release();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
