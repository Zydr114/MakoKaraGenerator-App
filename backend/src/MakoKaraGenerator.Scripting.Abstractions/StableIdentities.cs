using System.Globalization;

namespace MakoKaraGenerator.Scripting;

/// <summary>歌曲内容导出的确定身份；没有系统随机数、列表索引或采样时刻。</summary>
public static class StableIdentities
{
    /// <summary>分支与真实显示程序组成的连续通道；跨槽位和片段保持，合法并发返回集合。</summary>
    public static CursorTrackId CursorTrack(BranchId branch, ProgramId program)
    {
        if (!branch.IsValid || !program.IsValid) throw new ArgumentException("通道需要真实分支和程序身份。");
        return new(Encode("cursor-track", branch.Value, program.Value));
    }
    /// <summary>一次源演唱区间，所有展示装载共用；未定时元素不能调用。</summary>
    public static OccurrenceId Occurrence(BranchId branch, LineId line, TokenId token, TimeRange range)
    {
        if (!branch.IsValid || !line.IsValid || !token.IsValid || !range.IsValid)
            throw new ArgumentException("演唱身份需要有效的源身份和区间。");
        return new(Encode("occurrence", branch.Value, line.Value, token.Value,
            range.StartUs.ToString(CultureInfo.InvariantCulture), range.EndUs.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>稳定事件身份；kind 和 owner 来自宿主的具体事件类型与 occurrence／binding／cue。</summary>
    public static EventId Event(string kind, long timeUs, string owner, int subIndex = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind); ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs); ArgumentOutOfRangeException.ThrowIfNegative(subIndex);
        return new(Encode("event", kind, timeUs.ToString(CultureInfo.InvariantCulture), owner,
            subIndex.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>真实行演唱区间的事件所有者；没有元素时不伪造 OccurrenceId 或 TokenId。</summary>
    public static string LinePerformanceOwner(BranchId branch, LineId line, TimeRange range)
    {
        if (!branch.IsValid || !line.IsValid || !range.IsValid) throw new ArgumentException("行演唱所有者需要有效源身份和区间。");
        return Encode("line-performance", branch.Value, line.Value, range.StartUs.ToString(CultureInfo.InvariantCulture), range.EndUs.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>真实节点可见事件的所有者；可适用装载／前奏，但不制造源身份。</summary>
    public static string ElementOwner(NodeId node, BindingId? binding = null, CueId? cue = null)
    {
        if (!node.IsValid || binding is { IsValid: false } || cue is { IsValid: false }) throw new ArgumentException("节点事件身份无效。");
        return Encode("element", node.Value, binding?.Value ?? "", cue?.Value ?? "");
    }

    /// <summary>同一光标通道的确定后继边身份；不混用源元素和装载。</summary>
    public static string CursorTransitionOwner(CursorTrackId track, BindingId from, OccurrenceId? fromOccurrence, BindingId to, OccurrenceId? toOccurrence)
    {
        if (!track.IsValid || !from.IsValid || !to.IsValid || fromOccurrence is { IsValid: false } || toOccurrence is { IsValid: false })
            throw new ArgumentException("光标转换所有者身份无效。");
        return Encode("cursor-transition", track.Value, from.Value, fromOccurrence?.Value ?? "", to.Value, toOccurrence?.Value ?? "");
    }

    private static string Encode(params string[] parts)
        => string.Concat(parts.Select(static part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
}
