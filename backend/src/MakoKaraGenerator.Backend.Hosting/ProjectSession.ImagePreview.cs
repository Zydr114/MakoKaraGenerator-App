using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>仅用于 Writer 外读取已固定图片闭包；读取完成或放弃时释放短期引用。</summary>
public sealed class BackendProjectImageReadLease : IDisposable
{
    private Action? _release;
    private readonly object _gate = new();
    private bool _reading;
    private bool _disposeRequested;
    private readonly string? _directory;
    private readonly BackgroundSource _background;
    private readonly bool _assetPath;

    internal BackendProjectImageReadLease(string? directory, BackgroundSource background, Action release, bool assetPath = false)
    { _directory = directory; _background = background; _release = release; _assetPath = assetPath; }

    /// <summary>检查受管内容地址、全部路径链接和完整大小/SHA；不执行图片解码。</summary>
    public async Task<BackendOperationResult<byte[]>> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_release is null || _disposeRequested, this);
            if (_reading) throw new InvalidOperationException("固定图片只能读取一次。");
            _reading = true;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_directory is null || _background is not { Kind: BackgroundKind.Image, Media: { } media }
                || string.IsNullOrWhiteSpace(_background.Path)) return Unavailable();
            if (media.ByteLength is <= 0 or > ProjectImagePreviewSnapshotCodec.MaximumImageBytes)
                return BackendOperationResult<byte[]>.Rejected(new(BackendErrorCode.ResourceTooLarge,
                    "固定图片超过编码大小预算。", "background", FailureCode: "SnapshotSizeExceeded"));
            string root = Path.GetFullPath(_directory);
            string expected = _assetPath ? Path.GetFullPath(_background.Path) : Path.GetFullPath(Path.Combine(root, media.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            string normalizedPath = OperatingSystem.IsWindows() ? _background.Path.Replace('/', Path.DirectorySeparatorChar) : _background.Path;
            if (!string.Equals(normalizedPath, expected, StringComparison.Ordinal)
                || !string.Equals(Path.GetFullPath(_background.Path), expected, StringComparison.Ordinal)) return Unavailable();
            ProjectDirectoryHandles.EnsurePathHasNoLinks(expected);
            if (!Directory.Exists(root) || !File.Exists(expected)) return Unavailable();
            if (new FileInfo(expected).Length != media.ByteLength) return Changed();
            byte[] bytes = new byte[checked((int)media.ByteLength)];
            await using FileStream stream = new(expected, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != media.ByteLength) return Changed();
            try { await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException) { return Changed(); }
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0
                || stream.Length != media.ByteLength) return Changed();
            ProjectDirectoryHandles.EnsurePathHasNoLinks(expected);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), media.Hash, StringComparison.Ordinal))
                return Changed();
            return BackendOperationResult<byte[]>.Success(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Unavailable(); }
        finally
        {
            Action? release;
            lock (_gate) { _reading = false; _disposeRequested = true; release = _release; _release = null; }
            release?.Invoke();
        }
    }

    private static BackendOperationResult<byte[]> Unavailable() => BackendOperationResult<byte[]>.Rejected(new(
        BackendErrorCode.CommandNotSupported, "工程图片的已保存授权闭包不可用。", "background",
        FailureCode: "ProjectImageSourceUnavailable"));
    private static BackendOperationResult<byte[]> Changed() => BackendOperationResult<byte[]>.Rejected(new(
        BackendErrorCode.CommandNotSupported, "工程图片大小或内容已改变。", "background",
        FailureCode: "ProjectImageSourceChanged"));
    /// <summary>幂等归还工程闭包短期读引用。</summary>
    public void Dispose()
    {
        Action? release = null;
        lock (_gate)
        {
            _disposeRequested = true;
            if (!_reading) { release = _release; _release = null; }
        }
        release?.Invoke();
    }
}

public sealed partial class ProjectSession
{
    private readonly object _pinnedInputReadGate = new();
    private int _pinnedInputReaders;
    private TaskCompletionSource? _pinnedInputReadsDrained;

    // Called only under Writer, after exact owner/revision and closing checks. No file IO.
    internal BackendProjectImageReadLease AcquireProjectImageRead(ProjectSnapshot snapshot)
    {
        lock (_pinnedInputReadGate)
        {
            if (_pinnedInputReaders++ == 0)
                _pinnedInputReadsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        IDisposable? assetReference = null;
        bool assetPath = snapshot.Background.Path is { } path && _backgroundHistory.TryGetValue(path, out var state);
        try
        {
            if (assetPath) assetReference = _backgroundHistory[snapshot.Background.Path!].AcquireReference();
            return new(assetPath ? Path.GetDirectoryName(snapshot.Background.Path) : PinnedInputDirectory, snapshot.Background,
                () => { try { assetReference?.Dispose(); } finally { ReleaseProjectImageRead(); } }, assetPath);
        }
        catch { ReleaseProjectImageRead(); throw; }
    }

    private void ReleaseProjectImageRead()
    {
        lock (_pinnedInputReadGate)
            if (--_pinnedInputReaders == 0) _pinnedInputReadsDrained!.TrySetResult();
    }

    // Closing admission is blocked under Writer before this is called. Never await under Writer.
    internal Task DrainPinnedInputReadsAsync()
    {
        lock (_pinnedInputReadGate)
            return _pinnedInputReaders == 0 ? Task.CompletedTask : _pinnedInputReadsDrained!.Task;
    }
}
