using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>固定输入的四类只读源索引；查询不读取作者输出，也不推进历史。</summary>
public sealed class SourceIndexes
{
    private readonly SourceIndexLease _lease;
    internal SourceIndexes(TimelineIndex timeline, ISourceIndexQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        Timeline = timeline; _lease = new(query);
        Presentations = new(_lease); Layout = new(_lease); Cursors = new(_lease);
    }
    /// <summary>真实源事件与有限键序号。</summary>
    public TimelineIndex Timeline { get; }
    /// <summary>真实装载的揭示、隐藏、准备与原布局退出窗；不增加演唱事件。</summary>
    public SourcePresentationIndex Presentations { get; }
    /// <summary>同一生产布局后端的任意时刻基础几何。</summary>
    public SourceLayoutIndex Layout { get; }
    /// <summary>分轨道与身份区段的基础播放头，不是最终作者几何 memo。</summary>
    public SourceCursorIndex Cursors { get; }
    internal void Invalidate() => _lease.Invalidate();
    internal void SetQueryCancellation(CancellationToken token) => _lease.Query.SetCancellation(token);
}

/// <summary>一次真实源装载的固定时间身份；没有隐藏边界时保留 null。</summary>
public sealed record SourcePresentationLoad
{
    /// <summary>固定装载身份；Binding 中的阶段与进入进度为揭示边界的源采样。</summary>
    public SourcePresentationLoad(FrameLine binding, long revealTimeUs, long? hideTimeUs, int loadOrdinal,
        TimeRange? preparationRange = null, TimeRange? layoutExitRange = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.BindingId.IsValid || !binding.LineId.IsValid || revealTimeUs < 0 || hideTimeUs is { } hide && hide <= revealTimeUs
            || loadOrdinal < 0 || preparationRange is { IsValid: false } || layoutExitRange is { IsValid: false })
            throw new ArgumentException("源装载身份或时间窗无效。", nameof(binding));
        Binding = binding; RevealTimeUs = revealTimeUs; HideTimeUs = hideTimeUs; LoadOrdinal = loadOrdinal;
        PreparationRange = preparationRange; LayoutExitRange = layoutExitRange;
    }
    /// <summary>真实源行与装载，不制造新的演唱 occurrence。</summary>
    public FrameLine Binding { get; }
    /// <summary>实际揭示边界，包含。</summary>
    public long RevealTimeUs { get; }
    /// <summary>实际隐藏边界，不包含；源仍保留时为空。</summary>
    public long? HideTimeUs { get; }
    /// <summary>固定源事件表中的装载序号，从零开始。</summary>
    public int LoadOrdinal { get; }
    /// <summary>源前奏准备窗，与演唱区间独立。</summary>
    public TimeRange? PreparationRange { get; }
    /// <summary>hide 前已有布局退出窗；不包括 hide 后的作者退出副本。</summary>
    public TimeRange? LayoutExitRange { get; }
    /// <summary>按真实半开可见区间查询；缺少隐藏边界不猜测工程时长。</summary>
    public bool Contains(long timeUs) => timeUs >= RevealTimeUs && (HideTimeUs is not { } hide || timeUs < hide);
}

/// <summary>精确时刻的源展示相位，不替代元素的 Missing／Current／Sung。</summary>
/// <param name="Load">固定装载。</param><param name="TimeUs">查询时刻。</param>
/// <param name="DisplayPhase">准备、演唱或源布局保留相位。</param>
/// <param name="LayoutExitProgress">原 hide 前布局退出进度，未进入时为零。</param>
public sealed record SourcePresentationSample(SourcePresentationLoad Load, long TimeUs, DisplayPhase DisplayPhase, double LayoutExitProgress);

/// <summary>固定源装载查询；封闭 SourceFilter 始终与装载时固定 subscription 相交。</summary>
public sealed class SourcePresentationIndex
{
    private readonly SourceIndexLease _lease;
    internal SourcePresentationIndex(SourceIndexLease lease) => _lease = lease;
    /// <summary>取得订阅内的真实装载；范围外或不存在返回 null。</summary>
    public SourcePresentationLoad? Get(BindingId bindingId) => _lease.Query.Presentation(bindingId);
    /// <summary>返回精确时刻可见的源装载，不包含隐藏后副本。</summary>
    public ImmutableArray<SourcePresentationSample> GetAt(long timeUs, SourceFilter filter) => _lease.Query.PresentationsAt(timeUs, filter);
    /// <summary>返回与半开范围相交的真实装载，按揭示边界和装载序号稳定排序。</summary>
    public ImmutableArray<SourcePresentationLoad> GetRange(TimeRange range, SourceFilter filter) => _lease.Query.PresentationsIn(range, filter);
}

