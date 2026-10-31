using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>受 clientId 授权的固定字节资源。</summary>
public sealed record StoredAsset(
    string AssetId,
    string OwnerClientId,
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    string StoragePath,
    DateTimeOffset? ExpiresAtUtc = null);

/// <summary>资源拒绝原因；不把文件系统异常直接暴露给 HTTP。</summary>
public sealed class ResourceStoreException : Exception
{
    public ResourceStoreException(BackendErrorCode code, string message, string? field = null)
        : base(message) => (Code, Field) = (code, field);

    public BackendErrorCode Code { get; }
    public string? Field { get; }
}

/// <summary>
/// 只接受上传字节的受控资源存储。绝对路径、路径穿越和客户端指定存储位置均被拒绝。
/// </summary>
public sealed class ResourceStore
{
    private static readonly string[] AllowedContentTypes =
    [
        "application/json", "application/octet-stream", "application/ttml+xml", "text/plain", "text/lrc", "text/lyrics",
        "image/avif", "image/bmp", "image/gif", "image/jpeg", "image/png", "image/webp",
        "video/mp4", "video/quicktime", "video/x-matroska", "video/webm", "video/x-msvideo", "video/mpeg",
        "audio/aac", "audio/flac", "audio/m4a", "audio/mp4", "audio/mpeg",
        "audio/ogg", "audio/wav", "audio/x-flac", "audio/x-wav",
        "video/mp4", "video/quicktime", "video/webm", "video/x-matroska",
    ];

    private readonly string _root;
    private readonly long? _maxAssetBytes;
    private readonly long? _maxQuotaBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredAsset> _assets = new(StringComparer.Ordinal);
    private long _totalBytes;
    private int _pendingUploads;
    private readonly int _maxAssets;
    private readonly TimeSpan _unreferencedLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, int> _references = new(StringComparer.Ordinal);

    /// <summary>建立受控资源目录、可配置配额和可注入TTL时钟。</summary>
    public ResourceStore(BackendHostOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _root = Path.GetFullPath(options.ResourceRoot);
        _maxAssetBytes = options.MaxAssetBytes;
        _maxQuotaBytes = options.MaxAssetQuotaBytes;
        _maxAssets = options.MaxAssets;
        _unreferencedLifetime = TimeSpan.FromSeconds(options.UnreferencedAssetLifetimeSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_root);
    }

    /// <summary>包含准备中上传的资源数量上限。</summary>
    public int MaxAssets => _maxAssets;
    public long? MaxAssetBytes => _maxAssetBytes;
    public long? MaxAssetQuotaBytes => _maxQuotaBytes;
    public static IReadOnlyCollection<string> AllowedMimeTypes => AllowedContentTypes;
    /// <summary>最后引用释放之后的资源保留秒数。</summary>
    public int UnreferencedAssetLifetimeSeconds => (int)_unreferencedLifetime.TotalSeconds;
    /// <summary>当前已发布和暂存磁盘资源的实际预留字节。</summary>
    public long UsedBytes { get { lock (_gate) return _totalBytes; } }

