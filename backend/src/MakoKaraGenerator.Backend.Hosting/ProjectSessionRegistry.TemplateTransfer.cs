using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private readonly object _transferGate = new();
    private readonly Dictionary<string, RetainedTemplateImport> _templateImports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedTemplateExport> _templateExports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _templateTransferPending = new(StringComparer.Ordinal);
    private readonly string _templateTransferTempPrefix = "makokara-transfer-" + Guid.NewGuid().ToString("N") + "-";
    private static readonly JsonSerializerOptions TransferJson = new(JsonSerializerDefaults.Web);
    private static readonly EffectPackageContentBudget TransferBudget = new()
    {
        MaximumBytes = TemplateTransferLimits.MaxExpandedBytes,
        MaximumFileBytes = TemplateTransferLimits.MaxFileBytes,
        MaximumFiles = TemplateTransferLimits.MaxEntries,
    };
    private sealed record RetainedTemplateImport(string Owner, TemplateImportPreflightResponse Response,
        ImmutableArray<StyleTemplate> Templates);
    private sealed record RetainedTemplateExport(string Owner, TemplateArchiveExportResponse Response, byte[] Bytes)
    { internal int ActiveReads { get; set; } }

    /// <summary>固定授权上传内容并完整验证原.mktpl闭包；不执行或编译作者。</summary>
    public Task<BackendOperationResult<TemplateImportPreflightResponse>> PreflightTemplateImportAsync(string clientId,
        string libraryId, TemplateImportPreflightRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, $"template-libraries:{libraryId}:imports:preflight",
            request with { Metadata = null }, () => RunTemplateTransferAsync(clientId, true,
                async () =>
                {
                    if (libraryId != "personal") return TransferDestinationError<TemplateImportPreflightResponse>(libraryId);
                    if (string.IsNullOrWhiteSpace(request.AssetId)) return TemplateReject<TemplateImportPreflightResponse>(BackendErrorCode.InvalidRequest, "必须提供归档资源。", "assetId");
                    using IDisposable pin = _resources.AcquireReference(clientId, request.AssetId);
                    StoredAsset asset = _resources.GetAuthorized(clientId, request.AssetId);
                    if (asset.Size > TemplateTransferLimits.MaxArchiveBytes) return TransferTooLarge<TemplateImportPreflightResponse>();
                    byte[] bytes;
                    await using (var source = new FileStream(asset.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                    {
                        if (source.Length != asset.Size) throw new IOException("Fixed asset changed.");
                        bytes = new byte[checked((int)asset.Size)];
                        await source.ReadExactlyAsync(bytes).ConfigureAwait(false);
                        if (source.ReadByte() != -1) throw new IOException("Fixed asset changed.");
                    }
                    if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256) throw new IOException("Fixed asset changed.");
                    DirectoryInfo temporary = CreateTemplateTransferDirectory();
                    string path = Path.Combine(temporary.FullName, "archive.mktpl");
                    try
                    {
                        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                        long expanded = CheckTransferZip(path);
                        var templates = (await StyleTemplateTransfer.ReadArchiveAsync(path, TransferBudget).ConfigureAwait(false)).ToImmutableArray();
                        if (templates.Length is < 1 or > TemplateTransferLimits.MaxTemplates) return TransferTooLarge<TemplateImportPreflightResponse>();
                        TemplateTransferSummaryDto[] summaries = [.. templates.Select(item => new TemplateTransferSummaryDto(TemplateSummary(item),
                            TemplateDefinitionAvailability(item)))];
                        var response = new TemplateImportPreflightResponse("template-import-" + Guid.NewGuid().ToString("N"), asset.Sha256,
                            bytes.LongLength, expanded, summaries, TransferExpires());
                        CheckTransferReceipt(response);
                        lock (_transferGate) _templateImports.Add(response.PreflightId, new(clientId, response, templates));
                        return BackendOperationResult<TemplateImportPreflightResponse>.Success(response);
                    }
                    finally { temporary.Delete(recursive: true); }
                }), cancellationToken);

    /// <summary>预检对象只能成功消费一次；回执重放保留一次CAS与同一新身份映射。</summary>
    public Task<BackendOperationResult<TemplateImportResponse>> ImportTemplatesAsync(string clientId, string libraryId,
        TemplateImportRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, $"template-libraries:{libraryId}:imports", request with { Metadata = null },
            () => RunTemplateTransferAsync(clientId, false, async () =>
            {
                if (libraryId != "personal") return TransferDestinationError<TemplateImportResponse>(libraryId);
                if (string.IsNullOrWhiteSpace(request.ExpectedRevision)) return TemplateReject<TemplateImportResponse>(BackendErrorCode.InvalidRequest, "必须提供精确库修订。", "expectedRevision");
                if (_templateLibrary is null) return TemplateUnavailable<TemplateImportResponse>();
                RetainedTemplateImport retained;
                lock (_transferGate)
                {
                    PruneTemplateTransfers();
                    if (string.IsNullOrWhiteSpace(request.PreflightId) || !_templateImports.TryGetValue(request.PreflightId, out retained!) || retained.Owner != clientId)
                        return TemplateReject<TemplateImportResponse>(BackendErrorCode.ResourceNotFound, "预检对象不存在、已消费或过期。", "preflightId");
                }
                await _templateIo.WaitAsync().ConfigureAwait(false);
                try
                {
                    var destination = _templateLibrary.Read(TemplateLibraryCatalog.Personal);
                    if (RevisionError(request.ExpectedRevision, destination) is { } revisionError) return BackendOperationResult<TemplateImportResponse>.Rejected(revisionError);
                    PreparedTemplateLibraryEdit prepared = _templateLibrary.PrepareImport(destination, retained.Templates);
                    foreach (StyleTemplate template in prepared.Templates.Skip(destination.Templates.Length)) CheckTemplateCandidate(template, prepared);
                    TemplateImportIdentityDto[] identities = [.. retained.Templates.Select((item, index) =>
                        new TemplateImportIdentityDto(item.Id, prepared.Templates[destination.Templates.Length + index].Id))];
                    var response = new TemplateImportResponse(libraryId, TemplateRevision(destination), new string('f', 64), prepared.Changed, identities);
                    CheckTransferReceipt(response);
                    var committed = await _templateLibrary.CommitPreparedAsync(prepared).ConfigureAwait(false);
                    lock (_transferGate) _templateImports.Remove(request.PreflightId);
                    return BackendOperationResult<TemplateImportResponse>.Success(response with { Revision = TemplateRevision(committed) });
                }
                finally { _templateIo.Release(); }
            }), cancellationToken);

    /// <summary>从精确库修订捕获完整选中定义，发布原格式固定归档bytes。</summary>
    public Task<BackendOperationResult<TemplateArchiveExportResponse>> ExportTemplatesAsync(string clientId, string libraryId,
        TemplateArchiveExportRequest request, CancellationToken cancellationToken = default)
    {
        // Capture the caller's mutable array before the receipt owner computes the intent.
        request = request with { TemplateIds = request.TemplateIds is null ? null! : [.. request.TemplateIds] };
        return Receipts.ExecuteAsync(clientId, request.Metadata, $"template-libraries:{libraryId}:exports", request with { Metadata = null },
            () => RunTemplateTransferAsync(clientId, true, async () =>
            {
                if (string.IsNullOrWhiteSpace(request.ExpectedRevision)) return TemplateReject<TemplateArchiveExportResponse>(BackendErrorCode.InvalidRequest, "必须提供精确库修订。", "expectedRevision");
                if (request.TemplateIds is null || request.TemplateIds.Length is < 1 or > TemplateTransferLimits.MaxTemplates
                    || request.TemplateIds.Any(string.IsNullOrWhiteSpace) || request.TemplateIds.Distinct(StringComparer.Ordinal).Count() != request.TemplateIds.Length)
                    return TemplateReject<TemplateArchiveExportResponse>(BackendErrorCode.InvalidRequest, "必须提供1至100个唯一模板身份。", "templateIds");
                var captured = await ReadTemplateLibraryAsync(libraryId, request.ExpectedRevision, snapshot =>
                {
                    List<StyleTemplate> selected = [];
                    foreach (string id in request.TemplateIds)
                    {
                        StyleTemplate? item = snapshot.Templates.FirstOrDefault(item => item.Id == id);
                        if (item is null) return TemplateReject<StyleTemplate[]>(BackendErrorCode.ResourceNotFound, "选中的模板不存在。", "templateIds");
                        // Personal archives cannot claim reserved builtin identities. Keep the
                        // fixed definition and use a deterministic archive-local identity instead.
                        selected.Add(libraryId == "builtin" ? item with { Id = "exported:" + item.Id } : item);
                    }
                    return BackendOperationResult<StyleTemplate[]>.Success([.. selected]);
                }, CancellationToken.None).ConfigureAwait(false);
                if (!captured.Accepted) return BackendOperationResult<TemplateArchiveExportResponse>.Rejected(captured.Error!);
                DirectoryInfo temporary = CreateTemplateTransferDirectory();
                string path = Path.Combine(temporary.FullName, "archive.mktpl");
                try
                {
                    await StyleTemplateTransfer.WriteArchiveAsync(path, captured.Response!, TransferBudget).ConfigureAwait(false);
                    _ = CheckTransferZip(path);
                    long length = new FileInfo(path).Length;
                    if (length > TemplateTransferLimits.MaxArchiveBytes) return TransferTooLarge<TemplateArchiveExportResponse>();
                    byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    string id = "template-export-" + Guid.NewGuid().ToString("N");
                    var response = new TemplateArchiveExportResponse(id, libraryId, request.ExpectedRevision, [.. request.TemplateIds], bytes.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(bytes)), $"/api/v1/template-transfers/{id}/archive", TransferExpires());
                    CheckTransferReceipt(response);
                    lock (_transferGate) _templateExports.Add(id, new(clientId, response, bytes));
                    return BackendOperationResult<TemplateArchiveExportResponse>.Success(response);
                }
                finally { temporary.Delete(recursive: true); }
            }), cancellationToken);
    }

    /// <summary>已认证读者拥有固定字节引用，TTL或shutdown清理不截断在途读取。</summary>
    public BackendOperationResult<TemplateArchiveContentLease> AcquireTemplateArchive(string clientId, string exportId)
    {
        lock (_transferGate)
        {
            if (Volatile.Read(ref _stopping) != 0) return TemplateClosing<TemplateArchiveContentLease>();
            if (!_templateExports.TryGetValue(exportId, out var retained) || retained.Owner != clientId)
                return TemplateReject<TemplateArchiveContentLease>(BackendErrorCode.ResourceNotFound, "模板归档不存在。", "exportId");
            if (retained.Response.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            { if (retained.ActiveReads == 0) _templateExports.Remove(exportId); return TemplateReject<TemplateArchiveContentLease>(BackendErrorCode.ResourceExpired, "模板归档已过期。", "exportId"); }
            retained.ActiveReads++;
            return BackendOperationResult<TemplateArchiveContentLease>.Success(new(retained.Response, new MemoryStream(retained.Bytes, writable: false), () =>
            { lock (_transferGate) { retained.ActiveReads--; PruneTemplateTransfers(); } }));
        }
    }

    private async Task<BackendOperationResult<T>> RunTemplateTransferAsync<T>(string clientId, bool addsSlot, Func<Task<BackendOperationResult<T>>> action)
    {
        if (!BeginTemplateOperation()) return TemplateClosing<T>();
        bool reserved = false;
        try
        {
            lock (_transferGate)
            {
                PruneTemplateTransfers();
                if (_templateTransferPending.Contains(clientId)) return TemplateReject<T>(BackendErrorCode.SessionConflict, "当前client已有模板传输。", "library");
                long bytes = _templateImports.Values.Sum(item => item.Response.ExpandedBytes) + _templateExports.Values.Sum(item => item.Bytes.LongLength);
                int count = _templateImports.Count + _templateExports.Count;
                int own = _templateImports.Values.Count(item => item.Owner == clientId) + _templateExports.Values.Count(item => item.Owner == clientId);
                if (addsSlot && (count + _templateTransferPending.Count >= TemplateTransferLimits.MaxRetainedTotal
                    || own >= TemplateTransferLimits.MaxRetainedPerClient
                    || bytes + (long)(_templateTransferPending.Count + 1) * TemplateTransferLimits.MaxExpandedBytes > TemplateTransferLimits.MaxRetainedBytes))
                    return TemplateReject<T>(BackendErrorCode.ResourceQuotaExceeded, "模板传输保留配额不足。", "library");
                _templateTransferPending.Add(clientId); reserved = true;
            }
            return await Task.Run(action).ConfigureAwait(false);
        }
        catch (ResourceStoreException error) { return TemplateReject<T>(error.Code, error.Message, "assetId"); }
        catch (EffectPackageException error) when (error.Code.Contains("Budget", StringComparison.Ordinal) || error.Code.Contains("CountExceeded", StringComparison.Ordinal) || error.Code.Contains("Capacity", StringComparison.Ordinal))
        { return TransferTooLarge<T>(); }
        catch (Exception error) when (TemplateHandled(error) || error is InvalidDataException or OverflowException)
        { return TemplateFailure<T>(error); }
        finally
        {
            if (reserved) lock (_transferGate) _templateTransferPending.Remove(clientId);
            EndTemplateOperation();
        }
    }
    private static long CheckTransferZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > TemplateTransferLimits.MaxEntries) throw new TemplateLibraryResponseBudgetException();
        var usage = new EffectPackageContentUsage(TransferBudget);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            usage.Claim(entry.FullName, entry.Length); expanded = checked(expanded + entry.Length);
        }
        return expanded;
    }
    private DirectoryInfo CreateTemplateTransferDirectory()
    {
        // CreateTempSubdirectory creates a fresh private directory atomically. Keep
        // the mode explicit on Unix; Windows retains the current user's inherited ACL.
        DirectoryInfo directory = Directory.CreateTempSubdirectory(_templateTransferTempPrefix);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return directory;
        }
        catch { directory.Delete(recursive: true); throw; }
    }
    private DateTimeOffset TransferExpires() => _timeProvider.GetUtcNow().AddSeconds(TemplateTransferLimits.LifetimeSeconds);
    private static void CheckTransferReport<T>(T response)
    { if (JsonSerializer.SerializeToUtf8Bytes(response, TransferJson).Length > TemplateTransferLimits.MaxReportBytes) throw new TemplateLibraryResponseBudgetException(); }
    private void CheckTransferReceipt<T>(T response)
    {
        CheckTransferReport(response);
        TemplateLibraryResponseBudget.Check(BackendOperationResult<T>.Success(response), Receipts.MaxReceiptBytes,
            countValues: false, options: JsonSerializerOptions.Default);
    }
    private static BackendOperationResult<T> TransferTooLarge<T>() => TemplateReject<T>(BackendErrorCode.ResourceTooLarge, "模板归档超过压缩、解压、文件、模板或完整报告预算。", "archive");
    private static BackendOperationResult<T> TransferDestinationError<T>(string libraryId) => libraryId == "builtin"
        ? TemplateReject<T>(BackendErrorCode.ReadOnlySession, "内置目录只读。", "libraryId")
        : TemplateReject<T>(BackendErrorCode.ResourceNotFound, "模板库不存在。", "libraryId");
    // Called only with _transferGate held. Logical bytes remain charged until TTL or consumption.
    private void PruneTemplateTransfers()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (var item in _templateImports.Where(item => item.Value.Response.ExpiresAtUtc <= now).ToArray()) _templateImports.Remove(item.Key);
        foreach (var item in _templateExports.Where(item => item.Value.Response.ExpiresAtUtc <= now && item.Value.ActiveReads == 0).ToArray()) _templateExports.Remove(item.Key);
    }
    private bool HasTemplateTransfer(string clientId)
    { lock (_transferGate) { PruneTemplateTransfers(); return _templateTransferPending.Contains(clientId) || _templateImports.Values.Any(item => item.Owner == clientId) || _templateExports.Values.Any(item => item.Owner == clientId); } }
    private void ClearTemplateTransfers()
    { lock (_transferGate) { _templateImports.Clear(); _templateExports.Clear(); } }
}

/// <summary>固定归档读取；引用独立于保留目录生命周期。</summary>
public sealed class TemplateArchiveContentLease(TemplateArchiveExportResponse response, Stream stream, Action release) : IDisposable
{
    private Action? _release = release;
    public TemplateArchiveExportResponse Response { get; } = response;
    public Stream Stream { get; } = stream;
    public void Dispose() { try { Stream.Dispose(); } finally { Interlocked.Exchange(ref _release, null)?.Invoke(); } }
}
