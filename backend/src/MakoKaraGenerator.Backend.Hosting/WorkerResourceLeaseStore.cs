using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>
/// Host-owned staging lease for a package resource. The absolute file path is
/// private Worker IPC data and is never copied into a public DTO or URL.
/// </summary>
public sealed class WorkerResourceLeaseStore : IDisposable
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private bool _disposed;

    public WorkerResourceLeaseStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<WorkerResourceLease> StageAsync(
        string packageId,
        string packageRevision,
        string packagePath,
        string role,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (role is not ("image" or "video" or "font"))
            throw new WorkerProtocolException("当前资源 role 未接入 Worker 闭包检查。", "WorkerResourceRoleUnsupported");
        if (content.Length is <= 0 or > PackageResourceWorkerContract.MaximumGrantBytes)
            throw new WorkerProtocolException("Worker资源超过固定授权预算。", "WorkerResourceBudgetExceeded");
        cancellationToken.ThrowIfCancellationRequested();

        string grantId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_root, grantId);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "resource.bin");
        string sha = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        try
        {
            await using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            WorkerResourceLease lease = new(this, grantId, new(
                grantId, packageId, packageRevision, packagePath, role, path, content.Length, sha));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _leases.Add(grantId, new(lease, directory));
            }
            return lease;
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    internal void Release(string grantId)
    {
        string? directory = null;
        lock (_gate)
        {
            if (_leases.Remove(grantId, out Lease? lease)) directory = lease.Directory;
        }
        if (directory is not null) TryDelete(directory);
    }

    public bool HasLease(string grantId)
    {
        lock (_gate) return _leases.ContainsKey(grantId);
    }

    public void Dispose()
    {
        string[] directories;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            directories = [.. _leases.Values.Select(value => value.Directory)];
            _leases.Clear();
        }
        foreach (string directory in directories) TryDelete(directory);
        TryDelete(_root);
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Lease(WorkerResourceLease Public, string Directory);
}

/// <summary>一项固定包资源的 Worker grant；释放后 staging 文件立即失效。</summary>
public sealed class WorkerResourceLease : IDisposable
{
    private readonly WorkerResourceLeaseStore _owner;
    private int _released;

    internal WorkerResourceLease(WorkerResourceLeaseStore owner, string grantId, PackageResourceWorkerGrant grant)
    { _owner = owner; GrantId = grantId; Grant = grant; }

    public string GrantId { get; }
    public PackageResourceWorkerGrant Grant { get; }
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(GrantId);
    }
}