    /// <summary>把整个输入复制到受控目录后再发布一条不可变资源记录。</summary>
    public async Task<StoredAsset> UploadAsync(
        string ownerClientId,
        string fileName,
        string? contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        using PreparedUpload upload = await PrepareUploadAsync(ownerClientId, fileName, contentType, content,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return upload.Publish();
    }

    /// <summary>流式固定上传及SHA；新上传写前预留资源配额，已有回执重试只核完整hash。</summary>
    public async Task<PreparedUpload> PrepareUploadAsync(string ownerClientId, string fileName, string? contentType,
        Stream content, bool replayOnly = false, CancellationToken cancellationToken = default)
    {
        ValidateClientId(ownerClientId);
        ValidateFileName(fileName);
        ArgumentNullException.ThrowIfNull(content);

        string mime = NormalizeContentType(contentType, fileName);
        if (!IsAllowedContentType(mime))
            throw new ResourceStoreException(BackendErrorCode.UnsupportedMediaType, "资源 MIME 类型不受支持。", nameof(contentType));

        if (!replayOnly) lock (_gate)
        {
            CollectExpiredLocked();
            if (_assets.Count + _pendingUploads >= _maxAssets)
                throw new ResourceStoreException(BackendErrorCode.ResourceQuotaExceeded, "资源数量配额已用尽。", nameof(content));
            _pendingUploads++;
        }
        string assetId = "asset-" + Guid.NewGuid().ToString("N");
        string extension = Path.GetExtension(fileName);
        string clientDirectory = Path.Combine(_root, ownerClientId);

        string temporaryPath = Path.Combine(clientDirectory, "." + assetId + ".upload");
        string finalPath = Path.Combine(clientDirectory, assetId + extension);
        long length = 0;
        long reservedBytes = 0;
        try
        {
            if (!replayOnly) Directory.CreateDirectory(clientDirectory);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (FileStream? output = replayOnly ? null : new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    length = checked(length + read);
                    if (length > _maxAssetBytes)
                        throw new ResourceStoreException(BackendErrorCode.ResourceTooLarge, "资源超过单文件大小上限。", nameof(content));
                    // 暂存字节也占总配额；在写入前保留，不能让并发上传先把磁盘写满。
                    if (!replayOnly) lock (_gate)
                    {
                        CollectExpiredLocked();
                        if (_totalBytes > _maxQuotaBytes - read)
                            throw new ResourceStoreException(BackendErrorCode.ResourceQuotaExceeded, "资源配额已用尽。", nameof(content));
                        _totalBytes = checked(_totalBytes + read);
                        reservedBytes += read;
                    }
                    hash.AppendData(buffer, 0, read);
                    if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (length == 0)
                    throw new ResourceStoreException(BackendErrorCode.ResourceEmpty, "资源不能为空。", nameof(content));
            }
            // 关闭并完整刷新独占写入句柄后再重命名；Windows 不允许移动未共享删除的文件。
            cancellationToken.ThrowIfCancellationRequested();
            string sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return new(this, assetId, ownerClientId, fileName, mime, length, sha,
                replayOnly ? null : temporaryPath, finalPath);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            if (!replayOnly) lock (_gate)
            {
                _totalBytes -= reservedBytes;
                _pendingUploads--;
            }
            throw;
        }
    }

    /// <summary>未发布的固定文件由本对象独占；发布转移配额，其他结果必须释放。</summary>
    public sealed class PreparedUpload : IDisposable
    {
        private ResourceStore? _owner;
        private readonly string _assetId, _finalPath;
        private readonly string? _temporaryPath;
        internal PreparedUpload(ResourceStore owner, string assetId, string clientId, string fileName, string mime,
            long size, string sha256, string? temporaryPath, string finalPath)
        {
            _owner = owner; _assetId = assetId; OwnerClientId = clientId; FileName = fileName; ContentType = mime;
            Size = size; Sha256 = sha256; _temporaryPath = temporaryPath; _finalPath = finalPath;
        }
        public string OwnerClientId { get; }
        public string FileName { get; }
        public string ContentType { get; }
        public long Size { get; }
        public string Sha256 { get; }
        public bool ReplayOnly => _temporaryPath is null;
        internal bool BelongsTo(ResourceStore store) => ReferenceEquals(_owner, store);
        internal StoredAsset Publish()
        {
            ResourceStore owner = _owner ?? throw new ObjectDisposedException(nameof(PreparedUpload));
            lock (owner._gate)
            {
                if (!ReferenceEquals(_owner, owner) || _temporaryPath is null) throw new InvalidOperationException("上传候选未保留或已释放。");
                StoredAsset asset = new(_assetId, OwnerClientId, FileName, ContentType, Size, Sha256, _finalPath,
                    owner._timeProvider.GetUtcNow().Add(owner._unreferencedLifetime));
                bool moved = false;
                try
                {
                    File.Move(_temporaryPath, _finalPath);
                    moved = true;
                    owner._assets.Add(_assetId, asset);
                }
                catch
                {
                    if (moved) File.Delete(_finalPath);
                    throw;
                }
                owner._pendingUploads--; _owner = null;
                return asset;
            }
        }
        public void Dispose()
        {
            ResourceStore? owner = _owner;
            if (owner is null) return;
            lock (owner._gate)
            {
                if (!ReferenceEquals(_owner, owner)) return;
                _owner = null;
                if (_temporaryPath is null) return;
                try { File.Delete(_temporaryPath); }
                finally { owner._totalBytes -= Size; owner._pendingUploads--; }
            }
        }
    }

    public bool TryGetAuthorized(string ownerClientId, string assetId, out StoredAsset asset)
    {
        ValidateClientId(ownerClientId);
        if (!TryValidateAssetId(assetId)) { asset = null!; return false; }
        lock (_gate)
        {
            CollectExpiredLocked();
            if (_assets.TryGetValue(assetId, out asset!) && string.Equals(asset.OwnerClientId, ownerClientId, StringComparison.Ordinal)) return true;
        }
        asset = null!;
        return false;
    }

    public StoredAsset GetAuthorized(string ownerClientId, string assetId)
    {
        if (TryGetAuthorized(ownerClientId, assetId, out StoredAsset asset)) return asset;
        lock (_gate)
            if (_assets.ContainsKey(assetId)) throw new ResourceStoreException(BackendErrorCode.ResourceUnauthorized, "资源不属于当前客户端。", nameof(assetId));
        throw new ResourceStoreException(TryValidateAssetId(assetId) ? BackendErrorCode.ResourceExpired : BackendErrorCode.ResourceNotFound,
            "资源不存在或已到期，需重新上传。", nameof(assetId));
    }

    /// <summary>持有工程/任务引用。引用结束后重新开始未引用TTL。</summary>
    public IDisposable AcquireReference(string clientId, string assetId)
    {
        lock (_gate)
        {
            StoredAsset asset = GetAuthorized(clientId, assetId);
            _references[asset.AssetId] = _references.GetValueOrDefault(asset.AssetId) + 1;
            return new ReferenceLease(this, asset.AssetId);
        }
    }

    /// <summary>流与文件引用共同释放，响应结束之前TTL不会回收内容。</summary>
    public ResourceReadLease AcquireRead(string clientId, string assetId)
    {
        lock (_gate)
        {
            StoredAsset asset = GetAuthorized(clientId, assetId);
            IDisposable reference = AcquireReference(clientId, assetId);
            try
            {
                Stream stream = new FileStream(asset.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return new(asset, stream, reference);
            }
            catch { reference.Dispose(); throw; }
        }
    }

    /// <summary>已通过闭包校验的本机音频同样复制到有配额的资源目录。</summary>
    public async Task<StoredAsset> ImportProjectAudioAsync(string clientId, string path, CancellationToken cancellationToken = default)
    {
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await UploadAsync(clientId, Path.GetFileName(path), null, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>组合根启动并等待的临时资源回收循环；关闭时取消循环但不撤销读取引用。</summary>
    public async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(UnreferencedAssetLifetimeSeconds / 2, 1, 60));
        using PeriodicTimer timer = new(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) CollectExpired();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>回收已到期且没有工程、任务或读取引用的临时资源。</summary>
    public int CollectExpired()
    {
        lock (_gate) return CollectExpiredLocked();
    }

    /// <summary>仍被使用的资源阻止client回收；没有引用的上传继续由原TTL收集。</summary>
    public bool HasClientReferences(string clientId)
    {
        lock (_gate) return _assets.Values.Any(asset => asset.OwnerClientId == clientId
            && _references.GetValueOrDefault(asset.AssetId) > 0);
    }

    private int CollectExpiredLocked()
    {
        int count = 0;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (StoredAsset asset in _assets.Values.Where(asset => asset.ExpiresAtUtc <= now
            && _references.GetValueOrDefault(asset.AssetId) == 0).ToArray())
        {
            try { File.Delete(asset.StoragePath); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            _assets.Remove(asset.AssetId);
            _references.Remove(asset.AssetId);
            _totalBytes -= asset.Size;
            count++;
        }
        return count;
    }

    private void ReleaseReference(string assetId)
    {
        lock (_gate)
        {
            int count = _references.GetValueOrDefault(assetId);
            if (count <= 1)
            {
                _references.Remove(assetId);
                if (_assets.TryGetValue(assetId, out StoredAsset? asset))
                    _assets[assetId] = asset with { ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_unreferencedLifetime) };
            }
            else _references[assetId] = count - 1;
        }
    }

    private sealed class ReferenceLease(ResourceStore store, string assetId) : IDisposable
    {
        private ResourceStore? _store = store;
        public void Dispose() => Interlocked.Exchange(ref _store, null)?.ReleaseReference(assetId);
    }

    private static void ValidateClientId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value is "." or ".." || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new ResourceStoreException(BackendErrorCode.UnauthorizedClient, "客户端身份无效。", "clientId");
    }

    private static void ValidateFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 255 || Path.IsPathRooted(value) || value != Path.GetFileName(value)
            || value is "." or ".." || value.Contains('/') || value.Contains('\\') || value.Contains(':')
            || value.Any(char.IsControl))
            throw new ResourceStoreException(BackendErrorCode.ResourcePathNotAllowed, "资源文件名必须是单一文件名，不能包含路径。", nameof(value));
    }

