namespace MakoKaraGenerator.Scripting;

/// <summary>真实源行装载的事件，不因准备变演唱而再次进入。</summary>
public abstract record BindingTimelineEvent : TimelineEvent
{
    private protected BindingTimelineEvent(TimelineEventKind kind, FrameLine binding, long timeUs, EventSourceOrder order, ReplayReason reason)
        : base(kind, timeUs, order, BindingOwner(binding), reason) { Binding = binding; }
    /// <summary>真实装载及当时父槽位／片段。</summary>
    public FrameLine Binding { get; }
    private static string BindingOwner(FrameLine binding) { TimelineValidation.Binding(binding); return binding.BindingId.Value; }
}

/// <summary>装载进入可见区间，可能早于源起唱。</summary>
public sealed record BindingEntered : BindingTimelineEvent
{
    /// <summary>使用共享场景的实际装载身份。</summary>
    public BindingEntered(FrameLine binding, long timeUs, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.BindingEntered, binding, timeUs, order, reason) { }
}

/// <summary>装载离开半开可见区间。</summary>
public sealed record BindingLeft : BindingTimelineEvent
{
    /// <summary>使用离场前的实际装载身份。</summary>
    public BindingLeft(FrameLine binding, long timeUs, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.BindingLeft, binding, timeUs, order, reason) { }
}

/// <summary>真实节点可见身份的封闭判别；组没有假行，标记没有假字。</summary>
public abstract record SceneElementIdentity
{
    private protected SceneElementIdentity(NodeId nodeId)
    {
        if (!nodeId.IsValid) throw new ArgumentException("节点身份无效。", nameof(nodeId)); NodeId = nodeId;
    }
    /// <summary>真实节点。</summary>
    public NodeId NodeId { get; }
    internal abstract string Owner { get; }
    /// <summary>真实歌词或注音节点，共用该句装载。</summary>
    public sealed record Lyric : SceneElementIdentity
    {
        /// <summary>指定真实节点和当前装载；RubyId 只在注音时存在。</summary>
        public Lyric(NodeId nodeId, FrameLine binding, RubyId? rubyId = null) : base(nodeId)
        {
            TimelineValidation.Binding(binding);
            if (rubyId is { IsValid: false }) throw new ArgumentException("注音身份无效。", nameof(rubyId));
            Binding = binding; RubyId = rubyId;
        }
        /// <summary>真实源行装载。</summary>
        public FrameLine Binding { get; }
        /// <summary>适用的注音身份；基础字节点为空。</summary>
        public RubyId? RubyId { get; }
        internal override string Owner => StableIdentities.ElementOwner(NodeId, Binding.BindingId);
    }
    /// <summary>前奏文字标记，源关联只指向真实目标句。</summary>
    public sealed record Mark : SceneElementIdentity
    {
        /// <summary>指定真实标记、前奏及目标装载。</summary>
        public Mark(NodeId nodeId, CueId cueId, FrameLine target) : base(nodeId)
        {
            TimelineValidation.Binding(target);
            if (!cueId.IsValid || target.CueId != cueId) throw new ArgumentException("标记前奏与目标装载不符。", nameof(cueId));
            CueId = cueId; Target = target;
        }
        /// <summary>真实前奏。</summary>
        public CueId CueId { get; }
        /// <summary>目标源行装载，标记仍不拥有 TokenId。</summary>
        public FrameLine Target { get; }
        internal override string Owner => StableIdentities.ElementOwner(NodeId, cue: CueId);
    }
    /// <summary>真实组或槽位；共享父组可以没有单一源分支。</summary>
    public sealed record Group : SceneElementIdentity
    {
        /// <summary>指定节点及适用的真实源分支，不制造 LineId。</summary>
        public Group(NodeId nodeId, BranchId? branchId) : base(nodeId)
        {
            if (branchId is { IsValid: false }) throw new ArgumentException("组分支身份无效。", nameof(branchId)); BranchId = branchId;
        }
        /// <summary>定义中实际归属；共享组为空。</summary>
        public BranchId? BranchId { get; }
        internal override string Owner => StableIdentities.ElementOwner(NodeId);
    }
}

/// <summary>真实节点可见区间事件。</summary>
public abstract record ElementTimelineEvent : TimelineEvent
{
    private protected ElementTimelineEvent(TimelineEventKind kind, SceneElementIdentity element, long timeUs, EventSourceOrder order, ReplayReason reason)
        : base(kind, timeUs, order, (element ?? throw new ArgumentNullException(nameof(element))).Owner, reason) { Element = element; }
    /// <summary>具有适用强类型身份的真实节点。</summary>
    public SceneElementIdentity Element { get; }
}

/// <summary>节点开始可见；不是 TokenStarted。</summary>
public sealed record ElementVisible : ElementTimelineEvent
{
    /// <summary>固定真实节点及边界。</summary>
    public ElementVisible(SceneElementIdentity element, long timeUs, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.ElementVisible, element, timeUs, order, reason) { }
}

