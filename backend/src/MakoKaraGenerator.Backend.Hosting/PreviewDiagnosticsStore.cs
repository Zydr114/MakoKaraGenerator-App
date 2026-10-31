using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>
/// 预览诊断的有界只读页存储。页面只能由已经绑定会话代次的发布方写入；
/// 读取不会把不同代次或失效游标拼接在一起。
/// </summary>
public sealed class PreviewDiagnosticsStore
{
    private const int MaxRetainedPages = 32;
    private sealed class SessionPages
    {
        public required string ClientId { get; init; }
        public required PreviewSessionResponse Session { get; set; }
        public Dictionary<string, PreviewDiagnosticsPageDto> Pages { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionPages> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxSessions;

    public PreviewDiagnosticsStore(int maxSessions = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);
        _maxSessions = maxSessions;
    }

    public int Count { get { lock (_gate) return _sessions.Count; } }

    /// <summary>会话所有者显式更新代次；发布页面不能改变此绑定。</summary>
    public BackendErrorResponse? Activate(string clientId, PreviewSessionResponse session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.PreviewId, out SessionPages? existing))
            {
                if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
                    return new(BackendErrorCode.ResourceUnauthorized, "预览诊断不属于当前客户端。", "previewId");
                if (!SameInput(existing.Session, session)) existing.Pages.Clear();
                existing.Session = session;
            }
            else
            {
                if (_sessions.Count >= _maxSessions)
                    return new(BackendErrorCode.SessionQuotaExceeded, "预览诊断会话数量已达到上限。", "previewId");
                _sessions.Add(session.PreviewId, new SessionPages { ClientId = clientId, Session = session });
            }
            return null;
        }
    }

    /// <summary>关闭后旧发布返回会话不存在；重新开启必须由会话所有者显式激活。</summary>
    public BackendErrorResponse? Close(string clientId, string previewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(previewId, out SessionPages? existing))
                return new(BackendErrorCode.PreviewSessionNotFound, "预览会话不存在。", "previewId");
            if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
                return new(BackendErrorCode.ResourceUnauthorized, "预览诊断不属于当前客户端。", "previewId");
            _sessions.Remove(previewId);
            return null;
        }
    }

    public BackendErrorResponse? Publish(string clientId, PreviewSessionResponse session, PreviewDiagnosticsPageDto page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        BackendErrorResponse? validation = PreviewTransportValidation.ValidateDiagnosticsPage(session, page);
        if (validation is not null) return validation;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.PreviewId, out SessionPages? existing))
                return new(BackendErrorCode.PreviewSessionNotFound, "预览会话未激活或已经关闭。", "previewId");
            if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
                return new(BackendErrorCode.ResourceUnauthorized, "预览诊断不属于当前客户端。", "previewId");
            if (!SameInput(existing.Session, session))
                return new(BackendErrorCode.PreviewGenerationConflict, "预览诊断属于过期的输入代次。", "previewGeneration");
            string key = page.Cursor ?? string.Empty;
            if (!existing.Pages.ContainsKey(key) && existing.Pages.Count >= MaxRetainedPages)
                return new(BackendErrorCode.DiagnosticLimitExceeded, "诊断页超过完整保留上限。", "limit");
            existing.Pages[key] = page with { Diagnostics = [.. page.Diagnostics] };
        }
        return null;
    }

    /// <summary>原子替换当前代次的一组固定诊断页；拒绝时保留上一完整快照。</summary>
    public BackendErrorResponse? PublishBatch(string clientId, PreviewSessionResponse session,
        IReadOnlyList<PreviewDiagnosticsPageDto> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0 || pages.Count > MaxRetainedPages)
            return new(BackendErrorCode.DiagnosticLimitExceeded, "诊断页快照数量超过保留上限。", "limit");
        // Bound allocation by validating page sizes before copying caller-owned arrays.
        Dictionary<string, PreviewDiagnosticsPageDto> snapshot = new(StringComparer.Ordinal);
        foreach (PreviewDiagnosticsPageDto page in pages)
        {
            BackendErrorResponse? validation = PreviewTransportValidation.ValidateDiagnosticsPage(session, page);
            if (validation is not null) return validation;
            if (!snapshot.TryAdd(page.Cursor ?? string.Empty, page with { Diagnostics = [.. page.Diagnostics] }))
                return new(BackendErrorCode.InvalidRequest, "诊断页快照包含重复游标。", "cursor");
        }
        if (!snapshot.ContainsKey(string.Empty))
            return new(BackendErrorCode.InvalidRequest, "诊断页快照缺少首页。", "cursor");
        HashSet<string> visited = new(StringComparer.Ordinal);
        string? cursor = string.Empty;
        while (cursor is not null)
        {
            if (!visited.Add(cursor) || !snapshot.TryGetValue(cursor, out PreviewDiagnosticsPageDto? page))
                return new(BackendErrorCode.InvalidRequest, "诊断页快照游标链不完整。", "nextCursor");
            cursor = page.NextCursor;
        }
        if (visited.Count != snapshot.Count)
            return new(BackendErrorCode.InvalidRequest, "诊断页快照包含不可达页面。", "cursor");

        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.PreviewId, out SessionPages? existing))
                return new(BackendErrorCode.PreviewSessionNotFound, "预览会话未激活或已经关闭。", "previewId");
            if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
                return new(BackendErrorCode.ResourceUnauthorized, "预览诊断不属于当前客户端。", "previewId");
            if (!SameInput(existing.Session, session))
                return new(BackendErrorCode.PreviewGenerationConflict, "预览诊断属于过期的输入代次。", "previewGeneration");
            existing.Pages.Clear();
            foreach (var pair in snapshot) existing.Pages.Add(pair.Key, pair.Value);
            return null;
        }
    }

    public bool TryRead(string clientId, string previewId, string? cursor, int? limit,
        out PreviewDiagnosticsPageDto? page, out BackendErrorResponse? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(previewId, out SessionPages? session))
            {
                page = null;
                error = new(BackendErrorCode.PreviewSessionNotFound, "预览会话不存在。", "previewId");
                return false;
            }
            if (!string.Equals(session.ClientId, clientId, StringComparison.Ordinal))
            {
                page = null;
                error = new(BackendErrorCode.ResourceUnauthorized, "预览诊断不属于当前客户端。", "previewId");
                return false;
            }
            if (limit is <= 0 || limit > session.Session.Limits.MaxDiagnosticsPageSize)
            {
                page = null;
                error = new(BackendErrorCode.DiagnosticLimitExceeded, "诊断分页参数超过服务公布的上限。", "limit");
                return false;
            }
            if (!session.Pages.TryGetValue(cursor ?? string.Empty, out PreviewDiagnosticsPageDto? found))
            {
                page = null;
                error = new(BackendErrorCode.DiagnosticCursorExpired, "诊断游标已经失效，请从当前代次重新读取。", "cursor");
                return false;
            }
            BackendErrorResponse? validation = PreviewTransportValidation.ValidateDiagnosticsPage(session.Session, found, cursor);
            if (validation is not null)
            {
                page = null;
                error = validation;
                return false;
            }
            // Pages are fixed by the publisher; do not drop diagnostics or invent
            // continuation cursors when a smaller read limit cannot represent one.
            if (limit is { } requestedLimit && found.Diagnostics.Length > requestedLimit)
            {
                page = null;
                error = new(BackendErrorCode.DiagnosticLimitExceeded, "读取上限小于已发布诊断页，请提高limit。", "limit");
                return false;
            }
            page = found with { Diagnostics = [.. found.Diagnostics] };
            error = null;
            return true;
        }
    }

    private static bool SameInput(PreviewSessionResponse left, PreviewSessionResponse right)
        => string.Equals(left.PreviewGeneration, right.PreviewGeneration, StringComparison.Ordinal)
            && left.Source == right.Source && left.Size == right.Size && left.AlphaMode == right.AlphaMode
            && left.Limits == right.Limits && left.RenderBackend == right.RenderBackend;
}
