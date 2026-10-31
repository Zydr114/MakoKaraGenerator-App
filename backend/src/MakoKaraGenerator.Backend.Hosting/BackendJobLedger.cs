using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>有界任务诊断账本；它不重放工程写入，也不恢复旧进程的会话。</summary>
internal sealed class BackendJobLedger
{
    internal sealed record Entry(string HostInstanceId, string OwnerClientId, JobResponse Job,
        string? OutputRootGrantId, string? OutputRelativePath, string? ProjectFileSha256);
    private readonly string _root;
    private readonly string _host;
    private readonly TimeProvider _time;
    private readonly ProjectPersistenceOptions _options;
    private readonly object _gate = new();
    private const int MaxEntryBytes = 32 * 1024;

    internal BackendJobLedger(string host, ProjectPersistenceOptions options, TimeProvider time)
    {
        _root = Path.Combine(options.StateRoot, "jobs"); _host = host; _time = time; _options = options;
        Directory.CreateDirectory(_root);
        lock (_gate)
        {
            foreach (Entry previous in ReadEntries())
            {
                if (previous.HostInstanceId == host || IsTerminal(previous.Job.State)) continue;
                WriteCore(previous with { Job = previous.Job with { State = "unknown", CanCancel = false,
                    ExpiresAtUtc = _time.GetUtcNow() + options.JobRetention,
                    Error = new(BackendErrorCode.InternalError, "前一宿主未记录终态，请核对工程入口，不要自动重放写入。") } }, replace: true);
            }
            PruneCore();
        }
    }

    internal void Write(JobResponse job, string owner, string? outputRoot, string? outputRelative, string? outputDirectory)
    {
        string? hash = null;
        if (job.State == "succeeded" && outputDirectory is not null)
        {
            string file = Path.Combine(outputDirectory, Core.Persistence.ProjectJson.ProjectFileName);
            if (File.Exists(file)) using (FileStream input = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
        lock (_gate)
        {
            PruneCore();
            WriteCore(new(_host, owner, job, outputRoot, outputRelative, hash), replace: File.Exists(PathFor(job.JobId)));
        }
    }

    internal RecoveredJobResponse[] ReadRecovered()
        => ReadRecovered(null);

    internal RecoveredJobResponse[] ReadRecovered(string? ownerClientId)
    {
        lock (_gate)
        {
            PruneCore();
            return [.. ReadEntries().Where(entry => entry.HostInstanceId != _host
                && (ownerClientId is null || string.Equals(entry.OwnerClientId, ownerClientId, StringComparison.Ordinal))).Select(entry =>
                new RecoveredJobResponse(entry.Job, entry.HostInstanceId, entry.OutputRootGrantId,
                    entry.OutputRelativePath, entry.ProjectFileSha256))];
        }
    }

    private void WriteCore(Entry entry, bool replace)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (bytes.Length > MaxEntryBytes) throw new IOException("任务账本条目超过大小上限。");
        FileInfo[] existing = [.. Directory.EnumerateFiles(_root, "job-*.json").Select(path => new FileInfo(path))];
        if (!replace && existing.Length >= _options.MaxRetainedJobs) throw new ResourceStoreException(BackendErrorCode.ResourceQuotaExceeded, "持久任务账本已达到条目上限。");
        long total = existing.Sum(info => info.Length);
        string target = PathFor(entry.Job.JobId);
        long old = File.Exists(target) ? new FileInfo(target).Length : 0;
        if (total - old + bytes.Length > _options.MaxJobLedgerBytes)
            throw new ResourceStoreException(BackendErrorCode.ResourceQuotaExceeded, "持久任务账本已达到字节上限。");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Entry[] ReadEntries()
    {
        List<Entry> entries = [];
        foreach (string file in Directory.EnumerateFiles(_root, "job-*.json").Take(_options.MaxRetainedJobs + 1))
        {
            FileInfo info = new(file);
            if (info.LinkTarget is not null || info.Length > MaxEntryBytes) throw new IOException("任务账本条目无效或超限。");
            Entry entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(file)) ?? throw new IOException("任务账本条目无效。");
            if (PathFor(entry.Job.JobId) != file) throw new IOException("任务账本身份不一致。");
            entries.Add(entry);
        }
        if (entries.Count > _options.MaxRetainedJobs) throw new IOException("任务账本条目总数超过上限。");
        return [.. entries];
    }
    private void PruneCore()
    {
        foreach (Entry entry in ReadEntries())
            if (IsTerminal(entry.Job.State) && entry.Job.ExpiresAtUtc <= _time.GetUtcNow()) File.Delete(PathFor(entry.Job.JobId));
    }
    private string PathFor(string id)
    {
        if (!id.StartsWith("job-", StringComparison.Ordinal) || id.Length != 36 || !Guid.TryParseExact(id.AsSpan(4), "N", out _)) throw new IOException("任务标识无效。");
        return Path.Combine(_root, id + ".json");
    }
    internal static bool IsTerminal(string state) => state is "succeeded" or "failed" or "cancelled" or "unknown";
}
