using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Rendering;

/// <summary>源歌词的展示阶段，显示和演唱事件分离。</summary>
public enum SceneDisplayPhase
{
    /// <summary>正常源时间显示或演唱。</summary>
    Performance,
    /// <summary>前奏提前显示，演唱进度保持为零。</summary>
    Preparation,
}

/// <summary>运行中的前奏容器装载，只读时间与目标关联。</summary>
public sealed record SceneLeadInSample
{
    /// <summary>前奏身份。</summary>
    public required string CueId { get; init; }
    /// <summary>显示程序。</summary>
    public required string ProgramId { get; init; }
    /// <summary>提示容器。</summary>
    public required string ContainerId { get; init; }
    /// <summary>目标文本的稳定场景节点。</summary>
    public required string TargetElementId { get; init; }
    /// <summary>与演唱阶段连续的目标装载身份。</summary>
    public required string TargetBindingId { get; init; }
    /// <summary>源分支。</summary>
    public required string BranchId { get; init; }
    /// <summary>目标源行。</summary>
    public required string TargetLineId { get; init; }
    /// <summary>完整半开准备时间。</summary>
    public TimeRange Range { get; init; }
    /// <summary>已经过的步骤数。</summary>
    public int ElapsedMarks { get; init; }
    /// <summary>归一化倒计时进度（0..1）。</summary>
    public double NormalizedProgress { get; init; }
    /// <summary>闪烁阶段的全体透明度调制倍率（0.25..1）。</summary>
    public double BlinkOpacity { get; init; } = 1;
}

/// <summary>前奏提示组动画曲线求值器，兼顾旧版离散步进与连续闪烁／渐进擦除。</summary>
public static class LeadInAnimationEvaluator
{
    /// <summary>计算当前时刻提示组的全体闪烁透明度倍率（0..1）。</summary>
    public static double EvaluateBlinkOpacity(TimeRange range, CountdownAnimationSpec animation, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (animation.Mode == CountdownAnimationMode.DiscreteSteps || animation.BlinkRatio <= 0 || animation.BlinkCount < 1)
            return 1d;
        double u = NormalizedProgress(range, timeUs);
        if (u >= animation.BlinkRatio) return 1d;
        double phase = (u / animation.BlinkRatio) * animation.BlinkCount;
        // 1.0 -> 0.25 -> 1.0 smooth cosine breathing cycles, ending cleanly at 1.0 when wipe begins.
        return 0.625d + 0.375d * Math.Cos(2d * Math.PI * phase);
    }

    /// <summary>计算单个标记的局部擦除进度（0..1）与是否已完全经过。</summary>
    public static (bool IsElapsed, double WipeProgress) EvaluateMark(TimeRange range, int markIndex, int markCount,
        CountdownAnimationSpec animation, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(animation);
        int count = Math.Max(1, markCount);
        if (animation.Mode == CountdownAnimationMode.DiscreteSteps)
        {
            bool elapsed = markIndex < LeadInTiming.ElapsedMarks(range, count, timeUs);
            return (elapsed, elapsed ? 1d : 0d);
        }
        double u = NormalizedProgress(range, timeUs);
        if (animation.BlinkRatio > 0 && animation.BlinkCount >= 1 && u < animation.BlinkRatio)
            return (false, 0d);
        double wipeU = animation.BlinkRatio >= 1d ? 1d : Math.Clamp((u - animation.BlinkRatio) / (1d - animation.BlinkRatio), 0d, 1d);
        double progress = Math.Clamp(wipeU * count - markIndex, 0d, 1d);
        return (progress >= 0.999999d, progress);
    }

    /// <summary>计算准备区间内的归一化时间（0..1）。</summary>
    public static double NormalizedProgress(TimeRange range, long timeUs)
    {
        long duration = range.DurationUs;
        if (duration <= 0) return 1d;
        return Math.Clamp((timeUs - range.StartUs) / (double)duration, 0d, 1d);
    }
}

public static partial class LyricSceneSampler
{
    private static ImmutableArray<SceneLeadInSample> SampleLeadIns(ProjectSnapshot snapshot, SceneFrame frame)
    {
        List<SceneLeadInSample> cues = [];
        foreach (var sample in frame.Lines.Where(line => line.DisplayPhase == SceneDisplayPhase.Preparation && line.CueId is not null))
        {
            var program = snapshot.LyricPresentation.FindProgram(sample.Branch.Id)!;
            var cue = program.LeadIns.First(item => item.Id == sample.CueId);
            var range = LeadInTiming.Range(sample.Frame.Line!, cue.DurationUs)!.Value;
            var content = (CountdownSequenceContent)snapshot.LyricPresentation.FindNode(cue.IndicatorContainerId)!.Content!;
            cues.Add(new() { CueId = cue.Id, ProgramId = program.Id, ContainerId = cue.IndicatorContainerId,
                TargetElementId = sample.ElementId, TargetBindingId = sample.BindingId!, BranchId = sample.Branch.Id,
                TargetLineId = sample.Frame.Line!.Id, Range = range,
                ElapsedMarks = LeadInTiming.ElapsedMarks(range, content.MarkNodeIds.Length, frame.TimeUs),
                NormalizedProgress = LeadInAnimationEvaluator.NormalizedProgress(range, frame.TimeUs),
                BlinkOpacity = LeadInAnimationEvaluator.EvaluateBlinkOpacity(range, content.Animation, frame.TimeUs) });
        }
        return [.. cues];
    }

