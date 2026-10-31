using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>有限客户端、访问令牌和事件窗口；超限不驱逐仍拥有工程的客户端。</summary>
public sealed record ClientLifecycleOptions
{
    /// <summary>同时保留的客户端数量上限。</summary>
    public int MaxClients { get; init; } = 16;
    /// <summary>每客户端可保留的工程会话数量上限。</summary>
    public int MaxSessionsPerClient { get; init; } = 16;
    /// <summary>短期访问凭证有效秒数。</summary>
    public int TokenLifetimeSeconds { get; init; } = 120;
    /// <summary>客户端无心跳后转为休眠的秒数，不删除工程。</summary>
    public int LeaseLifetimeSeconds { get; init; } = 300;
    /// <summary>每客户端事件重放条数上限。</summary>
    public int MaxEventsPerClient { get; init; } = 256;

    /// <summary>拒绝不受限制的数量或租约时间。</summary>
    public void Validate()
    {
        if (MaxClients is <= 0 or > 1024 || MaxSessionsPerClient is <= 0 or > 256
            || TokenLifetimeSeconds is <= 0 or > 3600 || LeaseLifetimeSeconds is <= 0 or > 86400
            || MaxEventsPerClient is <= 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(ClientLifecycleOptions), "客户端租约和数量必须处于有限允许范围。");
    }
}

/// <summary>进程内小型结果联合；HTTP适配只投影其中一个值。</summary>
public sealed record BackendOperationResult<T>(T? Response, BackendErrorResponse? Error)
{
    /// <summary>结果是否成功。</summary>
    public bool Accepted => Error is null;
    /// <summary>构造成功的固定响应。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "小型结果联合的构造入口与其响应类型保持一致。") ]
    public static BackendOperationResult<T> Success(T response) => new(response, null);
    /// <summary>构造中立拒绝。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "小型结果联合的构造入口与其响应类型保持一致。") ]
    public static BackendOperationResult<T> Rejected(BackendErrorResponse error) => new(default, error);
}