/// <summary>任意时刻的基础布局查询；复用正式几何后端与固定 shaping，不使用编辑试样。</summary>
public sealed class SourceLayoutIndex
{
    private readonly SourceIndexLease _lease;
    internal SourceLayoutIndex(SourceIndexLease lease) => _lease = lease;
    /// <summary>真实 t 的基础几何；未显示返回空集合，几何缺失不会伪造。</summary>
    public BaseSceneGeometry GetBaseAt(long timeUs, SourceFilter filter) => _lease.Query.LayoutAt(timeUs, filter);
}

/// <summary>一个基础播放头的真实身份区段；ActiveRange 是演唱区间与实际装载可见区间的交集。</summary>
public sealed record SourceCursorSegment
{
    /// <summary>建立不跨身份的真实半开区段；Identity 保留原始完整演唱区间。</summary>
    public SourceCursorSegment(CursorTrackId trackId, CursorIdentity identity, TimeRange activeRange)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!trackId.IsValid || !activeRange.IsValid || activeRange.StartUs < identity.TimeRange.StartUs || activeRange.EndUs > identity.TimeRange.EndUs)
            throw new ArgumentException("基础播放头区段必须属于同一真实身份。", nameof(activeRange));
        TrackId = trackId; Identity = identity; ActiveRange = activeRange;
    }
    /// <summary>真实分支程序轨道。</summary>
    public CursorTrackId TrackId { get; }
    /// <summary>真实源、装载和演唱身份。</summary>
    public CursorIdentity Identity { get; }
    /// <summary>该身份实际可作为 Current 的半开时间窗。</summary>
    public TimeRange ActiveRange { get; }
}

/// <summary>源基础播放头索引；保留并发、间隙与断点，不跨身份插值。</summary>
public sealed class SourceCursorIndex
{
    private readonly SourceIndexLease _lease;
    internal SourceCursorIndex(SourceIndexLease lease) => _lease = lease;
    /// <summary>查询单轨道与半开窗口相交的完整身份区段；无此轨道返回空。</summary>
    public ImmutableArray<SourceCursorSegment> GetSegments(CursorTrackId trackId, TimeRange range) => _lease.Query.CursorSegments(trackId, range);
    /// <summary>读取同一生产基础几何上的当前播放头；退出副本与作者文字输出不参与。</summary>
    public ImmutableArray<PlaybackCursor> GetBaseAt(long timeUs, SourceFilter filter, CursorAnchorMode anchorMode)
        => _lease.Query.CursorsAt(timeUs, filter, anchorMode);
}

internal interface ISourceIndexQuery
{
    void SetCancellation(CancellationToken token);
    SourcePresentationLoad? Presentation(BindingId bindingId);
    ImmutableArray<SourcePresentationSample> PresentationsAt(long timeUs, SourceFilter filter);
    ImmutableArray<SourcePresentationLoad> PresentationsIn(TimeRange range, SourceFilter filter);
    BaseSceneGeometry LayoutAt(long timeUs, SourceFilter filter);
    ImmutableArray<SourceCursorSegment> CursorSegments(CursorTrackId trackId, TimeRange range);
    ImmutableArray<PlaybackCursor> CursorsAt(long timeUs, SourceFilter filter, CursorAnchorMode anchorMode);
}

internal sealed class SourceIndexLease(ISourceIndexQuery? query)
{
    private ISourceIndexQuery? _query = query;
    private bool _expired;
    internal ISourceIndexQuery Query => _query ?? throw new EffectOutputException(_expired ? "SourceIndexExpired" : "SourceIndexUnavailable",
        _expired ? "固定源索引已经离开所属会话。" : "此上下文没有实际源索引查询服务。");
    internal void Invalidate() { _expired = true; _query = null; }
}
