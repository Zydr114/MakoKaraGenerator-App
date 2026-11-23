using System.Collections.Immutable;
using MakoKaraGenerator.Core.Internal;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>槽位序列布局的触发方式。</summary>
public enum SlotRevealMode
{
    /// <summary>按上一行行时间进度触发。</summary>
    Progress = 0,

    /// <summary>按上一行起点后的固定时间触发。</summary>
    TimeAfterPreviousStart = 1,

    /// <summary>按下一行起点前的提前量触发。</summary>
    LeadBeforeNextStart = 2,
}

/// <summary>槽位序列槽位冲突时的处理方式。</summary>
public enum SlotConflictPolicy
{
    /// <summary>保留旧行到自身结束，槽位释放后再显示新行。</summary>
    QueueUntilSlotFree = 0,

    /// <summary>允许超过最大可见行数，适合用户明确需要的编排。</summary>
    AllowOverflow = 1,

    /// <summary>新行进入时提前结束占用槽位的旧行。</summary>
    ReplacePrevious = 2,

    /// <summary>槽位冲突时跳过本次新行显示。</summary>
    Skip = 3,
}

/// <summary>槽位序列槽位中旧行的保留策略。</summary>
public enum SlotRetentionPolicy
{
    /// <summary>保留到旧行自己的行时间结束。</summary>
    UntilLineEnd = 0,

    /// <summary>保留到下一行满足显示条件。</summary>
    UntilNextReveal = 1,

    /// <summary>新行满足显示条件后立即隐藏旧行。</summary>
    Immediate = 2,
}

/// <summary>槽位序列布局的一个槽位。</summary>
public sealed record SlotSequenceSlot
{
    /// <summary>槽位标识。</summary>
    public required string Id { get; init; }

    /// <summary>槽位中的默认位置。</summary>
    public ScenePlacement Placement { get; init; } = ScenePlacement.Default;

    /// <summary>创建一个槽位。</summary>
    /// <param name="id">槽位标识。</param>
    /// <param name="placement">槽位默认位置。</param>
    /// <returns>新的槽位。</returns>
    /// <exception cref="ArgumentException">标识为空或位置无效时抛出。</exception>
    public static SlotSequenceSlot Create(string id, ScenePlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!placement.IsValid)
        {
            throw new ArgumentException("槽位位置无效。", nameof(placement));
        }

        return new SlotSequenceSlot { Id = id, Placement = placement };
    }
}

/// <summary>
/// 槽位序列交错动画配置。它只描述策略，不保存当前播放时刻。
/// </summary>
public sealed record SlotSequenceAnimationProfile
{
    private ImmutableArray<SlotSequenceSlot> _slots;

    /// <summary>默认的左上/右下槽位序列配置。</summary>
    public static SlotSequenceAnimationProfile Default { get; } = new()
    {
        Slots =
        [
            SlotSequenceSlot.Create("upper-left", ScenePlacement.Create(NormalizedPosition.Create(0.28d, 0.30d))),
            SlotSequenceSlot.Create("lower-right", ScenePlacement.Create(NormalizedPosition.Create(0.72d, 0.70d))),
        ],
    };

    /// <summary>触发方式。</summary>
    public SlotRevealMode RevealMode { get; init; } = SlotRevealMode.Progress;

    /// <summary>按进度触发时使用的阈值。</summary>
    public double RevealProgress { get; init; } = 0.5d;

    /// <summary>按固定时间触发时使用的延迟（微秒）。</summary>
    public long RevealDelayUs { get; init; }

    /// <summary>按提前量触发时使用的提前时间（微秒）。</summary>
    public long RevealLeadUs { get; init; }

    /// <summary>默认允许同时显示的行数。</summary>
    public int MaxVisibleLines { get; init; } = 2;

    /// <summary>槽位冲突策略。</summary>
    public SlotConflictPolicy ConflictPolicy { get; init; } = SlotConflictPolicy.QueueUntilSlotFree;

    /// <summary>旧行保留策略。</summary>
    public SlotRetentionPolicy RetentionPolicy { get; init; } = SlotRetentionPolicy.UntilLineEnd;

    /// <summary>槽位序列，至少需要一个槽位。</summary>
    public ImmutableArray<SlotSequenceSlot> Slots
    {
        get => _slots.IsDefault ? ImmutableArray<SlotSequenceSlot>.Empty : _slots;
        init => _slots = value;
    }

