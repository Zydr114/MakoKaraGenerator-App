using System.Runtime.CompilerServices;
using System.Collections.Immutable;

namespace MakoKaraGenerator.Rendering;

/// <summary>
/// 为渲染管线增加有界的内存帧缓存。
/// </summary>
/// <remarks>
/// <para>
/// 缓存只保存像素副本，不保存调用方拥有的 <see cref="RenderFrame" />，因此调用方仍可按
/// 原有约定立即释放返回帧。缓存键使用 <see cref="ProjectSnapshot" /> 的引用，而不是记录值相等：
/// 编辑产生新快照时会清空旧快照的缓存，避免逐次比较整个歌词文档。
/// </para>
/// <para>
/// 这是预览侧的性能优化，不改变渲染语义；缓存未命中时完全交给内部管线处理。
/// </para>
/// </remarks>
public sealed class CachedRenderPipeline : IRenderPipeline, ILineGeometryProvider
{
    private readonly IRenderPipeline _inner;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<RenderCacheKey, CacheEntry> _entries = [];
    private readonly LinkedList<RenderCacheKey> _lru = [];
    private ProjectSnapshot? _cachedSnapshot;

    /// <summary>创建缓存管线。</summary>
    /// <param name="inner">实际执行渲染的管线。</param>
    /// <param name="capacity">最多保存的帧数。</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> 为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 不为正时抛出。</exception>
    public CachedRenderPipeline(IRenderPipeline inner, int capacity = 8)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _inner = inner;
        _capacity = capacity;
    }

    /// <inheritdoc/>
    public string BackendName => _inner.BackendName;

    /// <inheritdoc/>
    public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) =>
        (_inner as ILineGeometryProvider)?.GetLineGeometry(snapshot, lineId);

    /// <inheritdoc/>
    public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null) =>
        _inner is ILineGeometryProvider provider ? provider.GetSceneFrame(snapshot, timeUs, editingLineId)
            : LyricSceneSampler.SampleFrame(snapshot, timeUs);


    /// <summary>清除全部缓存帧。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _cachedSnapshot = null;
        }
    }

    /// <inheritdoc/>
    public RenderFrame Render(RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.CancellationToken.ThrowIfCancellationRequested();
        PreparedRenderFrame.ValidateRequest(request);

        if (request.LayoutLineId is not null) return _inner.Render(request);

        RenderTarget target = request.ResolveTarget();
        RenderCacheKey key = new(
            request.Project,
            request.TimeUs,
            target,
            request.Quality,
            request.TransparentBackground,
            request.PreparedFrame);

        lock (_gate)
        {
            if (_cachedSnapshot is not null && !ReferenceEquals(_cachedSnapshot, request.Project))
            {
                _entries.Clear();
                _lru.Clear();
            }

            _cachedSnapshot = request.Project;

            if (_entries.TryGetValue(key, out CacheEntry? cached))
            {
                Touch(key, cached);
                return new RenderFrame(
                    (byte[])cached.Pixels.Clone(),
                    target,
                    cached.Format,
                    request.TimeUs) { MaterialDiagnostics = cached.MaterialDiagnostics, ImageEffectDiagnostics = cached.Diagnostics, DiagnosticSnapshot = cached.SceneDiagnostics };
            }
        }

        using RenderFrame rendered = _inner.Render(request);
        byte[] pixels = rendered.PixelMemory.ToArray();
        RenderFrame result = new(pixels, target, rendered.Format, request.TimeUs) { MaterialDiagnostics = rendered.MaterialDiagnostics, ImageEffectDiagnostics = rendered.ImageEffectDiagnostics, DiagnosticSnapshot = rendered.DiagnosticSnapshot };

        lock (_gate)
        {
            // 另一个线程可能已经填入同一键；覆盖成相同内容是安全的，且保持容量上限。
            Add(key, new CacheEntry((byte[])pixels.Clone(), rendered.Format, rendered.ImageEffectDiagnostics, rendered.DiagnosticSnapshot, rendered.MaterialDiagnostics));
        }

        return result;
    }

    private void Add(RenderCacheKey key, CacheEntry entry)
    {
        if (_entries.Remove(key, out CacheEntry? previous))
        {
            _lru.Remove(previous.Node);
        }

        entry.Node = _lru.AddFirst(key);
        _entries[key] = entry;

        while (_entries.Count > _capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _entries.Remove(last.Value);
        }
    }

    private void Touch(RenderCacheKey key, CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        entry.Node = _lru.AddFirst(key);
    }

    private sealed class CacheEntry(byte[] pixels, PixelFormat format, ImmutableArray<RenderImageEffectDiagnostic> diagnostics,
        RuntimeDiagnosticSnapshot sceneDiagnostics, ImmutableArray<RenderMaterialDiagnostic> materialDiagnostics)
    {
        public ImmutableArray<RenderMaterialDiagnostic> MaterialDiagnostics { get; } = materialDiagnostics;
        public byte[] Pixels { get; } = pixels;

        public PixelFormat Format { get; } = format;
        public ImmutableArray<RenderImageEffectDiagnostic> Diagnostics { get; } = diagnostics;
        public RuntimeDiagnosticSnapshot SceneDiagnostics { get; } = sceneDiagnostics;

        public LinkedListNode<RenderCacheKey> Node { get; set; } = null!;
    }

    private sealed class RenderCacheKey(
        ProjectSnapshot snapshot,
        long timeUs,
        RenderTarget target,
        RenderQuality quality,
        bool transparent,
        PreparedRenderFrame? prepared) : IEquatable<RenderCacheKey>
    {
        private readonly ProjectSnapshot _snapshot = snapshot;
        private readonly long _timeUs = timeUs;
        private readonly RenderTarget _target = target;
        private readonly RenderQuality _quality = quality;
        private readonly bool _transparent = transparent;
        private readonly PreparedRenderFrame? _prepared = prepared;

        public bool Equals(RenderCacheKey? other) => other is not null
            && ReferenceEquals(_snapshot, other._snapshot)
            && _timeUs == other._timeUs
            && _target == other._target
            && _quality == other._quality
            && _transparent == other._transparent
            && ReferenceEquals(_prepared, other._prepared);

        public override bool Equals(object? obj) => Equals(obj as RenderCacheKey);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(_snapshot),
            _timeUs,
            _target,
            _quality,
            _transparent,
            _prepared is null ? 0 : RuntimeHelpers.GetHashCode(_prepared));
    }
}
