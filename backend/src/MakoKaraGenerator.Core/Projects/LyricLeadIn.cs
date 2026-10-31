using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>前奏绑定的制作来源，不代表源歌词时间的精度。</summary>
public enum LeadInOrigin
{
    /// <summary>用户创建或修改，自动生成不覆盖。</summary>
    Manual,
    /// <summary>自动生成，允许提出更新差异。</summary>
    Generated,
}

/// <summary>一条前奏绑定；时间从目标句的已知起唱点派生。</summary>
public sealed record LyricLeadIn
{
    /// <summary>稳定身份。</summary>
    public required string Id { get; init; }
    /// <summary>用户名称。</summary>
    public string? Name { get; init; }
    /// <summary>禁用时保留配置并恢复原显示规则。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>本显示程序中的目标源行。</summary>
    public required string TargetLineId { get; init; }
    /// <summary>使用的提示容器。</summary>
    public required string IndicatorContainerId { get; init; }
    /// <summary>完整提示时长，整数微秒。</summary>
    public long DurationUs { get; init; } = 3_000_000;
    /// <summary>手工或自动来源。</summary>
    public LeadInOrigin Origin { get; init; }
}

/// <summary>一个显示程序的自动生成参数和明确排除项。</summary>
public sealed record LeadInGenerationSettings
{
    private ImmutableArray<string> _excludedLineIds;
    /// <summary>是否生成歌曲开头提示。</summary>
    public bool IncludeOpening { get; init; } = true;
    /// <summary>是否生成长间奏后的提示。</summary>
    public bool IncludeLongGaps { get; init; } = true;
    /// <summary>长演唱空档阈值，等于阈值也纳入候选。</summary>
    public long GapThresholdUs { get; init; } = 8_000_000;
    /// <summary>建议提示时长。</summary>
    public long DurationUs { get; init; } = 3_000_000;
    /// <summary>明确选择的提示容器。</summary>
    public string? IndicatorContainerId { get; init; }
    /// <summary>用户明确排除的源行。</summary>
    public ImmutableArray<string> ExcludedLineIds { get => _excludedLineIds.IsDefault ? [] : _excludedLineIds; init => _excludedLineIds = value; }
    /// <summary>按内容比较生成设置。</summary>
    public bool Equals(LeadInGenerationSettings? other) => other is not null && IncludeOpening == other.IncludeOpening
        && IncludeLongGaps == other.IncludeLongGaps && GapThresholdUs == other.GapThresholdUs && DurationUs == other.DurationUs
        && IndicatorContainerId == other.IndicatorContainerId && ExcludedLineIds.SequenceEqual(other.ExcludedLineIds);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(IncludeOpening); hash.Add(IncludeLongGaps); hash.Add(GapThresholdUs);
        hash.Add(DurationUs); hash.Add(IndicatorContainerId); foreach (var id in ExcludedLineIds) hash.Add(id); return hash.ToHashCode();
    }
}

/// <summary>已知起唱锚点与时间依据。</summary>
/// <param name="TimeUs">归零点。</param>
/// <param name="TimingStatus">源时间状态，保留导入或估计的依据。</param>
/// <param name="UsesLineTime">是否使用行起点。</param>
/// <param name="HasMissingPrefix">首个已知起唱点前有未定时元素。</param>
public readonly record struct LeadInAnchor(long TimeUs, TimingStatus TimingStatus, bool UsesLineTime, bool HasMissingPrefix);

/// <summary>前奏时间纯规则；不改变源行或逐字时间。</summary>
public static class LeadInTiming
{
    /// <summary>首个有效定时元素起点；全缺时使用有效行起点。</summary>
    public static LeadInAnchor? Anchor(LyricLine line)
    {
        if (line.Time is not { IsValid: true } range) return null;
        for (int index = 0; index < line.Tokens.Length; index++)
            if (line.Tokens[index].Time is { IsValid: true } time)
                return new(time.StartUs, line.Tokens[index].TimingStatus, false, index > 0);
        return new(range.StartUs, line.TimingStatus, true, false);
    }
    /// <summary>计算完整半开准备区间；不足时不缩短。</summary>
    public static TimeRange? Range(LyricLine line, long durationUs)
    {
        var anchor = Anchor(line);
        return anchor is { } value && durationUs > 0 && value.TimeUs >= durationUs
            ? new(value.TimeUs - durationUs, value.TimeUs) : null;
    }
    /// <summary>保守演唱占用：完整定时取首末元素，部分缺时取整行。</summary>
    public static TimeRange? Occupancy(LyricLine line)
    {
        if (line.Time is not { IsValid: true } range) return null;
        if (line.Tokens.IsEmpty || line.Tokens.Any(token => token.Time is not { IsValid: true })) return range;
        return new(line.Tokens.Min(token => token.Time!.Value.StartUs), line.Tokens.Max(token => token.Time!.Value.EndUs));
    }
    /// <summary>等时步骤边界，采用商余数避免乘法溢出。</summary>
    public static long StepBoundary(TimeRange range, int markCount, int boundaryIndex)
    {
        if (!range.IsValid || markCount < 1 || range.DurationUs < markCount || boundaryIndex < 0 || boundaryIndex > markCount)
            throw new ArgumentOutOfRangeException(nameof(markCount), "步骤需要有效区间、正数量和严格递增边界。");
        long quotient = range.DurationUs / markCount, remainder = range.DurationUs % markCount;
        return range.StartUs + quotient * boundaryIndex + remainder * boundaryIndex / markCount;
    }
    /// <summary>当前已经过的步骤数；区间末尾返回全部步骤。</summary>
    public static int ElapsedMarks(TimeRange range, int markCount, long timeUs)
    {
        _ = StepBoundary(range, markCount, 0);
        if (timeUs < range.StartUs) return 0;
        for (int step = 1; step <= markCount; step++) if (timeUs < StepBoundary(range, markCount, step)) return step - 1;
        return markCount;
    }
}
