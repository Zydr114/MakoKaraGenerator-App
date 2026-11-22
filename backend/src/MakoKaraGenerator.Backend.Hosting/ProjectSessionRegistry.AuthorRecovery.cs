using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private static string NewOwnershipRevision() => "sha256:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string OwnershipRevision(AuthorDraftDocument document) => document.OwnershipRevision ??
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("legacy\n" + document.DraftId + "\n" + document.OwnerClientId)));

    /// <summary>发现本机 profile 中的作者草稿及当前所有权状态。</summary>
    public BackendOperationResult<ProfileAuthorDraftListResponse> ReadProfileAuthorDrafts(string clientId, int limit = 100, string? afterDraftId = null)
    {
        if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<ProfileAuthorDraftListResponse>.Rejected(unavailable!);
        if (Clients is null) return AuthorReject<ProfileAuthorDraftListResponse>(BackendErrorCode.TaskUnavailable, "客户端注册表不可用。", "clients");
        if (limit is < 1 or > 100) return AuthorReject<ProfileAuthorDraftListResponse>(BackendErrorCode.InvalidRequest, "limit必须在1到100之间。", "limit");
        if (afterDraftId is not null && !ValidDraftId(afterDraftId)) return AuthorReject<ProfileAuthorDraftListResponse>(BackendErrorCode.InvalidRequest, "游标无效。", "afterDraftId");
        try
        {
            List<ProfileAuthorDraftDto> drafts = [];
            SortedSet<string> ids = new(StringComparer.Ordinal);
            foreach (string directory in Directory.EnumerateDirectories(_authorRoot!, "draft-*", SearchOption.TopDirectoryOnly))
            {
                string id = Path.GetFileName(directory);
                if (!ValidDraftId(id) || (afterDraftId is not null && string.CompareOrdinal(id, afterDraftId) <= 0) || !File.Exists(DocumentPath(id))) continue;
                ids.Add(id);
                if (ids.Count > limit + 1) ids.Remove(ids.Max!);
            }
            bool more = ids.Count > limit;
            foreach (string id in ids.Take(limit))
            {
                if (!TryReadAuthorDraft(null, id, out AuthorDraftDocument? document, out BackendErrorResponse? error))
                    return BackendOperationResult<ProfileAuthorDraftListResponse>.Rejected(error!);
                (string state, bool canClaim) = Clients.ReadAuthorOwnerState(document!.OwnerClientId, clientId);
                drafts.Add(new(document.DraftId, document.DraftRevision, document.Name, OwnershipRevision(document), state, canClaim));
            }
            return BackendOperationResult<ProfileAuthorDraftListResponse>.Success(new([.. drafts], more ? drafts[^1].DraftId : null));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return AuthorReject<ProfileAuthorDraftListResponse>(BackendErrorCode.ProjectLoadFailed, "作者草稿目录无法读取。", "draftId", "AuthorDraftStorageFailed"); }
    }

    /// <summary>使用内容和所有权修订原子认领可恢复的作者草稿。</summary>
    public Task<BackendOperationResult<AuthorDraftClaimResponse>> ClaimProfileAuthorDraftAsync(string clientId, string draftId,
        AuthorDraftClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Receipts.ExecuteAsync(clientId, request.Metadata, "POST /api/v1/profile/author-drafts/" + draftId + "/claim",
            request with { Metadata = null }, ExecuteAsync, cancellationToken);

        async Task<BackendOperationResult<AuthorDraftClaimResponse>> ExecuteAsync()
        {
            if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<AuthorDraftClaimResponse>.Rejected(unavailable!);
            if (Clients is null) return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.TaskUnavailable, "客户端注册表不可用。", "clients");
            if (!ValidDraftId(draftId)) return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.InvalidRequest, "草稿身份无效。", "draftId");
            if (!ValidRevision(request.ExpectedDraftRevision)) return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
            if (!ValidRevision(request.ExpectedOwnershipRevision)) return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.InvalidRequest, "所有权修订无效。", "expectedOwnershipRevision");
            SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, _ => new SemaphoreSlim(1, 1));
            bool entered = false;
            try
            {
                await gate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                entered = true;
                if (!TryReadAuthorDraft(null, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
                    return BackendOperationResult<AuthorDraftClaimResponse>.Rejected(error!);
                if (document!.DraftRevision != request.ExpectedDraftRevision)
                    return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.RevisionConflict, "草稿修订已经改变。", "expectedDraftRevision");
                if (OwnershipRevision(document) != request.ExpectedOwnershipRevision)
                    return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.RevisionConflict, "草稿所有权已经改变。", "expectedOwnershipRevision");
                bool sameOwner = document.OwnerClientId == clientId;
                IDisposable? reservation = null;
                if (!sameOwner && !Clients.TryReserveAuthorOwner(document.OwnerClientId, out reservation))
                    return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.RevisionConflict, "草稿所有者仍在使用中。", "draftId", "AuthorOwnerActive");
                using (reservation)
                {
                    AuthorDraftDocument updated = sameOwner && document.SchemaVersion == 1 ? document : document with
                    { OwnerClientId = clientId, SchemaVersion = 1, OwnershipRevision = NewOwnershipRevision() };
                    AuthorDraftResponse response = await ToAuthorResponseAsync(updated, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                    if (!ReferenceEquals(updated, document))
                    {
                        await _authorStorageGate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            if (!sameOwner)
                            {
                                (int count, long bytes) = GetAuthorUsage(clientId);
                                string directory = Path.GetDirectoryName(DocumentPath(draftId))!;
                                long size = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                                byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(updated, AuthorJson);
                                size += serialized.Length - new FileInfo(DocumentPath(draftId)).Length;
                                if (count >= MaxAuthorDraftsPerClient)
                                    return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.RequestQuotaExceeded, "客户端作者草稿数量达到上限。", "drafts");
                                if (size > MaxAuthorStorageBytesPerClient - bytes)
                                    return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "drafts");
                                SaveDocument(updated, serialized);
                            }
                            else if (!TryPersistAuthorDocument(updated))
                                return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "draftId");
                        }
                        finally { _authorStorageGate.Release(); }
                    }
                    return BackendOperationResult<AuthorDraftClaimResponse>.Success(new(response, OwnershipRevision(updated)));
                }
            }
            catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true)
            { return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
            catch (EffectPackageException packageError)
            { return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.CommandRejected, packageError.Message, packageError.Path, packageError.Code); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            { return AuthorReject<AuthorDraftClaimResponse>(BackendErrorCode.ProjectLoadFailed, "作者草稿无法读取或保存。", "draftId", "AuthorDraftStorageFailed"); }
            finally { if (entered) gate.Release(); }
        }
    }
}
