using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>音频特征的可用性；不可用不等同于已分析的静音。</summary>
public enum AudioAvailability
{
    /// <summary>没有音频源。</summary>
    NoAudio,
    /// <summary>没有完成分析。</summary>
    NotAnalyzed,
    /// <summary>分析已失败，保留原因。</summary>
    AnalysisFailed,
    /// <summary>当前区间已完成分析。</summary>
    Available,
    /// <summary>查询超出已分析音频的半开时长。</summary>
    OutsideRange,
}

/// <summary>分析器提供的规范低频／节奏值；不包含 FFT 或频带数组。</summary>
public readonly record struct AudioFeatureValues
{
    /// <summary>创建分析值；无节拍估计时 phase=null、confidence=0，不假造拍点。</summary>
    public AudioFeatureValues(double bassLevel, double pulse, double? beatPhase, double beatConfidence)
    {
        ColorRgba.Unit(bassLevel); ColorRgba.Unit(pulse); ColorRgba.Unit(beatConfidence);
        if (beatPhase is { } phase)
        {
            ColorRgba.Unit(phase);
            if (phase == 1) throw new ArgumentOutOfRangeException(nameof(beatPhase), "节拍相位为 [0,1)。");
        }
        else if (beatConfidence != 0) throw new ArgumentException("没有节拍估计时 confidence 必须为零。", nameof(beatConfidence));
        BassLevel = bassLevel; Pulse = pulse; BeatPhase = beatPhase; BeatConfidence = beatConfidence;
    }
    /// <summary>规范低频包络 [0,1]。</summary>
    public double BassLevel { get; }
    /// <summary>规范脉冲强度 [0,1]。</summary>
    public double Pulse { get; }
    /// <summary>节拍估计相位 [0,1)，没有估计时为空。</summary>
    public double? BeatPhase { get; }
    /// <summary>节拍估计可信度 [0,1]；不表示精准拍点。</summary>
    public double BeatConfidence { get; }
}

/// <summary>一个真实分析 hop 起点的值。</summary>
public sealed record AudioFeaturePoint
{
    /// <summary>创建已分析样本；时间为非负整数微秒。</summary>
    public AudioFeaturePoint(long timeUs, AudioFeatureValues values)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs); TimeUs = timeUs; Values = values;
    }
    /// <summary>该样本左端时刻。</summary>
    public long TimeUs { get; }
    /// <summary>已分析值。</summary>
    public AudioFeatureValues Values { get; }
}

/// <summary>某个查询时刻的音频输入；不可用时 Values=null。</summary>
/// <param name="TimeUs">请求时刻。</param><param name="Availability">明确状态。</param>
/// <param name="Values">左端分析 hop 的值，不由 UI 刷新次数生成。</param><param name="DiagnosticCode">不可用原因。</param>
public sealed record AudioFeatureSample(long TimeUs, AudioAvailability Availability, AudioFeatureValues? Values, string? DiagnosticCode);

/// <summary>半开查询的实际分析点；明确列出请求及有效范围，不隐式延长分析。</summary>
/// <param name="RequestedRange">请求区间。</param><param name="AvailableRange">与已分析音频相交的区间。</param>
/// <param name="Availability">完整可用、部分／完全越界或未分析状态。</param>
/// <param name="Points">时间戳位于请求区间的真实分析点，不插入假样本。</param><param name="DiagnosticCode">不可用原因。</param>
public sealed record AudioFeatureRange(TimeRange RequestedRange, TimeRange? AvailableRange, AudioAvailability Availability,
    ImmutableArray<AudioFeaturePoint> Points, string? DiagnosticCode);

/// <summary>一个固定分析修订的只读时间序列；v1 GetAt 使用左端 hop 保持，不估算未来拍点。</summary>
public sealed class AudioFeatures
{
    private readonly ImmutableArray<AudioFeaturePoint> _points;
    private readonly string? _unavailableCode;
    private AudioFeatures(AudioAvailability availability, string diagnosticCode)
    {
        if (availability is not (AudioAvailability.NoAudio or AudioAvailability.NotAnalyzed or AudioAvailability.AnalysisFailed))
            throw new ArgumentOutOfRangeException(nameof(availability));
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        Availability = availability; _unavailableCode = diagnosticCode; _points = [];
    }
    /// <summary>宿主建立已完成分析的固定序列；首点为 0，严格增时，不排序／填空。</summary>
    public AudioFeatures(string revision, long durationUs, ImmutableArray<AudioFeaturePoint> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationUs);
        if (points.IsDefaultOrEmpty || points.Any(static point => point is null) || points[0].TimeUs != 0)
            throw new ArgumentException("已分析序列必须从零开始。", nameof(points));
        long previous = -1;
        foreach (var point in points)
        {
            if (point.TimeUs <= previous || point.TimeUs >= durationUs) throw new ArgumentException("分析点必须严格递增并位于音频区间内。", nameof(points));
            previous = point.TimeUs;
        }
        Revision = revision; DurationUs = durationUs; Availability = AudioAvailability.Available; _points = points;
    }
    /// <summary>未提供分析服务的明确状态。</summary>
    public static AudioFeatures NotAnalyzed { get; } = Unavailable(AudioAvailability.NotAnalyzed, "AudioNotAnalyzed");
    /// <summary>建立带原因的不可用输入，不能将 Available 冒充空序列。</summary>
    public static AudioFeatures Unavailable(AudioAvailability availability, string diagnosticCode) => new(availability, diagnosticCode);
    /// <summary>整个固定序列的分析状态。</summary>
    public AudioAvailability Availability { get; }
    /// <summary>固定分析修订；未完成分析为空。</summary>
    public string? Revision { get; }
    /// <summary>分析音频的整数微秒时长；未完成分析为空。</summary>
    public long? DurationUs { get; }
    /// <summary>查询时刻；已分析静音可返回零，不可用／EndUs 返回空值和原因。</summary>
    public AudioFeatureSample GetAt(long timeUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        if (Availability != AudioAvailability.Available) return new(timeUs, Availability, null, _unavailableCode);
        if (timeUs >= DurationUs) return new(timeUs, AudioAvailability.OutsideRange, null, "AudioOutsideRange");
        int index = LowerBound(timeUs);
        if (index == _points.Length || _points[index].TimeUs != timeUs) index--;
        return new(timeUs, AudioAvailability.Available, _points[index].Values, null);
    }
    /// <summary>返回半开范围内的真实分析点；越界显式报告，不夹值或伪造边界点。</summary>
    public AudioFeatureRange GetRange(TimeRange range)
    {
        if (!range.IsValid) throw new ArgumentException("查询须为有效半开区间。", nameof(range));
        if (Availability != AudioAvailability.Available) return new(range, null, Availability, [], _unavailableCode);
        long end = Math.Min(range.EndUs, DurationUs!.Value);
        TimeRange? available = range.StartUs < end ? new(range.StartUs, end) : null;
        int first = LowerBound(range.StartUs), last = LowerBound(range.EndUs);
        bool complete = range.EndUs <= DurationUs;
        return new(range, available, complete ? AudioAvailability.Available : AudioAvailability.OutsideRange,
            _points[first..last], complete ? null : "AudioOutsideRange");
    }
    private int LowerBound(long timeUs)
    {
        int low = 0, high = _points.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_points[middle].TimeUs < timeUs) low = middle + 1; else high = middle;
        }
        return low;
    }
}
