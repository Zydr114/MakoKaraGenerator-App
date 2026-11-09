using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>绝对时间重建的活动展示片段，歌词保留源实例。</summary>
/// <param name="StartUs">片段起点。</param>
/// <param name="EndUs">片段终点。</param>
/// <param name="Clip">显式片段；默认归属为空。</param>
/// <param name="Lines">该片段的调度成员。</param>
public sealed record LyricDisplaySegment(long StartUs, long EndUs, LyricDisplayClip? Clip, ImmutableArray<LyricLine> Lines)
{
    /// <summary>活动显示容器。</summary>
    public string ContainerId(LyricDisplayProgram program) => Clip?.ContainerId ?? program.DefaultContainerId;
    /// <summary>不含时间数值的稳定片段身份。</summary>
    public string Identity => Clip?.Id ?? "default/" + Uri.EscapeDataString(Lines.FirstOrDefault()?.Id ?? "empty")
        + "/" + Uri.EscapeDataString(Lines.LastOrDefault()?.Id ?? "empty");
}

/// <summary>按源时间解析互斥展示；所有边界与引用均显式校验。</summary>
public static class LyricDisplaySegmentResolver
{
    /// <summary>完整源调度上下文；只决定时钟与轮换，不改变实际片段成员或绑定范围。</summary>
    public static ImmutableArray<LyricLine> ScheduleMembers(LyricBranch branch, LyricDisplaySegment segment)
    {
        if (segment.Clip?.ScheduleContext is not { } context) return segment.Lines;
        var sourceIndices = branch.Lines.Select((line, index) => (line.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var indices = context.LineIds.Select(id => sourceIndices.GetValueOrDefault(id, -1)).ToArray();
        if (indices.Length == 0 || indices.Any(index => index < 0) || !indices.SequenceEqual(indices.Distinct().Order())
            || segment.Lines.Any(line => !context.LineIds.Contains(line.Id)))
            throw new InvalidOperationException("片段调度上下文必须是有序、唯一的源成员，并包含实际片段成员：" + segment.Clip.Id);
        var members = indices.Select(index => branch.Lines[index]).ToImmutableArray();
        var timed = members.Where(line => line.Time is { IsValid: true }).ToArray();
        for (int index = 1; index < timed.Length; index++)
            if (timed[index].Time!.Value.StartUs < timed[index - 1].Time!.Value.StartUs || timed[index].Time!.Value.EndUs < timed[index - 1].Time!.Value.EndUs)
                throw new InvalidOperationException("片段调度上下文的源时间顺序反转：" + segment.Clip.Id);
        _ = ScheduleRange(branch, segment);
        return members;
    }

    /// <summary>由当前源时间解析原调度边界，不保存时间副本。</summary>
    public static TimeRange ScheduleRange(LyricBranch branch, LyricDisplaySegment segment)
    {
        if (segment.Clip?.ScheduleContext is not { } context) return new(segment.StartUs, segment.EndUs);
        if (context.LineIds.IsDefaultOrEmpty) throw new InvalidOperationException("片段上下文没有源成员。");
        if (!context.IsDefaultSegment)
        {
            if (context.PreviousLineId is not null || context.NextLineId is not null)
                throw new InvalidOperationException("显式片段上下文不能设置默认段边界。");
            var timed = branch.Lines.Where(line => context.LineIds.Contains(line.Id) && line.Time is { IsValid: true }).ToArray();
            if (timed.Length == 0) throw new InvalidOperationException("片段上下文没有有效时间。");
            return new(timed[0].Time!.Value.StartUs, timed[^1].Time!.Value.EndUs);
        }
        long Boundary(string? id, bool previous)
        {
            if (id is null) return previous ? 0 : long.MaxValue;
            int index = IndexOf(branch, id);
            int memberIndex = IndexOf(branch, context.LineIds[previous ? 0 : context.LineIds.Length - 1]);
            if (index < 0 || (previous ? index >= memberIndex : index <= memberIndex) || branch.Lines[index].Time is not { IsValid: true })
                throw new InvalidOperationException("默认调度边界需要有序、有效的相邻源行。");
            return previous ? branch.Lines[index].Time!.Value.EndUs : branch.Lines[index].Time!.Value.StartUs;
        }
        var range = new TimeRange(Boundary(context.PreviousLineId, true), Boundary(context.NextLineId, false));
        if (!range.IsValid || segment.Lines.Any(line => line.Time is { IsValid: true } time && (time.StartUs < range.StartUs || time.EndUs > range.EndUs)))
            throw new InvalidOperationException("默认调度边界不包含实际片段成员。");
        return range;
    }

    /// <summary>按实际片段和默认归属列出全部互斥源成员段。</summary>
    public static ImmutableArray<LyricDisplaySegment> Segments(LyricBranch branch, LyricDisplayProgram program)
    {
        List<LyricDisplaySegment> segments = []; HashSet<string> seen = new(StringComparer.Ordinal);
        void Add(LyricDisplaySegment segment) { if (seen.Add(segment.Identity)) segments.Add(segment); }
        Add(Resolve(branch, program, 0));
        foreach (var clip in program.Clips)
        {
            var range = Range(branch, clip);
            Add(new(range.StartUs, range.EndUs, clip, Members(branch, clip)));
            Add(Resolve(branch, program, range.EndUs));
        }
        return [.. segments.OrderBy(segment => segment.StartUs)];
    }

    /// <summary>解析有效、连续且按源时间递增的片段成员。</summary>
    public static ImmutableArray<LyricLine> Members(LyricBranch branch, LyricDisplayClip clip)
    {
        if (clip.LineIds.IsDefaultOrEmpty) throw new InvalidOperationException("片段没有源成员：" + clip.Id);
        int start = IndexOf(branch, clip.LineIds[0]);
        if (start < 0 || start + clip.LineIds.Length > branch.Lines.Length)
            throw new InvalidOperationException("片段源成员不存在：" + clip.Id);
        var lines = branch.Lines.Skip(start).Take(clip.LineIds.Length).ToImmutableArray();
        if (!lines.Select(line => line.Id).SequenceEqual(clip.LineIds)
            || lines.Any(line => line.Time is not { IsValid: true }))
            throw new InvalidOperationException("片段成员必须连续、按源顺序排列且具有行时间：" + clip.Id);
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Time!.Value.StartUs < lines[i - 1].Time!.Value.StartUs
                || lines[i].Time!.Value.EndUs < lines[i - 1].Time!.Value.EndUs)
                throw new InvalidOperationException("片段成员时间顺序反转：" + clip.Id);
        return lines;
    }

    /// <summary>由首句起点至末句终点取得半开区间。</summary>
    public static TimeRange Range(LyricBranch branch, LyricDisplayClip clip)
    {
        var members = Members(branch, clip);
        return new(members[0].Time!.Value.StartUs, members[^1].Time!.Value.EndUs);
    }

    // 片段跨度与校验只依赖不可变的分支与展示程序：按分支对象记忆（程序以引用或值相同核对），逐帧与每次锚点补算
    // 只按时刻挑选跨度，默认段成员按（起，止）复用，不再随全曲行数逐次复制、校验与过滤。校验失败不记忆、照旧抛出。
    private sealed class ResolvedProgram(LyricDisplayProgram program, LyricDisplaySegment[] spans, HashSet<string> assigned)
    {
        internal LyricDisplayProgram Program { get; } = program;
        internal LyricDisplaySegment[] Spans { get; } = spans;
        internal HashSet<string> Assigned { get; } = assigned;
        internal ConcurrentDictionary<(long Start, long End), LyricDisplaySegment> Defaults { get; } = new();
    }
    private static readonly ConditionalWeakTable<LyricBranch, ResolvedProgram> Programs = new();

    private static ResolvedProgram Prepare(LyricBranch branch, LyricDisplayProgram program)
    {
        if (Programs.TryGetValue(branch, out var resolved)
            && (ReferenceEquals(resolved.Program, program) || resolved.Program.Equals(program))) return resolved;
        var spans = program.Clips.Select(clip => (Clip: clip, Members: Members(branch, clip), Range: Range(branch, clip))).ToArray();
        HashSet<string> assigned = new(StringComparer.Ordinal);
        foreach (var span in spans)
        {
            if (span.Clip.LineIds.Any(id => !assigned.Add(id))) throw new InvalidOperationException("源行重复分配到片段。");
            if (branch.Lines.Any(line => !span.Clip.LineIds.Contains(line.Id) && line.Time is { IsValid: true } time
                && time.StartUs < span.Range.EndUs && span.Range.StartUs < time.EndUs))
                throw new InvalidOperationException("片段与其他源行的演唱时间冲突：" + span.Clip.Id);
        }
        for (int i = 0; i < spans.Length; i++)
            for (int j = i + 1; j < spans.Length; j++)
                if (spans[i].Range.StartUs < spans[j].Range.EndUs && spans[j].Range.StartUs < spans[i].Range.EndUs)
                    throw new InvalidOperationException("展示片段区间重叠。");
        resolved = new(program, [.. spans.Select(static span => new LyricDisplaySegment(span.Range.StartUs, span.Range.EndUs, span.Clip, span.Members))],
            assigned);
        Programs.AddOrUpdate(branch, resolved);
        return resolved;
    }

    /// <summary>解析活动片段，显式片段与默认片段的轮换互相隔离。</summary>
    public static LyricDisplaySegment Resolve(LyricBranch branch, LyricDisplayProgram program, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(program);
        var resolved = Prepare(branch, program);
        long start = 0, end = long.MaxValue;
        foreach (var span in resolved.Spans)
        {
            if (span.StartUs <= timeUs && timeUs < span.EndUs) return span;
            if (span.EndUs <= timeUs) start = Math.Max(start, span.EndUs);
            if (span.StartUs > timeUs) end = Math.Min(end, span.StartUs);
        }
        return resolved.Defaults.GetOrAdd((start, end), static (range, state) => new(range.Start, range.End, null,
            [.. state.Branch.Lines.Where(line => !state.Assigned.Contains(line.Id)
                && (line.Time is not { IsValid: true } time || time.StartUs >= range.Start && time.EndUs <= range.End))]),
            (Branch: branch, resolved.Assigned));
    }

    /// <summary>按行 ID 在分支中定位源行，不复制分支行数组；不存在时返回 -1。</summary>
    public static int IndexOf(LyricBranch branch, string lineId)
    {
        ArgumentNullException.ThrowIfNull(branch);
        for (int index = 0; index < branch.Lines.Length; index++)
            if (branch.Lines[index].Id == lineId) return index;
        return -1;
    }
}
