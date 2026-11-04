using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>固定源／场景时间线；不运行作者代码或按 UI 帧累积事件。</summary>
public sealed class TimelineProjection
{
    private readonly Lazy<TimelineHistory.ValidatedEventTable> _historyTable;
    private TimelineProjection(ContentCatalog content, SceneCatalog scene, ImmutableArray<TimelineEvent> events, ReplayReason reason)
    {
        Content = content; Scene = scene; Events = events;
        CursorTransitions = [.. events.OfType<CursorTransitionEvent>()];
        _historyTable = new(() => CreateHistoryTable(reason, default));
    }
    /// <summary>与事件一致的完整源目录。</summary>
    public ContentCatalog Content { get; }
    /// <summary>与事件一致的完整场景目录。</summary>
    public SceneCatalog Scene { get; }
    /// <summary>稳定排序的源／装载／可见／前奏、真实分析 hop 和语义光标转换事件。</summary>
    public ImmutableArray<TimelineEvent> Events { get; }
    /// <summary>同一固定事件表的真实后继集合；并发边不选择唯一光标。</summary>
    public ImmutableArray<CursorTransitionEvent> CursorTransitions { get; }
    /// <summary>后继两端均按实际输出绑定筛选，不用宽源订阅扩大权限或跳过范围外目标。</summary>
    public ImmutableArray<CursorTransitionEvent> GetCursorSuccessors(CursorIdentity from, EffectBinding binding)
    {
        ArgumentNullException.ThrowIfNull(from);
        var scope = ResolvedSourceSubscription.Create(this, binding, new SourceSubscription.BoundSources());
        return [.. CursorTransitions.Where(edge => edge.From == from && scope.Matches(edge))];
    }
    /// <summary>从真实源时间和共享显示计划建立一次事件表；缺时间字绝不制造起唱。</summary>
    public static TimelineProjection Create(ProjectSnapshot snapshot, ReplayReason reason, AudioFeatures? audio = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var content = ContentProjection.Create(snapshot); var scene = SceneProjection.Create(snapshot, content);
        List<TimelineEvent> events = [];
        AddPerformance(content, reason, events, cancellationToken);
        AddDisplay(snapshot, content, scene, reason, events, cancellationToken);
        CursorTransitionProjection.Add(snapshot, content, reason, events, cancellationToken);
        if (audio is { Availability: AudioAvailability.Available, DurationUs: { } duration })
            foreach (var point in audio.GetRange(new(0, duration)).Points)
            { cancellationToken.ThrowIfCancellationRequested(); events.Add(new AudioFeatureTick(audio.Revision!, point, reason)); }
        events.Sort(TimelineEvent.Comparer);
        if (events.Select(static evt => evt.EventId).Distinct().Count() != events.Count)
            throw new PreparedFrameException("DuplicateTimelineEvent", "固定事件表中存在重复身份，不能悄悄去重。");
        return new(content, scene, [.. events], reason);
    }
    /// <summary>截取已处理边界的真实历史；不会因请求未来区间而暴露未来事件。</summary>
    public TimelineHistory HistoryAt(long lastBoundaryUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastBoundaryUs);
        return _historyTable.Value.At(lastBoundaryUs);
    }
    internal TimelineHistory.ValidatedEventTable CreateHistoryTable(ReplayReason reason, CancellationToken token)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var records = ImmutableArray.CreateBuilder<EventRecord>(Events.Length);
        foreach (var evt in Events) { token.ThrowIfCancellationRequested(); records.Add(new(evt.WithReplayReason(reason))); }
        return new(Content, records.MoveToImmutable(), token);
    }
    private static void AddPerformance(ContentCatalog content, ReplayReason reason, List<TimelineEvent> events, CancellationToken cancellationToken)
    {
        foreach (var line in content.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int branchOrder = content.GetBranch(line.BranchId).SourceOrder;
            var timed = line.TokenIds.Select(content.GetToken).Where(static token => token.TimeRange is not null).ToArray();
            foreach (var token in timed)
            {
                EventSourceOrder order = new(branchOrder, line.SourceOrder, token.SourceOrder);
                events.Add(new TokenStarted(token, order, reason)); events.Add(new TokenCompleted(token, order, reason));
            }
            TimeRange? range = timed.Length == 0 ? line.TimeRange
                : new(timed.Min(static token => token.TimeRange!.Value.StartUs), timed.Max(static token => token.TimeRange!.Value.EndUs));
            if (range is not { } performance) continue;
            var source = timed.Length == 0 ? ProgressSource.Line : ProgressSource.Token;
            EventSourceOrder rowOrder = new(branchOrder, line.SourceOrder);
            events.Add(new LinePerformanceStarted(line, performance, source, rowOrder, reason));
            events.Add(new LinePerformanceCompleted(line, performance, source, rowOrder, reason));
        }
    }
    private static void AddDisplay(ProjectSnapshot snapshot, ContentCatalog content, SceneCatalog scene, ReplayReason reason, List<TimelineEvent> events,
        CancellationToken cancellationToken)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        SortedSet<long> boundaries = [0];
        foreach (var line in content.Lines)
            if (line.TimeRange is { } range) { boundaries.Add(range.StartUs); boundaries.Add(range.EndUs); }
        foreach (var evt in events) boundaries.Add(evt.TimeUs);
        foreach (var branch in snapshot.Lyrics.Branches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (branch.TimingMode == LyricBranchTimingMode.None) continue;
            var program = tree.FindProgram(branch.Id)!;
            foreach (var occurrence in LyricDisplayScheduleBuilder.Build(branch, program, tree))
            { boundaries.Add(occurrence.RevealTimeUs); boundaries.Add(occurrence.HideTimeUs); }
        }
        foreach (var clip in scene.Clips) { boundaries.Add(clip.TimeRange.StartUs); boundaries.Add(clip.TimeRange.EndUs); }
        foreach (var cue in scene.LeadIns.Where(static cue => cue.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cue.TimeRange is not { } range) throw new PreparedFrameException("InvalidLeadInTimeline", "启用前奏缺少有效准备区间：" + cue.Id.Value);
            var contentNode = (CountdownSequenceContent)tree.FindNode(cue.ContainerId.Value)!.Content!;
            var coreRange = new Core.Time.TimeRange(range.StartUs, range.EndUs);
            for (int step = 0; step <= contentNode.MarkNodeIds.Length; step++)
                boundaries.Add(LeadInTiming.StepBoundary(coreRange, contentNode.MarkNodeIds.Length, step));
            var frame = LyricSceneSampler.SampleFrame(snapshot, range.StartUs, cancellationToken);
            var target = FrameProjection.Lines(frame, scene).SingleOrDefault(line => line.LineId == cue.TargetLineId && line.CueId == cue.Id)
                ?? throw new PreparedFrameException("InvalidLeadInTimeline", "前奏开始时共享场景没有目标装载：" + cue.Id.Value);
            var association = new LeadInEventTarget(cue, target, contentNode.MarkNodeIds.Length);
            var order = Order(content, target);
            events.Add(new LeadInStarted(association, order, reason));
            for (int step = 1; step < contentNode.MarkNodeIds.Length; step++)
                events.Add(new LeadInStepChanged(association, LeadInTiming.StepBoundary(coreRange, contentNode.MarkNodeIds.Length, step), step, order, reason));
            events.Add(new LeadInCompleted(association, order, reason));
        }
        // 候选边界由源／片段／槽位／准备计划产生；可见变化只认共享采样器的真实左右状态。
        foreach (long boundary in boundaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = LyricSceneSampler.SampleFrame(snapshot, boundary, cancellationToken);
            var nextLines = FrameProjection.Lines(after, scene).ToDictionary(static line => line.BindingId);
            var nextElements = Elements(after, scene, content, tree);
            var before = boundary == 0 ? null : LyricSceneSampler.SampleFrame(snapshot, boundary - 1, cancellationToken);
            var previousLines = before is null ? [] : FrameProjection.Lines(before, scene).ToDictionary(static line => line.BindingId);
            var previousElements = before is null ? [] : Elements(before, scene, content, tree);
            foreach (var pair in previousLines)
                if (!nextLines.ContainsKey(pair.Key)) events.Add(new BindingLeft(pair.Value, boundary, Order(content, pair.Value), reason));
            foreach (var pair in nextLines)
                if (!previousLines.ContainsKey(pair.Key)) events.Add(new BindingEntered(pair.Value, boundary, Order(content, pair.Value), reason));
            foreach (var pair in previousElements)
                if (!nextElements.ContainsKey(pair.Key)) events.Add(new ElementHidden(pair.Value.Element, boundary, pair.Value.Order, reason));
            foreach (var pair in nextElements)
                if (!previousElements.ContainsKey(pair.Key)) events.Add(new ElementVisible(pair.Value.Element, boundary, pair.Value.Order, reason));
        }
    }
    private static EventSourceOrder Order(ContentCatalog content, FrameLine line) => new(content.GetBranch(line.BranchId).SourceOrder, content.GetLine(line.LineId).SourceOrder);
    private sealed record VisibleElement(SceneElementIdentity Element, EventSourceOrder Order);
    private static Dictionary<string, VisibleElement> Elements(SceneFrame frame, SceneCatalog scene, ContentCatalog content, LyricPresentationDefinition tree)
    {
        Dictionary<string, VisibleElement> elements = new(StringComparer.Ordinal);
        var lines = FrameProjection.Lines(frame, scene);
        void Add(SceneElementIdentity element, EventSourceOrder order)
        {
            // 共享父组只有一个可见身份；真实多个装载则有各自 binding key。
            string owner = element switch
            {
                SceneElementIdentity.Lyric lyric => StableIdentities.ElementOwner(lyric.NodeId, lyric.Binding.BindingId),
                SceneElementIdentity.Mark mark => StableIdentities.ElementOwner(mark.NodeId, cue: mark.CueId),
                SceneElementIdentity.Group group => StableIdentities.ElementOwner(group.NodeId),
                _ => throw new InvalidOperationException("未知节点身份。"),
            };
            elements.TryAdd(owner, new(element, order));
        }
        void Ancestors(NodeId id)
        {
            NodeId? current = id;
            while (current is { } nodeId)
            {
                var node = scene.GetNode(nodeId);
                if (node.Kind == SceneNodeKind.Group)
                {
                    var order = node.BranchId is { } branch ? new EventSourceOrder(content.GetBranch(branch).SourceOrder) : EventSourceOrder.Global;
                    Add(new SceneElementIdentity.Group(nodeId, node.BranchId), order);
                }
                current = node.ParentId;
            }
        }
        foreach (var line in lines)
        {
            var order = Order(content, line); Add(new SceneElementIdentity.Lyric(line.NodeId, line), order);
            foreach (var ruby in content.GetLine(line.LineId).RubyIds)
            {
                var node = scene.Nodes.Single(item => item.RubyId == ruby);
                Add(new SceneElementIdentity.Lyric(node.Id, line, ruby), order);
            }
            Ancestors(line.ParentId);
        }
        foreach (var cue in frame.LeadIns)
        {
            var target = lines.Single(line => line.BindingId.Value == cue.TargetBindingId);
            var indicator = (CountdownSequenceContent)tree.FindNode(cue.ContainerId)!.Content!;
            foreach (string mark in indicator.MarkNodeIds) Add(new SceneElementIdentity.Mark(new(mark), new(cue.CueId), target), Order(content, target));
            Ancestors(new(cue.ContainerId));
        }
        return elements;
    }
}
