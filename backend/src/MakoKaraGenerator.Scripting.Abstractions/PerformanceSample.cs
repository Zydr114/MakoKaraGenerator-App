namespace MakoKaraGenerator.Scripting;

/// <summary>进度依据；Missing 与明确的尚未起唱不是同一状态。</summary>
public enum ProgressSource
{
    /// <summary>该元素有自己的时间。</summary>
    Token,
    /// <summary>注音片段自身的时间。</summary>
    RubySegment,
    /// <summary>只用于行级依据，不制造元素起唱。</summary>
    Line,
    /// <summary>源缺时间。</summary>
    Missing,
    /// <summary>源区间与当前文字映射不可用。</summary>
    Unavailable,
}

/// <summary>共享 LyricSampler 导出的真实歌曲微秒进度，不由脚本重分时间。</summary>
/// <param name="TimeRange">源区间；缺失为空。</param><param name="DurationUs">有效时长；缺失为空。</param>
/// <param name="ElapsedUs">相对源起点的时间；可以为负，不擅自修正。</param>
/// <param name="Progress">有效归一化进度；缺失为空。</param><param name="IsCurrent">处于半开演唱区间。</param>
/// <param name="IsSung">已到或越过终点。</param><param name="ProgressSource">依据。</param>
public sealed record PerformanceSample(TimeRange? TimeRange, long? DurationUs, long? ElapsedUs,
    double? Progress, bool IsCurrent, bool IsSung, ProgressSource ProgressSource);
