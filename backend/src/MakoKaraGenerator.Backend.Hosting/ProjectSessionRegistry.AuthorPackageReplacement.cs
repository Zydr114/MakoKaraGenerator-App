using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>
    /// 只读检查：草稿保留修订里的一个图片或字体换成本客户端上传的资源时，原文件和新文件的格式、尺寸或家族名，
    /// 以及给作者的提醒。与保存走同一套校验；不改变草稿。
    /// </summary>
    public async Task<BackendOperationResult<AuthorPackageFileReplacementInspectionResponse>> InspectAuthorPackageFileReplacementAsync(
        string clientId, string draftId, AuthorPackageFileReplacementInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidDraftId(draftId)) return ReplacementReject(BackendErrorCode.InvalidRequest, "草稿身份无效。", "draftId");
        if (!ValidRevision(request.ExpectedDraftRevision)) return ReplacementReject(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
        if (!ValidRevision(request.SourceRevision)) return ReplacementReject(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
        if (string.IsNullOrWhiteSpace(request.Path)) return ReplacementReject(BackendErrorCode.InvalidRequest, "必须给出要替换的文件路径。", "path");
        if (string.IsNullOrWhiteSpace(request.AssetId)) return ReplacementReject(BackendErrorCode.InvalidRequest, "必须给出新文件的资源。", "assetId");
        if (!AuthorReady(out BackendErrorResponse? unavailable))
            return BackendOperationResult<AuthorPackageFileReplacementInspectionResponse>.Rejected(unavailable!);
        if (!BeginTemplateOperation()) return TemplateClosing<AuthorPackageFileReplacementInspectionResponse>();
        SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
        bool entered = false;
        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authorStop?.Token ?? CancellationToken.None);
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false); entered = true;
            if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? draft, out BackendErrorResponse? error))
                return BackendOperationResult<AuthorPackageFileReplacementInspectionResponse>.Rejected(error!);
            if (draft!.DraftRevision != request.ExpectedDraftRevision)
                return ReplacementReject(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (!ReadAuthorSourceHistory(draft).Revisions.Any(item => item.SourceRevision == request.SourceRevision))
                return ReplacementReject(BackendErrorCode.ResourceNotFound, "源码修订不属于此草稿保留历史。", "sourceRevision");
            EffectPackageContents package = await LoadPackageAsync(draftId, request.SourceRevision, lifetime.Token).ConfigureAwait(false);
            EffectPackageFile? current = package.Files.SingleOrDefault(file => file.Path == request.Path);
            if (current is null || IsEditableAuthorRole(current.Role) || !PackageAuthorReplacementInspector.CanReplace(current.Role))
                return ReplacementReject(BackendErrorCode.InvalidRequest, "这个修订里没有可替换的图片或字体文件。", "path", "AuthorReplacementTargetInvalid");
            // 固定包内容不可变：解码在草稿锁外进行，不挡住同一草稿的其他操作。
            gate.Release(); entered = false;
            byte[] bytes = await ReadAuthorReplacementAssetAsync(clientId, request.AssetId, current.Path, lifetime.Token).ConfigureAwait(false);
            PackageAuthorReplacementInspection inspection = await PackageAuthorReplacementInspector.InspectAsync(current, bytes, lifetime.Token)
                .ConfigureAwait(false);
            return BackendOperationResult<AuthorPackageFileReplacementInspectionResponse>.Success(new(draftId, draft.DraftRevision,
                request.SourceRevision, current.Path, current.Role,
                ResourceInfo(inspection.Current, current.Content.Length, current.Sha256),
                ResourceInfo(inspection.Replacement, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))),
                [.. inspection.Warnings.Select(warning => new AuthorPackageReplacementWarningDto(warning.Code, warning.Message))]));
        }
        catch (ResourceStoreException error)
        { return ReplacementReject(error.Code, error.Message, "assetId"); }
        catch (DirectoryNotFoundException)
        { return ReplacementReject(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "sourceRevision"); }
        catch (EffectPackageException error)
        { return ReplacementReject(BackendErrorCode.InvalidRequest, error.Message, ReplacementField(error.Code, "path", "assetId"), error.Code); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _authorStop?.IsCancellationRequested == true)
        { return ReplacementReject(BackendErrorCode.SessionClosing, "作者工作流正在停止。", null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return ReplacementReject(BackendErrorCode.ProjectLoadFailed, "作者草稿固定包或历史无法读取。", "sourceRevision", "AuthorDraftStorageFailed"); }
        finally { if (entered) gate.Release(); EndTemplateOperation(); }
    }

    /// <summary>
    /// 读出保存请求列出的替换：path 须是基准修订里的图片或字体且只出现一次，新字节须通过替换检查。
    /// 返回 path→新字节；失败给出 replacedFiles[i] 字段和具名失败码。
    /// </summary>
    private async Task<(Dictionary<string, byte[]>? Replacements, BackendErrorResponse? Error)> ReadAuthorReplacementsAsync(
        string clientId, AuthorPackageFileReplacementDto[]? requested, Dictionary<string, EffectPackageFile> fixedFiles,
        CancellationToken token)
    {
        Dictionary<string, byte[]> replacements = new(StringComparer.Ordinal);
        if (requested is null) return (replacements, null);
        if (requested.Length > fixedFiles.Count)
            return (null, new(BackendErrorCode.InvalidRequest, "替换的文件比可替换的文件还多。", "replacedFiles", FailureCode: "AuthorReplacementTargetInvalid"));
        for (int index = 0; index < requested.Length; index++)
        {
            string field = $"replacedFiles[{index}]";
            AuthorPackageFileReplacementDto? item = requested[index];
            if (item is null || string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.AssetId))
                return (null, new(BackendErrorCode.InvalidRequest, "每项替换都要给出 path 和 assetId。", field));
            if (!fixedFiles.TryGetValue(item.Path, out EffectPackageFile? prior) || !PackageAuthorReplacementInspector.CanReplace(prior.Role)
                || replacements.ContainsKey(item.Path))
                return (null, new(BackendErrorCode.InvalidRequest, "要替换的文件必须是基准修订里的图片或字体，且每个只能替换一次。",
                    field + ".path", FailureCode: "AuthorReplacementTargetInvalid"));
            try
            {
                byte[] bytes = await ReadAuthorReplacementAssetAsync(clientId, item.AssetId, prior.Path, token).ConfigureAwait(false);
                _ = await PackageAuthorReplacementInspector.InspectAsync(prior, bytes, token).ConfigureAwait(false);
                replacements.Add(item.Path, bytes);
            }
            catch (ResourceStoreException error)
            { return (null, new(error.Code, error.Message, field + ".assetId")); }
            catch (EffectPackageException error)
            { return (null, new(BackendErrorCode.InvalidRequest, error.Message, field + ReplacementField(error.Code, ".path", ".assetId"), FailureCode: error.Code)); }
        }
        return (replacements, null);
    }

    /// <summary>读出本客户端一个未过期资源的完整字节，先按包单文件预算拒绝过大的资源，再核对上传时的大小和摘要。</summary>
    private async Task<byte[]> ReadAuthorReplacementAssetAsync(string clientId, string assetId, string targetPath, CancellationToken token)
    {
        using IDisposable lease = _resources.AcquireReference(clientId, assetId);
        StoredAsset asset = _resources.GetAuthorized(clientId, assetId);
        long limit = EffectPackageContentBudget.Default.MaximumFileBytes;
        if (asset.Size > limit)
            throw new EffectPackageException("PackageFileBudgetExceeded", targetPath, $"新文件 {asset.Size} 字节，单文件限制 {limit}。");
        byte[] bytes = await File.ReadAllBytesAsync(asset.StoragePath, token).ConfigureAwait(false);
        if (bytes.LongLength != asset.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256, StringComparison.Ordinal))
            throw new ResourceStoreException(BackendErrorCode.ResourceExpired, "资源内容与上传记录不一致，需重新上传。", nameof(assetId));
        return bytes;
    }

    private static AuthorPackageResourceInfoDto ResourceInfo(PackageAuthorResourceInfo info, long size, string sha256)
        => new(info.Format, size, sha256, info.Width, info.Height, info.FamilyNames is null ? null : [.. info.FamilyNames]);

    /// <summary>目标不对的错误指向 path，其余（格式、解码、预算）都是新文件的问题，指向 assetId。</summary>
    private static string ReplacementField(string code, string target, string asset) => code == "AuthorReplacementTargetInvalid" ? target : asset;

    private static BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> ReplacementReject(BackendErrorCode code,
        string message, string? field, string? failureCode = null)
        => AuthorReject<AuthorPackageFileReplacementInspectionResponse>(code, message, field, failureCode);
}