    private static ImmutableArray<SceneLineSample> SamplePreparedProgram(ProjectSnapshot snapshot, LyricBranch branch, LyricDisplayProgram program, long timeUs)
    {
        var active = LyricDisplayScheduleBuilder.Build(branch, program, snapshot.LyricPresentation).Where(item => item.Contains(timeUs)).ToArray();
        var preparing = active.FirstOrDefault(item => item.Preparation is { } range && range.Contains(timeUs));
        if (preparing is not null) active = [preparing];
        else if (active.Length > 0 && active.All(item => item.SlotIndex < 0))
            active = [active.OrderBy(item => item.Line.Time!.Value.StartUs).ThenBy(item => item.SourceIndex).Last()];
        return [.. active.Select(occurrence =>
        {
            string containerId = occurrence.Segment.ContainerId(program);
            var presentation = snapshot.LyricPresentation.ResolveDisplay(branch.Id, containerId);
            SceneLineSample sample;
            if (presentation.SlotSequenceProfile is { } profile)
            {
                int segmentIndex = Array.FindIndex(occurrence.Segment.Lines.ToArray(), line => line.Id == occurrence.Line.Id);
                sample = CreateSlotSample(snapshot, branch, occurrence.Segment, presentation, profile,
                    new(segmentIndex, occurrence.Line.Id, occurrence.SlotIndex, 0, occurrence.RevealTimeUs, occurrence.HideTimeUs), timeUs);
            }
            else
            {
                sample = CreateSample(snapshot, branch, LyricSampler.SampleLine(occurrence.Line, timeUs, occurrence.SourceIndex)) with
                { DisplayContainerId = containerId, Presentation = presentation, DisplayClipId = occurrence.Segment.Clip?.Id,
                    Placement = presentation.DefaultPlacement, BindingId = BindingId(program, occurrence.Segment, containerId, occurrence.Line.Id) };
            }
            sample = sample with { CueId = occurrence.LeadIn?.Id };
            if (occurrence.Preparation is not { } range || !range.Contains(timeUs)) return sample;
            return sample with { DisplayPhase = SceneDisplayPhase.Preparation, Frame = sample.Frame with
            { LineProgress = 0, LineProgressSource = ProgressSource.Unavailable, CurrentTokenIndex = -1,
                Tokens = [.. sample.Frame.Tokens.Select(token => token with { Progress = 0, IsCurrent = false, Time = null, ActiveSegment = null, Bridge = null })],
                RubySegments = [.. sample.Frame.RubySegments.Select(segment => segment with
                    { Progress = 0, IsCurrent = false, Source = ProgressSource.Unavailable, Time = null })] } };
        })];
    }

    private static string BindingId(LyricDisplayProgram program, LyricDisplaySegment segment, string parentId, string lineId) =>
        Uri.EscapeDataString(program.Id) + "/binding/" + Uri.EscapeDataString(segment.Identity) + "/"
        + Uri.EscapeDataString(parentId) + "/" + Uri.EscapeDataString(lineId);

    private static SceneLineSample CreateSlotSample(ProjectSnapshot snapshot, LyricBranch branch, LyricDisplaySegment segment,
        LyricBranchPresentation presentation, SlotSequenceAnimationProfile profile, SlotSequenceSample entry, long timeUs)
    {
        var line = segment.Lines[entry.LineIndex];
        int sourceIndex = LyricDisplaySegmentResolver.IndexOf(branch, line.Id);
        string containerId = segment.ContainerId(snapshot.LyricPresentation.FindProgram(branch.Id)!);
        string slotNodeId = snapshot.LyricPresentation.FindNode(containerId)!.Layout!.SlotIds[entry.SlotIndex];
        return CreateSample(snapshot, branch, LyricSampler.SampleLine(line, timeUs, sourceIndex)) with
        {
            SlotId = profile.Slots[entry.SlotIndex].Id, Placement = SceneDefinitionBuilder.SlotTextPlacement(profile.Slots[entry.SlotIndex].Placement),
            ParentNodeId = slotNodeId, DisplayContainerId = containerId, Presentation = presentation, DisplayClipId = segment.Clip?.Id,
            BindingId = BindingId(snapshot.LyricPresentation.FindProgram(branch.Id)!, segment, slotNodeId, line.Id),
        };
    }
}
