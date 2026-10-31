using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Timeline;

/// <summary>
/// 时间轴视图窗口：时间与像素之间的<b>唯一</b>映射。
/// </summary>
/// <remarks>
/// <para>
/// 标尺、波形采样、逐字块定位、播放头与命中测试<b>必须全部</b>使用同一个
/// <see cref="TimeAxis"/> 实例。这条约束是"三条轨道必须对齐"的实现机制，
/// 见 docs/decisions.md ADR-0011。
/// </para>
/// <para>
/// 反例（旧项目 MakoKaraGenerator 的病根）：绘制路径用 <c>(Width - 12)</c> 计算时间↔像素，
/// 命中测试用 <c>(Width - 24)</c>，且各自重复实现一遍。
/// 结果是视觉上勉强对齐、点击却偏几像素，缩放后偏差被放大。
/// </para>
/// <para>
/// 本类型是值类型且不可变：任何平移或缩放都返回新实例，
/// 避免多条轨道因为持有一份共享的可变状态而出现"某条轨道没跟上"的问题。
/// </para>
/// </remarks>
public readonly record struct TimeAxis
{
    /// <summary>允许的最小缩放（每秒对应的像素数）。防止缩到退化成一条线。</summary>
    public const double MinPixelsPerSecond = 0.5d;

    /// <summary>允许的最大缩放。约等于 1 像素对应 50 微秒。</summary>
    public const double MaxPixelsPerSecond = 20000d;

    private const double MicrosecondsPerSecond = 1_000_000d;

    private TimeAxis(double viewportWidthPx, double pixelsPerSecond, long viewStartUs)
    {
        ViewportWidthPx = viewportWidthPx;
        PixelsPerSecond = pixelsPerSecond;
        ViewStartUs = viewStartUs;
    }

    /// <summary>视口宽度（像素）。所有轨道共用同一个值。</summary>
    public double ViewportWidthPx { get; }

    /// <summary>缩放：每秒对应多少像素。</summary>
    public double PixelsPerSecond { get; }

    /// <summary>视口左端对应的时刻（微秒）。</summary>
    public long ViewStartUs { get; }

    /// <summary>视口覆盖的时间长度（微秒）。</summary>
    public long VisibleDurationUs => UsFromPixels(ViewportWidthPx);

    /// <summary>视口右端对应的时刻（微秒）。</summary>
    public long ViewEndUs => ViewStartUs + VisibleDurationUs;

    /// <summary>创建一个视图窗口。</summary>
    /// <param name="viewportWidthPx">视口宽度（像素），必须为正。</param>
    /// <param name="pixelsPerSecond">缩放，必须为正且有限；超出范围会被钳制。</param>
    /// <param name="viewStartUs">视口左端时刻（微秒），负值会被钳制为 0。</param>
    /// <returns>新的视图窗口。</returns>
    /// <exception cref="ArgumentOutOfRangeException">视口宽度非正，或缩放不是有限正数时抛出。</exception>
    public static TimeAxis Create(double viewportWidthPx, double pixelsPerSecond, long viewStartUs = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidthPx);

        if (!double.IsFinite(pixelsPerSecond) || pixelsPerSecond <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pixelsPerSecond),
                pixelsPerSecond,
                "缩放必须是有限正数。");
        }

        return new TimeAxis(
            viewportWidthPx,
            Math.Clamp(pixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond),
            Math.Max(0L, viewStartUs));
    }

    /// <summary>把时间换算为相对视口左端的横向位置。</summary>
    /// <param name="timeUs">时刻（微秒）。</param>
    /// <returns>横向像素位置；视口左端为 0，视口外可能为负或大于视口宽度。</returns>
    public double TimeToX(long timeUs) => PixelsFromUs(timeUs - ViewStartUs);

    /// <summary>把相对视口左端的横向位置换算为时间。</summary>
    /// <param name="x">横向像素位置。</param>
    /// <returns>对应的时刻（微秒）。</returns>
    public long XToTime(double x) => ViewStartUs + UsFromPixels(x);

    /// <summary>把一段时长换算为像素长度。</summary>
    /// <param name="durationUs">时长（微秒）。</param>
    /// <returns>像素长度。</returns>
    public double PixelsFromUs(long durationUs) => durationUs / MicrosecondsPerSecond * PixelsPerSecond;

    /// <summary>把像素长度换算为时长。</summary>
    /// <param name="pixels">像素长度。</param>
    /// <returns>时长（微秒）。</returns>
    public long UsFromPixels(double pixels) => (long)Math.Round(pixels / PixelsPerSecond * MicrosecondsPerSecond);

    /// <summary>改变视口宽度，保持缩放与左端时刻不变。</summary>
    /// <param name="viewportWidthPx">新的视口宽度（像素）。</param>
    /// <returns>新的视图窗口。</returns>
    public TimeAxis WithViewportWidth(double viewportWidthPx) =>
        Create(viewportWidthPx, PixelsPerSecond, ViewStartUs);

    /// <summary>
    /// 以指定像素位置为锚点缩放，锚点处的时间保持不变。
    /// </summary>
    /// <param name="anchorX">锚点的横向像素位置。</param>
    /// <param name="factor">缩放倍数；大于 1 放大（看得更细）。</param>
    /// <returns>新的视图窗口。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> 不是有限正数时抛出。</exception>
    /// <remarks>
    /// "锚点时间不动"是滚轮缩放的体验关键：鼠标指着哪一刻，缩放后那一刻仍在鼠标下。
    /// </remarks>
    public TimeAxis ZoomedAt(double anchorX, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "缩放倍数必须是有限正数。");
        }

        long anchorTimeUs = XToTime(anchorX);
        double pixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);

        // 反解 ViewStartUs，使 TimeToX(anchorTimeUs) 仍等于 anchorX。
        long newStartUs = anchorTimeUs - (long)Math.Round(anchorX / pixelsPerSecond * MicrosecondsPerSecond);

        return Create(ViewportWidthPx, pixelsPerSecond, newStartUs);
    }

    /// <summary>横向平移。</summary>
    /// <param name="deltaPx">平移量（像素）；正值表示内容向右移动，即视口向左移动。</param>
    /// <returns>新的视图窗口。</returns>
    public TimeAxis PannedBy(double deltaPx) =>
        Create(ViewportWidthPx, PixelsPerSecond, ViewStartUs - UsFromPixels(deltaPx));

    /// <summary>
    /// 把视口钳制到 <c>[0, contentDurationUs]</c> 之内。
    /// </summary>
    /// <param name="contentDurationUs">内容总时长（微秒）。</param>
    /// <returns>新的视图窗口。</returns>
    /// <remarks>
    /// 内容比视口短时（整曲放得下）左端归零，而不是把内容顶到右边，避免出现大片空白。
    /// </remarks>
    public TimeAxis ClampedTo(long contentDurationUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentDurationUs);

        long visible = VisibleDurationUs;

        if (visible <= 0 || contentDurationUs <= visible)
        {
            return Create(ViewportWidthPx, PixelsPerSecond, 0);
        }

        long maxStartUs = contentDurationUs - visible;
        return Create(ViewportWidthPx, PixelsPerSecond, Math.Clamp(ViewStartUs, 0, maxStartUs));
    }

    /// <summary>
    /// 构造一个让指定时间区间恰好填满视口的视图窗口。
    /// </summary>
    /// <param name="startUs">区间起点（微秒）。</param>
    /// <param name="endUs">区间终点（微秒），必须大于起点。</param>
    /// <param name="paddingRatio">两侧留白占区间长度的比例。</param>
    /// <returns>新的视图窗口。</returns>
    /// <exception cref="ArgumentOutOfRangeException">区间非法时抛出。</exception>
    /// <remarks>用于"聚焦到当前歌词行"这类操作（docs/development.md §8.4 的 F 键）。</remarks>
    public TimeAxis ShowingRange(long startUs, long endUs, double paddingRatio = 0.08d)
    {
        if (endUs <= startUs)
        {
            throw new ArgumentOutOfRangeException(nameof(endUs), endUs, "区间终点必须大于起点。");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(paddingRatio);

        long length = endUs - startUs;
        long paddedLength = length + (long)Math.Round(length * paddingRatio * 2d);

        return Create(ViewportWidthPx, ViewportWidthPx / (paddedLength / MicrosecondsPerSecond), startUs - (long)Math.Round(length * paddingRatio));
    }

    /// <summary>以 <c>"[start, end) px/s=…"</c> 形式返回，便于诊断。</summary>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[{ViewStartUs}, {ViewEndUs}) us  {PixelsPerSecond:0.##} px/s  w={ViewportWidthPx:0.##}");
}

