using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private sealed record AuthorSourceHistoryDocument(int SchemaVersion, AuthorSourceRevisionDto[] Revisions);

    /// <summary>在所有权和精确草稿修订锁内读取保留源码历史；游标必须仍然保留。</summary>
    public async Task<BackendOperationResult<AuthorSourceRevisionsResponse>> ReadAuthorSourceRevisionsAsync(
        string clientId, string draftId, string expectedDraftRevision, string? afterSourceRevision = null,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        if (!ValidDraftId(draftId)) return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.InvalidRequest, "草稿身份无效。", "draftId");
        if (!ValidRevision(expectedDraftRevision)) return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
        if (afterSourceRevision is not null && !ValidRevision(afterSourceRevision)) return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.InvalidRequest, "源码历史游标无效。", "afterSourceRevision");
        int count = limit ?? 100;
        if (count is < 1 or > 100) return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.InvalidRequest, "历史页大小必须在1至100之间。", "limit");
        if (!BeginTemplateOperation()) return TemplateClosing<AuthorSourceRevisionsResponse>();
        SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
        bool entered = false;
        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authorStop?.Token ?? CancellationToken.None);
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            entered = true;
            if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? draft, out BackendErrorResponse? error))
                return BackendOperationResult<AuthorSourceRevisionsResponse>.Rejected(error!);
            if (draft!.DraftRevision != expectedDraftRevision)
                return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            AuthorSourceRevisionDto[] retained = ReadAuthorSourceHistory(draft).Revisions;
            if (afterSourceRevision is not null && !retained.Any(item => item.SourceRevision == afterSourceRevision))
                return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.ResourceNotFound, "源码历史游标不属于保留的修订。", "afterSourceRevision");
            AuthorSourceRevisionDto[] candidates = [.. retained.Where(item => afterSourceRevision is null || StringComparer.Ordinal.Compare(item.SourceRevision, afterSourceRevision) > 0).Take(count + 1)];
            AuthorSourceRevisionDto[] page = [.. candidates.Take(count)];
            return BackendOperationResult<AuthorSourceRevisionsResponse>.Success(new(draftId, draft.DraftRevision, page,
                candidates.Length > count ? page[^1].SourceRevision : null));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return AuthorReject<AuthorSourceRevisionsResponse>(BackendErrorCode.ProjectLoadFailed, "作者源码历史无法读取。", "sourceRevisions", "AuthorSourceHistoryInvalid"); }
        finally { if (entered) gate.Release(); EndTemplateOperation(); }
    }

    private string AuthorSourceHistoryPath(string draftId) => Path.Combine(_authorRoot!, draftId, "source-history.json");

    // Directory names describe retained immutable packages. Missing metadata remains unknown for old drafts.
    private AuthorSourceHistoryDocument ReadAuthorSourceHistory(AuthorDraftDocument draft)
    {
        string packageRoot = Path.Combine(_authorRoot!, draft.DraftId, "packages");
        HashSet<string> retained = Directory.Exists(packageRoot)
            ? Directory.EnumerateDirectories(packageRoot).Select(path => "sha256:" + Path.GetFileName(path)).Where(ValidRevision).ToHashSet(StringComparer.Ordinal)
            : [];
        string path = AuthorSourceHistoryPath(draft.DraftId);
        Dictionary<string, AuthorSourceRevisionDto> metadata = new(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            AuthorSourceHistoryDocument? history = JsonSerializer.Deserialize<AuthorSourceHistoryDocument>(File.ReadAllBytes(path), AuthorJson);
            if (history is null || history.SchemaVersion != 1 || history.Revisions is null) throw new JsonException("unsupported source history schema");
            foreach (AuthorSourceRevisionDto item in history.Revisions)
            {
                if (item is null || !ValidRevision(item.SourceRevision) || !metadata.TryAdd(item.SourceRevision, item)
                    || (item.Compilation is not null && item.Compilation.SourceRevision != item.SourceRevision))
                    throw new JsonException("invalid source history revision");
            }
        }
        return new(1, [.. retained.Order(StringComparer.Ordinal).Select(revision => metadata.GetValueOrDefault(revision) ?? new(revision,
            Compilation: draft.LastCompilation?.SourceRevision == revision ? draft.LastCompilation : null))]);
    }

    private byte[] BuildAuthorSourceHistory(AuthorDraftDocument document, EffectPackageContents? package,
        bool packageExisted, AuthorCompilationSummaryDto? compilation)
    {
        Dictionary<string, AuthorSourceRevisionDto> revisions = ReadAuthorSourceHistory(document).Revisions.ToDictionary(item => item.SourceRevision, StringComparer.Ordinal);
        if (package is not null && !revisions.ContainsKey(package.Revision))
            revisions.Add(package.Revision, new(package.Revision, packageExisted ? null : ServerTimeUtc));
        AuthorCompilationSummaryDto? summary = compilation ?? document.LastCompilation;
        if (summary is not null && revisions.TryGetValue(summary.SourceRevision, out AuthorSourceRevisionDto? revision)
            && (revision.Compilation is null || revision.Compilation.CompletedAtUtc <= summary.CompletedAtUtc))
            revisions[summary.SourceRevision] = revision with { Compilation = summary with { Stale = false } };
        return JsonSerializer.SerializeToUtf8Bytes(new AuthorSourceHistoryDocument(1, [.. revisions.Values.OrderBy(item => item.SourceRevision, StringComparer.Ordinal)]), AuthorJson);
    }

    private static void SaveAuthorHistoryBytes(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { TryDelete(temporary); }
    }
}
