using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>
/// 有界的、客户端隔离的预览帧句柄存储。
/// 发布只接受已经生成的 PNG 字节，并在复制前经过传输和内容完整性合同校验。
/// </summary>
public sealed class PreviewFrameHandleStore
{
    private sealed class Entry(string clientId, PreviewFrameMetadataDto metadata, byte[] content, PreviewFrameInspectionResponse? inspection)
    {
        public string ClientId { get; } = clientId;
        public PreviewFrameMetadataDto Metadata { get; } = metadata;
        public byte[] Content { get; } = content;
        public PreviewFrameInspectionResponse? Inspection { get; } = inspection;
        public int Readers { get; set; }
        public bool Retained { get; set; } = true;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ClientId, PreviewSessionResponse Session)> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxFrames;
    private readonly long _maxBytes;
    private readonly int _maxReaders;
    private readonly long _maxReadBytes;
    private readonly int _maxSessions;
    private long _retainedBytes;
    private int _activeReaders;
    private long _activeReadBytes;

    public PreviewFrameHandleStore(int maxFrames = 8, long maxBytes = PreviewFrameContract.MaxFrameBytes * 8,
        int maxReaders = 8, long maxReadBytes = PreviewFrameContract.MaxFrameBytes * 8, int maxSessions = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);
        _maxFrames = maxFrames;
        _maxBytes = maxBytes;
        _maxReaders = maxReaders;
        _maxReadBytes = maxReadBytes;
        _maxSessions = maxSessions;
    }

    public int Count { get { lock (_gate) return _frames.Count; } }
    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public int ActiveReaders { get { lock (_gate) return _activeReaders; } }
    public long ActiveReadBytes { get { lock (_gate) return _activeReadBytes; } }

    /// <summary>由会话所有者显式绑定当前不透明代次；仅发布帧不能切换或复活会话。</summary>
    public BackendErrorResponse? Activate(string clientId, PreviewSessionResponse session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.PreviewId, out var existing))
            {
                if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
                    return new(BackendErrorCode.ResourceUnauthorized, "预览会话不属于当前客户端。", "previewId");
                if (!SameInput(existing.Session, session)) RemovePreview(session.PreviewId);
            }
            else if (_sessions.Count >= _maxSessions)
                return new(BackendErrorCode.SessionQuotaExceeded, "预览帧会话数量已达到上限。", "previewId");
            _sessions[session.PreviewId] = (clientId, session);
            return null;
        }
    }

    /// <summary>关闭绑定并使尚未取得租约的旧帧失效；已有读租约继续计入预算。</summary>
    public BackendErrorResponse? Close(string clientId, string previewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(previewId, out var existing))
                return new(BackendErrorCode.PreviewSessionNotFound, "预览会话不存在。", "previewId");
            if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
                return new(BackendErrorCode.ResourceUnauthorized, "预览会话不属于当前客户端。", "previewId");
            _sessions.Remove(previewId);
            RemovePreview(previewId);
            return null;
        }
    }

    /// <summary>
    /// 发布单帧；<paramref name="lastRequestSequence"/> 是当前会话已接受的前一序号。
    /// <paramref name="inspection"/> 与帧同寿命，已由 Worker 和 Host 两侧限制在固定字节预算内，不计入 PNG 保留预算。
    /// </summary>
    public BackendErrorResponse? Publish(
        string clientId,
        PreviewSessionResponse session,
        PreviewFrameRequest request,
        PreviewFrameMetadataDto metadata,
        ReadOnlySpan<byte> content,
        long lastRequestSequence,
        DateTimeOffset nowUtc,
        PreviewFrameInspectionResponse? inspection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metadata);

        BackendErrorResponse? transport = PreviewTransportValidation.ValidateFrame(session, request, metadata, nowUtc);
        if (transport is not null) return transport;
        BackendErrorResponse? integrity = PreviewFrameContract.Validate(metadata, content, session.Limits,
            session.PreviewId, session.PreviewGeneration, session.Source.SourceRevision, lastRequestSequence, nowUtc);
        if (integrity is not null) return integrity;
        if (string.IsNullOrWhiteSpace(metadata.FrameId))
            return new(BackendErrorCode.InvalidRequest, "预览帧标识不能为空。", "frameId");
        if ((inspection is null) != (metadata.InspectionUrl is null)
            || inspection is not null && (inspection.FrameId != metadata.FrameId || inspection.PreviewGeneration != metadata.PreviewGeneration
                || inspection.SourceRevision != metadata.SourceRevision || inspection.TimeUs != metadata.TimeUs))
            return new(BackendErrorCode.InvalidRequest, "检查投影与预览帧身份不一致。", "inspectionUrl");

        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.PreviewId, out var active))
                return new(BackendErrorCode.PreviewSessionNotFound, "预览会话未激活或已经关闭。", "previewId");
            if (!string.Equals(active.ClientId, clientId, StringComparison.Ordinal))
                return new(BackendErrorCode.ResourceUnauthorized, "预览会话不属于当前客户端。", "previewId");
            if (!SameInput(active.Session, session))
                return new(BackendErrorCode.PreviewGenerationConflict, "预览帧属于过期的输入代次。", "previewGeneration");
            RemoveExpired(nowUtc);
            if (_frames.ContainsKey(metadata.FrameId) || _frames.Values.Any(item => item.Metadata.Content.HandleId == metadata.Content.HandleId))
                return new(BackendErrorCode.InvalidRequest, "预览帧句柄已经存在。", "frameId");
            if (session.Limits.MaxRetainedFrames <= 0 || session.Limits.MaxRetainedFrameBytes <= 0)
                return new(BackendErrorCode.InvalidRequest, "预览会话保留配额无效。", "limits");
            if (metadata.Content.SizeBytes > _maxBytes)
                return new(BackendErrorCode.ResourceQuotaExceeded, "预览帧保留配额不足。", "content.sizeBytes");
            KeyValuePair<string, Entry>[] priorFrames = _frames
                .Where(pair => string.Equals(pair.Value.Metadata.PreviewId, session.PreviewId, StringComparison.Ordinal))
                .OrderBy(pair => long.Parse(pair.Value.Metadata.RequestSeq, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            int evictions = 0;
            long sessionBytes = priorFrames.Sum(pair => pair.Value.Content.LongLength);
            while (evictions < priorFrames.Length
                && (priorFrames.Length - evictions >= session.Limits.MaxRetainedFrames
                    || content.Length > session.Limits.MaxRetainedFrameBytes - sessionBytes))
                sessionBytes -= priorFrames[evictions++].Value.Content.LongLength;
            if (_frames.Count - evictions >= _maxFrames)
                return new(BackendErrorCode.RequestQuotaExceeded, "预览帧保留数量已达到上限。", "frameId");
            long bytesFreed = priorFrames.Take(evictions).Where(pair => pair.Value.Readers == 0)
                .Sum(pair => pair.Value.Content.LongLength);
            if (content.Length > _maxBytes - (_retainedBytes - bytesFreed))
                return new(BackendErrorCode.ResourceQuotaExceeded, "预览帧保留配额不足。", "content.sizeBytes");
            // Allocate only after reserving the store budget under the same gate.
            byte[] copy = content.ToArray();
            foreach (var prior in priorFrames.Take(evictions)) Remove(prior.Key, prior.Value);
            _frames.Add(metadata.FrameId, new(clientId, metadata, copy, inspection));
            _retainedBytes += copy.LongLength;
        }
        return null;
    }

    /// <summary>兼容的独立副本读取；HTTP传输应使用有界的 <see cref="TryAcquireRead"/>。</summary>
    public bool TryRead(string clientId, string frameId, DateTimeOffset nowUtc,
        out PreviewFrameReadResult? result, out BackendErrorResponse? error)
    {
        if (!TryAcquireRead(clientId, frameId, nowUtc, out PreviewFrameReadLease? lease, out error))
        {
            result = null;
            return false;
        }
        using (lease)
        {
            byte[] content = new byte[checked((int)lease!.Stream.Length)];
            lease.Stream.ReadExactly(content);
            result = new(lease.Metadata, content);
            return true;
        }
    }

    /// <summary>借用不可写帧流，不复制整个帧；租约在所有响应路径（含HEAD/304/失败）结束时释放。</summary>
    public bool TryAcquireRead(string clientId, string frameId, DateTimeOffset nowUtc,
        out PreviewFrameReadLease? lease, out BackendErrorResponse? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        lock (_gate)
        {
            if (!_frames.TryGetValue(frameId, out Entry? entry))
            {
                RemoveExpired(nowUtc);
                lease = null;
                error = new(BackendErrorCode.PreviewFrameNotFound, "预览帧不存在。", "frameId");
                return false;
            }
            if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
            {
                lease = null;
                error = new(BackendErrorCode.ResourceUnauthorized, "预览帧不属于当前客户端。", "frameId");
                return false;
            }
            if (entry.Metadata.Content.ExpiresAtUtc <= nowUtc)
            {
                Remove(frameId, entry);
                lease = null;
                error = new(BackendErrorCode.PreviewFrameExpired, "预览帧句柄已经到期。", "expiresAtUtc");
                return false;
            }
            if (_activeReaders >= _maxReaders)
            {
                lease = null;
                error = new(BackendErrorCode.RequestQuotaExceeded, "预览帧并发读取已达到上限。", "frameId");
                return false;
            }
            if (entry.Content.LongLength > _maxReadBytes - _activeReadBytes)
            {
                lease = null;
                error = new(BackendErrorCode.ResourceQuotaExceeded, "预览帧读取字节预算不足。", "content.sizeBytes");
                return false;
            }
            entry.Readers++;
            _activeReaders++;
            _activeReadBytes += entry.Content.LongLength;
            lease = new(entry.Metadata, new MemoryStream(entry.Content, writable: false), () => Release(entry));
            error = null;
            return true;
        }
    }

    /// <summary>读取与帧同寿命的检查投影；帧不存在、不属于客户端或到期时与读取帧内容的错误一致。</summary>
    public bool TryReadInspection(string clientId, string frameId, DateTimeOffset nowUtc,
        out PreviewFrameInspectionResponse? inspection, out BackendErrorResponse? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        inspection = null;
        lock (_gate)
        {
            if (!_frames.TryGetValue(frameId, out Entry? entry))
            {
                RemoveExpired(nowUtc);
                error = new(BackendErrorCode.PreviewFrameNotFound, "预览帧不存在。", "frameId");
                return false;
            }
            if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
            {
                error = new(BackendErrorCode.ResourceUnauthorized, "预览帧不属于当前客户端。", "frameId");
                return false;
            }
            if (entry.Metadata.Content.ExpiresAtUtc <= nowUtc)
            {
                Remove(frameId, entry);
                error = new(BackendErrorCode.PreviewFrameExpired, "预览帧句柄已经到期。", "expiresAtUtc");
                return false;
            }
            if (entry.Inspection is null)
            {
                error = new(BackendErrorCode.PreviewInspectionUnavailable, "这一帧请求时没有开启检查，或检查投影没有生成。", "frameId");
                return false;
            }
            inspection = entry.Inspection;
            error = null;
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset nowUtc)
    {
        foreach ((string id, Entry entry) in _frames.ToArray())
            if (entry.Metadata.Content.ExpiresAtUtc <= nowUtc) Remove(id, entry);
    }

    private static bool SameInput(PreviewSessionResponse left, PreviewSessionResponse right)
        => string.Equals(left.PreviewGeneration, right.PreviewGeneration, StringComparison.Ordinal)
            && left.Source == right.Source && left.Size == right.Size && left.AlphaMode == right.AlphaMode
            && left.Limits == right.Limits && left.RenderBackend == right.RenderBackend;

    private void RemovePreview(string previewId)
    {
        foreach ((string id, Entry entry) in _frames.ToArray())
            if (string.Equals(entry.Metadata.PreviewId, previewId, StringComparison.Ordinal)) Remove(id, entry);
    }

    private void Remove(string id, Entry entry)
    {
        if (!_frames.Remove(id)) return;
        entry.Retained = false;
        if (entry.Readers == 0) _retainedBytes -= entry.Content.LongLength;
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Readers--;
            _activeReaders--;
            _activeReadBytes -= entry.Content.LongLength;
            if (!entry.Retained && entry.Readers == 0) _retainedBytes -= entry.Content.LongLength;
        }
    }
}

public sealed record PreviewFrameReadResult(PreviewFrameMetadataDto Metadata, byte[] Content);

/// <summary>按请求拥有的只读流租约；帧到期后在已有读者释放前仍计入保留字节预算。</summary>
public sealed class PreviewFrameReadLease : IDisposable
{
    private Action? _release;
    internal PreviewFrameReadLease(PreviewFrameMetadataDto metadata, Stream stream, Action release)
    {
        Metadata = metadata;
        Stream = stream;
        _release = release;
    }
    public PreviewFrameMetadataDto Metadata { get; }
    public Stream Stream { get; }
    public void Dispose()
    {
        Action? release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        Stream.Dispose();
        release();
    }
}