/// <summary>时间吸附方式。</summary>
/// <remarks>
/// docs/development.md §8.3 要求"帧、毫秒、节拍等可选吸附方式"。
/// 节拍吸附需要先有 BPM 与拍号信息（docs §18 待决策第 11 项），
/// 因此本阶段先提供关闭、毫秒与帧三档。
/// </remarks>
public enum TimeSnapMode
{
    /// <summary>不吸附。</summary>
    None = 0,

    /// <summary>吸附到最近的毫秒。</summary>
    Millisecond = 1,

    /// <summary>吸附到最近的视频帧边界。</summary>
    Frame = 2,
}

/// <summary>按指定方式吸附时间。</summary>
public static class TimeSnapping
{
    /// <summary>把时间吸附到最近的目标点。</summary>
    /// <param name="timeUs">原始时刻（微秒）。</param>
    /// <param name="mode">吸附方式。</param>
    /// <param name="frameRate">帧率，仅在 <see cref="TimeSnapMode.Frame"/> 时使用。</param>
    /// <returns>吸附后的时刻。</returns>
    public static long Snap(long timeUs, TimeSnapMode mode, FrameRate frameRate) => mode switch
    {
        TimeSnapMode.Millisecond => SnapToMillisecond(timeUs),
        TimeSnapMode.Frame => TimeUs.FromFrames(TimeUs.ToFrames(timeUs, frameRate), frameRate),
        _ => timeUs,
    };

    /// <summary>吸附到最近的毫秒。</summary>
    /// <param name="timeUs">原始时刻（微秒）。</param>
    /// <returns>吸附后的时刻。</returns>
    public static long SnapToMillisecond(long timeUs) =>
        timeUs < 0 ? -TimeUs.ToMillisecondsRounded(-timeUs) * TimeUs.PerMillisecond
                   : TimeUs.ToMillisecondsRounded(timeUs) * TimeUs.PerMillisecond;
}
