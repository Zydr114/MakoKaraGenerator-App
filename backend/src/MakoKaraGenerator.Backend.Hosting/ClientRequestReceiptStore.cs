using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>同client跨方法、路径的有界回执。预留发生在任何修订判断和副作用之前。</summary>
public sealed class ClientRequestReceiptStore
{
    private sealed record Receipt(string Fingerprint, DateTimeOffset ValidUntilUtc,
        TaskCompletionSource<byte[]> Completion);
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Receipt>> _receipts = new(StringComparer.Ordinal);
    private readonly string _hostInstanceId;
    private readonly ClientRegistry? _clients;
    private bool _stopping;
    private readonly TimeProvider _timeProvider;
    private readonly ProjectCommandRetentionOptions _options;

    /// <summary>构造共享client回执窗口与固定元信息大小上限。</summary>
    public ClientRequestReceiptStore(string hostInstanceId, ProjectCommandRetentionOptions? options = null,
        TimeProvider? timeProvider = null, ClientRegistry? clients = null)
    {
        _hostInstanceId = hostInstanceId;
        _clients = clients;
        _options = options ?? new();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>请求允许的最大重试窗口秒数。</summary>
    public int MaxRetryWindowSeconds => _options.MaxRetryWindowSeconds;
    /// <summary>每客户端有效和在途回执的数量上限。</summary>
    public int MaxReceiptsPerClient => _options.MaxReceiptsPerSession;
    /// <summary>单个序列化回执的字节上限。</summary>
    public int MaxReceiptBytes => _options.MaxReceiptBytes;
    /// <summary>由数量和单项字节上限导出的客户端累计上限。</summary>
    public long MaxReceiptBytesPerClient => (long)MaxReceiptBytes * MaxReceiptsPerClient;

    /// <summary>拒绝新的占位，已有请求仍可重试或排空。</summary>
    public void StopAccepting() { lock (_gate) _stopping = true; }
    /// <summary>等待所有已接受的执行槽终结，不取消其领域操作。</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (_gate) pending = [.. _receipts.Values.SelectMany(receipts => receipts.Values)
            .Select(receipt => receipt.Completion.Task)];
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>规范UTC元信息后先占用client/requestId回执，再执行具名方法和路径；接受后取消只终止等待。</summary>
    public async Task<BackendOperationResult<T>> ExecuteAsync<T>(string clientId, RequestMetadataDto? metadata,
        string operationKey, object? semanticPayload, Func<Task<BackendOperationResult<T>>> execute,
        CancellationToken cancellationToken = default)
    {
        using IDisposable? pin = _clients?.TryPinClient(clientId);
        if (_clients is not null && pin is null)
            return Reject<T>(BackendErrorCode.UnauthorizedClient, "客户端已不存在或正在回收。");
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (metadata is null || !string.Equals(metadata.HostInstanceId, _hostInstanceId, StringComparison.Ordinal))
            return Reject<T>(BackendErrorCode.HostRestarted, "请求hostInstanceId已失效。", nameof(RequestMetadataDto.HostInstanceId));
        if (metadata.ValidUntilUtc is not { } validUntilUtc || validUntilUtc <= now)
            return Reject<T>(BackendErrorCode.RequestExpired, "请求validUntilUtc缺失或已过期。", nameof(RequestMetadataDto.ValidUntilUtc));
        if (validUntilUtc - now > TimeSpan.FromSeconds(_options.MaxRetryWindowSeconds))
            return Reject<T>(BackendErrorCode.InvalidRequest, "请求validUntilUtc超出允许窗口。", nameof(RequestMetadataDto.ValidUntilUtc));
        if (string.IsNullOrWhiteSpace(metadata.RequestId) || metadata.RequestId.Length > 128)
            return Reject<T>(BackendErrorCode.InvalidRequest, "requestId必须为不超过128字符的非空值。", nameof(RequestMetadataDto.RequestId));
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { operationKey, semanticPayload, HostInstanceId = metadata.HostInstanceId, ValidUntilUtc = validUntilUtc.ToUniversalTime() })));
        Receipt receipt;
        bool owner = false;
        lock (_gate)
        {
            if (!_receipts.TryGetValue(clientId, out Dictionary<string, Receipt>? clientReceipts))
                _receipts.Add(clientId, clientReceipts = new(StringComparer.Ordinal));
            foreach (string id in clientReceipts.Where(pair => pair.Value.ValidUntilUtc <= now && pair.Value.Completion.Task.IsCompleted)
                .Select(pair => pair.Key).ToArray()) clientReceipts.Remove(id);
            if (clientReceipts.TryGetValue(metadata.RequestId, out receipt!))
            {
                if (receipt.Fingerprint != fingerprint)
                    return Reject<T>(BackendErrorCode.RequestIdReuse, "requestId已绑定另一个方法、路径或语义载荷。", nameof(metadata.RequestId));
            }
            else
            {
                if (_stopping)
                    return Reject<T>(BackendErrorCode.SessionClosing, "服务正在停止接收新的修改请求。");
                if (clientReceipts.Count >= MaxReceiptsPerClient)
                    return Reject<T>(BackendErrorCode.RequestQuotaExceeded, "客户端有效请求回执已达到上限。", nameof(metadata.RequestId));
                receipt = new(fingerprint, validUntilUtc, new(TaskCreationOptions.RunContinuationsAsynchronously));
                clientReceipts.Add(metadata.RequestId, receipt);
                owner = true;
            }
        }
        if (owner)
        {
            BackendOperationResult<T> result;
            try { result = await execute().ConfigureAwait(false); }
            catch (Exception)
            {
                // 不能在结果不明时移除占用，让同一请求重新产生副作用。
                result = Reject<T>(BackendErrorCode.InternalError, "请求结果未能完成，请读取当前状态核对。");
            }
            byte[] bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(result);
                if (bytes.Length > MaxReceiptBytes)
                    bytes = JsonSerializer.SerializeToUtf8Bytes(Reject<T>(BackendErrorCode.InternalError,
                        "请求结果超过回执上限，请读取当前状态核对。"));
            }
            catch (Exception)
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(Reject<T>(BackendErrorCode.InternalError,
                    "请求结果无法归档，请读取当前状态核对。"));
            }
            receipt.Completion.TrySetResult(bytes);
        }
        // 请求取消只结束该等待，不撤销已接受的领域操作。
        byte[] completedBytes = await receipt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BackendOperationResult<T>>(completedBytes)!;
    }

    /// <summary>预备上传用于保留有效回执重试；该查询不替代后续完整语义匹配。</summary>
    public bool HasUnexpiredReceipt(string clientId, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        lock (_gate) return _receipts.TryGetValue(clientId, out Dictionary<string, Receipt>? receipts)
            && receipts.TryGetValue(requestId, out Receipt? receipt) && receipt.ValidUntilUtc > _timeProvider.GetUtcNow();
    }

    /// <summary>仅删除已完成且到期的回执，不驱逐有效或执行中的请求。</summary>
    public void Prune(string clientId)
    {
        lock (_gate)
        {
            if (!_receipts.TryGetValue(clientId, out Dictionary<string, Receipt>? receipts)) return;
            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (string id in receipts.Where(pair => pair.Value.ValidUntilUtc <= now && pair.Value.Completion.Task.IsCompleted)
                .Select(pair => pair.Key).ToArray()) receipts.Remove(id);
        }
    }

    /// <summary>有效重试回执和未完成领域操作都阻止身份回收。</summary>
    public bool HasProtectedClient(string clientId)
    {
        lock (_gate) return _receipts.TryGetValue(clientId, out Dictionary<string, Receipt>? receipts)
            && receipts.Values.Any(receipt => !receipt.Completion.Task.IsCompleted || receipt.ValidUntilUtc > _timeProvider.GetUtcNow());
    }

    /// <summary>只能在claim且确认无有效回执之后移除client内存索引。</summary>
    internal void RemoveClient(string clientId) { lock (_gate) _receipts.Remove(clientId); }

    private static BackendOperationResult<T> Reject<T>(BackendErrorCode code, string message, string? field = null) =>
        BackendOperationResult<T>.Rejected(new(code, message, field));
}
