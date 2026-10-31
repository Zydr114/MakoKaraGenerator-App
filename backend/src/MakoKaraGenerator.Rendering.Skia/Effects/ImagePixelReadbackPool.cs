namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>有界的临时图像读回缓冲；不保存图像、设备或图像内容凭据。</summary>
internal static class ImagePixelReadbackPool
{
    internal const int MaximumPooledBuffers = 3;
    internal const int MaximumBufferBytes = 16 * 1024 * 1024;
    internal const long MaximumPooledBytes = 32L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly List<byte[]> Buffers = [];
    private static long _pooledBytes;

    internal static ImagePixelReadbackLease Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (length > MaximumBufferBytes) return new(new byte[length], length, false);
        lock (Gate)
        {
            int index = Buffers.FindIndex(buffer => buffer.Length >= length);
            if (index >= 0)
            {
                var buffer = Buffers[index];
                Buffers.RemoveAt(index);
                _pooledBytes -= buffer.Length;
                return new(buffer, length, true);
            }
        }
        return new(new byte[length], length, true);
    }

    internal static ImagePixelReadbackPoolUsage Usage
    {
        get { lock (Gate) return new(Buffers.Count, _pooledBytes); }
    }

    internal static void Return(byte[] buffer, bool eligible)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!eligible || buffer.Length > MaximumBufferBytes) return;
        lock (Gate)
        {
            if (Buffers.Count >= MaximumPooledBuffers || _pooledBytes > MaximumPooledBytes - buffer.Length) return;
            Buffers.Add(buffer);
            _pooledBytes += buffer.Length;
        }
    }
}

internal sealed class ImagePixelReadbackLease : IDisposable
{
    private byte[]? _buffer;
    private readonly bool _eligible;

    internal ImagePixelReadbackLease(byte[] buffer, int length, bool eligible)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, buffer.Length);
        _buffer = buffer; Length = length; _eligible = eligible;
    }

    internal byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(ImagePixelReadbackLease));
    internal int Length { get; }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ImagePixelReadbackPool.Return(buffer, _eligible);
    }
}

internal readonly record struct ImagePixelReadbackPoolUsage(int Buffers, long Bytes);
