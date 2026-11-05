using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private const long MaximumAuthorInspectionResponseBytes = 8L * 1024 * 1024;
    // Matches the HTTP JSON shape for this declaration-only DTO (no enum-valued fields).
    // Count exactly the UTF-8 bytes that the current server emits, including null omission and the HTTP default encoder.
    private static readonly JsonSerializerOptions AuthorInspectionJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>只检查当前owner草稿保留的精确源码声明，不创建可分发reference或执行作者。</summary>
    public async Task<BackendOperationResult<AuthorSourceInspectionResponse>> InspectAuthorSourceAsync(string clientId,
        string draftId, string expectedDraftRevision, string sourceRevision, CancellationToken cancellationToken = default)
    {
        if (!ValidDraftId(draftId)) return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.InvalidRequest, "草稿身份无效。", "draftId");
        if (!ValidRevision(expectedDraftRevision)) return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
        if (!ValidRevision(sourceRevision)) return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
        if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<AuthorSourceInspectionResponse>.Rejected(unavailable!);
        if (!BeginTemplateOperation()) return TemplateClosing<AuthorSourceInspectionResponse>();
        SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
        bool entered = false;
        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authorStop?.Token ?? CancellationToken.None);
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false); entered = true;
            if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? draft, out BackendErrorResponse? error))
                return BackendOperationResult<AuthorSourceInspectionResponse>.Rejected(error!);
            if (draft!.DraftRevision != expectedDraftRevision)
                return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (!ReadAuthorSourceHistory(draft).Revisions.Any(item => item.SourceRevision == sourceRevision))
                return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.ResourceNotFound, "源码修订不属于此草稿保留历史。", "sourceRevision");
            EffectPackageContents package = await LoadPackageAsync(draftId, sourceRevision, lifetime.Token).ConfigureAwait(false);
            if (package.PackageId != draft.PackageId || package.Revision != sourceRevision)
                return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.ProjectLoadFailed, "固定源码包身份不一致。", "sourceRevision", "AuthorSourceIdentityMismatch");
            EffectPackageInspectionProjection projection = ProjectEffectPackageDeclarations(package, lifetime.Token);
            AuthorSourceInspectionResponse response = new(draftId, draft.DraftRevision, sourceRevision, package.PackageId,
                package.Revision, package.Name, package.Version, package.Profile.PackageVersion, package.Profile.SdkVersion,
                package.Profile.ShaderProfile, projection.StructureValid, projection.Parameters, projection.Exports,
                projection.BindingKinds, projection.Inputs, projection.Files, projection.Diagnostics);
            using var budget = new AuthorInspectionBudgetStream(MaximumAuthorInspectionResponseBytes, lifetime.Token);
            JsonSerializer.Serialize(budget, response, AuthorInspectionJson);
            lifetime.Token.ThrowIfCancellationRequested();
            return BackendOperationResult<AuthorSourceInspectionResponse>.Success(response);
        }
        catch (AuthorInspectionBudgetException)
        { return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.ResourceTooLarge, "源码声明检查响应超过8MiB。", "sourceRevision", "AuthorSourceInspectionTooLarge"); }
        catch (DirectoryNotFoundException)
        { return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "sourceRevision"); }
        catch (EffectPackageException error)
        { return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _authorStop?.IsCancellationRequested == true)
        { return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return AuthorReject<AuthorSourceInspectionResponse>(BackendErrorCode.ProjectLoadFailed, "作者草稿固定源码或历史无法读取。", "sourceRevision", "AuthorSourceInspectionReadFailed"); }
        finally { if (entered) gate.Release(); EndTemplateOperation(); }
    }

    private sealed class AuthorInspectionBudgetException : Exception { }
    // Count the exact Web JSON UTF-8 without retaining a second complete response buffer.
    private sealed class AuthorInspectionBudgetStream(long maximumBytes, CancellationToken token) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { token.ThrowIfCancellationRequested(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length > maximumBytes - _written) throw new AuthorInspectionBudgetException();
            _written += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