    /// <summary>配置是否有效。</summary>
    public bool IsValid
    {
        get
        {
            if (!Enum.IsDefined(RevealMode) || !Enum.IsDefined(ConflictPolicy)
                || !Enum.IsDefined(RetentionPolicy)
                || !double.IsFinite(RevealProgress) || RevealProgress is < 0d or > 1d
                || RevealDelayUs < 0L || RevealLeadUs < 0L
                || MaxVisibleLines < 1
                || Slots.Length < 1)
            {
                return false;
            }

            HashSet<string> ids = new(StringComparer.Ordinal);

            foreach (SlotSequenceSlot slot in Slots)
            {
                if (slot is null || string.IsNullOrWhiteSpace(slot.Id) || slot.Placement is null || !slot.Placement.IsValid || !ids.Add(slot.Id))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>按槽位内容比较两个槽位序列配置，而不是比较数组引用。</summary>
    /// <param name="other">另一个配置。</param>
    /// <returns>配置参数和槽位内容均相同时为 <see langword="true"/>。</returns>
    public bool Equals(SlotSequenceAnimationProfile? other)
    {
        if (other is null)
        {
            return false;
        }

        return RevealMode == other.RevealMode
            && RevealProgress.Equals(other.RevealProgress)
            && RevealDelayUs == other.RevealDelayUs
            && RevealLeadUs == other.RevealLeadUs
            && MaxVisibleLines == other.MaxVisibleLines
            && ConflictPolicy == other.ConflictPolicy
            && RetentionPolicy == other.RetentionPolicy
            && SequenceEquality.ContentEquals(Slots, other.Slots);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RevealMode);
        hash.Add(RevealProgress);
        hash.Add(RevealDelayUs);
        hash.Add(RevealLeadUs);
        hash.Add(MaxVisibleLines);
        hash.Add(ConflictPolicy);
        hash.Add(RetentionPolicy);
        SequenceEquality.AddContentHash(ref hash, Slots);
        return hash.ToHashCode();
    }
}

/// <summary>槽位序列采样后的一条可见歌词。</summary>
/// <param name="LineIndex">原始行序号。</param>
/// <param name="LineId">歌词行标识。</param>
/// <param name="SlotIndex">使用的槽位序号。</param>
/// <param name="LineProgress">行级时间进度。</param>
/// <param name="RevealTimeUs">该行在本配置下的实际显示时刻。</param>
/// <param name="HideTimeUs">该行在冲突和保留策略下的实际隐藏时刻。</param>
public readonly record struct SlotSequenceSample(
    int LineIndex,
    string LineId,
    int SlotIndex,
    double LineProgress,
    long RevealTimeUs,
    long HideTimeUs);

/// <summary>槽位序列动画配置和采样器的纯逻辑实现。</summary>
public static class SlotSequenceAnimationScheduler
{
    /// <summary>解析静态编辑试样使用的槽位；未定时行不占用槽位。</summary>
    /// <param name="branch">歌词分支。</param>
    /// <param name="presentation">分支内部布局定义。</param>
    /// <param name="lineId">目标行稳定标识。</param>
    /// <returns>槽位序列默认槽位位置，否则为空。</returns>
    public static ScenePlacement? EditingPlacement(LyricBranch branch, LyricBranchPresentation presentation, string lineId)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (presentation.PolicyFor(branch) != LyricBranchAnimationPolicy.Karaoke || presentation.SlotSequenceProfile is not { IsValid: true } profile) return null;
        int ordinal = 0;
        foreach (LyricLine line in branch.Lines)
        {
            if (line.Time is not { IsValid: true }) continue;
            if (line.Id == lineId) return profile.Slots[ordinal % profile.Slots.Length].Placement;
            ordinal++;
        }
        return null;
    }

    /// <summary>
    /// 在指定时刻采样一组行。行必须按歌词顺序传入；无效或无时间的行不会被静默制造时间。
    /// </summary>
    /// <param name="lines">按顺序排列的歌词行。</param>
    /// <param name="timeUs">当前播放时刻。</param>
    /// <param name="profile">槽位序列配置。</param>
    /// <returns>按行顺序返回当前可见行。</returns>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException">配置无效时抛出。</exception>
    public static ImmutableArray<SlotSequenceSample> Sample(
        IReadOnlyList<LyricLine> lines,
        long timeUs,
        SlotSequenceAnimationProfile? profile = null)
        => [.. Build(lines, profile).Where(entry => entry.RevealTimeUs <= timeUs && timeUs < entry.HideTimeUs)
            .Select(entry => entry with { LineProgress = lines[entry.LineIndex].Time!.Value.ProgressAt(timeUs) })];

    /// <summary>重建完整槽位显示计划，准备区间覆盖揭示起点并终止前序保留，不改源时间。</summary>
    /// <param name="lines">按源顺序排列的行。</param>
    /// <param name="profile">槽位配置。</param>
    /// <param name="preparations">可选前奏目标及完整准备区间。</param>
    /// <returns>全部有效装载，序号仍按原槽位轮换。</returns>
    public static ImmutableArray<SlotSequenceSample> Build(IReadOnlyList<LyricLine> lines,
        SlotSequenceAnimationProfile? profile = null, IReadOnlyDictionary<string, TimeRange>? preparations = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        SlotSequenceAnimationProfile settings = profile ?? SlotSequenceAnimationProfile.Default;

        if (!settings.IsValid)
        {
            throw new ArgumentException("槽位序列动画配置无效。", nameof(profile));
        }

        if (lines.Count == 0)
        {
            return [];
        }

        List<SlotSequenceSample> schedule = [];
        int ordinal = 0;
        long afterPreparation = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            LyricLine line = lines[index];
            if (line.Time is not { IsValid: true } current || current.StartUs < 0 || current.EndUs <= current.StartUs)
                continue;
            int slot = ordinal++ % settings.Slots.Length;
            long reveal = current.StartUs;
            if (schedule.Count > 0)
            {
                SlotSequenceSample previous = schedule[^1];
                reveal = Math.Max(previous.RevealTimeUs,
                    GetTriggerTime(lines[previous.LineIndex].Time!.Value, current, settings));
            }
            if (preparations is not null && preparations.TryGetValue(line.Id, out var preparation))
            {
                reveal = preparation.StartUs;
                for (int oldIndex = 0; oldIndex < schedule.Count; oldIndex++)
                    schedule[oldIndex] = schedule[oldIndex] with { HideTimeUs = Math.Min(schedule[oldIndex].HideTimeUs, reveal) };
                afterPreparation = preparation.EndUs;
            }
            else reveal = Math.Max(reveal, afterPreparation);
            while (true)
            {
                var occupied = schedule.Where(entry => entry.RevealTimeUs <= reveal && entry.HideTimeUs > reveal).ToArray();
                var sameSlot = occupied.Where(entry => entry.SlotIndex == slot).ToArray();
                bool conflict = sameSlot.Length > 0 || occupied.Length >= settings.MaxVisibleLines;
                if (!conflict || settings.ConflictPolicy == SlotConflictPolicy.AllowOverflow) break;
                if (settings.ConflictPolicy == SlotConflictPolicy.Skip) { reveal = current.EndUs; break; }
                if (settings.ConflictPolicy == SlotConflictPolicy.ReplacePrevious)
                {
                    var retired = sameSlot.Length > 0 ? sameSlot : [occupied.OrderBy(entry => entry.RevealTimeUs).First()];
                    foreach (var old in retired)
                    {
                        int oldIndex = schedule.IndexOf(old);
                        schedule[oldIndex] = old with { HideTimeUs = reveal };
                    }
                    continue;
                }
                reveal = sameSlot.Length > 0 ? sameSlot.Max(entry => entry.HideTimeUs)
                    : occupied.Min(entry => entry.HideTimeUs);
            }
            if (reveal >= current.EndUs) continue;
            if (settings.RetentionPolicy != SlotRetentionPolicy.UntilLineEnd && schedule.Count > 0)
            {
                var previous = schedule[^1];
                schedule[^1] = previous with { HideTimeUs = Math.Min(previous.HideTimeUs, reveal) };
            }
            schedule.Add(new SlotSequenceSample(index, line.Id, slot, 0, reveal, current.EndUs));
        }
        return [.. schedule.Where(entry => entry.RevealTimeUs < entry.HideTimeUs)];
    }

    private static long ProgressTrigger(TimeRange range, double progress)
    {
        if (progress >= 1) return range.EndUs;
        if (progress <= 0) return range.StartUs;
        double offset = Math.Round(range.DurationUs * progress);
        return offset >= range.DurationUs ? range.EndUs : range.StartUs + (long)offset;
    }

    private static long GetTriggerTime(
        TimeRange previousTime,
        TimeRange nextTime,
        SlotSequenceAnimationProfile profile) => profile.RevealMode switch
        {
            SlotRevealMode.Progress => ProgressTrigger(previousTime, profile.RevealProgress),
            SlotRevealMode.TimeAfterPreviousStart => profile.RevealDelayUs > long.MaxValue - previousTime.StartUs
                ? long.MaxValue : previousTime.StartUs + profile.RevealDelayUs,
            SlotRevealMode.LeadBeforeNextStart => Math.Max(0L, nextTime.StartUs - profile.RevealLeadUs),
            _ => previousTime.StartUs,
        };
}