/// <summary>节点可见区间结束。</summary>
public sealed record ElementHidden : ElementTimelineEvent
{
    /// <summary>固定离场前的真实节点及边界。</summary>
    public ElementHidden(SceneElementIdentity element, long timeUs, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.ElementHidden, element, timeUs, order, reason) { }
}

/// <summary>一条真实前奏与连续目标装载的只读事件关联。</summary>
public sealed record LeadInEventTarget
{
    /// <summary>固定有效启用前奏、真实目标和标记数量。</summary>
    public LeadInEventTarget(SourceCue cue, FrameLine target, int markCount)
    {
        ArgumentNullException.ThrowIfNull(cue); TimelineValidation.Binding(target);
        if (!cue.Id.IsValid || !cue.Enabled || cue.TimeRange is not { IsValid: true } range || markCount <= 0 || range.DurationUs < markCount
            || cue.Id != target.CueId || cue.ProgramId != target.ProgramId || cue.TargetLineId != target.LineId || !cue.ContainerId.IsValid)
            throw new ArgumentException("前奏事件需要有效配置和同一真实目标装载。", nameof(cue));
        Cue = cue; Target = target; MarkCount = markCount;
    }
    /// <summary>真实前奏配置与派生区间。</summary>
    public SourceCue Cue { get; }
    /// <summary>准备到起唱保持的真实目标装载。</summary>
    public FrameLine Target { get; }
    /// <summary>真实提示标记数量。</summary>
    public int MarkCount { get; }
}

/// <summary>前奏时间事件，不拥有独立演唱元素。</summary>
public abstract record LeadInTimelineEvent : TimelineEvent
{
    private protected LeadInTimelineEvent(TimelineEventKind kind, LeadInEventTarget target, long timeUs, int elapsedMarks,
        EventSourceOrder order, ReplayReason reason) : base(kind, Validate(kind, target, timeUs, elapsedMarks), order, target.Cue.Id.Value, reason, elapsedMarks)
    { Target = target; ElapsedMarks = elapsedMarks; }
    /// <summary>真实前奏和连续目标装载。</summary>
    public LeadInEventTarget Target { get; }
    /// <summary>已经过的实际步骤数；完成时为 MarkCount。</summary>
    public int ElapsedMarks { get; }
    /// <summary>真实前奏身份。</summary>
    public CueId CueId => Target.Cue.Id;
    private static long Validate(TimelineEventKind kind, LeadInEventTarget target, long timeUs, int elapsed)
    {
        ArgumentNullException.ThrowIfNull(target); TimeRange range = target.Cue.TimeRange!.Value;
        bool valid = kind switch
        {
            TimelineEventKind.LeadInStarted => timeUs == range.StartUs && elapsed == 0,
            TimelineEventKind.LeadInCompleted => timeUs == range.EndUs && elapsed == target.MarkCount,
            TimelineEventKind.LeadInStepChanged => elapsed > 0 && elapsed < target.MarkCount
                && timeUs == range.StartUs + range.DurationUs / target.MarkCount * elapsed + range.DurationUs % target.MarkCount * elapsed / target.MarkCount,
            _ => false,
        };
        if (!valid) throw new ArgumentException("前奏事件与真实边界／步骤不符。"); return timeUs;
    }
    private protected static LeadInEventTarget RequireTarget(LeadInEventTarget target)
    { ArgumentNullException.ThrowIfNull(target); return target; }
}

/// <summary>前奏倒计时开始，目标句开始一次入场。</summary>
public sealed record LeadInStarted : LeadInTimelineEvent
{
    /// <summary>从真实准备起点构造。</summary>
    public LeadInStarted(LeadInEventTarget target, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.LeadInStarted, target, RequireTarget(target).Cue.TimeRange!.Value.StartUs, 0, order, reason) { }
}

/// <summary>前奏中间步骤改变；最后归零由 Completed 表示。</summary>
public sealed record LeadInStepChanged : LeadInTimelineEvent
{
    /// <summary>固定共享前奏规则生成的精确整数边界。</summary>
    public LeadInStepChanged(LeadInEventTarget target, long timeUs, int elapsedMarks, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.LeadInStepChanged, target, timeUs, elapsedMarks, order, reason) { }
}

/// <summary>前奏归零，目标装载保持并衔接真实起唱。</summary>
public sealed record LeadInCompleted : LeadInTimelineEvent
{
    /// <summary>从真实准备终点构造。</summary>
    public LeadInCompleted(LeadInEventTarget target, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.LeadInCompleted, target, RequireTarget(target).Cue.TimeRange!.Value.EndUs, target.MarkCount, order, reason) { }
}

