using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>共享有界任务文件IO；授权与保留身份仅由唯一jobs/ledger提供。</summary>
internal sealed class BackendJobSidecarStore
{
    private readonly string _root;
    private readonly string _prefix;
    private readonly string _errorPrefix;
    private readonly int _maxBytes;
    private readonly long _quota;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _temporaries = new(StringComparer.Ordinal);
    private readonly Action<string>? _beforeFlush;
    private readonly Action<string>? _beforeReadLeaseRelease;
    private long _reserved;

    internal BackendJobSidecarStore(string root, string prefix, string errorPrefix, int maxBytes, long quota, Action<string>? beforeFlush = null)
        : this(root, prefix, errorPrefix, maxBytes, quota, beforeFlush, null) { }

    internal BackendJobSidecarStore(string root, string prefix, string errorPrefix, int maxBytes, long quota,
        Action<string>? beforeFlush, Action<string>? beforeReadLeaseRelease)
    {
        _root = root; _prefix = prefix; _errorPrefix = errorPrefix; _maxBytes = maxBytes; _quota = quota; _beforeFlush = beforeFlush;
        _beforeReadLeaseRelease = beforeReadLeaseRelease;
        Directory.CreateDirectory(root);
        if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("任务结果目录不能为链接。");
        foreach (string path in Directory.EnumerateFiles(root, prefix + "*.json"))
        {
            FileInfo file = new(path);
            if (file.LinkTarget is not null || file.Length > maxBytes) throw Invalid();
            string id = Path.GetFileNameWithoutExtension(path); PathFor(id);
            _entries.Add(id, new(file.Length));
        }
        DiscoverTemporaries();
    }
    internal long UsedBytes { get { lock (_gate) return checked(_reserved + _entries.Values.Sum(value => value.Size) + _temporaries.Values.Sum()); } }
    internal Reservation? TryReserve()
    {
        lock (_gate)
        {
            if (UsedBytes > _quota - _maxBytes) return null;
            _reserved += _maxBytes; return new(this);
        }
    }
    internal Prepared Prepare(string jobId, byte[] bytes, Reservation? reservation = null)
    {
        if (bytes.Length is <= 0 || bytes.Length > _maxBytes) throw Failure("BudgetExceeded");
        string id = _prefix + ValidateJob(jobId); string target = PathFor(id);
        lock (_gate)
        {
            if (_entries.ContainsKey(id) || !_pending.Add(id)) throw Failure("AlreadyPublished");
            bool allocated = false;
            try
            {
                if (reservation is null)
                {
                    if (UsedBytes > _quota - bytes.LongLength) throw Failure("StorageQuotaExceeded");
                    _reserved += bytes.LongLength; allocated = true;
                }
                else if (reservation.Owner != this || reservation.Consumed) throw Invalid();
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(bytes); _beforeFlush?.Invoke(temporary); output.Flush(flushToDisk: true); }
                    return new(this, id, temporary, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), reservation);
                }
                catch (Exception primary)
                {
                    try { File.Delete(temporary); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                    {
                        primary.Data["JobSidecarCleanupFailure"] = cleanup;
                        RetainTemporary(temporary, bytes.LongLength, reservation, primary);
                        allocated = false;
                    }
                    throw;
                }
            }
            catch
            {
                _pending.Remove(id);
                if (allocated) _reserved -= bytes.LongLength;
                throw;
            }
        }
    }
    internal BackendJobReportReferenceDto Publish(string jobId, byte[] bytes)
    { using Prepared prepared = Prepare(jobId, bytes); return prepared.Commit(); }
    internal ReadLease AcquireRead(string jobId, BackendJobReportReferenceDto reference)
    {
        if (reference.ReportId != _prefix + ValidateJob(jobId)) throw Invalid();
        lock (_gate)
        {
            if (!_entries.TryGetValue(reference.ReportId, out Entry? entry) || entry.Expired) throw Invalid();
            string path = PathFor(reference.ReportId); FileInfo file = new(path);
            if (file.LinkTarget is not null || file.Length <= 0 || file.Length > _maxBytes
                || file.Length != reference.SizeBytes || entry.Size != reference.SizeBytes) throw Invalid();
            FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            entry.Readers++;
            return new(this, reference, stream);
        }
    }
    internal byte[] Read(string jobId, BackendJobReportReferenceDto reference)
    { using ReadLease lease = AcquireRead(jobId, reference); return lease.ReadVerifiedBytes(); }
    internal void Prune(IEnumerable<string> retainedIds)
    {
        HashSet<string> retained = new(retainedIds, StringComparer.Ordinal);
        lock (_gate)
        {
            foreach ((string id, Entry entry) in _entries.ToArray())
            {
                if (retained.Contains(id)) continue;
                entry.Expired = true;
                if (entry.Readers == 0) Delete(id);
            }
            DiscoverTemporaries();
            foreach (string path in _temporaries.Keys.ToArray())
            {
                if (new FileInfo(path).LinkTarget is not null) throw Invalid();
                File.Delete(path); _temporaries.Remove(path);
            }
        }
    }
    private void Delete(string id)
    {
        string path = PathFor(id);
        if (new FileInfo(path).LinkTarget is not null) throw Invalid();
        File.Delete(path); _entries.Remove(id);
    }
    private void DiscoverTemporaries()
    {
        foreach (string path in Directory.EnumerateFiles(_root, _prefix + "*.tmp"))
        {
            string name = Path.GetFileName(path); int identityLength = _prefix.Length + 32;
            if (name.Length != identityLength + 42 || !name.AsSpan(identityLength, 6).SequenceEqual(".json.")
                || !Guid.TryParseExact(name.AsSpan(identityLength + 6, 32), "N", out _)
                || !name.EndsWith(".tmp", StringComparison.Ordinal)) throw Invalid();
            string id = name[..identityLength]; PathFor(id);
            FileInfo file = new(path);
            if (file.LinkTarget is not null || file.Length > _maxBytes) throw Invalid();
            if (!_pending.Contains(id)) _temporaries[path] = file.Length;
        }
    }
    private void RetainTemporary(string path, long upperBound, Reservation? reservation, Exception? primary = null)
    {
        long size = upperBound;
        try
        {
            FileInfo file = new(path);
            if (file.LinkTarget is not null || file.Length > _maxBytes) throw Invalid();
            size = file.Length;
        }
        catch (Exception accounting) when (accounting is IOException or UnauthorizedAccessException)
        { if (primary is not null) primary.Data["JobSidecarAccountingFailure"] = accounting; }
        _temporaries[path] = size;
        _reserved -= reservation is null ? upperBound : _maxBytes;
        if (reservation is not null) reservation.Consumed = true;
    }
    private string PathFor(string id)
    {
        if (!id.StartsWith(_prefix, StringComparison.Ordinal) || !Guid.TryParseExact(id.AsSpan(_prefix.Length), "N", out _)) throw Invalid();
        return Path.Combine(_root, id + ".json");
    }
    private static string ValidateJob(string id)
    {
        if (!id.StartsWith("job-", StringComparison.Ordinal) || !Guid.TryParseExact(id.AsSpan(4), "N", out _)) throw new IOException("任务身份无效。");
        return id[4..];
    }
    private JobSidecarException Invalid() => Failure("StoreInvalid");
    private JobSidecarException Failure(string suffix) => new(_errorPrefix + suffix);
    private sealed class Entry(long size) { internal long Size = size; internal int Readers; internal bool Expired; }

    internal sealed class Reservation : IDisposable
    {
        private readonly BackendJobSidecarStore _originalOwner;
        internal Reservation(BackendJobSidecarStore owner) { Owner = owner; _originalOwner = owner; }
        internal BackendJobSidecarStore? Owner;
        internal bool Consumed;
        public void Dispose()
        {
            lock (_originalOwner._gate)
            {
                if (Owner is null) return;
                if (!Consumed) _originalOwner._reserved -= _originalOwner._maxBytes;
                Owner = null;
            }
        }
    }
    internal sealed class Prepared(BackendJobSidecarStore owner, string id, string temporary, long size, string hash, Reservation? reservation) : IDisposable
    {
        private bool _complete;
        internal BackendJobReportReferenceDto Commit()
        {
            lock (owner._gate)
            {
                if (_complete || reservation is not null && (reservation.Owner != owner || reservation.Consumed)) throw owner.Invalid();
                File.Move(temporary, owner.PathFor(id));
                owner._entries.Add(id, new(size));
                owner._reserved -= reservation is null ? size : owner._maxBytes;
                if (reservation is not null) reservation.Consumed = true;
                owner._pending.Remove(id); _complete = true;
                return new(id, size, hash);
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_complete) return;
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    owner.RetainTemporary(temporary, size, reservation, cleanup);
                    owner._pending.Remove(id); _complete = true; throw;
                }
                owner._pending.Remove(id);
                if (reservation is null) owner._reserved -= size;
                _complete = true;
            }
        }
    }
    internal sealed class ReadLease(BackendJobSidecarStore owner, BackendJobReportReferenceDto reference, FileStream stream) : IDisposable
    {
        private int _disposed;
        internal BackendJobReportReferenceDto Reference { get; } = reference;
        internal Stream Stream => stream;
        internal byte[] ReadVerifiedBytes()
        {
            byte[] bytes = new byte[checked((int)Reference.SizeBytes)];
            stream.Position = 0; stream.ReadExactly(bytes); stream.Position = 0;
            if (stream.Length != Reference.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != Reference.Sha256) throw owner.Invalid();
            return bytes;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Exception? primary = null;
            try { stream.Dispose(); owner._beforeReadLeaseRelease?.Invoke(Reference.ReportId); }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try
                {
                    lock (owner._gate)
                    {
                        Entry entry = owner._entries[Reference.ReportId]; entry.Readers--;
                        if (entry.Expired && entry.Readers == 0) owner.Delete(Reference.ReportId);
                    }
                }
                catch (Exception cleanup) when (primary is not null) { primary.Data["JobSidecarReadCleanupFailure"] = cleanup; }
            }
        }
    }
}
internal sealed class JobSidecarException(string code) : IOException("任务结果存储不可用或超过预算。")
{ internal string Code { get; } = code; }
