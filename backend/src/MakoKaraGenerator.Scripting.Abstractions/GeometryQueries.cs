using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>只读基本场景几何；不包含作者效果、背景、粒子或未来历史。</summary>
public sealed record BaseSceneGeometry
{
    /// <summary>真实采样时刻。</summary>
    public required long TimeUs { get; init; }
    /// <summary>筛选后的真实装载。</summary>
    public ImmutableArray<FrameLine> Lines { get; init; } = [];
    /// <summary>同一后端的基础元素族。</summary>
    public ImmutableArray<TokenGeometrySample> Tokens { get; init; } = [];
    /// <summary>同一后端的完整行。</summary>
    public ImmutableArray<LineGeometrySample> LineGeometry { get; init; } = [];
    /// <summary>源筛选匹配的真实提示标记。</summary>
    public ImmutableArray<FrameMark> Marks { get; init; } = [];
    /// <summary>匹配显示对象的真实祖先组；Project 保留完整节点目录。</summary>
    public ImmutableArray<FrameGroup> Groups { get; init; } = [];
    /// <summary>真实提示标记固定字形。</summary>
    public ImmutableArray<TextMarkGeometrySample> MarkGeometry { get; init; } = [];
    /// <summary>真实祖先组参考框及运行矩阵。</summary>
    public ImmutableArray<GroupGeometrySample> GroupGeometry { get; init; } = [];
}

/// <summary>阶段限定的基本布局查询，不能递归求完整效果帧或推进共享状态。</summary>
public sealed class GeometryQueries
{
    private Func<long, SourceFilter, BaseSceneGeometry>? _get;
    /// <summary>宿主建立本阶段只读查询。</summary>
    public GeometryQueries(Func<long, SourceFilter, BaseSceneGeometry> get) { ArgumentNullException.ThrowIfNull(get); _get = get; }
    /// <summary>查询真实时刻与源筛选；无可见对象返回空，不套用编辑试样。</summary>
    public BaseSceneGeometry GetBaseAt(long timeUs, SourceFilter filter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs); ArgumentNullException.ThrowIfNull(filter);
        var get = _get ?? throw new EffectOutputException("InvalidGeometryQueryStage", "几何查询通道已离开所属阶段。");
        return get(timeUs, filter);
    }
    /// <summary>宿主结束阶段并释放回调引用。</summary>
    public void Invalidate() => _get = null;
    /// <summary>没有注入宿主查询时拒绝，不伪造几何。</summary>
    public static GeometryQueries Unavailable => new(static (_, _) => throw new EffectOutputException("GeometryQueriesUnavailable", "此上下文没有几何查询服务。"));
}
