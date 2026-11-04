using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>固定事件索引建立预算；事件输入、有限键和桶内引用分别计数。</summary>
public sealed record TimelineIndexBudget
{
    /// <summary>最多读取的输入事件数，包含重复身份。</summary>
    public int MaximumEvents { get; init; } = 200_000;
    /// <summary>最多建立的类型／来源／原文有限键。</summary>
    public int MaximumKeys { get; init; } = 1_000_000;
    /// <summary>全部键桶中的事件引用总量。</summary>
    public int MaximumReferences { get; init; } = 4_000_000;
    internal void Validate()
    {
        if (MaximumEvents <= 0 || MaximumKeys <= 0 || MaximumReferences <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents), "索引建立预算必须为正数。");
    }
}

/// <summary>事件索引的封闭强类型来源范围；源演唱 occurrence 与展示 Binding 分别查询。</summary>
public abstract record TimelineScope
{
    private TimelineScope() { }
    /// <summary>全工程的真实事件。</summary>
    public sealed record Project : TimelineScope;
    /// <summary>真实源分支。</summary>
    public sealed record Branch(BranchId Id) : TimelineScope;
    /// <summary>真实源行。</summary>
    public sealed record Line(LineId Id) : TimelineScope;
    /// <summary>真实源元素。</summary>
    public sealed record Token(TokenId Id) : TimelineScope;
    /// <summary>真实源演唱 occurrence，不随重复装载增加。</summary>
    public sealed record Occurrence(OccurrenceId Id) : TimelineScope;
    /// <summary>一次展示装载，不伪造源演唱。</summary>
    public sealed record Binding(BindingId Id) : TimelineScope;
    /// <summary>真实场景节点。</summary>
    public sealed record Node(NodeId Id) : TimelineScope;
    /// <summary>真实前奏。</summary>
    public sealed record Cue(CueId Id) : TimelineScope;
}

/// <summary>有限事件键；文本使用精确 ordinal 匹配，仅适用于拥有真实源文本的事件。</summary>
public sealed record TimelineSelector
{
    /// <summary>指定真实事件类型、来源范围及可选源文本，不接受任意 predicate。</summary>
    public TimelineSelector(TimelineEventKind kind, TimelineScope? scope = null, string? sourceText = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        scope ??= new TimelineScope.Project();
        bool valid = scope switch
        {
            TimelineScope.Project => true,
            TimelineScope.Branch item => item.Id.IsValid,
            TimelineScope.Line item => item.Id.IsValid,
            TimelineScope.Token item => item.Id.IsValid,
            TimelineScope.Occurrence item => item.Id.IsValid,
            TimelineScope.Binding item => item.Id.IsValid,
            TimelineScope.Node item => item.Id.IsValid,
            TimelineScope.Cue item => item.Id.IsValid,
            _ => false,
        };
        if (!valid) throw new ArgumentException("事件查询范围身份无效。", nameof(scope));
        Kind = kind; Scope = scope; SourceText = sourceText;
    }
    /// <summary>真实封闭事件种类。</summary>
    public TimelineEventKind Kind { get; }
    /// <summary>明确的来源或装载范围。</summary>
    public TimelineScope Scope { get; }
    /// <summary>可选的原始源文本；不归一化、不按系统区域匹配。</summary>
    public string? SourceText { get; }
}

/// <summary>固定源事件的不可变索引；建立时排序并按稳定事件身份去重，查询不从零重放。</summary>
/// <remarks>每个有限键的 Last／Next／Count 为 O(log n)，Nth／ordinal 为 O(1)，范围枚举为 O(log n+k)。</remarks>
public sealed class TimelineIndex
{
    private sealed class Bucket
    {
        internal Bucket(ImmutableArray<TimelineEvent> events, CancellationToken token)
        {
            Events = events;
            var eventOrdinals = ImmutableDictionary.CreateBuilder<EventId, int>();
            var occurrenceOrdinals = ImmutableDictionary.CreateBuilder<OccurrenceId, int>();
            for (int index = 0; index < events.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                eventOrdinals.Add(events[index].EventId, index + 1);
                if (events[index] is TokenPerformanceEvent performance) occurrenceOrdinals.Add(performance.OccurrenceId, index + 1);
            }
            EventOrdinals = eventOrdinals.ToImmutable(); OccurrenceOrdinals = occurrenceOrdinals.ToImmutable();
        }
        internal ImmutableArray<TimelineEvent> Events { get; }
        internal ImmutableDictionary<EventId, int> EventOrdinals { get; }
        internal ImmutableDictionary<OccurrenceId, int> OccurrenceOrdinals { get; }
    }

