namespace MakoKaraGenerator.Core.Time;

/// <summary>
/// 半开时间区间 <c>[startUs, endUs)</c>，单位为微秒。
/// </summary>
/// <remarks>
/// <para>
/// 语义见 docs/development.md §5.3：<c>startUs >= 0</c>、<c>endUs > startUs</c>，
/// 相邻区间可以无缝相接，允许空隙，但重叠必须被显式报告而不是静默覆盖。
/// </para>
/// <para>
/// <b>本类型刻意允许表示非法数据。</b> 原因是校验器（CORE-05）必须能报告
/// "行/元素时间不满足约束" 并把状态标记为 <see cref="Lyrics.TimingStatus.Invalid"/>；
/// 如果构造函数直接抛异常，就无法把非法项目文件读进内存并给出可读诊断。
/// 因此：
/// <list type="bullet">
///   <item>可信代码路径（新建、编辑、导入后校验通过）应使用 <see cref="Create"/>；</item>
///   <item>反序列化与外部输入应使用 <see cref="TryCreate"/> 并记录诊断；</item>
///   <item>使用 <see cref="DurationUs"/>、<see cref="ProgressAt"/> 前必须先确认 <see cref="IsValid"/>。</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="StartUs">区间起点（含），微秒。</param>
/// <param name="EndUs">区间终点（不含），微秒。</param>
public readonly record struct TimeRange(long StartUs, long EndUs)
{
    /// <summary>判断一对数值是否满足区间约束，不构造实例。</summary>
    /// <param name="startUs">区间起点。</param>
    /// <param name="endUs">区间终点。</param>
    /// <returns>满足 <c>startUs >= 0</c> 且 <c>endUs > startUs</c> 时为 <see langword="true"/>。</returns>
    public static bool IsValidPair(long startUs, long endUs) => startUs >= 0 && endUs > startUs;

    /// <summary>本区间是否满足约束。</summary>
    public bool IsValid => IsValidPair(StartUs, EndUs);

    /// <summary>创建一个已校验的区间。</summary>
    /// <param name="startUs">区间起点，必须非负。</param>
    /// <param name="endUs">区间终点，必须大于起点。</param>
    /// <returns>新的区间实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">不满足区间约束时抛出。</exception>
    public static TimeRange Create(long startUs, long endUs)
    {
        if (startUs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startUs), startUs, "区间起点必须非负。");
        }

        if (endUs <= startUs)
        {
            throw new ArgumentOutOfRangeException(nameof(endUs), endUs, "区间终点必须大于起点。");
        }

        return new TimeRange(startUs, endUs);
    }

    /// <summary>尝试创建一个区间，不抛异常。</summary>
    /// <param name="startUs">区间起点。</param>
    /// <param name="endUs">区间终点。</param>
    /// <param name="range">成功时为创建的区间；失败时为 <c>default</c>。</param>
    /// <returns>满足约束时为 <see langword="true"/>。</returns>
    public static bool TryCreate(long startUs, long endUs, out TimeRange range)
    {
        range = new TimeRange(startUs, endUs);
        return range.IsValid;
    }

    /// <summary>区间长度（微秒）。</summary>
    /// <exception cref="InvalidOperationException"><see cref="IsValid"/> 为 <see langword="false"/> 时抛出。</exception>
    /// <remarks>不对非法区间返回负数：静默的错误数值比异常更难排查。</remarks>
    public long DurationUs => TryGetDurationUs(out long durationUs)
        ? durationUs
        : throw new InvalidOperationException($"区间 [{StartUs}, {EndUs}) 非法，无法计算时长。");

    /// <summary>尝试取得区间长度。</summary>
    /// <param name="durationUs">成功时为区间长度（微秒）。</param>
    /// <returns>区间合法时为 <see langword="true"/>。</returns>
    public bool TryGetDurationUs(out long durationUs)
    {
        durationUs = IsValid ? EndUs - StartUs : 0;
        return IsValid;
    }

    /// <summary>判断某个时刻是否落在区间内（半开区间，终点不含）。</summary>
    /// <param name="timeUs">待判断的时刻。</param>
    /// <returns>落在 <c>[StartUs, EndUs)</c> 内时为 <see langword="true"/>。</returns>
    public bool Contains(long timeUs) => timeUs >= StartUs && timeUs < EndUs;

    /// <summary>
    /// 计算逐字进度，公式为 <c>clamp((t - startUs) / (endUs - startUs), 0, 1)</c>
    /// （docs/development.md §5.3）。
    /// </summary>
    /// <param name="timeUs">当前播放时刻。</param>
    /// <returns>0 到 1 之间的进度值。</returns>
    /// <exception cref="InvalidOperationException"><see cref="IsValid"/> 为 <see langword="false"/> 时抛出。</exception>
    public double ProgressAt(long timeUs)
    {
        if (!TryGetDurationUs(out long durationUs))
        {
            throw new InvalidOperationException($"区间 [{StartUs}, {EndUs}) 非法，无法计算进度。");
        }

        if (timeUs <= StartUs)
        {
            return 0d;
        }

        if (timeUs >= EndUs)
        {
            return 1d;
        }

        return (double)(timeUs - StartUs) / durationUs;
    }

    /// <summary>判断两个半开区间是否有重叠。</summary>
    /// <param name="other">另一个区间。</param>
    /// <returns>存在非空交集时为 <see langword="true"/>。</returns>
    public bool Overlaps(TimeRange other) => StartUs < other.EndUs && other.StartUs < EndUs;

    /// <summary>求两个区间的交集。</summary>
    /// <param name="other">另一个区间。</param>
    /// <returns>交集；无交集时为 <see langword="null"/>。</returns>
    public TimeRange? Intersect(TimeRange other)
    {
        long start = Math.Max(StartUs, other.StartUs);
        long end = Math.Min(EndUs, other.EndUs);

        return end > start ? new TimeRange(start, end) : null;
    }

    /// <summary>整体平移，保持时长不变。</summary>
    /// <param name="deltaUs">平移量（微秒），可为负。</param>
    /// <returns>平移后的区间（可能因此变为非法，调用方需自行校验）。</returns>
    /// <exception cref="OverflowException">平移结果溢出 <see cref="long"/> 时抛出。</exception>
    public TimeRange Shift(long deltaUs) =>
        new(checked(StartUs + deltaUs), checked(EndUs + deltaUs));

    /// <summary>以新的起点构造区间，保持终点不变。</summary>
    /// <param name="startUs">新的起点。</param>
    /// <returns>新的区间（可能因此变为非法，调用方需自行校验）。</returns>
    public TimeRange WithStart(long startUs) => new(startUs, EndUs);

    /// <summary>以新的终点构造区间，保持起点不变。</summary>
    /// <param name="endUs">新的终点。</param>
    /// <returns>新的区间（可能因此变为非法，调用方需自行校验）。</returns>
    public TimeRange WithEnd(long endUs) => new(StartUs, endUs);

    /// <summary>以 <c>"[startUs, endUs)"</c> 形式返回，便于诊断输出。</summary>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{StartUs}, {EndUs})");
}
