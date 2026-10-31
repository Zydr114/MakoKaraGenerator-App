using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>一次真实时间线事件的不可变记录，没有脚本实例或可写源对象。</summary>
public sealed record EventRecord
{
    /// <summary>保存真实封闭事件。</summary>
    public EventRecord(TimelineEvent timelineEvent) { ArgumentNullException.ThrowIfNull(timelineEvent); Event = timelineEvent; }
    /// <summary>完整强类型事件。</summary>
    public TimelineEvent Event { get; }
    /// <summary>确定事件身份。</summary>
    public EventId EventId => Event.EventId;
    /// <summary>整数微秒时刻。</summary>
    public long TimeUs => Event.TimeUs;
    /// <summary>事件种类。</summary>
    public TimelineEventKind Kind => Event.Kind;
}

/// <summary>截至一个已处理边界的只读事件前缀；未来事件不是“已发生”。</summary>
public sealed class TimelineHistory
{
    private readonly ContentCatalog _content;
    private readonly ImmutableArray<EventRecord> _table;
    private readonly int _visibleCount;
    private ImmutableArray<EventRecord> _records;
    /// <summary>宿主建立已处理历史；未来、乱序、重复或不属于源目录的事件拒绝。</summary>
    public TimelineHistory(ContentCatalog content, long lastBoundaryUs, ImmutableArray<EventRecord> records)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentOutOfRangeException.ThrowIfNegative(lastBoundaryUs);
        if (records.IsDefault) throw new ArgumentException("历史需要明确的不可变前缀。", nameof(records));
        ValidateRecords(content, lastBoundaryUs, records, default);
        _content = content; LastBoundaryUs = lastBoundaryUs; _table = records; _visibleCount = records.Length; _records = records;
    }
    private static void ValidateRecords(ContentCatalog content, long lastBoundaryUs, ImmutableArray<EventRecord> records, CancellationToken token)
    {
        HashSet<EventId> seen = [];
        for (int index = 0; index < records.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var record = records[index];
            if (record is null || record.TimeUs > lastBoundaryUs || !seen.Add(record.EventId)
                || index > 0 && TimelineEvent.Comparer.Compare(records[index - 1].Event, record.Event) >= 0)
                throw new ArgumentException("历史不能包含未来、重复或乱序事件。", nameof(records));
            ValidateSource(content, record.Event);
        }
    }
    private TimelineHistory(ContentCatalog content, ImmutableArray<EventRecord> table, int count, long boundary)
    { _content = content; _table = table; _visibleCount = count; LastBoundaryUs = boundary; }
    /// <summary>最后已处理边界，不是渲染帧的任意分数时刻。</summary>
    public long LastBoundaryUs { get; }
    /// <summary>稳定排序的已发生记录；没有未来表或用户实例引用。</summary>
    public ImmutableArray<EventRecord> Records
    {
        get
        {
            if (_records.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref _records, ImmutableArray.Create(_table.AsSpan(0, _visibleCount)));
            return _records;
        }
    }

    // Only the execution host can create this table. Author-facing construction still validates every input.
    internal sealed class ValidatedEventTable
    {
        private readonly ContentCatalog _content;
        private readonly ImmutableArray<EventRecord> _records;
        internal ValidatedEventTable(ContentCatalog content, ImmutableArray<EventRecord> records, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (records.IsDefault) throw new ArgumentException("事件表必须明确。", nameof(records));
            ValidateRecords(content, long.MaxValue, records, token);
            _content = content; _records = records;
        }
        internal TimelineEvent EventAt(int index) => _records[index].Event;
        internal TimelineHistory Prefix(long boundary, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(boundary);
            if ((uint)count > (uint)_records.Length || count > 0 && _records[count - 1].TimeUs > boundary)
                throw new ArgumentOutOfRangeException(nameof(count));
            return new(_content, _records, count, boundary);
        }
        internal TimelineHistory At(long boundary)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(boundary);
            int low = 0, high = _records.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_records[middle].TimeUs <= boundary) low = middle + 1; else high = middle;
            }
            return Prefix(boundary, low);
        }
    }
    /// <summary>读取半开查询内的真实 TokenStarted；没有逐字时间不会制造起唱。</summary>
    public ImmutableArray<EventRecord> GetStarts(TimeRange range, SourceFilter filter)
    {
        if (!range.IsValid) throw new ArgumentException("历史查询需要有效半开区间。", nameof(range));
        ArgumentNullException.ThrowIfNull(filter); ValidateFilter(filter);
        int first = LowerBound(range.StartUs), last = LowerBound(range.EndUs);
        ImmutableArray<EventRecord>.Builder result = ImmutableArray.CreateBuilder<EventRecord>();
        for (int index = first; index < last; index++)
            if (_table[index].Event is TokenStarted started && Matches(filter, started.Source)) result.Add(_table[index]);
        return result.ToImmutable();
    }
    private void ValidateFilter(SourceFilter filter)
    {
        switch (filter)
        {
            case SourceFilter.Project: break;
            case SourceFilter.Branch branch: _ = _content.GetBranch(branch.Id); break;
            case SourceFilter.Lines lines: foreach (var id in lines.Ids) _ = _content.GetLine(id); break;
            case SourceFilter.Tokens tokens: foreach (var id in tokens.Ids) _ = _content.GetToken(id); break;
            default: throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }
    private static bool Matches(SourceFilter filter, SourceToken token) => filter switch
    {
        SourceFilter.Project => true,
        SourceFilter.Branch branch => token.BranchId == branch.Id,
        SourceFilter.Lines lines => lines.Ids.Contains(token.LineId),
        SourceFilter.Tokens tokens => tokens.Ids.Contains(token.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };
    private int LowerBound(long timeUs)
    {
        int low = 0, high = _visibleCount;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_table[middle].TimeUs < timeUs) low = middle + 1; else high = middle;
        }
        return low;
    }
    private static void ValidateSource(ContentCatalog content, TimelineEvent evt)
    {
        switch (evt)
        {
            case TokenPerformanceEvent token:
                if (content.GetToken(token.TokenId) != token.Source || token.SourceOrder.Branch != content.GetBranch(token.BranchId).SourceOrder
                    || token.SourceOrder.Line != content.GetLine(token.LineId).SourceOrder) throw new ArgumentException("历史元素事件不属于固定源目录。");
                break;
            case LinePerformanceEvent line:
                if (content.GetLine(line.LineId) != line.Source || line.SourceOrder.Branch != content.GetBranch(line.BranchId).SourceOrder)
                    throw new ArgumentException("历史行事件不属于固定源目录。");
                var timed = line.Source.TokenIds.Select(content.GetToken).Where(static item => item.TimeRange is not null).ToArray();
                var range = timed.Length == 0 ? line.Source.TimeRange : new TimeRange(timed.Min(static token => token.TimeRange!.Value.StartUs), timed.Max(static token => token.TimeRange!.Value.EndUs));
                if (range != line.PerformanceRange || line.ProgressSource != (timed.Length == 0 ? ProgressSource.Line : ProgressSource.Token))
                    throw new ArgumentException("历史行演唱区间不符，不能伪造整行起唱。");
                break;
            case BindingTimelineEvent binding: ValidateBinding(content, binding.Binding); break;
            case ElementTimelineEvent { Element: SceneElementIdentity.Lyric lyric }: ValidateBinding(content, lyric.Binding); break;
            case ElementTimelineEvent { Element: SceneElementIdentity.Mark mark }: ValidateBinding(content, mark.Target); break;
            case ElementTimelineEvent { Element: SceneElementIdentity.Group { BranchId: { } branch } }: _ = content.GetBranch(branch); break;
            case LeadInTimelineEvent cue: ValidateBinding(content, cue.Target.Target); break;
            case CursorTransitionEvent transition: ValidateCursor(content, transition.From); ValidateCursor(content, transition.To); break;
        }
    }
    private static void ValidateBinding(ContentCatalog content, FrameLine binding)
    {
        if (content.GetLine(binding.LineId).BranchId != binding.BranchId) throw new ArgumentException("历史装载源关联不符。");
    }
    private static void ValidateCursor(ContentCatalog content, CursorIdentity cursor)
    {
        if (content.GetLine(cursor.LineId).BranchId != cursor.BranchId) throw new ArgumentException("历史光标源关联不符。");
        if (cursor.TokenId is { } id)
        {
            var source = content.GetToken(id);
            if (source.LineId != cursor.LineId || source.TimeRange != cursor.TimeRange
                || cursor.OccurrenceId != StableIdentities.Occurrence(cursor.BranchId, cursor.LineId, id, cursor.TimeRange))
                throw new ArgumentException("历史光标不能伪造元素区间或 occurrence。");
        }
        else
        {
            var line = content.GetLine(cursor.LineId);
            if (line.TimeRange != cursor.TimeRange || line.TokenIds.Any(id => content.GetToken(id).TimeRange is not null))
                throw new ArgumentException("行光标依据只能使用无逐字时间的真实行区间。");
        }
    }
}
