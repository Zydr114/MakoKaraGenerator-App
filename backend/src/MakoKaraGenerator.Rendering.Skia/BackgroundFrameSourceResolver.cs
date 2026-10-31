using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>按媒体种类、规范路径、固定修订及视频播放策略缓存独立来源。</summary>
/// <remarks>提供者由组合根持有；本解析器只拥有缓存来源，关闭时全部释放。</remarks>
public sealed class BackgroundFrameSourceResolver : IBackgroundFrameSourceResolver
{
    private readonly Dictionary<CacheKey, IBackgroundFrameSource> _sources = [];
    private readonly object _gate = new();
    private readonly IVideoFrameProvider? _videoProvider;
    private bool _disposed;

    /// <summary>创建解析器；未注入视频提供者时视频解析为空。</summary>
    public BackgroundFrameSourceResolver(IVideoFrameProvider? videoProvider = null) => _videoProvider = videoProvider;

    /// <inheritdoc/>
    public IBackgroundFrameSource? Resolve(BackgroundSource background) => Resolve(background, CancellationToken.None);

    /// <inheritdoc/>
    public IBackgroundFrameSource? Resolve(BackgroundSource background, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(background);
        if (!background.HasMedia || background.Path is not { } path || background.HasVideo && _videoProvider is null) return null;

        string canonicalPath = Path.GetFullPath(path);
        var key = new CacheKey(background.Kind,
            OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath,
            background.Media?.Hash, background.Media?.ByteLength,
            background.HasVideo ? background.Playback : null);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sources.TryGetValue(key, out var cached)) return cached;
        }

        // IO is outside the cache gate. Each caller owns its candidate until publication.
        IBackgroundFrameSource? candidate = background.HasVideo
            ? OpenVideo(canonicalPath, background.Playback, cancellationToken)
            : new ImageBackgroundFrameSource(canonicalPath);
        try
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sources.TryGetValue(key, out var winner)) return winner;
                _sources.Add(key, candidate);
                var published = candidate;
                candidate = null;
                return published;
            }
        }
        finally { candidate?.Dispose(); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        IBackgroundFrameSource[] sources;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sources = [.. _sources.Values];
            _sources.Clear();
        }
        List<Exception> errors = [];
        foreach (var source in sources)
        {
            try { source.Dispose(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
            { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException("背景来源释放失败。", errors);
    }

    private IBackgroundFrameSource OpenVideo(string path, BackgroundPlaybackMode playback, CancellationToken token)
    {
        var source = _videoProvider!.Open(path, out string? reason, token);
        return source is null ? new MissingVideoBackgroundFrameSource(reason) : new VideoBackgroundFrameSource(source, playback);
    }

    private readonly record struct CacheKey(BackgroundKind Kind, string Path, string? Hash, long? ByteLength, BackgroundPlaybackMode? Playback);
}
