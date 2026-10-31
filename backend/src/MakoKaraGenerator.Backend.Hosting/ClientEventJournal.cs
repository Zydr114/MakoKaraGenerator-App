using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>每客户端独立单调事件序列及有限重放窗口。序列不复用工程修订。</summary>
public sealed class ClientEventJournal
{
    private sealed class StreamState
    {
        public long Sequence;
        public Queue<BackendEventResponse> Events { get; } = new();
        public TaskCompletionSource Changed { get; set; } = NewSignal();
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly string _hostInstanceId;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly Func<string, bool>? _isClientActive;

    /// <summary>构造有限客户端事件窗口。</summary>
    public ClientEventJournal(string hostInstanceId, int capacity = 256, TimeProvider? timeProvider = null,
        Func<string, bool>? isClientActive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        if (capacity is <= 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _hostInstanceId = hostInstanceId;
        _isClientActive = isClientActive;
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>捕获当前client事件游标；必须在工程快照锁内调用。</summary>
    public string Cursor(string clientId)
    {
        lock (_gate) return MakeCursor(clientId, State(clientId).Sequence);
    }

    /// <summary>在工程writer提交内追加小型事件，返回独立单调游标。</summary>
    public string Append(string clientId, string type, ProjectSessionSummaryResponse? summary = null,
        ProjectChangeSetDto? changeSet = null, JobSummaryDto? job = null, PreviewEventDto? preview = null)
    {
        lock (_gate)
        {
            if (_isClientActive is not null && !_isClientActive(clientId)) return MakeCursor(clientId, 0);
            StreamState state = State(clientId);
            long seq = checked(++state.Sequence);
            string cursor = MakeCursor(clientId, seq);
            state.Events.Enqueue(new(seq.ToString(CultureInfo.InvariantCulture), cursor, type, _hostInstanceId,
                _timeProvider.GetUtcNow(), summary is null ? null : summary with { EventCursor = cursor }, Clone(changeSet), job, preview));
            while (state.Events.Count > _capacity) state.Events.Dequeue();
            TaskCompletionSource signal = state.Changed;
            state.Changed = NewSignal();
            signal.TrySetResult();
            return cursor;
        }
    }

    /// <summary>在任务状态闸门内追加固定小元信息，不携带报告或大结果。</summary>
    public string AppendJob(string clientId, string type, JobSummaryDto job) => Append(clientId, type, job: job);

    /// <summary>有限重放；主机/client不匹配或缺口返回resyncRequired。</summary>
    public ClientEventsResponse ReadAfter(string clientId, string? afterCursor)
    {
        lock (_gate) return ReadAfterLocked(clientId, afterCursor);
    }

    /// <summary>检查游标和登记等待使用同一锁，避免读后订阅之间丢更新。</summary>
    public async Task<ClientEventsResponse> WaitForEventsAsync(string clientId, string? afterCursor,
        CancellationToken cancellationToken = default)
    {
        Task changed;
        lock (_gate)
        {
            ClientEventsResponse ready = ReadAfterLocked(clientId, afterCursor);
            if (ready.ResyncRequired || ready.Events.Length > 0) return ready;
            changed = State(clientId).Changed.Task;
        }
        await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        return ReadAfter(clientId, afterCursor);
    }

    private ClientEventsResponse ReadAfterLocked(string clientId, string? afterCursor)
    {
        if (_isClientActive is not null && !_isClientActive(clientId))
            return new(MakeCursor(clientId, 0), [], true);
        StreamState state = State(clientId);
        string current = MakeCursor(clientId, state.Sequence);
        long after = 0;
        if (afterCursor is not null && (!TryParseCursor(clientId, afterCursor, out after) || after > state.Sequence))
            return new(current, [], true);
        long first = state.Events.Count == 0 ? state.Sequence + 1 : long.Parse(state.Events.Peek().EventSeq, CultureInfo.InvariantCulture);
        if (after < first - 1) return new(current, [], true);
        return new(current, [.. state.Events.Where(item => long.Parse(item.EventSeq, CultureInfo.InvariantCulture) > after)
            .Select(item => item with { ChangeSet = Clone(item.ChangeSet) })], false);
    }

    /// <summary>移除已回收client的内存窗口；晚到通知由身份predicate拒绝。</summary>
    public void RemoveClient(string clientId)
    {
        lock (_gate)
            if (_streams.Remove(clientId, out StreamState? state)) state.Changed.TrySetResult();
    }

    private StreamState State(string clientId)
    {
        if (!_streams.TryGetValue(clientId, out StreamState? state))
        {
            state = new();
            if (_isClientActive is null || _isClientActive(clientId)) _streams.Add(clientId, state);
            else state.Changed.TrySetResult();
        }
        return state;
    }
    private string MakeCursor(string clientId, long sequence) => _hostInstanceId + ":" + clientId + ":" + sequence.ToString(CultureInfo.InvariantCulture);
    private bool TryParseCursor(string clientId, string cursor, out long sequence)
    {
        string prefix = _hostInstanceId + ":" + clientId + ":";
        sequence = 0;
        return cursor.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence >= 0;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ProjectChangeSetDto? Clone(ProjectChangeSetDto? value) => value is null ? null : value with
    { Areas = [.. value.Areas], BranchIds = [.. value.BranchIds], LineIds = [.. value.LineIds], TokenIds = [.. value.TokenIds] };
}
