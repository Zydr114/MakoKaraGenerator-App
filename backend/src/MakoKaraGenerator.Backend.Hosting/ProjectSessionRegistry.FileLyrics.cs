using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private readonly object _lyricsExportGate = new();
    private readonly Dictionary<string, RetainedLyricsPlan> _lyricsPlans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedLyricsContent> _lyricsContents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _lyricsOperations = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions FileLyricsJson = new(JsonSerializerDefaults.Web);
    private sealed record RetainedLyricsPlan(string Owner, LyricsDocument Document, LyricsExportOptions Options, LyricsExportPlanResponse Response);
    private sealed record RetainedLyricsContent(string Owner, LyricsExportResponse Response, byte[] Bytes);

    /// <summary>Enumerate one authorized directory, bounded before sorting or paging; never loads a project.</summary>
    public BackendOperationResult<ProjectDirectoryEntriesResponse> ReadProjectDirectoryEntries(string clientId, string rootGrantId,
        string relativePath = "", int offset = 0, int limit = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (offset < 0 || limit is < 1 or > FileLyricsLimits.MaxDirectoryPageSize || relativePath.Length > 2048)
            return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.InvalidRequest, "目录分页或相对路径长度无效。");
        if (_persistence is null || !_persistence.AllowedProjectRoots.TryGetValue(rootGrantId, out string? configuredRoot))
            return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.PathNotAllowed, "工程根未获启动器授权。");
        try
        {
            string root = Path.GetFullPath(configuredRoot);
            ProjectDirectoryHandles.EnsurePathHasNoLinks(root);
            string path = relativePath.Length == 0 ? root : ProjectDirectoryHandles.Resolve(root, relativePath);
            if (!Directory.Exists(path)) return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.ResourceNotFound, "目录不存在。");
            List<ProjectDirectoryEntryDto> directories = [];
            int scanned = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (++scanned > FileLyricsLimits.MaxDirectoryEntries)
                    return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.ResourceQuotaExceeded, "单层目录超过4096条目，未返回不完整列表。");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.PathNotAllowed, "目录列表不能包含链接。");
                if ((attributes & FileAttributes.Directory) == 0) continue;
                string name = Path.GetFileName(entry);
                string relative = relativePath.Length == 0 ? name : relativePath.Replace('\\', '/') + "/" + name;
                _ = ProjectDirectoryHandles.Resolve(root, relative);
                string marker = Path.Combine(entry, ProjectJson.ProjectFileName);
                if (new FileInfo(marker).LinkTarget is not null || new DirectoryInfo(marker).LinkTarget is not null)
                    return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.PathNotAllowed, "工程入口不能是链接。");
                directories.Add(new(name, relative, File.Exists(marker)));
            }
            ProjectDirectoryHandles.EnsurePathHasNoLinks(path);
            directories.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            if (offset > directories.Count) return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.InvalidRequest, "offset超过当前目录数量。");
            int count = Math.Min(limit, directories.Count - offset);
            return BackendOperationResult<ProjectDirectoryEntriesResponse>.Success(new(rootGrantId, relativePath.Replace('\\', '/'), offset,
                directories.Count, offset + count < directories.Count ? offset + count : null, directories.GetRange(offset, count).ToArray()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Reject<ProjectDirectoryEntriesResponse>(BackendErrorCode.PathNotAllowed, "目录无效、不可访问或包含链接。"); }
    }

    /// <summary>Capture an immutable branch at the exact current revision and retain its actual Core assessment.</summary>
    public Task<BackendOperationResult<LyricsExportPlanResponse>> PlanLyricsExportAsync(string clientId, string sessionId,
        LyricsExportPlanRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, $"project-sessions:{sessionId}:lyrics-export-plans", request with { Metadata = null },
            () => PlanLyricsExportCoreAsync(clientId, sessionId, request), cancellationToken);

    private async Task<BackendOperationResult<LyricsExportPlanResponse>> PlanLyricsExportCoreAsync(string clientId, string sessionId, LyricsExportPlanRequest request)
    {
        if (!ValidLyricsOptions(request) || request.Scope is not ("branch" or "document")
            || request.Scope == "branch" && string.IsNullOrWhiteSpace(request.BranchId)
            || request.Scope == "document" && (request.BranchId is not null || request.Format != "ttml.project"))
            return Reject<LyricsExportPlanResponse>(BackendErrorCode.InvalidRequest, "歌词导出格式、来源或选项无效。");
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return BackendOperationResult<LyricsExportPlanResponse>.Rejected(MissingSessionError(sessionId));
        LyricsDocument original;
        LyricBranch? branch;
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CheckLyricsRevision(session, request.ExpectedRevision) is { } error) return BackendOperationResult<LyricsExportPlanResponse>.Rejected(error);
            original = session.Workspace.Project.Lyrics;
            branch = original.Branches.FirstOrDefault(branch => branch.Id == request.BranchId);
            if (request.Scope == "branch" && branch is null) return Reject<LyricsExportPlanResponse>(BackendErrorCode.ResourceNotFound, "歌词来源不存在。");
        }
        finally { session.Writer.Release(); }
        if (ReserveLyricsOperation(clientId, addsSlot: true) is { } reservationError)
            return BackendOperationResult<LyricsExportPlanResponse>.Rejected(reservationError);
        try
        {
            LyricsDocument document = request.Scope == "document" ? original : original with { PrimaryBranchId = branch!.Id, Branches = [branch] };
            if (!WithinLyricsSourceBudget(document)) return Reject<LyricsExportPlanResponse>(BackendErrorCode.ResourceTooLarge, "歌词来源超过10000行、100000元素/注音或4MiB文本预算。");
            LyricsExportOptions options = CoreLyricsOptions(request.Options);
            LyricsExportPlan plan = LyricsExporter(request.Format).Plan(document, options);
            List<LyricsExportLossDto> additional = [];
            if (request.Scope == "branch" && original.Branches.Length > 1)
                additional.Add(new("omittedBranches", original.Branches.Length - 1, "单来源导出不保存其它歌词分支。"));
            if (request.Scope == "branch")
            {
                var lineIds = document.AllLines.Select(line => line.Id).ToHashSet(StringComparer.Ordinal);
                int externalLinks = document.AllLines.Count(line => line.PrimaryLineId is not null && !lineIds.Contains(line.PrimaryLineId));
                if (externalLinks > 0) additional.Add(new("crossBranchPrimaryLineLinks", externalLinks, "单来源导出省略了关联主歌词行所属的分支。"));
            }
            if (request.Format is "lrc.plain" or "lrc.enhanced")
            {
                int ruby = document.Lines.Sum(line => line.RubyAnnotations.Length);
                if (ruby > 0) additional.Add(new("rubyAnnotations", ruby, "LRC不保存选中来源的注音及其关联。"));
                int lineEnds = document.Lines.Count(line => line.Time is { IsValid: true });
                if (lineEnds > 0) additional.Add(new("lineEndTimes", lineEnds, "LRC仅写行起点，不保存行终点。"));
                int tokenEnds = document.Lines.Sum(line => line.Tokens.Count(token => token.Time is { IsValid: true }));
                if (request.Format == "lrc.enhanced" && tokenEnds > 0) additional.Add(new("tokenEndTimes", tokenEnds, "增强LRC仅写元素起点，不保存元素终点。"));
                int links = document.Lines.Count(line => line.PrimaryLineId is not null);
                if (links > 0) additional.Add(new("primaryLineLinks", links, "LRC不保存与主歌词行的稳定关联，也不合成跟随时间。"));
                int invalidTimes = document.Lines.Count(line => line.Time is { IsValid: false })
                    + document.Lines.Sum(line => line.Tokens.Count(token => token.Time is { IsValid: false }));
                if (invalidTimes > 0) additional.Add(new("invalidTimeRanges", invalidTimes, "非法时间区间不会写入LRC；无合法行时间的行将被跳过。"));
            }
            var response = new LyricsExportPlanResponse("lyrics-plan-" + Guid.NewGuid().ToString("N"), sessionId, request.ExpectedRevision,
                request.Scope == "document" ? null : branch!.Id, request.Format, request.Options, request.Options.WriteByteOrderMark ? "utf-8-bom" : "utf-8",
                _timeProvider.GetUtcNow().AddSeconds(FileLyricsLimits.LifetimeSeconds), LyricsAssessment(plan), additional.ToArray(),
                plan.HasLosses || plan.Issues.Count > 0 || additional.Count > 0, request.Scope == "document" ? 0 : original.Branches.Length - 1, request.Scope);
            if (!WithinLyricsReportBudget(response)) return Reject<LyricsExportPlanResponse>(BackendErrorCode.ResourceTooLarge, "完整损失报告超过64KiB预算，未截断。");
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CheckLyricsRevision(session, request.ExpectedRevision) is { } error) return BackendOperationResult<LyricsExportPlanResponse>.Rejected(error);
                lock (_lyricsExportGate)
                {
                    response = response with { ExpiresAtUtc = _timeProvider.GetUtcNow().AddSeconds(FileLyricsLimits.LifetimeSeconds) };
                    _lyricsPlans.Add(response.PlanId, new(clientId, document, options, response));
                }
            }
            finally { session.Writer.Release(); }
            return BackendOperationResult<LyricsExportPlanResponse>.Success(response);
        }
        finally { ReleaseLyricsOperation(clientId); }
    }

    /// <summary>Execute the accepted plan without modifying the project, revision, history or dirty state.</summary>
    public Task<BackendOperationResult<LyricsExportResponse>> ExportLyricsAsync(string clientId, string sessionId,
        LyricsExportRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, $"project-sessions:{sessionId}:lyrics-exports", request with { Metadata = null },
            () => ExportLyricsCoreAsync(clientId, sessionId, request), cancellationToken);

    private async Task<BackendOperationResult<LyricsExportResponse>> ExportLyricsCoreAsync(string clientId, string sessionId, LyricsExportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId)) return Reject<LyricsExportResponse>(BackendErrorCode.InvalidRequest, "planId不能为空。");
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return BackendOperationResult<LyricsExportResponse>.Rejected(MissingSessionError(sessionId));
        RetainedLyricsPlan plan;
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CheckLyricsRevision(session, request.ExpectedRevision) is { } error) return BackendOperationResult<LyricsExportResponse>.Rejected(error);
            lock (_lyricsExportGate)
            {
                if (!_lyricsPlans.TryGetValue(request.PlanId, out plan!) || plan.Owner != clientId || plan.Response.ProjectSessionId != sessionId)
                    return Reject<LyricsExportResponse>(BackendErrorCode.ResourceNotFound, "歌词导出计划不存在或不属于此会话。");
                if (plan.Response.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                { _lyricsPlans.Remove(request.PlanId); return Reject<LyricsExportResponse>(BackendErrorCode.ResourceExpired, "歌词导出计划已经过期。"); }
                if (plan.Response.ProjectRevision != request.ExpectedRevision)
                    return Reject<LyricsExportResponse>(BackendErrorCode.RevisionConflict, "计划来源修订已经改变，请重新评估。");
                if (plan.Response.RequiresLossAcceptance && !request.AcceptLosses)
                    return Reject<LyricsExportResponse>(BackendErrorCode.CommandRejected, "必须明确接受计划中的损失及跳过行报告。请求未生成内容。");
            }
        }
        finally { session.Writer.Release(); }
        if (ReserveLyricsOperation(clientId, addsSlot: false) is { } reservationError)
            return BackendOperationResult<LyricsExportResponse>.Rejected(reservationError);
        try
        {
            using BoundedLyricsStream stream = new();
            LyricsExportReport report;
            try { report = LyricsExporter(plan.Response.Format).Export(plan.Document, stream, plan.Options); }
            catch (LyricsContentBudgetException) { return Reject<LyricsExportResponse>(BackendErrorCode.ResourceTooLarge, "歌词导出超过4MiB预算，未发布不完整内容。"); }
            byte[] bytes = stream.ToArray();
            string id = "lyrics-export-" + Guid.NewGuid().ToString("N");
            var response = new LyricsExportResponse(id, request.PlanId, sessionId, plan.Response.ProjectRevision, plan.Response.BranchId,
                plan.Response.Format, plan.Response.Encoding, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                $"/api/v1/lyrics-exports/{id}/content", _timeProvider.GetUtcNow().AddSeconds(FileLyricsLimits.LifetimeSeconds),
                new(report.ExporterId, report.ExporterName, report.SourceLineCount, report.ExportedLineCount, report.SkippedLineCount,
                    report.DroppedTokenTimeCount, report.RoundedTimeCount, report.WrittenByteCount, report.MetadataCount, LyricsAssessment(report.Plan),
                    report.WrittenRubySegmentCount, report.SkippedRubySegmentCount, report.DroppedRubySegmentTimeCount),
                plan.Response.AdditionalLosses, Scope: plan.Response.Scope);
            if (!WithinLyricsReportBudget(response)) return Reject<LyricsExportResponse>(BackendErrorCode.ResourceTooLarge, "完整导出报告超过64KiB预算，未截断。");
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CheckLyricsRevision(session, request.ExpectedRevision) is { } error) return BackendOperationResult<LyricsExportResponse>.Rejected(error);
                lock (_lyricsExportGate)
                {
                    if (!_lyricsPlans.TryGetValue(request.PlanId, out RetainedLyricsPlan? current) || !ReferenceEquals(current, plan))
                        return Reject<LyricsExportResponse>(BackendErrorCode.ResourceNotFound, "歌词导出计划已释放或被消费。");
                    if (plan.Response.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                    { _lyricsPlans.Remove(request.PlanId); return Reject<LyricsExportResponse>(BackendErrorCode.ResourceExpired, "歌词导出计划已经过期。"); }
                    response = response with { ExpiresAtUtc = _timeProvider.GetUtcNow().AddSeconds(FileLyricsLimits.LifetimeSeconds) };
                    _lyricsContents.Add(id, new(clientId, response, bytes));
                    // A result replaces its plan slot; the original metadata retries use the shared receipt.
                    _lyricsPlans.Remove(request.PlanId);
                }
            }
            finally { session.Writer.Release(); }
            return BackendOperationResult<LyricsExportResponse>.Success(response);
        }
        finally { ReleaseLyricsOperation(clientId); }
    }

    /// <summary>Acquire immutable bytes; an in-flight reader owns its reference until it finishes.</summary>
    public BackendOperationResult<LyricsExportContentLease> AcquireLyricsExport(string clientId, string exportId)
    {
        lock (_lyricsExportGate)
        {
            if (!_lyricsContents.TryGetValue(exportId, out RetainedLyricsContent? content) || content.Owner != clientId)
                return Reject<LyricsExportContentLease>(BackendErrorCode.ResourceNotFound, "歌词导出内容不存在。");
            if (content.Response.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            { _lyricsContents.Remove(exportId); return Reject<LyricsExportContentLease>(BackendErrorCode.ResourceExpired, "歌词导出内容已经过期。"); }
            if (!TryGet(clientId, content.Response.ProjectSessionId, out ProjectSession session) || session.IsClosed)
            { _lyricsContents.Remove(exportId); return Reject<LyricsExportContentLease>(BackendErrorCode.ResourceNotFound, "工程会话已经关闭。"); }
            LyricsExportResponse response = content.Response with
            {
                AdditionalLosses = [.. content.Response.AdditionalLosses],
                Report = content.Response.Report with { Plan = content.Response.Report.Plan with
                    { Losses = [.. content.Response.Report.Plan.Losses], Issues = [.. content.Response.Report.Plan.Issues] } },
            };
            return BackendOperationResult<LyricsExportContentLease>.Success(new(response, new MemoryStream(content.Bytes, writable: false)));
        }
    }

    private BackendErrorResponse? CheckLyricsRevision(ProjectSession session, string? expectedRevision)
    {
        if (Volatile.Read(ref _stopping) != 0 || session.IsClosed || session.IsClosing || session.ClosePending)
            return new(BackendErrorCode.SessionClosing, "工程会话正在关闭。");
        if (string.IsNullOrEmpty(expectedRevision) || !long.TryParse(expectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision)
            || revision < 0 || Revision(revision) != expectedRevision)
            return new(BackendErrorCode.InvalidRequest, "expectedRevision必须为规范非负十进制修订。", "expectedRevision");
        return session.ProjectRevision != revision ? new(BackendErrorCode.RevisionConflict, "工程修订已经改变。") : null;
    }

    private static ILyricsExporter LyricsExporter(string format) => format switch
    {
        "lrc.plain" => new PlainLrcExporter(),
        "lrc.enhanced" => new EnhancedLrcExporter(),
        "ttml.project" => new TtmlProjectExporter(),
        "ttml.amll" => new TtmlAmllExporter(),
        _ => throw new ArgumentException("未知歌词导出格式。", nameof(format)),
    };
    private static LyricsExportOptions CoreLyricsOptions(LyricsExportOptionsDto options) => new()
    { Title = options.Title, Artist = options.Artist, Album = options.Album, NewLine = options.NewLine,
        WriteByteOrderMark = options.WriteByteOrderMark, MillisecondPrecision = options.MillisecondPrecision };
    private static bool ValidLyricsOptions(LyricsExportPlanRequest request) => request.Format is "lrc.plain" or "lrc.enhanced" or "ttml.project" or "ttml.amll"
        && request.Options is { NewLine: "\n" or "\r\n" } options && ValidMetadata(options.Title) && ValidMetadata(options.Artist) && ValidMetadata(options.Album);
    private static bool ValidMetadata(string? text) => text is null || text.Length <= 1024 && !text.Any(char.IsControl) && !text.Contains(']');
    private static LyricsExportAssessmentDto LyricsAssessment(LyricsExportPlan plan) => new(
        [.. plan.Losses.Select(loss => new LyricsExportLossDto(loss.Feature, loss.AffectedCount, loss.Message))], plan.ExportableLineCount,
        plan.SkippedLineCount, [.. plan.Issues.Select(issue => new LyricsExportIssueDto(issue.Kind.ToString(), issue.LineId, issue.Message))], plan.HasLosses);
    private bool WithinLyricsReportBudget<T>(T response) => JsonSerializer.SerializeToUtf8Bytes(response, FileLyricsJson).Length <= FileLyricsLimits.MaxReportBytes
        && JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<T>.Success(response)).Length <= Receipts.MaxReceiptBytes;
    private static bool WithinLyricsSourceBudget(LyricsDocument document)
    {
        long bytes = 0; long tokens = 0;
        // Include empty branches and metadata before visiting every line in every branch.
        if (document.Branches.Length > FileLyricsLimits.MaxSourceTokens) return false;
        bytes += Encoding.UTF8.GetByteCount(document.PrimaryBranchId);
        foreach (LyricBranch branch in document.Branches)
        {
            bytes += Encoding.UTF8.GetByteCount(branch.Id) + (long)Encoding.UTF8.GetByteCount(branch.Name)
                + (branch.Language is null ? 0 : Encoding.UTF8.GetByteCount(branch.Language));
            if (branch.StyleOverride is not null) bytes += JsonSerializer.SerializeToUtf8Bytes(branch.StyleOverride, FileLyricsJson).LongLength;
            if (bytes > FileLyricsLimits.MaxSourceBytes) return false;
        }
        int lines = 0;
        foreach (LyricLine line in document.AllLines)
        {
            if (++lines > FileLyricsLimits.MaxSourceLines) return false;
            tokens += (long)line.Tokens.Length + line.RubyAnnotations.Length + line.RubyAnnotations.Sum(ruby => (long)ruby.EffectiveSegments.Count);
            if (tokens > FileLyricsLimits.MaxSourceTokens) return false;
            bytes += Encoding.UTF8.GetByteCount(line.Id) + (long)Encoding.UTF8.GetByteCount(line.Text);
            if (line.PrimaryLineId is not null) bytes += Encoding.UTF8.GetByteCount(line.PrimaryLineId);
            foreach (LyricToken token in line.Tokens) bytes += Encoding.UTF8.GetByteCount(token.Id) + (long)Encoding.UTF8.GetByteCount(token.Text);
            foreach (RubyAnnotation ruby in line.RubyAnnotations)
            {
                bytes += Encoding.UTF8.GetByteCount(ruby.Id) + (long)Encoding.UTF8.GetByteCount(ruby.Text)
                    + Encoding.UTF8.GetByteCount(ruby.BaseTokenId) + (ruby.Language is null ? 0 : Encoding.UTF8.GetByteCount(ruby.Language));
                foreach (RubyTextSegment segment in ruby.EffectiveSegments)
                    bytes += Encoding.UTF8.GetByteCount(segment.Id) + (long)Encoding.UTF8.GetByteCount(segment.Text);
            }
            if (bytes > FileLyricsLimits.MaxSourceBytes || line.RubyAnnotations.Length > FileLyricsLimits.MaxSourceTokens) return false;
        }
        return true;
    }
    private bool HasLyricsCapacity(string clientId) => _lyricsPlans.Count + _lyricsContents.Count + _lyricsOperations.Values.Count(addsSlot => addsSlot) < FileLyricsLimits.MaxRetainedTotal
        && _lyricsPlans.Values.Count(plan => plan.Owner == clientId) + _lyricsContents.Values.Count(content => content.Owner == clientId)
            + (_lyricsOperations.GetValueOrDefault(clientId) ? 1 : 0) < FileLyricsLimits.MaxRetainedPerClient;
    private BackendErrorResponse? ReserveLyricsOperation(string clientId, bool addsSlot)
    {
        lock (_lyricsExportGate)
        {
            CollectLyricsExports();
            if (_lyricsOperations.Count >= 2 || _lyricsOperations.ContainsKey(clientId))
                return new(BackendErrorCode.RequestQuotaExceeded, "歌词评估和导出全局最多并发2次，每client最多1次。");
            if (addsSlot && !HasLyricsCapacity(clientId))
                return new(BackendErrorCode.ResourceQuotaExceeded, "歌词计划及结果保留名额已满。");
            _lyricsOperations.Add(clientId, addsSlot); return null;
        }
    }
    private void ReleaseLyricsOperation(string clientId) { lock (_lyricsExportGate) _lyricsOperations.Remove(clientId); }
    private void CollectLyricsExports()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (string id in _lyricsPlans.Where(pair => pair.Value.Response.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) _lyricsPlans.Remove(id);
        foreach (string id in _lyricsContents.Where(pair => pair.Value.Response.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) _lyricsContents.Remove(id);
    }
    private bool HasLyricsExport(string clientId)
    { lock (_lyricsExportGate) { CollectLyricsExports(); return _lyricsOperations.ContainsKey(clientId) || _lyricsPlans.Values.Any(plan => plan.Owner == clientId) || _lyricsContents.Values.Any(content => content.Owner == clientId); } }
    private void RemoveLyricsExports(string clientId)
    {
        lock (_lyricsExportGate)
        {
            foreach (string id in _lyricsPlans.Where(pair => pair.Value.Owner == clientId).Select(pair => pair.Key).ToArray()) _lyricsPlans.Remove(id);
            foreach (string id in _lyricsContents.Where(pair => pair.Value.Owner == clientId).Select(pair => pair.Key).ToArray()) _lyricsContents.Remove(id);
        }
    }
    private void RemoveSessionLyricsExports(string sessionId)
    {
        lock (_lyricsExportGate)
        {
            foreach (string id in _lyricsPlans.Where(pair => pair.Value.Response.ProjectSessionId == sessionId).Select(pair => pair.Key).ToArray()) _lyricsPlans.Remove(id);
            foreach (string id in _lyricsContents.Where(pair => pair.Value.Response.ProjectSessionId == sessionId).Select(pair => pair.Key).ToArray()) _lyricsContents.Remove(id);
        }
    }
    private void ClearLyricsExports() { lock (_lyricsExportGate) { _lyricsPlans.Clear(); _lyricsContents.Clear(); } }
    private sealed class LyricsContentBudgetException : IOException { }
    private sealed class BoundedLyricsStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { Require(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Require(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Require(1); base.WriteByte(value); }
        private void Require(int count) { if (Position + count > FileLyricsLimits.MaxContentBytes) throw new LyricsContentBudgetException(); }
    }
}

/// <summary>An immutable LRC read reference; bytes remain alive for the response even if the session closes.</summary>
public sealed class LyricsExportContentLease(LyricsExportResponse response, Stream stream) : IDisposable
{
    public LyricsExportResponse Response { get; } = response;
    public Stream Stream { get; } = stream;
    public void Dispose() => Stream.Dispose();
}
