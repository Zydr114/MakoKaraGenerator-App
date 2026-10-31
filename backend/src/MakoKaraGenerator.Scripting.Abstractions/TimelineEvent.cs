namespace MakoKaraGenerator.Scripting;

/// <summary>歌曲时间事件的完整 v1 种类；不由 UI 帧差分编号。</summary>
public enum TimelineEventKind
{
    /// <summary>真实逐字起唱。</summary>
    TokenStarted,
    /// <summary>逐字半开区间结束。</summary>
    TokenCompleted,
    /// <summary>一行首个定时字起唱；无逐字时间时使用行起点。</summary>
    LinePerformanceStarted,
    /// <summary>一行最后定时字结束；无逐字时间时使用行终点。</summary>
    LinePerformanceCompleted,
    /// <summary>真实源行装载进入。</summary>
    BindingEntered,
    /// <summary>真实源行装载离场。</summary>
    BindingLeft,
    /// <summary>真实节点可见，不等于起唱。</summary>
    ElementVisible,
    /// <summary>真实节点不再可见。</summary>
    ElementHidden,
    /// <summary>前奏准备开始。</summary>
    LeadInStarted,
    /// <summary>前奏中间步骤改变。</summary>
    LeadInStepChanged,
    /// <summary>前奏归零结束。</summary>
    LeadInCompleted,
    /// <summary>语义光标转换；具体几何由光标阶段提供。</summary>
    CursorTransition,
    /// <summary>固定音频分析 hop，不是设备回调。</summary>
    AudioFeatureTick,
}

/// <summary>会话建立或重放的原因，不进入源事件身份。</summary>
public enum ReplayReason
{
    /// <summary>首次播放。</summary>
    Initial,
    /// <summary>跳转重建。</summary>
    Seek,
    /// <summary>循环重建。</summary>
    Loop,
    /// <summary>固定输入修订。</summary>
    InputsChanged,
    /// <summary>独立导出会话。</summary>
    Export,
    /// <summary>独立未来语义查询。</summary>
    AnchorQuery,
}

/// <summary>稳定的源顺序键；-1 表示没有该层源对象，不能伪造 TokenId。</summary>
public readonly record struct EventSourceOrder
{
    /// <summary>固定分支／行／元素顺序；缺行不得附带元素序号。</summary>
    public EventSourceOrder(int branch, int line = -1, int token = -1)
    {
        if (branch < 0 || line < -1 || token < -1 || line == -1 && token != -1)
            throw new ArgumentOutOfRangeException(nameof(branch));
        Branch = branch; Line = line; Token = token;
    }
    /// <summary>源分支序号；全工程非源输入放在 Int32.MaxValue。</summary>
    public int Branch { get; }
    /// <summary>源行序号，纯组／全工程输入为 -1。</summary>
    public int Line { get; }
    /// <summary>源元素序号，无适用元素为 -1。</summary>
    public int Token { get; }
    /// <summary>不属于任何歌词分支的固定输入顺序。</summary>
    public static EventSourceOrder Global => new(int.MaxValue);
}

/// <summary>封闭事件合同；外部程序集不能扩展种类或可写身份。</summary>
public abstract record TimelineEvent
{
    private protected TimelineEvent(TimelineEventKind kind, long timeUs, EventSourceOrder order, string owner,
        ReplayReason reason, int subIndex = 0)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind; TimeUs = timeUs; SourceOrder = order; ReplayReason = reason;
        EventId = StableIdentities.Event(kind.ToString(), timeUs, owner, subIndex);
    }
    /// <summary>固定种类。</summary>
    public TimelineEventKind Kind { get; }
    /// <summary>非负整数微秒边界。</summary>
    public long TimeUs { get; }
    /// <summary>稳定事件身份，独立于重绘／重放原因。</summary>
    public EventId EventId { get; }
    /// <summary>先分支、行、元素，再稳定身份的排序依据。</summary>
    public EventSourceOrder SourceOrder { get; }
    /// <summary>当前会话重建原因，不是 UI 操作录制。</summary>
    public ReplayReason ReplayReason { get; private init; }
    /// <summary>结束／离场=0，进入／准备／音频=1，起唱=2，光标转换=3。</summary>
    public int BoundaryPriority => Kind switch
    {
        TimelineEventKind.TokenCompleted or TimelineEventKind.LinePerformanceCompleted or TimelineEventKind.BindingLeft
            or TimelineEventKind.ElementHidden or TimelineEventKind.LeadInCompleted => 0,
        TimelineEventKind.BindingEntered or TimelineEventKind.ElementVisible or TimelineEventKind.LeadInStarted
            or TimelineEventKind.LeadInStepChanged or TimelineEventKind.AudioFeatureTick => 1,
        TimelineEventKind.TokenStarted or TimelineEventKind.LinePerformanceStarted => 2,
        TimelineEventKind.CursorTransition => 3,
        _ => throw new InvalidOperationException("未知事件种类。"),
    };
    /// <summary>固定同一事件在另一重放原因下的视图；身份及源数据保持。</summary>
    public TimelineEvent WithReplayReason(ReplayReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return reason == ReplayReason ? this : this with { ReplayReason = reason };
    }
    /// <summary>合同的完整排序；同边界必须先结束旧区间，再进入和起唱。</summary>
    public static IComparer<TimelineEvent> Comparer { get; } = Comparer<TimelineEvent>.Create(static (left, right) =>
    {
        int result = left.TimeUs.CompareTo(right.TimeUs);
        if (result == 0) result = left.BoundaryPriority.CompareTo(right.BoundaryPriority);
        if (result == 0) result = left.SourceOrder.Branch.CompareTo(right.SourceOrder.Branch);
        if (result == 0) result = left.SourceOrder.Line.CompareTo(right.SourceOrder.Line);
        if (result == 0) result = left.SourceOrder.Token.CompareTo(right.SourceOrder.Token);
        return result == 0 ? StringComparer.Ordinal.Compare(left.EventId.Value, right.EventId.Value) : result;
    });
}

