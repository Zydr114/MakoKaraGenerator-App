using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>只消费同一内容身份的内存图片；创建时完整解码，不访问文件系统。</summary>
public sealed class MemoryImageBackgroundFrameSourceResolver : IBackgroundFrameSourceResolver
{
    /// <summary>最大编码字节数。</summary>
    public const int MaximumEncodedBytes = 2 * 1024 * 1024;
    /// <summary>最大单边像素数。</summary>
    public const int MaximumDimension = 4096;
    /// <summary>最大 BGRA 解码字节数。</summary>
    public const long MaximumDecodedBytes = 16 * 1024 * 1024;

    private readonly string _logicalPath;
    private readonly string _hash;
    private readonly int _encodedLength;
    private readonly object _gate = new();
    private readonly MemoryImageBackgroundFrameSource _source;
    private bool _disposed;

    /// <summary>预算先于像素分配；损坏输入在透明输出创建前同样拒绝。</summary>
    public MemoryImageBackgroundFrameSourceResolver(string logicalPath, ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > MaximumEncodedBytes)
            throw new ImageBackgroundDecodeException("PackageImageBudgetExceeded", "固定图片超过编码大小预算。");
        _hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _encodedLength = bytes.Length;
        if (logicalPath != new MediaFileRevision(_hash, _encodedLength, "image").RelativePath)
            throw new ImageBackgroundDecodeException("ProjectImageSourceChanged", "固定图片内容身份不匹配。");
        _logicalPath = logicalPath;
        _source = new(Decode(bytes, cancellationToken));
    }

    /// <inheritdoc/>
    public IBackgroundFrameSource? Resolve(BackgroundSource background) => Resolve(background, CancellationToken.None);

    /// <inheritdoc/>
    public IBackgroundFrameSource? Resolve(BackgroundSource background, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(background);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!background.HasImage || background.Path != _logicalPath
                || background.Media is not { } media || media.Hash != _hash || media.ByteLength != _encodedLength
                || !Enum.IsDefined(background.Fit) || !Enum.IsDefined(background.Playback))
                throw new ImageBackgroundDecodeException("ProjectImageSourceChanged", "固定图片背景身份不匹配。");
            return _source;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }

    private static SKBitmap Decode(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        SKBitmap? bitmap = null;
        try
        {
            using SKData data = SKData.CreateCopy(bytes);
            using SKCodec codec = SKCodec.Create(data)
                ?? throw new ImageBackgroundDecodeException("PackageImageDecodeFailed", "固定图片编码无法读取。");
            int width = codec.Info.Width, height = codec.Info.Height;
            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension
                || (long)width * height > MaximumDecodedBytes / 4)
                throw new ImageBackgroundDecodeException("PackageImageBudgetExceeded", "固定图片超过尺寸或解码内存预算。");
            token.ThrowIfCancellationRequested();
            // Preserve the codec's color space, as the existing file-backed background does.
            SKImageInfo info = codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Premul);
            bitmap = new(info);
            if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new ImageBackgroundDecodeException("PackageImageDecodeFailed", "固定图片像素解码不完整或失败。");
            token.ThrowIfCancellationRequested();
            bitmap.SetImmutable(); // 只读共享：绘制复用同一纹理，见 ImageBackgroundFrameSource。
            SKBitmap result = bitmap;
            bitmap = null;
            return result;
        }
        catch (ImageBackgroundDecodeException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        { throw new ImageBackgroundDecodeException("PackageImageDecodeFailed", "固定图片无法解码。", error); }
        finally { bitmap?.Dispose(); }
    }

    private sealed class MemoryImageBackgroundFrameSource(SKBitmap bitmap) : IBackgroundFrameSource
    {
        private readonly object _gate = new();
        private bool _disposed;

        public SKBitmap? GetFrame(long timeUs) => GetFrame(timeUs, CancellationToken.None);
        public SKBitmap? GetFrame(long timeUs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return bitmap;
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                bitmap.Dispose();
            }
        }
    }
}

/// <summary>无路径详情的固定图片解码失败。</summary>
public sealed class ImageBackgroundDecodeException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>稳定失败码。</summary>
    public string Code { get; } = code;
}
