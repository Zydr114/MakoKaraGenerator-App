namespace MakoKaraGenerator.Core.Time;

/// <summary>正式导出共用的固定帧数与整数帧起始时刻，不使用验证任务的前缀计划。</summary>
public sealed class CompositionFramePlan
{
    private readonly FrameRate _rate;
    private CompositionFramePlan(int totalFrames, FrameRate rate)
    { TotalFrames = totalFrames; _rate = rate; VideoDurationUs = Time(totalFrames); }
    /// <summary>按原导出规则 Round(AwayFromZero)、至少一帧得到的总帧数。</summary>
    public int TotalFrames { get; }
    /// <summary>实际固定帧数对应的整数视频时长。</summary>
    public long VideoDurationUs { get; }
    /// <summary>创建正式输出计划，拒绝不能表示的帧数或时长。</summary>
    public static CompositionFramePlan Create(long durationUs, FrameRate frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationUs);
        if (frameRate.Numerator < 1 || frameRate.Denominator < 1) throw new ArgumentOutOfRangeException(nameof(frameRate));
        double frames = Math.Max(1d, Math.Round(durationUs / 1_000_000d * frameRate.Value, MidpointRounding.AwayFromZero));
        if (!double.IsFinite(frames) || frames > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(durationUs));
        return new((int)frames, frameRate);
    }
    /// <summary>第 index 帧的起始时刻，按原整数除法向下取整。</summary>
    public long FrameTimeUs(int index)
    {
        if (index < 0 || index >= TotalFrames) throw new ArgumentOutOfRangeException(nameof(index));
        return Time(index);
    }
    private long Time(int index) => checked((long)((Int128)index * 1_000_000 * _rate.Denominator / _rate.Numerator));
}
