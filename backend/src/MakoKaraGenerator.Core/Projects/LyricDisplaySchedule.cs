using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>一条真实歌词装载，准备与演唱共享身份和显示时钟。</summary>
public sealed record LyricDisplayOccurrence
{
    /// <summary>原始源行。</summary>
    public required LyricLine Line { get; init; }
    /// <summary>所属源序号，不因前奏插入改变。</summary>
    public int SourceIndex { get; init; }
    /// <summary>源片段身份及实际容器。</summary>
    public required LyricDisplaySegment Segment { get; init; }
    /// <summary>实际槽位序号，自由布局为 -1。</summary>
    public int SlotIndex { get; init; } = -1;
    /// <summary>本次装载的揭示与进入动画起点。</summary>
    public long RevealTimeUs { get; init; }
    /// <summary>半开隐藏终点。</summary>
    public long HideTimeUs { get; init; }
    /// <summary>本句的启用前奏。</summary>
    public LyricLeadIn? LeadIn { get; init; }
    /// <summary>实际准备区间。</summary>
    public TimeRange? Preparation { get; init; }
    /// <summary>是否在此时可见。</summary>
    public bool Contains(long timeUs) => RevealTimeUs <= timeUs && timeUs < HideTimeUs;
}

/// <summary>按程序重建共用显示计划，统筹未来片段、槽位与准备阶段。</summary>
public static class LyricDisplayScheduleBuilder
{
    /// <summary>生成完整计划；前奏不改变源成员、容器归属或槽位轮换序号。</summary>
    public static ImmutableArray<LyricDisplayOccurrence> Build(LyricBranch branch, LyricDisplayProgram program, LyricPresentationDefinition tree)
    {
        var segments = LyricDisplaySegmentResolver.Segments(branch, program);
        var prepared = program.LeadIns.Where(cue => cue.Enabled).Select(cue =>
        {
            var line = branch.Lines.First(source => source.Id == cue.TargetLineId);
            return (Cue: cue, Range: LeadInTiming.Range(line, cue.DurationUs)
                ?? throw new InvalidOperationException("前奏区间无效：" + cue.Id),
                Index: LyricDisplaySegmentResolver.IndexOf(branch, line.Id));
        }).ToArray();
        List<LyricDisplayOccurrence> schedule = [];
        foreach (var segment in segments)
        {
            var layout = tree.FindNode(segment.ContainerId(program))!.Layout!;
            var members = LyricDisplaySegmentResolver.ScheduleMembers(branch, segment);
            var assigned = segment.Lines.Select(line => line.Id).ToHashSet(StringComparer.Ordinal);
            var preparations = prepared.Where(item => members.Any(line => line.Id == item.Cue.TargetLineId))
                .ToDictionary(item => item.Cue.TargetLineId, item => item.Range, StringComparer.Ordinal);
            var raw = layout.Kind == LyricContainerLayoutKind.SlotSequence
                ? SlotSequenceAnimationScheduler.Build(members, layout.SlotAnimation!.ToProfile(layout.SlotIds.Select(id => tree.FindNode(id)!)), preparations)
                : [.. members.Select((line, index) => (Line: line, Index: index)).Where(item => item.Line.Time is { IsValid: true })
                    .Select(item => new SlotSequenceSample(item.Index, item.Line.Id, -1, 0,
                        preparations.TryGetValue(item.Line.Id, out var range) ? range.StartUs : item.Line.Time!.Value.StartUs, item.Line.Time!.Value.EndUs))];
            var clock = LyricDisplaySegmentResolver.ScheduleRange(branch, segment);
            foreach (var occurrence in raw)
            {
                var line = members[occurrence.LineIndex];
                if (!assigned.Contains(line.Id)) continue;
                var cue = prepared.FirstOrDefault(item => item.Cue.TargetLineId == line.Id);
                schedule.Add(new() { Line = line, SourceIndex = LyricDisplaySegmentResolver.IndexOf(branch, line.Id),
                    Segment = segment, SlotIndex = occurrence.SlotIndex,
                    RevealTimeUs = cue.Cue is null ? Math.Max(clock.StartUs, occurrence.RevealTimeUs) : occurrence.RevealTimeUs,
                    HideTimeUs = Math.Min(clock.EndUs, occurrence.HideTimeUs), LeadIn = cue.Cue,
                    Preparation = cue.Cue is null ? null : cue.Range });
            }
        }
        foreach (var preparation in prepared)
            for (int index = 0; index < schedule.Count; index++)
            {
                var occurrence = schedule[index];
                if (occurrence.SourceIndex < preparation.Index)
                    schedule[index] = occurrence with { HideTimeUs = Math.Min(occurrence.HideTimeUs, preparation.Range.StartUs) };
                else if (occurrence.SourceIndex > preparation.Index)
                    schedule[index] = occurrence with { RevealTimeUs = Math.Max(occurrence.RevealTimeUs, preparation.Range.EndUs) };
            }
        return [.. schedule.Where(item => item.RevealTimeUs < item.HideTimeUs).OrderBy(item => item.SourceIndex)];
    }
}
