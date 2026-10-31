namespace MakoKaraGenerator.Scripting;

/// <summary>有效的歌曲微秒半开区间 [StartUs,EndUs)；不用于表示缺失时间。</summary>
public readonly record struct TimeRange
{
    /// <summary>创建非负、严格递增且可表示时长的区间。</summary>
    /// <param name="startUs">起点，整数微秒。</param><param name="endUs">终点，不包含。</param>
    public TimeRange(long startUs, long endUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startUs);
        if (endUs <= startUs) throw new ArgumentOutOfRangeException(nameof(endUs), "终点必须晚于起点。");
        StartUs = startUs; EndUs = endUs;
    }
    /// <summary>起点。</summary>
    public long StartUs { get; }
    /// <summary>终点，不包含。</summary>
    public long EndUs { get; }
    /// <summary>整数微秒时长。</summary>
    public long DurationUs => EndUs - StartUs;
    /// <summary>默认结构无效，不代表 0 时长演唱。</summary>
    public bool IsValid => StartUs >= 0 && EndUs > StartUs;
    /// <summary>按半开语义包含时刻。</summary>
    public bool Contains(long timeUs) => IsValid && timeUs >= StartUs && timeUs < EndUs;
}