/// <summary>只适用于真实定时元素的事件；不用于标记、组或缺时间字。</summary>
public abstract record TokenPerformanceEvent : TimelineEvent
{
    private protected TokenPerformanceEvent(TimelineEventKind kind, SourceToken token, EventSourceOrder order, ReplayReason reason, bool completed)
        : base(kind, TokenBoundary(token, order, completed), order, TokenOwner(token), reason)
    { Source = token; OccurrenceId = StableIdentities.Occurrence(token.BranchId, token.LineId, token.Id, token.TimeRange!.Value); }
    /// <summary>固定真实源元素；不是当前装载的伪元素。</summary>
    public SourceToken Source { get; }
    /// <summary>真实元素身份。</summary>
    public TokenId TokenId => Source.Id;
    /// <summary>真实源行。</summary>
    public LineId LineId => Source.LineId;
    /// <summary>真实源分支。</summary>
    public BranchId BranchId => Source.BranchId;
    /// <summary>同一源区间的稳定演唱身份，多个装载共用。</summary>
    public OccurrenceId OccurrenceId { get; }
    private static long TokenBoundary(SourceToken token, EventSourceOrder order, bool completed)
    {
        var range = TokenRange(token, order); return completed ? range.EndUs : range.StartUs;
    }
    private static TimeRange TokenRange(SourceToken token, EventSourceOrder order)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!token.Id.IsValid || !token.LineId.IsValid || !token.BranchId.IsValid || token.TimeRange is not { IsValid: true } range
            || !Enum.IsDefined(token.TimingStatus) || token.TimingStatus is SourceTimingStatus.Missing or SourceTimingStatus.Invalid
            || order.Line < 0 || order.Token != token.SourceOrder) throw new ArgumentException("起唱事件需要真实有效的定时源元素。", nameof(token));
        return range;
    }
    private static string TokenOwner(SourceToken token) => StableIdentities.Occurrence(token.BranchId, token.LineId, token.Id, token.TimeRange!.Value).Value;
}

/// <summary>源元素恰在 StartUs 起唱。</summary>
public sealed record TokenStarted : TokenPerformanceEvent
{
    /// <summary>从固定源区间生成事件，不从可见次数生成。</summary>
    public TokenStarted(SourceToken token, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.TokenStarted, token, order, reason, false) { }
}

/// <summary>源元素恰在 EndUs 完成，该时刻已不再 Current。</summary>
public sealed record TokenCompleted : TokenPerformanceEvent
{
    /// <summary>从固定源区间生成完成事件。</summary>
    public TokenCompleted(SourceToken token, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.TokenCompleted, token, order, reason, true) { }
}

/// <summary>真实行演唱区间的事件；无逐字时间时明确使用行依据。</summary>
public abstract record LinePerformanceEvent : TimelineEvent
{
    private protected LinePerformanceEvent(TimelineEventKind kind, SourceLine line, TimeRange range, ProgressSource source,
        EventSourceOrder order, ReplayReason reason, bool completed)
        : base(kind, Validate(line, range, source, order, completed), order, LineOwner(line, range), reason)
    { Source = line; PerformanceRange = range; ProgressSource = source; }
    /// <summary>真实源行。</summary>
    public SourceLine Source { get; }
    /// <summary>真实源行身份。</summary>
    public LineId LineId => Source.Id;
    /// <summary>真实源分支。</summary>
    public BranchId BranchId => Source.BranchId;
    /// <summary>首末定时元素包围区间，或无逐字时间时的原行区间。</summary>
    public TimeRange PerformanceRange { get; }
    /// <summary>Token 或 Line，不制造元素起唱事件。</summary>
    public ProgressSource ProgressSource { get; }
    private static long Validate(SourceLine line, TimeRange range, ProgressSource source, EventSourceOrder order, bool completed)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.Id.IsValid || !line.BranchId.IsValid || !range.IsValid || source is not (ProgressSource.Token or ProgressSource.Line)
            || order.Line != line.SourceOrder || order.Token != -1) throw new ArgumentException("行演唱事件输入无效。", nameof(line));
        return completed ? range.EndUs : range.StartUs;
    }
    private static string LineOwner(SourceLine line, TimeRange range) => StableIdentities.LinePerformanceOwner(line.BranchId, line.Id, range);
}

/// <summary>行首个定时字起唱，或无逐字时间时行开始。</summary>
public sealed record LinePerformanceStarted : LinePerformanceEvent
{
    /// <summary>固定真实行和明确演唱依据。</summary>
    public LinePerformanceStarted(SourceLine line, TimeRange range, ProgressSource source, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.LinePerformanceStarted, line, range, source, order, reason, false) { }
}

/// <summary>行最后定时字完成，或无逐字时间时行结束。</summary>
public sealed record LinePerformanceCompleted : LinePerformanceEvent
{
    /// <summary>固定真实行和明确演唱依据。</summary>
    public LinePerformanceCompleted(SourceLine line, TimeRange range, ProgressSource source, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.LinePerformanceCompleted, line, range, source, order, reason, true) { }
}