/// <summary>语义光标端点的真实源／装载身份；行依据不制造元素或 occurrence。</summary>
public sealed record CursorIdentity
{
    /// <summary>固定端点源身份和实际区间；Token 模式必须有真实元素及 occurrence。</summary>
    public CursorIdentity(BranchId branchId, LineId lineId, BindingId bindingId, TimeRange range,
        ProgressSource source, TokenId? tokenId = null, OccurrenceId? occurrenceId = null)
    {
        if (!branchId.IsValid || !lineId.IsValid || !bindingId.IsValid || !range.IsValid
            || source is not (ProgressSource.Token or ProgressSource.Line)
            || source == ProgressSource.Token && (tokenId is not { IsValid: true } || occurrenceId is not { IsValid: true })
            || source == ProgressSource.Line && (tokenId is not null || occurrenceId is not null))
            throw new ArgumentException("光标身份必须反映真实逐字或行依据。");
        BranchId = branchId; LineId = lineId; BindingId = bindingId; TimeRange = range;
        ProgressSource = source; TokenId = tokenId; OccurrenceId = occurrenceId;
    }
    /// <summary>真实源分支。</summary>
    public BranchId BranchId { get; }
    /// <summary>真实源行。</summary>
    public LineId LineId { get; }
    /// <summary>实际装载。</summary>
    public BindingId BindingId { get; }
    /// <summary>真实演唱区间。</summary>
    public TimeRange TimeRange { get; }
    /// <summary>明确的进度依据。</summary>
    public ProgressSource ProgressSource { get; }
    /// <summary>逐字模式的真实元素，行模式为空。</summary>
    public TokenId? TokenId { get; }
    /// <summary>逐字模式的真实演唱身份，行模式为空。</summary>
    public OccurrenceId? OccurrenceId { get; }
}

/// <summary>语义光标转换的依据。</summary>
public enum CursorTransitionReason
{
    /// <summary>同一句的下一定时元素。</summary>
    TokenChange,
    /// <summary>进入下一真实源行。</summary>
    LineChange,
    /// <summary>相同源内容换到另一个真实装载。</summary>
    BindingChange,
}

/// <summary>语义光标转换事件；不冒充最终几何端点或唯一合法后继。</summary>
public sealed record CursorTransitionEvent : TimelineEvent
{
    /// <summary>固定一个合法后继边；并发后继可以提供多个这样的事件。</summary>
    public CursorTransitionEvent(CursorTrackId trackId, CursorIdentity from, CursorIdentity to, long timeUs,
        CursorTransitionReason transitionReason, EventSourceOrder order, ReplayReason reason)
        : base(TimelineEventKind.CursorTransition, timeUs, order, Owner(trackId, from, to), reason)
    {
        if (!Enum.IsDefined(transitionReason) || from.BranchId != to.BranchId) throw new ArgumentException("光标转换不得暗中跨分支。");
        TrackId = trackId; From = from; To = to; TransitionReason = transitionReason;
        GapUs = to.TimeRange.StartUs > from.TimeRange.EndUs ? to.TimeRange.StartUs - from.TimeRange.EndUs : 0;
    }
    /// <summary>真实连续演唱通道。</summary>
    public CursorTrackId TrackId { get; }
    /// <summary>离场前的真实源／装载。</summary>
    public CursorIdentity From { get; }
    /// <summary>一个合法后继的真实源／装载。</summary>
    public CursorIdentity To { get; }
    /// <summary>非负演唱空档；相接或重叠为零，不改变源时间。</summary>
    public long GapUs { get; }
    /// <summary>转换依据。</summary>
    public CursorTransitionReason TransitionReason { get; }
    private static string Owner(CursorTrackId trackId, CursorIdentity from, CursorIdentity to)
    {
        ArgumentNullException.ThrowIfNull(from); ArgumentNullException.ThrowIfNull(to);
        if (!trackId.IsValid) throw new ArgumentException("光标通道无效。", nameof(trackId));
        return StableIdentities.CursorTransitionOwner(trackId, from.BindingId, from.OccurrenceId, to.BindingId, to.OccurrenceId);
    }
}

/// <summary>真实离线音频分析点事件，不以设备或 UI 时钟生成。</summary>
public sealed record AudioFeatureTick : TimelineEvent
{
    /// <summary>固定分析修订及真实 hop 样本。</summary>
    public AudioFeatureTick(string revision, AudioFeaturePoint point, ReplayReason reason)
        : base(TimelineEventKind.AudioFeatureTick, (point ?? throw new ArgumentNullException(nameof(point))).TimeUs,
            EventSourceOrder.Global, revision, reason) { AnalysisRevision = revision; Point = point; }
    /// <summary>固定分析修订。</summary>
    public string AnalysisRevision { get; }
    /// <summary>真实 hop 样本。</summary>
    public AudioFeaturePoint Point { get; }
}

internal static class TimelineValidation
{
    internal static void Binding(FrameLine binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.LineId.IsValid || !binding.BranchId.IsValid || !binding.BindingId.IsValid || !binding.NodeId.IsValid
            || !binding.ParentId.IsValid || !binding.ProgramId.IsValid || binding.ClipId is { IsValid: false } || binding.CueId is { IsValid: false }
            || !Enum.IsDefined(binding.DisplayPhase) || !double.IsFinite(binding.EnterProgress) || binding.EnterProgress is < 0 or > 1)
            throw new ArgumentException("装载身份或显示状态无效。", nameof(binding));
    }
}
