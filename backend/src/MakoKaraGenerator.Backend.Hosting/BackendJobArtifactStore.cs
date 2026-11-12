using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>共享Job账本引用的不可变媒体/报告包；无独立任务状态或恢复账本。</summary>
internal sealed class BackendJobArtifactStore
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _staging;
    private readonly BackendExportOptions _options;
    private readonly Dictionary<string, Bundle> _bundles = new(StringComparer.Ordinal);
    private long _retainedBytes;
    private long _reservedBytes;
    private readonly List<Reservation> _abandoned = [];

    internal BackendJobArtifactStore(string stateRoot, BackendExportOptions options)
    {
        _options = options; _root = Path.Combine(stateRoot, "export-artifacts"); _staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(_staging); RejectLink(_root); RejectLink(_staging);
        foreach (string directory in Directory.EnumerateDirectories(_staging))
        {
            try { DeleteOwnedDirectory(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Reservation abandoned = new(this, Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length), null) { Stage = directory, Released = true };
                _abandoned.Add(abandoned); _reservedBytes += abandoned.Bytes;
            }
        }
        foreach (string directory in Directory.EnumerateDirectories(_root, "artifact-*"))
        {
            string id = Path.GetFileName(directory); ValidateArtifactId(id); RejectLink(directory);
            FileInfo[] files = [.. Directory.EnumerateFiles(directory).Select(value => new FileInfo(value))];
            if (Directory.EnumerateDirectories(directory).Any() || files.Any(value => value.LinkTarget is not null))
                throw new IOException("产物包含非法目录或链接。");
            long size = files.Sum(value => value.Length);
            _bundles.Add(id, new(directory, size)); _retainedBytes = checked(_retainedBytes + size);
        }
    }

    internal long UsedBytes { get { lock (_gate) return _retainedBytes + _reservedBytes; } }
    internal Reservation? TryReserve()
    {
        lock (_gate)
        {
            long? available = _options.MaxArtifactStorageBytes is { } storage ? storage - _retainedBytes - _reservedBytes : null;
            long? media = _options.MaxMediaBytes;
            if (available is { } remaining)
            {
                if (remaining <= _options.MaxReportBytes) return null;
                media = media is { } perArtifact ? Math.Min(perArtifact, remaining - _options.MaxReportBytes) : remaining - _options.MaxReportBytes;
            }
            long size = media is { } maximum ? checked(maximum + _options.MaxReportBytes) : 0;
            _reservedBytes = checked(_reservedBytes + size); return new(this, size, media);
        }
    }

    internal string CreateCandidate(Reservation reservation, string jobId, string format)
    {
        lock (_gate)
        {
            CheckReservation(reservation);
            if (reservation.Stage is not null) throw new InvalidOperationException("候选目录只能创建一次。");
            string stage = Path.Combine(_staging, ValidateJobId(jobId));
            if (Directory.Exists(stage)) throw new IOException("任务候选目录已存在。");
            Directory.CreateDirectory(stage); RejectLink(stage); reservation.Stage = stage;
            return Path.Combine(stage, MediaFile(format));
        }
    }

    internal async Task PrepareAsync(Reservation reservation, string jobId, ExportReportDto report, byte[] reportBytes,
        CancellationToken cancellationToken)
    {
        string stage;
        lock (_gate) { CheckReservation(reservation); stage = reservation.Stage ?? throw new IOException("任务候选目录不存在。"); }
        RejectLink(stage);
        string mediaPath = Path.Combine(stage, MediaFile(report.Format)); FileInfo media = new(mediaPath);
        if (media.LinkTarget is not null || media.Length <= 0 || media.Length > reservation.MaximumMediaBytes || media.Length != report.MediaSize
            || reportBytes.Length is <= 0 || reportBytes.Length > _options.MaxReportBytes)
            throw new ExportTaskException("ExportArtifactInvalid", "候选产物长度或类型无效。");
        string[] files = Directory.GetFiles(stage);
        if (files.Length != 1 || files[0] != mediaPath || Directory.EnumerateDirectories(stage).Any())
            throw new ExportTaskException("ExportArtifactInvalid", "候选包含额外文件。");
        await using (FileStream input = new(mediaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)) != report.Sha256)
                throw new ExportTaskException("ExportArtifactInvalid", "候选媒体哈希不一致。");
        cancellationToken.ThrowIfCancellationRequested();
        string reportPath = Path.Combine(stage, "report.json");
        using (FileStream output = new(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(reportBytes); output.Flush(flushToDisk: true); }
        string id = "artifact-" + ValidateJobId(jobId);
        string mime = report.Format == "opaqueMp4" ? "video/mp4" : "video/quicktime";
        reservation.PreparedResult = new(new(id, mime, report.MediaSize, report.Sha256),
            new("report-" + ValidateJobId(jobId), reportBytes.LongLength, WorkerProtocol.ComputeSha256(reportBytes)),
            report.FallbackDiagnostics.Length);
    }

    internal ExportJobResultDto Publish(Reservation reservation)
    {
        lock (_gate)
        {
            CheckReservation(reservation);
            string stage = reservation.Stage ?? throw new IOException("任务候选目录不存在。");
            ExportJobResultDto result = reservation.PreparedResult ?? throw new IOException("任务完整报告尚未准备。");
            string target = Path.Combine(_root, result.Artifact.ArtifactId);
            // Both files become one immutable bundle in a single same-filesystem rename.
            Directory.Move(stage, target);
            long actual = checked(result.Artifact.SizeBytes + result.Report.SizeBytes);
            _bundles.Add(result.Artifact.ArtifactId, new(target, actual)); _retainedBytes += actual;
            _reservedBytes -= reservation.Bytes; reservation.Committed = true;
            return result;
        }
    }

    internal void PrepareFailure(Reservation reservation, string jobId, byte[] reportBytes)
    {
        lock (_gate)
        {
            CheckReservation(reservation);
            string stage = reservation.Stage ?? throw new IOException("任务候选目录不存在。");
            RejectLink(stage);
            if (Directory.EnumerateDirectories(stage).Any() || Directory.EnumerateFiles(stage).Any(value => new FileInfo(value).LinkTarget is not null))
                throw new IOException("失败候选包含非法目录或链接。");
            // The executor has already drained every writer. A failure bundle contains no media.
            foreach (string file in Directory.EnumerateFiles(stage)) File.Delete(file);
            if (reportBytes.Length is <= 0 || reportBytes.Length > _options.MaxReportBytes) throw new IOException("完整失败报告超过预算。");
            using (FileStream output = new(Path.Combine(stage, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(reportBytes); output.Flush(flushToDisk: true); }
            reservation.PreparedFailure = new("report-" + ValidateJobId(jobId), reportBytes.LongLength, WorkerProtocol.ComputeSha256(reportBytes));
        }
    }
    internal BackendJobReportReferenceDto PublishFailure(Reservation reservation, string jobId)
    {
        lock (_gate)
        {
            CheckReservation(reservation);
            BackendJobReportReferenceDto report = reservation.PreparedFailure ?? throw new IOException("失败报告尚未完整准备。");
            string id = "artifact-" + ValidateJobId(jobId); string target = Path.Combine(_root, id);
            Directory.Move(reservation.Stage!, target);
            _bundles.Add(id, new(target, report.SizeBytes)); _retainedBytes += report.SizeBytes;
            _reservedBytes -= reservation.Bytes; reservation.Committed = true;
            return report;
        }
    }
    internal byte[] ReadFailureReport(string jobId, BackendJobReportReferenceDto reference)
    {
        lock (_gate)
        {
            string id = "artifact-" + ValidateJobId(jobId);
            if (reference.ReportId != "report-" + ValidateJobId(jobId) || !_bundles.TryGetValue(id, out Bundle? bundle) || bundle.Expired)
                throw new IOException("失败报告身份不一致或已到期。");
            RejectLink(bundle.Path);
            FileInfo file = new(Path.Combine(bundle.Path, "report.json"));
            if (file.LinkTarget is not null || file.Length <= 0 || file.Length > _options.MaxReportBytes || file.Length != reference.SizeBytes
                || Directory.GetFiles(bundle.Path).Length != 1 || Directory.EnumerateDirectories(bundle.Path).Any())
                throw new IOException("失败包必须只包含登记报告。");
            byte[] bytes = File.ReadAllBytes(file.FullName);
            if (WorkerProtocol.ComputeSha256(bytes) != reference.Sha256) throw new IOException("失败报告哈希不一致。");
            return bytes;
        }
    }

    internal byte[] ReadReport(string jobId, ExportJobResultDto result)
    {
        lock (_gate)
        {
            Bundle bundle = FindBundle(jobId, result);
            string path = Path.Combine(bundle.Path, "report.json"); FileInfo file = new(path);
            if (file.LinkTarget is not null || file.Length <= 0 || file.Length > _options.MaxReportBytes || file.Length != result.Report.SizeBytes)
                throw new IOException("已登记报告长度或类型无效。");
            byte[] bytes = File.ReadAllBytes(path);
            if (WorkerProtocol.ComputeSha256(bytes) != result.Report.Sha256) throw new IOException("已登记报告哈希不一致。");
            return bytes;
        }
    }

    internal BackendExportReadLease AcquireRead(string jobId, ExportJobResultDto result)
    {
        lock (_gate)
        {
            Bundle bundle = FindBundle(jobId, result);
            string media = result.Artifact.ContentType switch { "video/mp4" => "media.mp4", "video/quicktime" => "media.mov", _ => throw new IOException("产物媒体类型无效。") };
            string path = Path.Combine(bundle.Path, media); FileInfo file = new(path);
            if (file.LinkTarget is not null || file.Length <= 0 || file.Length > _options.MaxMediaBytes || file.Length != result.Artifact.SizeBytes)
                throw new IOException("产物长度或类型不一致。");
            FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            bundle.Readers++;
            return new(input, result.Artifact, () => ReleaseReader(result.Artifact.ArtifactId));
        }
    }

    internal void Prune(IEnumerable<string> retainedArtifactIds)
    {
        lock (_gate)
        {
            foreach (Reservation abandoned in _abandoned.ToArray())
            {
                try { DeleteOwnedDirectory(abandoned.Stage!); _reservedBytes -= abandoned.Bytes; _abandoned.Remove(abandoned); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Unremoved bytes stay reserved. */ }
            }
            HashSet<string> retained = new(retainedArtifactIds, StringComparer.Ordinal);
            foreach ((string id, Bundle bundle) in _bundles.ToArray())
            {
                bundle.Expired = !retained.Contains(id);
                if (bundle.Expired && bundle.Readers == 0) DeleteBundle(id, bundle);
            }
        }
    }
    private void ReleaseReader(string id)
    {
        lock (_gate)
        {
            Bundle bundle = _bundles[id]; bundle.Readers--;
            if (bundle.Expired && bundle.Readers == 0) DeleteBundle(id, bundle);
        }
    }
    private void ReleaseReservation(Reservation reservation)
    {
        lock (_gate)
        {
            if (reservation.Released) return;
            reservation.Released = true;
            if (!reservation.Committed)
            {
                try { if (reservation.Stage is not null) DeleteOwnedDirectory(reservation.Stage); _reservedBytes -= reservation.Bytes; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { _abandoned.Add(reservation); } // Never release disk accounting while owned files remain.

            }
        }
    }
    private Bundle FindBundle(string jobId, ExportJobResultDto result)
    {
        if (result.Artifact.ArtifactId != "artifact-" + ValidateJobId(jobId) || result.Report.ReportId != "report-" + ValidateJobId(jobId)
            || !_bundles.TryGetValue(result.Artifact.ArtifactId, out Bundle? bundle) || bundle.Expired)
            throw new IOException("任务产物身份无效或已到期。");
        RejectLink(bundle.Path); return bundle;
    }
    private void DeleteBundle(string id, Bundle bundle)
    {
        DeleteOwnedDirectory(bundle.Path); _bundles.Remove(id); _retainedBytes -= bundle.Bytes;
    }
    private static void CheckReservation(Reservation reservation)
    {
        if (reservation.Released || reservation.Committed) throw new InvalidOperationException("产物预留已经释放或提交。");
    }
    private static string MediaFile(string format) => format switch
    { "opaqueMp4" => "media.mp4", "transparentProRes4444" => "media.mov", _ => throw new ArgumentException("导出格式不受支持。", nameof(format)) };
    private static string ValidateJobId(string id)
    {
        if (!id.StartsWith("job-", StringComparison.Ordinal) || !Guid.TryParseExact(id.AsSpan(4), "N", out _)) throw new IOException("任务身份无效。");
        return id[4..];
    }
    private static void ValidateArtifactId(string id)
    {
        if (!id.StartsWith("artifact-", StringComparison.Ordinal) || !Guid.TryParseExact(id.AsSpan(9), "N", out _)) throw new IOException("产物身份无效。");
    }
    private static void RejectLink(string path)
    { if (new DirectoryInfo(path).LinkTarget is not null) throw new IOException("产物目录不能为链接。"); }
    private static void DeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return; RejectLink(path);
        if (Directory.EnumerateDirectories(path).Any() || Directory.EnumerateFiles(path).Any(value => new FileInfo(value).LinkTarget is not null))
            throw new IOException("候选清理拒绝额外目录或链接。");
        Directory.Delete(path, recursive: true);
    }
    private sealed class Bundle(string path, long bytes)
    { internal string Path { get; } = path; internal long Bytes { get; } = bytes; internal int Readers; internal bool Expired; }
    internal sealed class Reservation(BackendJobArtifactStore owner, long bytes, long? maximumMediaBytes) : IDisposable
    {
        internal long Bytes { get; } = bytes;
        internal long? MaximumMediaBytes { get; } = maximumMediaBytes; internal string? Stage; internal bool Committed; internal bool Released;
        internal ExportJobResultDto? PreparedResult;
        internal BackendJobReportReferenceDto? PreparedFailure;
        public void Dispose() => owner.ReleaseReservation(this);
    }
}

/// <summary>视频流读取租约；到期后已获得的流可以读完，释放前持续占用产物预算。</summary>
public sealed class BackendExportReadLease : IDisposable
{
    private Action? _release;
    internal BackendExportReadLease(Stream stream, BackendExportArtifactReferenceDto artifact, Action release)
    { Stream = stream; Artifact = artifact; _release = release; }
    public Stream Stream { get; }
    public BackendExportArtifactReferenceDto Artifact { get; }
    public async Task<bool> VerifyHashAsync(CancellationToken cancellationToken = default)
    {
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(Stream, cancellationToken).ConfigureAwait(false));
        Stream.Seek(0, SeekOrigin.Begin); return hash == Artifact.Sha256;
    }
    public void Dispose()
    {
        Action? action = Interlocked.Exchange(ref _release, null);
        if (action is null) return;
        try { Stream.Dispose(); } finally { action(); }
    }
}
internal sealed class ExportTaskException(string code, string message) : Exception(message)
{ internal string Code { get; } = code; }
