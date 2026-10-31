namespace MakoKaraGenerator.Timeline;

/// <summary>时间轴上的轨道。</summary>
public enum TimelineLane
{
    /// <summary>整曲缩略条。使用独立的时间坐标系。</summary>
    Overview = 0,

    /// <summary>时间标尺。</summary>
    Ruler = 1,

    /// <summary>波形轨。</summary>
    Waveform = 2,

    /// <summary>逐字块轨。</summary>
    Token = 3,

    /// <summary>歌词行的独立时间区间轨。</summary>
    Line = 4,
}

/// <summary>一条轨道的矩形区域。</summary>
/// <param name="Lane">轨道。</param>
/// <param name="Left">左端（像素）。</param>
/// <param name="Top">上端（像素）。</param>
/// <param name="Width">宽度（像素）。</param>
/// <param name="Height">高度（像素）。</param>
public readonly record struct TimelineLaneRect(TimelineLane Lane, double Left, double Top, double Width, double Height)
{
    /// <summary>右端（像素）。</summary>
    public double Right => Left + Width;

    /// <summary>下端（像素）。</summary>
    public double Bottom => Top + Height;

    /// <summary>判断纵坐标是否落在本轨道内（下端不含）。</summary>
    /// <param name="y">纵坐标。</param>
    /// <returns>落在本轨道内时为 <see langword="true"/>。</returns>
    public bool ContainsY(double y) => y >= Top && y < Bottom;
}

/// <summary>各轨道的高度。</summary>
/// <param name="Overview">缩略条高度。</param>
/// <param name="Ruler">标尺高度。</param>
/// <param name="Waveform">波形轨高度。</param>
/// <param name="Token">逐字块轨高度。</param>
/// <param name="Line">歌词行轨高度。</param>
/// <remarks>
/// ⚠️ 刻意<b>不</b>给主构造函数的参数写默认值。
/// 结构体的主构造函数即使全部参数都有默认值，<c>new TimelineLaneHeights()</c>
/// 也<b>不会</b>使用它们：它绑定到结构体隐式的无参构造函数，得到全零。
/// （已实测：<c>new H()</c> 得到 0/0，而不是声明的默认值。）
/// 默认值因此只能通过 <see cref="Default"/> 显式取得。
/// </remarks>
public readonly record struct TimelineLaneHeights(double Overview, double Ruler, double Waveform, double Token, double Line = 22d)
{
    /// <summary>默认高度：缩略 28、标尺 22、波形 60、行 22、逐字块 56。</summary>
    public static TimelineLaneHeights Default { get; } = new(28d, 22d, 60d, 56d, 22d);
}

/// <summary>
/// 时间轴的轨道布局与共享映射。
/// </summary>
/// <remarks>
/// <para>
/// 本类型的职责就是把 ADR-0011 的对齐不变式变成代码结构：
/// </para>
/// <list type="number">
///   <item><see cref="Axis"/> 是标尺、波形轨与逐字块轨<b>共用</b>的唯一映射；</item>
///   <item>这三条轨道的 <see cref="TimelineLaneRect.Left"/> 与
///         <see cref="TimelineLaneRect.Width"/> <b>永远相同</b>（由
///         <see cref="AllMainLanesShareHorizontalExtent"/> 断言）；</item>
///   <item>缩略条使用独立的 <see cref="OverviewAxis"/>（整曲压缩），
///         只负责显示"主视图现在看的是哪一段"以及接受拖拽定位。</item>
/// </list>
/// <para>
/// 控件不允许自己计算这些值：它必须通过本类型取矩形与映射，
/// 因此"某条轨道多加了一个内边距"这种事在结构上就不会发生。
/// </para>
/// </remarks>
public sealed class TimelineLayout
{
    private readonly TimelineLaneRect[] _lanes;
    private readonly Dictionary<TimelineLane, int> _indexByLane;

    private TimelineLayout(
        TimeAxis axis,
        TimeAxis overviewAxis,
        TimelineLaneHeights heights,
        TimelineLaneRect[] lanes)
    {
        Axis = axis;
        OverviewAxis = overviewAxis;
        Heights = heights;
        _lanes = lanes;
        _indexByLane = new Dictionary<TimelineLane, int>(lanes.Length);

        for (int i = 0; i < lanes.Length; i++)
        {
            _indexByLane[lanes[i].Lane] = i;
        }
    }

    /// <summary>标尺、波形轨与逐字块轨共用的映射。</summary>
    public TimeAxis Axis { get; }

    /// <summary>缩略条使用的独立映射（整曲压缩）。</summary>
    public TimeAxis OverviewAxis { get; }

    /// <summary>各轨道高度。</summary>
    public TimelineLaneHeights Heights { get; }

    /// <summary>自上而下的轨道矩形。</summary>
    public IReadOnlyList<TimelineLaneRect> Lanes => _lanes;

