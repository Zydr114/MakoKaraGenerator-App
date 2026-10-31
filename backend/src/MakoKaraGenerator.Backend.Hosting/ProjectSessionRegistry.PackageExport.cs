using System.Security.Cryptography;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private readonly SemaphoreSlim _packageDownloadSlots = new(2, 2);

    /// <summary>固定当前所有者精确草稿修订中的完整包；读取名额保持到内容租约关闭。</summary>
    public Task<BackendOperationResult<EffectPackageDownloadLease>> OpenAuthorPackageAsync(string clientId,
        string draftId, string expectedDraftRevision, string sourceRevision, CancellationToken cancellationToken = default)
        => OpenEffectPackageDownloadAsync(async token =>
        {
            if (!ValidDraftId(draftId)) return AuthorReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "草稿身份无效。", "draftId");
            if (!ValidRevision(expectedDraftRevision)) return AuthorReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
            if (!ValidRevision(sourceRevision)) return AuthorReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
            SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? draft, out BackendErrorResponse? error))
                    return BackendOperationResult<EffectPackageContents>.Rejected(error!);
                if (draft!.DraftRevision != expectedDraftRevision)
                    return AuthorReject<EffectPackageContents>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
                try { return BackendOperationResult<EffectPackageContents>.Success(await LoadPackageAsync(draftId, sourceRevision, token).ConfigureAwait(false)); }
                catch (DirectoryNotFoundException) { return AuthorReject<EffectPackageContents>(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "sourceRevision"); }
            }
            finally { gate.Release(); }
        }, cancellationToken);

    /// <summary>固定精确个人配置库修订中组件原有的完整包，不改变导出或参数。</summary>
    public Task<BackendOperationResult<EffectPackageDownloadLease>> OpenScriptConfigurationPackageAsync(
        string configurationId, string expectedRevision, CancellationToken cancellationToken = default)
        => OpenEffectPackageDownloadAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(configurationId)) return AuthorReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "配置身份不能为空。", "configurationId");
            if (expectedRevision != "missing" && !(expectedRevision?.Length == 64 && expectedRevision.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))))
                return AuthorReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "配置库修订无效。", "expectedRevision");
            await _scriptConfigurationIo.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!TryReadScriptLibrary(out var library, out BackendErrorResponse? error)) return BackendOperationResult<EffectPackageContents>.Rejected(error!);
                if (RevisionText(library.Revision) != expectedRevision)
                    return AuthorReject<EffectPackageContents>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "expectedRevision");
                var configuration = library.Configurations.FirstOrDefault(item => item.Id == configurationId);
                return configuration is null
                    ? AuthorReject<EffectPackageContents>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId")
                    : BackendOperationResult<EffectPackageContents>.Success(configuration.Package);
            }
            finally { _scriptConfigurationIo.Release(); }
        }, cancellationToken);

    private async Task<BackendOperationResult<EffectPackageDownloadLease>> OpenEffectPackageDownloadAsync(
        Func<CancellationToken, Task<BackendOperationResult<EffectPackageContents>>> capture, CancellationToken cancellationToken)
    {
        if (!BeginTemplateOperation()) return TemplateClosing<EffectPackageDownloadLease>();
        bool slot = false, leased = false;
        DirectoryInfo? directory = null;
        FileStream? stream = null;
        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authorStop?.Token ?? CancellationToken.None);
            slot = await _packageDownloadSlots.WaitAsync(0, lifetime.Token).ConfigureAwait(false);
            if (!slot) return AuthorReject<EffectPackageDownloadLease>(BackendErrorCode.RequestQuotaExceeded, "效果包同时下载数量达到2个上限。", "package");
            BackendOperationResult<EffectPackageContents> captured = await capture(lifetime.Token).ConfigureAwait(false);
            if (!captured.Accepted) return BackendOperationResult<EffectPackageDownloadLease>.Rejected(captured.Error!);
            EffectPackageContents package = captured.Response!;
            directory = CreateTemplateTransferDirectory();
            string path = Path.Combine(directory.FullName, "package.mkfx");
            await EffectPackageTransfer.WriteArchiveAsync(path, package, lifetime.Token).ConfigureAwait(false);
            // Re-read the generated archive through the same closure validator used for import.
            EffectPackageContents checkedPackage = await EffectPackageTransfer.ReadArchiveAsync(path, ProjectEffectsJson.PackageProfile,
                cancellationToken: lifetime.Token).ConfigureAwait(false);
            if (checkedPackage.Revision != package.Revision || checkedPackage.PackageId != package.PackageId) throw new IOException("export package identity changed");
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            string sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, lifetime.Token).ConfigureAwait(false));
            stream.Position = 0;
            DirectoryInfo ownedDirectory = directory;
            EffectPackageDownloadLease lease = new(stream, package.PackageId, package.Revision, sha256, () =>
            {
                try { if (ownedDirectory.Exists) ownedDirectory.Delete(recursive: true); }
                finally { _packageDownloadSlots.Release(); EndTemplateOperation(); }
            }, _authorStop?.Token ?? CancellationToken.None);
            leased = true;
            return BackendOperationResult<EffectPackageDownloadLease>.Success(lease);
        }
        catch (EffectPackageException error)
        { return AuthorReject<EffectPackageDownloadLease>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return AuthorReject<EffectPackageDownloadLease>(BackendErrorCode.ProjectLoadFailed, "完整效果包无法导出。", "package", "EffectPackageExportFailed"); }
        finally
        {
            if (!leased)
            {
                try { stream?.Dispose(); if (directory?.Exists == true) directory.Delete(recursive: true); }
                finally { if (slot) _packageDownloadSlots.Release(); EndTemplateOperation(); }
            }
        }
    }
}

/// <summary>请求期间的完整效果包；关闭后删除临时归档并释放并发名额。</summary>
public sealed class EffectPackageDownloadLease(Stream stream, string packageId, string packageRevision, string sha256,
    Action release, CancellationToken stopping) : IDisposable
{
    private Action? _release = release;
    /// <summary>可定位的完整 .mkfx 内容。</summary>
    public Stream Stream { get; } = stream;
    /// <summary>原包身份。</summary>
    public string PackageId { get; } = packageId;
    /// <summary>原包修订。</summary>
    public string PackageRevision { get; } = packageRevision;
    /// <summary>下载归档的 SHA-256。</summary>
    public string Sha256 { get; } = sha256;
    /// <summary>宿主停止信号，用于终止活动下载并参与排水。</summary>
    public CancellationToken Stopping { get; } = stopping;
    /// <summary>释放归档和下载名额；重复关闭无副作用。</summary>
    public void Dispose()
    {
        Action? close = Interlocked.Exchange(ref _release, null);
        if (close is null) return;
        try { Stream.Dispose(); }
        finally { close(); }
    }
}