    private readonly ImmutableDictionary<TimelineSelector, Bucket> _buckets;

    /// <summary>从宿主已有真实事件表建立索引；重复装载的同一源事件只保留一次，冲突身份拒绝。</summary>
    public TimelineIndex(IEnumerable<TimelineEvent> events, TimelineIndexBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        budget ??= new(); budget.Validate(); cancellationToken.ThrowIfCancellationRequested();
        long inputCount = 0, references = 0;
        Dictionary<EventId, TimelineEvent> unique = [];
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inputCount > budget.MaximumEvents) throw Budget();
            ArgumentNullException.ThrowIfNull(item);
            var normalized = item.WithReplayReason(ReplayReason.Initial);
            if (unique.TryGetValue(item.EventId, out var previous) && previous != normalized)
                throw new ArgumentException("相同事件身份不能具有不同源语义。", nameof(events));
            unique.TryAdd(item.EventId, normalized);
        }
        Events = [.. unique.Values.Order(TimelineEvent.Comparer)];
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<TimelineSelector, List<TimelineEvent>> buckets = [];
        foreach (var item in Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? text = item switch { TokenPerformanceEvent token => token.Source.Text, LinePerformanceEvent line => line.Source.Text, _ => null };
            foreach (var scope in Scopes(item).Distinct())
            {
                Add(new(item.Kind, scope), item);
                if (text is not null) Add(new(item.Kind, scope, text), item);
            }
        }
        var indexed = ImmutableDictionary.CreateBuilder<TimelineSelector, Bucket>();
        foreach (var item in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            indexed.Add(item.Key, new Bucket([.. item.Value], cancellationToken));
        }
        _buckets = indexed.ToImmutable();

        void Add(TimelineSelector selector, TimelineEvent item)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++references > budget.MaximumReferences) throw Budget();
            if (!buckets.TryGetValue(selector, out var values))
            {
                if (buckets.Count >= budget.MaximumKeys) throw Budget();
                values = []; buckets.Add(selector, values);
            }
            values.Add(item);
        }
        static EffectOutputException Budget() => new("TimelineIndexBudgetExceeded", "源事件索引的事件、有限键或引用数量超过建立预算。");
    }

    /// <summary>明确空索引。</summary>
    public static TimelineIndex Empty { get; } = new([]);
    /// <summary>全部真实事件，使用既有同边界优先级、来源顺序和稳定身份排序。</summary>
    public ImmutableArray<TimelineEvent> Events { get; }

    /// <summary>键内最后一个 TimeUs 小于或等于查询时刻的事件；边界事件在该时刻已发生。</summary>
    public TimelineEvent? Last(TimelineSelector selector, long timeUs)
    {
        var events = Get(selector); int index = UpperBound(events, timeUs) - 1;
        return index < 0 ? null : events[index];
    }
    /// <summary>键内第一个 TimeUs 严格晚于查询时刻的事件。</summary>
    public TimelineEvent? Next(TimelineSelector selector, long timeUs)
    {
        var events = Get(selector); int index = UpperBound(events, timeUs);
        return index == events.Length ? null : events[index];
    }
    /// <summary>键内截至查询时刻（包含该边界）的真实事件数量。</summary>
    public int Count(TimelineSelector selector, long timeUs) => UpperBound(Get(selector), timeUs);
    /// <summary>键内完整真实事件数量，包括未来。</summary>
    public int Count(TimelineSelector selector) => Get(selector).Length;
    /// <summary>半开范围内的真实事件数量。</summary>
    public int Count(TimelineSelector selector, TimeRange range)
    {
        ValidateRange(range); var events = Get(selector);
        return LowerBound(events, range.EndUs) - LowerBound(events, range.StartUs);
    }
    /// <summary>键内第 zeroBasedIndex 个真实事件；越界返回 null。</summary>
    public TimelineEvent? Nth(TimelineSelector selector, int zeroBasedIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedIndex); var events = Get(selector);
        return zeroBasedIndex < events.Length ? events[zeroBasedIndex] : null;
    }
    /// <summary>真实事件在键内从 1 开始的 ordinal；不在该范围返回 null。</summary>
    public int? Ordinal(TimelineSelector selector, EventId eventId)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!eventId.IsValid) throw new ArgumentException("事件身份无效。", nameof(eventId));
        return _buckets.TryGetValue(selector, out var bucket) && bucket.EventOrdinals.TryGetValue(eventId, out int ordinal) ? ordinal : null;
    }
    /// <summary>真实元素演唱 occurrence 在该事件种类与来源范围内从 1 开始的 ordinal；展示次数不参与。</summary>
    public int? Ordinal(TimelineSelector selector, OccurrenceId occurrenceId)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!occurrenceId.IsValid) throw new ArgumentException("演唱身份无效。", nameof(occurrenceId));
        return _buckets.TryGetValue(selector, out var bucket) && bucket.OccurrenceOrdinals.TryGetValue(occurrenceId, out int ordinal) ? ordinal : null;
    }
    /// <summary>半开范围内的稳定有序事件；只枚举所请求结果，不扫描其他键或重放作者代码。</summary>
    public ImmutableArray<TimelineEvent> GetRange(TimelineSelector selector, TimeRange range)
    {
        ValidateRange(range); var events = Get(selector);
        return events[LowerBound(events, range.StartUs)..LowerBound(events, range.EndUs)];
    }

    private ImmutableArray<TimelineEvent> Get(TimelineSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return _buckets.TryGetValue(selector, out var bucket) ? bucket.Events : [];
    }
    private static void ValidateRange(TimeRange range)
    {
        if (!range.IsValid) throw new ArgumentException("索引查询需要有效半开区间。", nameof(range));
    }
    private static int LowerBound(ImmutableArray<TimelineEvent> events, long timeUs) => Bound(events, timeUs, false);
    private static int UpperBound(ImmutableArray<TimelineEvent> events, long timeUs) => Bound(events, timeUs, true);
    private static int Bound(ImmutableArray<TimelineEvent> events, long timeUs, bool includeEqual)
    {
        int low = 0, high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].TimeUs < timeUs || includeEqual && events[middle].TimeUs == timeUs) low = middle + 1;
            else high = middle;
        }
        return low;
    }
    private static IEnumerable<TimelineScope> Scopes(TimelineEvent item)
    {
        yield return new TimelineScope.Project();
        switch (item)
        {
            case TokenPerformanceEvent token:
                yield return new TimelineScope.Branch(token.BranchId); yield return new TimelineScope.Line(token.LineId);
                yield return new TimelineScope.Token(token.TokenId); yield return new TimelineScope.Occurrence(token.OccurrenceId); break;
            case LinePerformanceEvent line:
                yield return new TimelineScope.Branch(line.BranchId); yield return new TimelineScope.Line(line.LineId); break;
            case BindingTimelineEvent binding:
                foreach (var scope in BindingScopes(binding.Binding)) yield return scope; break;
            case ElementTimelineEvent element:
                yield return new TimelineScope.Node(element.Element.NodeId);
                switch (element.Element)
                {
                    case SceneElementIdentity.Lyric lyric:
                        foreach (var scope in BindingScopes(lyric.Binding)) yield return scope; break;
                    case SceneElementIdentity.Mark mark:
                        foreach (var scope in BindingScopes(mark.Target)) yield return scope;
                        yield return new TimelineScope.Cue(mark.CueId); break;
                    case SceneElementIdentity.Group { BranchId: { } branch }: yield return new TimelineScope.Branch(branch); break;
                }
                break;
            case LeadInTimelineEvent leadIn:
                foreach (var scope in BindingScopes(leadIn.Target.Target)) yield return scope;
                yield return new TimelineScope.Cue(leadIn.CueId); break;
            case CursorTransitionEvent cursor:
                yield return new TimelineScope.Branch(cursor.To.BranchId); yield return new TimelineScope.Line(cursor.To.LineId);
                yield return new TimelineScope.Binding(cursor.To.BindingId);
                if (cursor.To.TokenId is { } id) yield return new TimelineScope.Token(id);
                if (cursor.To.OccurrenceId is { } occurrence) yield return new TimelineScope.Occurrence(occurrence);
                break;
        }
    }
    private static IEnumerable<TimelineScope> BindingScopes(FrameLine binding)
    {
        yield return new TimelineScope.Branch(binding.BranchId); yield return new TimelineScope.Line(binding.LineId);
        yield return new TimelineScope.Binding(binding.BindingId);
    }
}
