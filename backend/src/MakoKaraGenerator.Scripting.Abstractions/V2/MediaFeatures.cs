namespace MakoKaraGenerator.Scripting.V2;

/// <summary>仅当前 Text 或 Visual 调用有效的媒体采样句柄；不能保留到准备结果。</summary>
public sealed class MediaFeatures
{
    private readonly object _gate = new();
    private readonly MediaQueryCatalog _catalog;
    private readonly Vector2 _canvasSize;
    private readonly CancellationToken _token;
    private Func<MediaQueryProfile, Rect2, long, CancellationToken, MediaFeatureSample>? _sample;
    private bool _closed;

    internal MediaFeatures(MediaQueryCatalog catalog, Vector2 canvasSize,
        Func<MediaQueryProfile, Rect2, long, CancellationToken, MediaFeatureSample>? sample = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (canvasSize.X <= 0 || canvasSize.Y <= 0) throw new ArgumentOutOfRangeException(nameof(canvasSize));
        if ((catalog.Input is null) != (sample is null))
            throw new EffectOutputException("MediaFeatureBindingRequired", "固定媒体目录与宿主采样绑定必须同时提供。");
        _catalog = catalog; _canvasSize = canvasSize; _sample = sample; _token = token;
    }

    /// <summary>当前阶段的固定目录；保存目录本身不会保存此阶段服务。</summary>
    public MediaQueryCatalog Catalog { get { lock (_gate) { RequireOpen(); return _catalog; } } }

    /// <summary>对已声明查询和明确工程时刻求值；非 Available 返回空数值，非法调用仍拒绝。</summary>
    public MediaFeatureSample Sample(MediaQueryId query, Rect2 rect, long projectTimeUs)
    {
        lock (_gate)
        {
            RequireOpen();
            if (_catalog.Input is null || _sample is null)
                throw new EffectOutputException("MediaFeatureBindingRequired", "当前旧入口没有固定媒体绑定。");
            MediaQueryProfile profile = _catalog.Resolve(query);
            double width = profile.Space == MediaCoordinateSpace.SourceNormalized ? 1 : _canvasSize.X;
            double height = profile.Space == MediaCoordinateSpace.SourceNormalized ? 1 : _canvasSize.Y;
            if (projectTimeUs < 0 || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top)
                || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)
                || rect.Width <= 0 || rect.Height <= 0 || rect.Left < 0 || rect.Top < 0 || rect.Right > width || rect.Bottom > height)
                throw new EffectOutputException("InvalidMediaQuery", "媒体查询需要有效工程时刻及域内非空矩形。");
            MediaFeatureSample result = _sample(profile, rect, projectTimeUs, _token);
            RequireOpen();
            if (result is null || !ReferenceEquals(result.Input, _catalog.Input) || !ReferenceEquals(result.Query, profile)
                || result.Rect != rect || result.ProjectTimeUs != projectTimeUs)
                throw new EffectOutputException("MediaFeatureResultMismatch", "宿主媒体结果必须保留本次完整固定查询身份。");
            return result;
        }
    }

    internal void ValidateCatalog(MediaQueryCatalog catalog, Vector2 canvasSize)
    {
        lock (_gate)
        {
            RequireOpen();
            if (!ReferenceEquals(_catalog, catalog) || _canvasSize != canvasSize)
                throw new EffectOutputException("MediaFeatureBindingMismatch", "帧与采样句柄必须共用精确固定目录。");
        }
    }
    internal void Invalidate() { lock (_gate) { _closed = true; _sample = null; } }
    private void RequireOpen()
    {
        if (_closed) throw new EffectOutputException("InvalidMediaFeatureStage", "媒体采样句柄已离开所属阶段。");
        _token.ThrowIfCancellationRequested();
    }
}