    internal static string NormalizeContentType(string? value, string fileName)
    {
        string mime = value?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        if (mime.Length > 0) return mime;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".lrc" or ".txt" => "text/plain",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
    }

    private static bool IsAllowedContentType(string value) => AllowedContentTypes.Contains(value, StringComparer.Ordinal);

    private static bool TryValidateAssetId(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 80 && value.StartsWith("asset-", StringComparison.Ordinal)
        && value.Skip(6).All(character => char.IsAsciiLetterOrDigit(character));
}

/// <summary>已授权资源读取；调用方必须在整个二进制响应期间保留本租约。</summary>
public sealed class ResourceReadLease(StoredAsset asset, Stream stream, IDisposable reference) : IDisposable, IAsyncDisposable
{
    private IDisposable? _reference = reference;
    /// <summary>固定字节的授权摘要。</summary>
    public StoredAsset Asset { get; } = asset;
    /// <summary>响应完成之前保持存活的只读流。</summary>
    public Stream Stream { get; } = stream;
    /// <summary>幂等关闭读取流并释放TTL引用。</summary>
    public void Dispose()
    {
        try { Stream.Dispose(); }
        finally { Interlocked.Exchange(ref _reference, null)?.Dispose(); }
    }
    /// <summary>异步关闭读取流后释放TTL引用。</summary>
    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _reference, null)?.Dispose(); }
    }
}
