using System.Collections.Concurrent;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>
/// 将不可变工程包文件惰性投影为现有 ResourceStore asset，并把引用绑定到工程会话生命周期。
/// </summary>
public sealed class PackageAssetLeaseStore : IDisposable
{
    private sealed class Entry(StoredAsset asset, IDisposable lease)
    {
        internal StoredAsset Asset { get; } = asset;
        internal IDisposable Lease { get; } = lease;
    }

    private readonly ResourceStore _resources;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private int _disposed;

    public PackageAssetLeaseStore(ResourceStore resources) => _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    /// <summary>同一工程、包修订和逻辑路径只建立一个 asset，并持有一个工程引用。</summary>
    public async Task<StoredAsset> MaterializeAsync(string clientId, string sessionId, string resourceId,
        string packagePath, string packageRole, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRole);
        cancellationToken.ThrowIfCancellationRequested();
        string key = Key(clientId, sessionId, resourceId);
        if (_entries.TryGetValue(key, out Entry? existing)
            && _resources.TryGetAuthorized(clientId, existing.Asset.AssetId, out StoredAsset current))
            return current;

        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(key, out existing)
                && _resources.TryGetAuthorized(clientId, existing.Asset.AssetId, out current))
                return current;

            string fileName = Path.GetFileName(packagePath);
            string contentType = ContentType(packagePath, packageRole);
            StoredAsset asset = await _resources.UploadAsync(clientId, fileName, contentType,
                new MemoryStream(content.ToArray(), writable: false), cancellationToken).ConfigureAwait(false);
            IDisposable lease;
            try { lease = _resources.AcquireReference(clientId, asset.AssetId); }
            catch { throw; }
            Entry next = new(asset, lease);
            if (!_entries.TryAdd(key, next))
            {
                lease.Dispose();
                try { _resources.CollectExpired(); } catch { }
                if (_entries.TryGetValue(key, out Entry? winner)
                    && _resources.TryGetAuthorized(clientId, winner.Asset.AssetId, out current)) return current;
            }
            return asset;
        }
        finally
        {
            gate.Release();
            if (_gates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate))) gate.Dispose();
        }
    }

    /// <summary>关闭工程或客户端回收时释放该工程的引用；资源随后按 ResourceStore TTL 回收。</summary>
    public void ReleaseSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        foreach ((string key, Entry entry) in _entries.ToArray())
        {
            string[] parts = key.Split('\n', 3);
            if (parts.Length == 3 && parts[1] == sessionId
                && _entries.TryRemove(key, out Entry? removed)) removed.Lease.Dispose();
        }
    }

    public bool HasSessionLease(string sessionId) => _entries.Keys.Any(key => { string[] parts = key.Split('\n', 3); return parts.Length == 3 && parts[1] == sessionId; });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (Entry entry in _entries.Values) entry.Lease.Dispose();
        _entries.Clear();
        foreach (SemaphoreSlim gate in _gates.Values) gate.Dispose();
        _gates.Clear();
    }

    private static string Key(string clientId, string sessionId, string resourceId) => clientId + "\n" + sessionId + "\n" + resourceId;

    private static string ContentType(string path, string role)
    {
        if (path.Equals("manifest.json", StringComparison.Ordinal)) return "application/json";
        if (role == "image") return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg" : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/png";
        if (role is "csharp" or "graph" or "shader" or "shaderProgram") return "text/plain";
        return "application/octet-stream";
    }
}