/// <summary>本机启动凭证登记的客户端。短期访问失效不会丢失客户端或工程。</summary>
public sealed class ClientRegistry
{
    private sealed class Client(string id, string resumeToken, string accessToken,
        DateTimeOffset tokenExpiresAt, DateTimeOffset leaseExpiresAt)
    {
        public string Id { get; } = id;
        public int Operations;
        public bool Claimed;
        public string ResumeToken { get; } = resumeToken;
        public string AccessToken { get; set; } = accessToken;
        public DateTimeOffset TokenExpiresAt { get; set; } = tokenExpiresAt;
        public DateTimeOffset LeaseExpiresAt { get; set; } = leaseExpiresAt;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly string _startupSecret;
    private readonly TimeProvider _timeProvider;
    /// <summary>本次服务启动的身份。</summary>
    public string HostInstanceId { get; }
    /// <summary>实际使用的有限生命周期策略。</summary>
    public ClientLifecycleOptions Options { get; }

    /// <summary>使用启动秘密和可注入时钟建立本机凭证注册表。</summary>
    public ClientRegistry(string hostInstanceId, string startupSecret, ClientLifecycleOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(startupSecret);
        if (startupSecret.Length < 32) throw new ArgumentException("启动凭证至少包含32个字符。", nameof(startupSecret));
        HostInstanceId = hostInstanceId;
        _startupSecret = startupSecret;
        Options = options ?? new();
        Options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>验证启动凭证；恢复请求轮换访问凭证并保留既有client。</summary>
    public BackendOperationResult<ClientRegistrationResponse> Register(string startupSecret, ClientRegistrationRequest? request)
    {
        if (!SecretEquals(startupSecret, _startupSecret))
            return RejectRegistration(BackendErrorCode.UnauthorizedClient, "启动凭证无效。");
        if (request is null || request.ProtocolVersion != BackendProtocol.Version)
            return RejectRegistration(BackendErrorCode.InvalidRequest, "客户端协议版本不受支持。");
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            Client client;
            if (request.ResumeClientId is not null || request.ResumeToken is not null)
            {
                if (request.ResumeClientId is null || !_clients.TryGetValue(request.ResumeClientId, out client!)
                    || client.Claimed || !SecretEquals(request.ResumeToken, client.ResumeToken))
                    return RejectRegistration(BackendErrorCode.UnauthorizedClient, "客户端恢复凭证无效。");
                // 恢复产生新访问凭证；旧页持有的短期凭证随即失效。
                client.AccessToken = NewSecret();
                client.TokenExpiresAt = now.AddSeconds(Options.TokenLifetimeSeconds);
                client.LeaseExpiresAt = now.AddSeconds(Options.LeaseLifetimeSeconds);
            }
            else
            {
                if (_clients.Count >= Options.MaxClients)
                    return RejectRegistration(BackendErrorCode.ClientQuotaExceeded, "客户端数量达到上限，既有客户端可使用恢复凭证重连。");
                client = new("client-" + Guid.NewGuid().ToString("N"), NewSecret(), NewSecret(),
                    now.AddSeconds(Options.TokenLifetimeSeconds), now.AddSeconds(Options.LeaseLifetimeSeconds));
                _clients.Add(client.Id, client);
            }
            return BackendOperationResult<ClientRegistrationResponse>.Success(new(client.Id, HostInstanceId,
                client.AccessToken, client.ResumeToken, client.TokenExpiresAt, client.LeaseExpiresAt, now));
        }
    }

    /// <summary>登记配额不足时，锁外检查并清理已原子claim的过期空闲身份。</summary>
    public async Task<BackendOperationResult<ClientRegistrationResponse>> RegisterAsync(string startupSecret,
        ClientRegistrationRequest? request, Func<string, Task<bool>> tryReclaim)
    {
        ArgumentNullException.ThrowIfNull(tryReclaim);
        BackendOperationResult<ClientRegistrationResponse> result = Register(startupSecret, request);
        if (result.Error?.Code != BackendErrorCode.ClientQuotaExceeded) return result;
        string[] candidates;
        lock (_gate) candidates = [.. _clients.Values.Where(client => !client.Claimed && client.Operations == 0
            && client.LeaseExpiresAt <= _timeProvider.GetUtcNow()).Select(client => client.Id)];
        foreach (string id in candidates)
        {
            Client candidate;
            lock (_gate)
            {
                if (!_clients.TryGetValue(id, out candidate!) || candidate.Claimed || candidate.Operations != 0
                    || candidate.LeaseExpiresAt > _timeProvider.GetUtcNow()) continue;
                candidate.Claimed = true;
            }
            bool reclaimed = false;
            try { reclaimed = await tryReclaim(id).ConfigureAwait(false); }
            catch (Exception) { /* Uncertain cleanup keeps the identity recoverable. */ }
            finally
            {
                lock (_gate)
                {
                    if (reclaimed) _clients.Remove(id);
                    else candidate.Claimed = false;
                }
            }
            if (reclaimed)
            {
                result = Register(startupSecret, request);
                if (result.Error?.Code != BackendErrorCode.ClientQuotaExceeded) return result;
            }
        }
        return result;
    }

    /// <summary>原子认证并pin整个HTTP操作；包括SSE、响应流与断开后的finally。</summary>
    public BackendErrorResponse? TryAcquireOperation(string? clientId, string? accessToken, out IDisposable? pin)
    {
        lock (_gate)
        {
            pin = null;
            BackendErrorResponse? error = Authenticate(clientId, accessToken);
            if (error is not null) return error;
            Client client = _clients[clientId!];
            client.Operations++;
            pin = new OperationPin(this, client);
            return null;
        }
    }

    /// <summary>可信Hosting操作也必须在副作用之前取得pin；不能绕过正在回收的身份。</summary>
    internal IDisposable? TryPinClient(string clientId)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId, out Client? client) || client.Claimed) return null;
            client.Operations++;
            return new OperationPin(this, client);
        }
    }

    /// <summary>只供事件发布判断；claim期间不接受新的事件状态。</summary>
    public bool IsClientActive(string clientId)
    {
        lock (_gate) return _clients.TryGetValue(clientId, out Client? client) && !client.Claimed;
    }

    private sealed class OperationPin(ClientRegistry owner, Client client) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) client.Operations--;
        }
    }

    /// <summary>仅持有clientId不会取得访问权；错误不暴露恢复凭证。</summary>
    public BackendErrorResponse? Authenticate(string? clientId, string? accessToken)
    {
        lock (_gate)
        {
            if (clientId is null || !_clients.TryGetValue(clientId, out Client? client)
                || client.Claimed || !SecretEquals(accessToken, client.AccessToken))
                return new(BackendErrorCode.UnauthorizedClient, "客户端访问凭证无效。");
            return client.TokenExpiresAt <= _timeProvider.GetUtcNow()
                ? new(BackendErrorCode.ClientTokenExpired, "客户端访问令牌已过期，请用恢复凭证重新登记。") : null;
        }
    }

    /// <summary>只能在适配层完成Authenticate之后调用，不接受客户端自报身份作为认证。</summary>
    public ClientHeartbeatResponse Heartbeat(string clientId)
    {
        lock (_gate)
        {
            Client client = _clients[clientId];
            if (client.Claimed) throw new InvalidOperationException("客户端正在回收。");
            DateTimeOffset now = _timeProvider.GetUtcNow();
            client.TokenExpiresAt = now.AddSeconds(Options.TokenLifetimeSeconds);
            client.LeaseExpiresAt = now.AddSeconds(Options.LeaseLifetimeSeconds);
            return new(client.Id, HostInstanceId, client.TokenExpiresAt, client.LeaseExpiresAt, now);
        }
    }

    /// <summary>读取租约期限与休眠状态，不自动丢弃任何工程。</summary>
    public bool TryReadLease(string clientId, out DateTimeOffset leaseExpiresAtUtc, out bool dormant)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId, out Client? client))
            { leaseExpiresAtUtc = default; dormant = false; return false; }
            leaseExpiresAtUtc = client.LeaseExpiresAt;
            dormant = leaseExpiresAtUtc <= _timeProvider.GetUtcNow();
            return true;
        }
    }

    internal (string State, bool CanClaim) ReadAuthorOwnerState(string owner, string requester)
    {
        lock (_gate)
        {
            if (owner == requester) return ("ownedByCaller", true);
            if (!_clients.TryGetValue(owner, out Client? client)) return ("orphaned", true);
            if (client.LeaseExpiresAt > _timeProvider.GetUtcNow()) return ("active", false);
            return ("dormant", !client.Claimed && client.Operations == 0);
        }
    }

    // This reservation fences resume, new operations and client reclamation until publication.
    internal bool TryReserveAuthorOwner(string owner, out IDisposable? reservation)
    {
        lock (_gate)
        {
            reservation = null;
            if (!_clients.TryGetValue(owner, out Client? client)) return true;
            if (client.Claimed || client.Operations != 0 || client.LeaseExpiresAt > _timeProvider.GetUtcNow()) return false;
            client.Claimed = true;
            reservation = new AuthorOwnerReservation(this, client);
            return true;
        }
    }

    private sealed class AuthorOwnerReservation(ClientRegistry owner, Client client) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) client.Claimed = false;
        }
    }

    private static BackendOperationResult<ClientRegistrationResponse> RejectRegistration(BackendErrorCode code, string message) =>
        BackendOperationResult<ClientRegistrationResponse>.Rejected(new(code, message));
    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static bool SecretEquals(string? left, string right) => left is not null && left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
}