    /// <summary>按轨道取矩形。</summary>
    /// <param name="lane">轨道。</param>
    /// <returns>该轨道的矩形。</returns>
    /// <exception cref="KeyNotFoundException">轨道不存在时抛出。</exception>
    public TimelineLaneRect this[TimelineLane lane] => _lanes[_indexByLane[lane]];

    /// <summary>时间轴总高度（像素）。</summary>
    public double TotalHeight => _lanes.Length == 0 ? 0d : _lanes[^1].Bottom;

    /// <summary>
    /// 三条主轨道是否共享完全相同的水平范围。
    /// </summary>
    /// <remarks>
    /// 这是对齐不变式的自检属性。测试会断言它为 <see langword="true"/>；
    /// 它同时也是"缩略条之外不得有独立内边距"这条约定的可执行表达。
    /// </remarks>
    public bool AllMainLanesShareHorizontalExtent
    {
        get
        {
            TimelineLaneRect ruler = this[TimelineLane.Ruler];
            TimelineLaneRect waveform = this[TimelineLane.Waveform];
            TimelineLaneRect line = this[TimelineLane.Line];
            TimelineLaneRect token = this[TimelineLane.Token];

            return ruler.Left == waveform.Left
                && ruler.Left == line.Left
                && ruler.Left == token.Left
                && ruler.Width == waveform.Width
                && ruler.Width == line.Width
                && ruler.Width == token.Width;
        }
    }

    /// <summary>创建轨道布局。</summary>
    /// <param name="viewportWidthPx">视口宽度（像素）。</param>
    /// <param name="axis">主视图映射。</param>
    /// <param name="contentDurationUs">整曲时长（微秒）；为 0 时缩略条按单屏处理。</param>
    /// <param name="heights">各轨道高度；省略时使用默认值。</param>
    /// <returns>轨道布局。</returns>
    public static TimelineLayout Create(
        double viewportWidthPx,
        TimeAxis axis,
        long contentDurationUs,
        TimelineLaneHeights? heights = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidthPx);
        ArgumentOutOfRangeException.ThrowIfNegative(contentDurationUs);

        TimelineLaneHeights laneHeights = heights ?? TimelineLaneHeights.Default;

        // 缩略条：整曲塞进同一视口宽度。内容为空时退化为与主视图同样的缩放，
        // 这样它仍然会画出视窗框，而不是除零。
        double overviewPixelsPerSecond = contentDurationUs > 0
            ? viewportWidthPx / (contentDurationUs / 1_000_000d)
            : axis.PixelsPerSecond;

        TimeAxis overviewAxis = TimeAxis.Create(viewportWidthPx, overviewPixelsPerSecond);

        TimelineLaneRect[] lanes =
        [
            new TimelineLaneRect(TimelineLane.Overview, 0d, 0d, viewportWidthPx, laneHeights.Overview),
            new TimelineLaneRect(TimelineLane.Ruler, 0d, laneHeights.Overview, viewportWidthPx, laneHeights.Ruler),
            new TimelineLaneRect(
                TimelineLane.Waveform,
                0d,
                laneHeights.Overview + laneHeights.Ruler,
                viewportWidthPx,
                laneHeights.Waveform),
            new TimelineLaneRect(
                TimelineLane.Line,
                0d,
                laneHeights.Overview + laneHeights.Ruler + laneHeights.Waveform,
                viewportWidthPx,
                laneHeights.Line),
            new TimelineLaneRect(
                TimelineLane.Token,
                0d,
                laneHeights.Overview + laneHeights.Ruler + laneHeights.Waveform + laneHeights.Line,
                viewportWidthPx,
                laneHeights.Token),
        ];

        return new TimelineLayout(axis, overviewAxis, laneHeights, lanes);
    }

    /// <summary>取纵坐标所在的轨道。</summary>
    /// <param name="y">纵坐标（像素）。</param>
    /// <returns>轨道；落在所有轨道之外时为 <see langword="null"/>。</returns>
    public TimelineLane? LaneAtY(double y)
    {
        foreach (TimelineLaneRect lane in _lanes)
        {
            if (lane.ContainsY(y))
            {
                return lane.Lane;
            }
        }

        return null;
    }

    /// <summary>
    /// 计算缩略条上"当前主视图覆盖范围"的矩形。
    /// </summary>
    /// <returns>左端与宽度（像素）。</returns>
    /// <remarks>
    /// 缩略条与主视图的时间度量不同（ADR-0011 不变式 5），
    /// 这个视窗框是两者唯一的视觉联系；把它算在这里而不是控件里，
    /// 可以避免出现"缩略条自己按比例估一个框"这类第二套映射。
    /// </remarks>
    public (double Left, double Width) GetOverviewViewport()
    {
        double left = OverviewAxis.TimeToX(Axis.ViewStartUs);
        double right = OverviewAxis.TimeToX(Axis.ViewEndUs);

        double clampedLeft = Math.Clamp(left, 0d, OverviewAxis.ViewportWidthPx);
        double clampedRight = Math.Clamp(right, 0d, OverviewAxis.ViewportWidthPx);

        return (clampedLeft, Math.Max(0d, clampedRight - clampedLeft));
    }
}
