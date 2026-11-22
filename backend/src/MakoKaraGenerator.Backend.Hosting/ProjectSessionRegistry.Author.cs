using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>无 GUI 脚本配置作者工作流：固定包、源码修订、独立编译和显式采用。</summary>
public sealed partial class ProjectSessionRegistry
{
    private readonly object _authorConfigurationGate = new();
    private readonly SemaphoreSlim _authorStorageGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _authorDraftLocks = new(StringComparer.Ordinal);
    private string? _authorRoot;
    private IScriptCompilationService? _authorCompiler;
    private CancellationTokenSource? _authorStop;
    private BackendAuthorCompilationResultStore? _authorCompilationResults;
    private int _authorStopping;
    private const int MaxAuthorDraftsPerClient = 16;
    private const long MaxAuthorStorageBytesPerClient = 256L * 1024 * 1024;
    private const int MaxCompilationDiagnostics = 256;
    private const int MaxCompilationResponseBytes = 48 * 1024;
    private static readonly JsonSerializerOptions AuthorJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record AuthorDraftDocument(
        string DraftId,
        string OwnerClientId,
        string Name,
        string PackageId,
        string BaseSourceRevision,
        string CurrentSourceRevision,
        string ExportId,
        string? AdoptedSourceRevision,
        string DraftRevision,
        AuthorCompilationSummaryDto? LastCompilation,
        AuthorCompilationDiagnosticDto[]? LastCompilationDiagnostics = null,
        AuthorDraftConfigurationDocument? Configuration = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SchemaVersion = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnershipRevision = null);

    private sealed record AuthorDraftConfigurationDocument(
        string Name,
        string PackageId,
        string SourceRevision,
        string ExportId,
        string ParametersJson,
        string RelativeTarget,
        string ReadScope,
        string? BranchInput,
        string? FixedSeed,
        string OverlayLayer);

    /// <summary>配置作者草稿根和离线编译参考集；只由可信组合根调用一次。</summary>
    public void ConfigureAuthorWorkflows(string? referenceDirectory, IScriptCompilationService? compilationService = null)
    {
        lock (_authorConfigurationGate)
        {
            if (_authorRoot is not null) throw new InvalidOperationException("作者工作流已经配置。");
            if (_persistence is null) throw new InvalidOperationException("作者工作流需要先配置持久profile。");
            _authorRoot = Path.Combine(_persistence.StateRoot, "author-drafts");
            Directory.CreateDirectory(_authorRoot);
            _authorCompilationResults = new BackendAuthorCompilationResultStore(_persistence.StateRoot, long.MaxValue);
            _authorStop = new CancellationTokenSource();
            Volatile.Write(ref _authorStopping, 0);
            _authorCompiler = compilationService;
            if (_authorCompiler is null && !string.IsNullOrWhiteSpace(referenceDirectory) && Directory.Exists(referenceDirectory))
            {
                try { _authorCompiler = new ScriptCompiler(referenceDirectory, Path.Combine(_persistence.StateRoot, "script-artifacts")); }
                catch (ArgumentException) { _authorCompiler = null; }
            }
        }
    }

    /// <summary>读取当前客户端拥有的作者草稿。</summary>
    public async Task<BackendOperationResult<AuthorDraftResponse>> ReadAuthorDraftAsync(string clientId, string draftId,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
            return BackendOperationResult<AuthorDraftResponse>.Rejected(error!);
        using CancellationTokenSource? linked = CreateAuthorReadTokenSource(cancellationToken, out CancellationToken token);
        try { return BackendOperationResult<AuthorDraftResponse>.Success(await ToAuthorResponseAsync(document!, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
        catch (EffectPackageException packageError)
        { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.CommandRejected, packageError.Message, packageError.Path, packageError.Code); }
        catch (ArgumentException argumentError)
        { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ProjectLoadFailed, argumentError.Message, "configuration", "AuthorConfigurationInvalid"); }
        catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ProjectLoadFailed, "作者草稿配置或固定包无法读取。", "draftId", "AuthorDraftStorageFailed"); }
    }

    /// <summary>读取当前客户端拥有的全部可恢复作者草稿摘要。</summary>
    public async Task<BackendOperationResult<AuthorDraftListResponse>> ReadAuthorDraftsAsync(string clientId,
        CancellationToken cancellationToken = default)
    {
        if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<AuthorDraftListResponse>.Rejected(unavailable!);
        using CancellationTokenSource? linked = CreateAuthorReadTokenSource(cancellationToken, out CancellationToken token);
        List<AuthorDraftResponse> drafts = [];
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_authorRoot!, "draft-*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string draftId = Path.GetFileName(directory);
                if (!ValidDraftId(draftId)) continue;
                if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
                {
                    if (error?.Code == BackendErrorCode.ResourceUnauthorized) continue;
                    return BackendOperationResult<AuthorDraftListResponse>.Rejected(error!);
                }
                drafts.Add(await ToAuthorResponseAsync(document!, token).ConfigureAwait(false));
            }
            return BackendOperationResult<AuthorDraftListResponse>.Success(new([.. drafts]));
        }
        catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        { return AuthorReject<AuthorDraftListResponse>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
        catch (EffectPackageException packageError)
        { return AuthorReject<AuthorDraftListResponse>(BackendErrorCode.CommandRejected, packageError.Message, packageError.Path, packageError.Code); }
        catch (ArgumentException argumentError)
        { return AuthorReject<AuthorDraftListResponse>(BackendErrorCode.ProjectLoadFailed, argumentError.Message, "configuration", "AuthorConfigurationInvalid"); }
        catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return AuthorReject<AuthorDraftListResponse>(BackendErrorCode.ProjectLoadFailed, "作者草稿列表无法读取。", "drafts", "AuthorDraftStorageFailed"); }
    }

    /// <summary>读取精确草稿修订下的固定 C# 文件集合；旧修订只要闭包仍保留即可读取。</summary>
    public async Task<BackendOperationResult<AuthorDraftSourcesResponse>> ReadAuthorSourcesAsync(string clientId, string draftId,
        string expectedDraftRevision, string sourceRevision, CancellationToken cancellationToken = default)
    {
        if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
            return BackendOperationResult<AuthorDraftSourcesResponse>.Rejected(error!);
        if (!ValidRevision(expectedDraftRevision)) return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
        if (!ValidRevision(sourceRevision)) return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
        if (!string.Equals(document!.DraftRevision, expectedDraftRevision, StringComparison.Ordinal))
            return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
        using CancellationTokenSource? linked = CreateAuthorReadTokenSource(cancellationToken, out CancellationToken token);
        try
        {
            EffectPackageContents package = await LoadPackageAsync(document.DraftId, sourceRevision, token).ConfigureAwait(false);
            AuthorSourceFileDto[] files = [.. package.Files.Where(file => file.Role == "csharp")
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new AuthorSourceFileDto(file.Path, file.ReadUtf8()))];
            if (files.Length == 0) return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.ResourceNotFound, "源码修订不包含 C# 文件。", "sourceRevision");
            return BackendOperationResult<AuthorDraftSourcesResponse>.Success(new(document.DraftId, document.DraftRevision,
                sourceRevision, package.PackageId, package.Revision, files));
        }
        catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        { return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
        catch (DirectoryNotFoundException)
        { return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "sourceRevision"); }
        catch (EffectPackageException packageError)
        { return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.CommandRejected, packageError.Message, packageError.Path, packageError.Code); }
        catch (DecoderFallbackException)
        { return AuthorReject<AuthorDraftSourcesResponse>(BackendErrorCode.ProjectLoadFailed, "源码修订不是合法 UTF-8。", "sourceRevision", "AuthorSourceEncodingInvalid"); }
    }

    /// <summary>按精确草稿修订保存配置；只更新作者草稿，不写配置库或工程。</summary>
    public Task<BackendOperationResult<AuthorDraftResponse>> SaveAuthorConfigurationAsync(string clientId,
        string draftId, AuthorDraftConfigurationRequest request, CancellationToken cancellationToken = default)
        => RunAuthorMutationAsync(clientId, draftId, request.Metadata, "PUT /api/v1/drafts/configuration", request with { Metadata = null }, async document =>
        {
            if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (request.Configuration is null)
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "必须提供完整作者配置。", "configuration");
            try
            {
                AuthorDraftConfigurationDocument configuration = NormalizeConfiguration(document, request.Configuration);
                EffectPackageContents package = await LoadPackageAsync(document.DraftId, configuration.SourceRevision,
                    _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                AuthorDraftConfigurationDocument previous = document.Configuration ?? DefaultConfiguration(document);
                if (configuration.SourceRevision != previous.SourceRevision && !CanAdoptAuthorSource(document, package))
                    return AuthorReject<AuthorDraftResponse>(BackendErrorCode.CommandRejected,
                        "SDK2 源码修订须先编译通过，再明确采用。", "configuration.sourceRevision", "AuthorSourceCompilationRequired");
                _ = BuildConfiguration(package, configuration);
                AuthorDraftDocument updatedBase = document with { Configuration = configuration, Name = configuration.Name, ExportId = configuration.ExportId };
                AuthorDraftDocument updated = updatedBase with { DraftRevision = ComputeDraftRevision(updatedBase, null) };
                await _authorStorageGate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!TryPersistAuthorDocument(updated))
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "configuration");
                }
                finally { _authorStorageGate.Release(); }
                return BackendOperationResult<AuthorDraftResponse>.Success(
                    await ToAuthorResponseAsync(updated, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false));
            }
            catch (DirectoryNotFoundException)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "configuration.sourceRevision"); }
            catch (EffectPackageException packageError)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, packageError.Message, packageError.Path, packageError.Code); }
            catch (ArgumentException argumentError)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, argumentError.Message, "configuration", "AuthorConfigurationInvalid"); }
        }, cancellationToken);

    /// <summary>读取最近一次编译的完整诊断；诊断与固定源码修订一起持久化。</summary>
    public BackendOperationResult<AuthorCompilationResponse> ReadAuthorCompilation(string clientId, string draftId)
    {
        if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
            return BackendOperationResult<AuthorCompilationResponse>.Rejected(error!);
        if (document!.LastCompilation is null)
            return BackendOperationResult<AuthorCompilationResponse>.Rejected(new(BackendErrorCode.ResourceNotFound, "作者草稿尚无编译结果。", "compilation"));
        AuthorCompilationSummaryDto summary = document.LastCompilation;
        return BackendOperationResult<AuthorCompilationResponse>.Success(new(document.DraftId, document.DraftRevision,
            summary.SourceRevision, summary.Succeeded, summary.SourceRevision != document.CurrentSourceRevision, summary.ArtifactHash,
            document.LastCompilationDiagnostics ?? [], BackendProtocol.Version));
    }

    /// <summary>从当前客户端授权的固定包资源建立作者草稿。</summary>
    public Task<BackendOperationResult<AuthorDraftResponse>> CreateAuthorDraftAsync(string clientId,
        AuthorDraftCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Receipts.ExecuteAsync(clientId, request.Metadata, "POST /api/v1/drafts", request with { Metadata = null },
            () => ExecuteCreateAsync(_authorStop?.Token ?? CancellationToken.None), cancellationToken);

        async Task<BackendOperationResult<AuthorDraftResponse>> ExecuteCreateAsync(CancellationToken lifetime)
        {
            if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<AuthorDraftResponse>.Rejected(unavailable!);
            if (request.ConfigurationId is not null)
                return await CreateAuthorDraftFromConfigurationAsync(clientId, request, lifetime).ConfigureAwait(false);
            if (request.ExpectedConfigurationRevision is not null)
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "配置库修订必须与configurationId一起使用。", "expectedConfigurationRevision");
            if (string.IsNullOrWhiteSpace(request.PackageAssetId)) return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "必须提供效果包资源 ID。", "packageAssetId");
            if (string.IsNullOrWhiteSpace(request.ExportId)) return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "必须提供导出身份。", "exportId");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256) return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "草稿名称不能为空且不得超过256字符。", "name");
            try
            {
                using IDisposable pin = _resources.AcquireReference(clientId, request.PackageAssetId);
                StoredAsset asset = _resources.GetAuthorized(clientId, request.PackageAssetId);
                if (asset.Size > 64L * 1024 * 1024) return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceTooLarge, "效果包归档超过64MiB。", "packageAssetId");
                byte[] bytes = await ReadAssetBytesAsync(asset, lifetime).ConfigureAwait(false);
                string temp = Path.Combine(_authorRoot!, "." + Guid.NewGuid().ToString("N") + ".mkfx");
                try
                {
                    await File.WriteAllBytesAsync(temp, bytes, lifetime).ConfigureAwait(false);
                    EffectPackageContents package = await EffectPackageTransfer.ReadSupportedArchiveAsync(temp, cancellationToken: lifetime).ConfigureAwait(false);
                    _ = EffectPackageDefinition.Read(package, lifetime).GetExport(request.ExportId);
                    string draftId = "draft-" + Guid.NewGuid().ToString("N");
                    await _authorStorageGate.WaitAsync(lifetime).ConfigureAwait(false);
                    try
                    {
                        lifetime.ThrowIfCancellationRequested();
                        if (GetAuthorUsage(clientId).Drafts >= MaxAuthorDraftsPerClient)
                            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.RequestQuotaExceeded, "客户端作者草稿数量达到上限。", "drafts");
                        AuthorDraftDocument document = CreateDocument(draftId, clientId, request.Name.Trim(), package, request.ExportId);
                        if (!TryPersistAuthorDocument(document, package))
                            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "drafts");
                        return BackendOperationResult<AuthorDraftResponse>.Success(
                            await ToAuthorResponseAsync(document, lifetime).ConfigureAwait(false));
                    }
                    finally { _authorStorageGate.Release(); }
                }
                finally { TryDelete(temp); }
            }
            catch (ResourceStoreException error)
            { return AuthorReject<AuthorDraftResponse>(error.Code, error.Message, error.Field); }
            catch (EffectPackageException error)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
            catch (ArgumentException error)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.CommandRejected, error.Message, "exportId", "AuthorExportInvalid"); }
            catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ProjectLoadFailed, "效果包无法固定保存。", "packageAssetId", "AuthorPackagePersistFailed"); }
        }
    }

    /// <summary>以精确草稿修订提交完整 C# 源码集合并固定新的包修订。</summary>
    public Task<BackendOperationResult<AuthorSourceRevisionResponse>> SaveAuthorSourceRevisionAsync(string clientId,
        string draftId, AuthorSourceRevisionRequest request, CancellationToken cancellationToken = default)
        => RunAuthorMutationAsync(clientId, draftId, request.Metadata, "POST /api/v1/drafts/source-revision-jobs", request with { Metadata = null }, async document =>
        {
            if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorSourceRevisionResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (request.Files is null || request.Files.Length is < 1 or > 256)
                return AuthorReject<AuthorSourceRevisionResponse>(BackendErrorCode.InvalidRequest, "必须提交完整 C# 源码文件集合。", "files");
            EffectPackageContents basePackage = await LoadPackageAsync(document.DraftId, document.CurrentSourceRevision, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            Dictionary<string, EffectPackageFile> declared = basePackage.Files.Where(file => file.Role == "csharp").ToDictionary(file => file.Path, StringComparer.Ordinal);
            if (declared.Count != request.Files.Length || request.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path)
                || file.Text is null || !declared.ContainsKey(file.Path)) || request.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != request.Files.Length)
                return AuthorReject<AuthorSourceRevisionResponse>(BackendErrorCode.InvalidRequest, "files 必须一次提交当前包的全部且仅全部 C# 文件。", "files");
            Dictionary<string, ReadOnlyMemory<byte>> files = basePackage.Files.ToDictionary(file => file.Path, file => (ReadOnlyMemory<byte>)file.Content.ToArray(), StringComparer.Ordinal);
            foreach (AuthorSourceFileDto source in request.Files)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(source.Text);
                if (bytes.Length > 4 * 1024 * 1024) return AuthorReject<AuthorSourceRevisionResponse>(BackendErrorCode.ResourceTooLarge, "单个 C# 源码文件超过4MiB。", "files");
                files[source.Path] = bytes;
            }
            if (declared.All(pair => files[pair.Key].Span.SequenceEqual(pair.Value.Content.AsSpan())))
                return BackendOperationResult<AuthorSourceRevisionResponse>.Success(new(document.DraftId, document.DraftRevision,
                    document.PackageId, document.CurrentSourceRevision, [.. declared.Keys.Order(StringComparer.Ordinal)]));
            byte[] manifest = UpdateManifest(basePackage, files);
            EffectPackageContents package = EffectPackageContents.CreateSupported(manifest, files.Where(pair => pair.Key != "manifest.json"));
            await _authorStorageGate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            try
            {
                AuthorDraftConfigurationDocument? configuration = document.Configuration ?? DefaultConfiguration(document);
                AuthorDraftDocument updatedBase = document with
                {
                    CurrentSourceRevision = package.Revision,
                    Configuration = configuration,
                    LastCompilation = document.LastCompilation is { } previousCompilation
                        ? previousCompilation with { Stale = previousCompilation.SourceRevision != package.Revision } : null,
                };
                AuthorDraftDocument updated = updatedBase with { DraftRevision = ComputeDraftRevision(updatedBase, null) };
                if (!TryPersistAuthorDocument(updated, package))
                    return AuthorReject<AuthorSourceRevisionResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "files");
                return BackendOperationResult<AuthorSourceRevisionResponse>.Success(new(updated.DraftId, updated.DraftRevision,
                    updated.PackageId, updated.CurrentSourceRevision, [.. declared.Keys.Order(StringComparer.Ordinal)]));
            }
            finally { _authorStorageGate.Release(); }
        }, cancellationToken);

    /// <summary>独立编译指定保留源码修订，省略时编译最新修订，并持久化最近结果摘要。</summary>
    public Task<BackendOperationResult<AuthorCompilationResponse>> CompileAuthorDraftAsync(string clientId,
        string draftId, AuthorCompilationRequest request, CancellationToken cancellationToken = default)
        => RunAuthorMutationAsync(clientId, draftId, request.Metadata, "POST /api/v1/drafts/compilation-jobs", request with { Metadata = null }, async document =>
        {
            if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (_authorCompiler is null) return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.TaskUnavailable, "脚本编译参考集未部署。", "compiler");
            string sourceRevision = request.SourceRevision ?? document.CurrentSourceRevision;
            if (!ValidRevision(sourceRevision))
                return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
            if (!ReadAuthorSourceHistory(document).Revisions.Any(revision => revision.SourceRevision == sourceRevision))
                return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.ResourceNotFound, "源码修订不属于此草稿的保留历史。", "sourceRevision");
            EffectPackageContents package = await LoadPackageAsync(document.DraftId, sourceRevision, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            string artifactHash = "none";
            AuthorCompilationDiagnosticDto[] diagnostics;
            bool succeeded;
            try
            {
                await PackageAuthorResourceValidator.ValidateSvgAsync(package, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                EffectPackageDefinition definition = EffectPackageDefinition.Read(package, _authorStop?.Token ?? CancellationToken.None);
                ScriptCompilationResult result = await _authorCompiler.CompileAsync(EffectPackageSources.Read(package),
                    definition.Controllers.Select(controller => controller.EntryType), _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                ValidateAuthorCompilationEntries(definition, result);
                artifactHash = result.ArtifactHash;
                diagnostics = [.. result.Diagnostics.Select(diagnostic => new AuthorCompilationDiagnosticDto(diagnostic.Code, diagnostic.Message,
                    diagnostic.IsError, diagnostic.Path, diagnostic.Line, diagnostic.Column))];
                succeeded = result.Succeeded;
            }
            catch (EffectPackageException error)
            {
                diagnostics = [new(error.Code, error.Message, true, error.Path, 0, 0)];
                succeeded = false;
            }
            DateTimeOffset completed = ServerTimeUtc;
            AuthorCompilationResponse response = new(document.DraftId, document.DraftRevision,
                sourceRevision, succeeded, sourceRevision != document.CurrentSourceRevision, artifactHash, diagnostics);
            if (diagnostics.Length > MaxCompilationDiagnostics
                || JsonSerializer.SerializeToUtf8Bytes(response, AuthorJson).Length > MaxCompilationResponseBytes)
                return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.ResourceTooLarge, "编译完整响应超过作者回执预算。", "diagnostics");
            AuthorCompilationSummaryDto summary = new(sourceRevision, succeeded, sourceRevision != document.CurrentSourceRevision, artifactHash, diagnostics.Length, completed);
            AuthorDraftDocument updated = document with { LastCompilation = summary, LastCompilationDiagnostics = diagnostics };
            await _authorStorageGate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!TryPersistAuthorDocument(updated))
                    return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "diagnostics");
            }
            finally { _authorStorageGate.Release(); }
            return BackendOperationResult<AuthorCompilationResponse>.Success(response);
        }, cancellationToken);

    /// <summary>异步编译固定作者源码；任务输入不随草稿后续修订改变。</summary>
    public Task<BackendOperationResult<JobResponse>> StartAuthorCompilationAsync(string clientId, string draftId,
        AuthorCompilationRequest request, CancellationToken cancellationToken = default)
        => RunAuthorMutationAsync(clientId, draftId, request.Metadata, "POST /api/v1/drafts/compilation-jobs", request with { Metadata = null }, async document =>
        {
            if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<JobResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (_authorCompiler is null) return AuthorReject<JobResponse>(BackendErrorCode.TaskUnavailable, "脚本编译参考集未部署。", "compiler");
            string sourceRevision = request.SourceRevision ?? document.CurrentSourceRevision;
            if (!ValidRevision(sourceRevision))
                return AuthorReject<JobResponse>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
            if (!ReadAuthorSourceHistory(document).Revisions.Any(revision => revision.SourceRevision == sourceRevision))
                return AuthorReject<JobResponse>(BackendErrorCode.ResourceNotFound, "源码修订不属于此草稿的保留历史。", "sourceRevision");
            EffectPackageContents package = await LoadPackageAsync(document.DraftId, sourceRevision,
                _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            AuthorCompilationJobInputDto input = new(document.DraftId, document.DraftRevision, sourceRevision);
            lock (_jobGate) EnsureTaskScheduler();
            BackendTaskScheduler.Reservation? slot = _taskScheduler!.TryReserve(clientId, "author-draft:" + draftId);
            if (slot is null) return AuthorReject<JobResponse>(BackendErrorCode.RequestQuotaExceeded, "作者编译任务排队配额已满。");
            bool transferred = false;
            try
            {
                PruneAuthorCompilationResults();
                BackendOperationResult<JobResponse> accepted = StartJob(clientId, "author-compilation", null,
                    job => RunAuthorCompilationJobAsync(clientId, new(input, package, document.CurrentSourceRevision, slot), job),
                    cancelOnShutdown: true, deferRunning: true, authorCompilationInput: input);
                transferred = accepted.Accepted;
                return accepted;
            }
            finally { if (!transferred) slot.Dispose(); }
        }, cancellationToken);

    private sealed record AuthorCompilationWork(AuthorCompilationJobInputDto Input, EffectPackageContents Package, string LatestSourceRevision,
        BackendTaskScheduler.Reservation Slot);

    private async Task RunAuthorCompilationJobAsync(string clientId, AuthorCompilationWork work, BackendJob job)
    {
        try
        {
            await work.Slot.WaitAsync(job.Cancellation).ConfigureAwait(false);
            lock (_jobGate)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.SetState("running", true);
            }
            AuthorCompilationResponse response = await CompileFixedAuthorSourceAsync(work.Input, work.Package, job.Cancellation).ConfigureAwait(false);
            response = response with { Stale = response.SourceRevision != work.LatestSourceRevision };
            if (_authorCompilationResults is null) throw new IOException("作者编译结果存储未配置。");
            lock (_jobGate)
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
            }
            SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(work.Input.DraftId, static _ => new(1, 1));
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (TryReadAuthorDraft(clientId, work.Input.DraftId, out AuthorDraftDocument? current, out _))
                {
                    response = response with { Stale = response.SourceRevision != current!.CurrentSourceRevision };
                    AuthorCompilationSummaryDto summary = new(response.SourceRevision, response.Succeeded, response.Stale, response.ArtifactHash,
                        response.Diagnostics.Length, ServerTimeUtc);
                    bool sameDraft = current.DraftRevision == work.Input.DraftRevision;
                    AuthorDraftDocument updated = sameDraft
                        ? current with { LastCompilation = summary, LastCompilationDiagnostics = response.Diagnostics }
                        : current;
                    await _authorStorageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (!TryPersistAuthorDocument(updated, compilation: summary))
                            throw new IOException("作者编译历史超过草稿存储预算。");
                    }
                    finally { _authorStorageGate.Release(); }
                }
            }
            finally { gate.Release(); }
            lock (_jobGate)
            {
                BackendJobReportReferenceDto reference = _authorCompilationResults.Publish(job.Id, response);
                job.Finish(null, authorCompilationResult: reference);
            }
        }
        finally
        {
            work.Slot.Dispose();
        }
    }

    private async Task<AuthorCompilationResponse> CompileFixedAuthorSourceAsync(AuthorCompilationJobInputDto input, EffectPackageContents package, CancellationToken cancellationToken)
    {
        string artifactHash = "none";
        AuthorCompilationDiagnosticDto[] diagnostics;
        bool succeeded;
        try
        {
            await PackageAuthorResourceValidator.ValidateSvgAsync(package, cancellationToken).ConfigureAwait(false);
            EffectPackageDefinition definition = EffectPackageDefinition.Read(package, cancellationToken);
            ScriptCompilationResult result = await _authorCompiler!.CompileAsync(EffectPackageSources.Read(package),
                definition.Controllers.Select(controller => controller.EntryType), cancellationToken).ConfigureAwait(false);
            ValidateAuthorCompilationEntries(definition, result);
            artifactHash = result.ArtifactHash;
            diagnostics = [.. result.Diagnostics.Select(diagnostic => new AuthorCompilationDiagnosticDto(diagnostic.Code, diagnostic.Message,
                diagnostic.IsError, diagnostic.Path, diagnostic.Line, diagnostic.Column))];
            succeeded = result.Succeeded;
        }
        catch (EffectPackageException error)
        {
            diagnostics = [new(error.Code, error.Message, true, error.Path, 0, 0)];
            succeeded = false;
        }
        AuthorCompilationResponse response = new(input.DraftId, input.DraftRevision, input.SourceRevision, succeeded, false, artifactHash, diagnostics);
        if (diagnostics.Length > MaxCompilationDiagnostics || JsonSerializer.SerializeToUtf8Bytes(response, AuthorJson).Length > MaxCompilationResponseBytes)
            throw new AuthorCompilationBudgetException();
        return response;
    }

    public BackendOperationResult<AuthorCompilationResponse> ReadAuthorCompilationResult(string clientId, string jobId, bool recovered = false)
    {
        PruneAuthorCompilationResults();
        JobResponse? response = recovered
            ? _ledger?.ReadRecovered(clientId).FirstOrDefault(value => value.Job.JobId == jobId)?.Job
            : TryFindJob(clientId, jobId, out BackendJob current) ? current.Read() : null;
        if (response is null || response.Kind != "author-compilation" || response.AuthorCompilationResult is null || _authorCompilationResults is null)
            return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.JobNotFound, "任务不存在或尚无作者编译结果。");
        try
        {
            AuthorCompilationResponse result = _authorCompilationResults.Read(jobId, response.AuthorCompilationResult);
            if (result.DraftId != response.AuthorCompilationInput?.DraftId || result.DraftRevision != response.AuthorCompilationInput?.DraftRevision
                || result.SourceRevision != response.AuthorCompilationInput?.SourceRevision)
                throw new IOException("作者编译结果固定身份不一致。");
            return BackendOperationResult<AuthorCompilationResponse>.Success(result);
        }
        catch (IOException) { return AuthorReject<AuthorCompilationResponse>(BackendErrorCode.TaskExecutionFailed, "作者编译完整结果不可用。", "jobId"); }
    }

    private void PruneAuthorCompilationResults()
    {
        if (_authorCompilationResults is null) return;
        lock (_jobGate)
        {
            IEnumerable<JobResponse> current = _jobs.Values.Select(value => value.Read()).Where(value => value.ExpiresAtUtc > _timeProvider.GetUtcNow());
            IEnumerable<JobResponse> recovered = _ledger?.ReadRecovered().Select(value => value.Job) ?? [];
            _authorCompilationResults.Prune(current.Concat(recovered).Where(value => value.AuthorCompilationResult is not null)
                .Select(value => value.AuthorCompilationResult!.ReportId));
        }
    }

    /// <summary>显式将已有包修订绑定到当前作者配置；SDK2 C# 来源要求成功编译，不改个人库。</summary>
    public Task<BackendOperationResult<AuthorAdoptSourceRevisionResponse>> AdoptAuthorSourceRevisionAsync(string clientId,
        string draftId, AuthorAdoptSourceRevisionRequest request, CancellationToken cancellationToken = default)
        => RunAuthorMutationAsync(clientId, draftId, request.Metadata, "POST /api/v1/drafts/adopt-source-revision", request with { Metadata = null }, async document =>
        {
            if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (!ValidRevision(request.SourceRevision))
                return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
            try
            {
                if (!ReadAuthorSourceHistory(document).Revisions.Any(item => item.SourceRevision == request.SourceRevision))
                    return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.ResourceNotFound, "源码修订不属于此草稿保留历史。", "sourceRevision");
                EffectPackageContents package = await LoadPackageAsync(document.DraftId, request.SourceRevision, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                if (package.PackageId != document.PackageId || package.Revision != request.SourceRevision)
                    return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.ProjectLoadFailed,
                        "固定源码包身份不一致。", "sourceRevision", "AuthorSourceIdentityMismatch");
                if (!CanAdoptAuthorSource(document, package))
                    return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.CommandRejected,
                        "SDK2 源码修订须先编译通过，再明确采用；可在源码页查看并编译该修订。", "sourceRevision", "AuthorSourceCompilationRequired");
                AuthorDraftConfigurationDocument previousConfiguration = document.Configuration ?? DefaultConfiguration(document);
                AuthorDraftConfigurationDocument configuration = request.Configuration is { } input
                    ? NormalizeAdoptionConfiguration(document, request.SourceRevision, input)
                    : previousConfiguration with { SourceRevision = request.SourceRevision };
                // Full sample validation must precede every persistent state change.
                _ = BuildSampleOptions(BuildAdoptionConfiguration(package, configuration));
                AuthorDraftDocument adopted = document with
                {
                    AdoptedSourceRevision = request.SourceRevision,
                    Configuration = configuration,
                    Name = configuration.Name,
                    ExportId = configuration.ExportId,
                };
                AuthorDraftDocument updated = adopted with { DraftRevision = ComputeDraftRevision(adopted, null) };
                await _authorStorageGate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (updated != document && !TryPersistAuthorDocument(updated))
                        return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "sourceRevision");
                }
                finally { _authorStorageGate.Release(); }
                return BackendOperationResult<AuthorAdoptSourceRevisionResponse>.Success(new(updated.DraftId, updated.DraftRevision,
                    updated.PackageId, previousConfiguration.SourceRevision, request.SourceRevision, updated.ExportId));
            }
            catch (AuthorAdoptionConfigurationException error)
            { return AuthorReject<AuthorAdoptSourceRevisionResponse>(error.ErrorCode, error.Message, error.Field, error.FailureCode); }
            catch (DirectoryNotFoundException)
            { return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "sourceRevision"); }
            catch (EffectPackageException error)
            { return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
            catch (ArgumentException error)
            { return AuthorReject<AuthorAdoptSourceRevisionResponse>(BackendErrorCode.InvalidRequest, error.Message, "configuration", "AuthorConfigurationInvalid"); }
        }, cancellationToken);

    /// <summary>显式删除作者草稿，释放固定包存储和客户端回收保护。</summary>
    public Task<BackendOperationResult<AuthorDraftDeleteResponse>> DeleteAuthorDraftAsync(string clientId,
        string draftId, AuthorDraftDeleteRequest request, CancellationToken cancellationToken = default)
        => RunAuthorMutationAsync(clientId, draftId, request.Metadata, "DELETE /api/v1/drafts", request with { Metadata = null }, async document =>
        {
            if (!string.Equals(document.DraftRevision, request.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorDraftDeleteResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            await _authorStorageGate.WaitAsync(_authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            try { Directory.Delete(Path.Combine(_authorRoot!, document.DraftId), recursive: true); }
            finally { _authorStorageGate.Release(); }
            return BackendOperationResult<AuthorDraftDeleteResponse>.Success(new(document.DraftId, document.DraftRevision));
        }, cancellationToken);

    private async Task<BackendOperationResult<T>> RunAuthorMutationAsync<T, TRequest>(string clientId, string draftId,
        RequestMetadataDto? metadata, string operation, TRequest request, Func<AuthorDraftDocument, Task<BackendOperationResult<T>>> action,
        CancellationToken cancellationToken)
    {
        if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<T>.Rejected(unavailable!);
        CancellationToken linked = _authorStop?.Token ?? CancellationToken.None;
        return await Receipts.ExecuteAsync(clientId, metadata, operation + ":" + draftId, request, async () =>
        {
            if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
                return BackendOperationResult<T>.Rejected(error!);
            SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
            bool entered = false;
            try
            {
                await gate.WaitAsync(linked).ConfigureAwait(false);
                entered = true;
                linked.ThrowIfCancellationRequested();
                if (!TryReadAuthorDraft(clientId, draftId, out document, out error)) return BackendOperationResult<T>.Rejected(error!);
                return await action(document!).ConfigureAwait(false);
            }
            catch (EffectPackageException packageError)
            { return AuthorReject<T>(BackendErrorCode.CommandRejected, packageError.Message, packageError.Path, packageError.Code); }
            catch (ArgumentException argumentError)
            { return AuthorReject<T>(BackendErrorCode.InvalidRequest, argumentError.Message, "draftId"); }
            catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true)
            { return AuthorReject<T>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
            catch (JsonException)
            { return AuthorReject<T>(BackendErrorCode.ProjectLoadFailed, "作者源码历史无法读取。", "sourceRevisions", "AuthorSourceHistoryInvalid"); }
            catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
            { return AuthorReject<T>(BackendErrorCode.ProjectLoadFailed, "作者草稿持久化或读取失败。", "draftId", "AuthorDraftStorageFailed"); }
            finally { if (entered) gate.Release(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool AuthorReady(out BackendErrorResponse? error)
    {
        error = _authorRoot is null ? new(BackendErrorCode.TaskUnavailable, "作者工作流尚未配置。", "authorWorkflows")
            : Volatile.Read(ref _authorStopping) != 0 ? new(BackendErrorCode.SessionClosing, "作者工作流正在停止。", "authorWorkflows") : null;
        return error is null;
    }

    private bool TryReadAuthorDraft(string? clientId, string draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error)
    {
        document = null; error = null;
        if (_authorRoot is null) { error = new(BackendErrorCode.TaskUnavailable, "作者工作流尚未配置。", "authorWorkflows"); return false; }
        if (!ValidDraftId(draftId))
        { error = new(BackendErrorCode.InvalidRequest, "草稿身份无效。", "draftId"); return false; }
        string path = DocumentPath(draftId);
        if (!File.Exists(path)) { error = new(BackendErrorCode.ResourceNotFound, "作者草稿不存在。", "draftId"); return false; }
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("draft must be an object");
            bool hasSchema = json.RootElement.TryGetProperty("schemaVersion", out _);
            document = JsonSerializer.Deserialize<AuthorDraftDocument>(bytes, AuthorJson);
            if (document is null || document.DraftId != draftId) throw new IOException("draft identity mismatch");
            if ((hasSchema && (document.SchemaVersion != 1 || !ValidRevision(document.OwnershipRevision)))
                || (!hasSchema && (document.SchemaVersion is not null || document.OwnershipRevision is not null))
                || string.IsNullOrWhiteSpace(document.OwnerClientId)) throw new JsonException("unsupported draft schema");
            if (!ValidRevision(document.DraftRevision) || !ValidRevision(document.BaseSourceRevision)
                || !ValidRevision(document.CurrentSourceRevision)
                || (document.AdoptedSourceRevision is not null && !ValidRevision(document.AdoptedSourceRevision)))
                throw new JsonException("draft revision identity mismatch");
            if (clientId is not null && !string.Equals(document.OwnerClientId, clientId, StringComparison.Ordinal))
            { document = null; error = new(BackendErrorCode.ResourceUnauthorized, "作者草稿不属于当前客户端。", "draftId"); return false; }
            return true;
        }
        catch (JsonException)
        { error = new(BackendErrorCode.ProjectLoadFailed, "作者草稿状态无效。", "draftId", "AuthorDraftStateInvalid"); return false; }
        catch (IOException)
        { error = new(BackendErrorCode.ProjectLoadFailed, "作者草稿状态无法读取。", "draftId", "AuthorDraftStateUnreadable"); return false; }
    }

    private CancellationTokenSource? CreateAuthorReadTokenSource(CancellationToken cancellationToken, out CancellationToken token)
    {
        CancellationToken stop = _authorStop?.Token ?? CancellationToken.None;
        if (!cancellationToken.CanBeCanceled)
        {
            token = stop;
            return null;
        }
        if (!stop.CanBeCanceled)
        {
            token = cancellationToken;
            return null;
        }
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
        token = linked.Token;
        return linked;
    }

    private async Task<AuthorDraftResponse> ToAuthorResponseAsync(AuthorDraftDocument document,
        CancellationToken cancellationToken = default)
    {
        AuthorDraftConfigurationDto? configuration = null;
        AuthorDraftSampleOptionsDto? samples = null;
        AuthorDraftConfigurationDocument saved = document.Configuration ?? DefaultConfiguration(document);
        EffectPackageContents package = await LoadPackageAsync(document.DraftId, saved.SourceRevision,
            cancellationToken).ConfigureAwait(false);
        ScriptConfiguration script = BuildConfiguration(package, saved);
        configuration = ToConfigurationDto(script);
        samples = BuildSampleOptions(script);
        EffectPackageContents currentPackage = string.Equals(saved.SourceRevision, document.CurrentSourceRevision, StringComparison.Ordinal)
            ? package : await LoadPackageAsync(document.DraftId, document.CurrentSourceRevision,
                cancellationToken).ConfigureAwait(false);
        return new(document.DraftId, document.DraftRevision, document.Name, document.PackageId, document.BaseSourceRevision,
            document.CurrentSourceRevision, document.ExportId, document.AdoptedSourceRevision,
            _authorCompiler is not null && currentPackage.Files.Any(file => file.Role == "csharp"), true,
            document.LastCompilation is { } compilation
                ? compilation with { Stale = compilation.SourceRevision != document.CurrentSourceRevision } : null,
            BackendProtocol.Version, configuration, samples, package.Profile.PackageVersion, package.Profile.SdkVersion);
    }

    private static AuthorDraftConfigurationDocument NormalizeConfiguration(AuthorDraftDocument document,
        AuthorDraftConfigurationInputDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 256)
            throw new ArgumentException("配置名称不能为空且不得超过256字符。", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PackageId) || string.IsNullOrWhiteSpace(input.SourceRevision)
            || string.IsNullOrWhiteSpace(input.ExportId)) throw new ArgumentException("配置必须包含完整包和导出身份。", nameof(input));
        if (!ValidRevision(input.SourceRevision)) throw new ArgumentException("源码修订无效。", nameof(input));
        if (!string.Equals(document.PackageId, input.PackageId, StringComparison.Ordinal))
            throw new ArgumentException("配置包身份必须属于当前草稿。", nameof(input));
        if (!Enum.IsDefined(input.RelativeTarget) || !Enum.IsDefined(input.ReadScope) || !Enum.IsDefined(input.OverlayLayer))
            throw new ArgumentException("配置目标、读取范围或图元层无效。", nameof(input));
        if (input.ReadScope == TemplateScriptReadScopeDto.BranchInput
            ? string.IsNullOrWhiteSpace(input.BranchInput)
            : input.BranchInput is not null)
            throw new ArgumentException("分支读取范围必须提供branchInput，其他范围必须为null。", nameof(input));
        if (input.FixedSeed is not null && (!ulong.TryParse(input.FixedSeed, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || input.FixedSeed.Length == 0)) throw new ArgumentException("fixedSeed必须是UInt64十进制字符串或null。", nameof(input));
        _ = EffectParameterOverrides.Parse(input.ParametersJson);
        return new(input.Name.Trim(), input.PackageId, input.SourceRevision, input.ExportId, input.ParametersJson,
            input.RelativeTarget.ToString(), input.ReadScope.ToString(), input.BranchInput, input.FixedSeed,
            input.OverlayLayer switch
            {
                TemplateEffectOverlayLayerDto.Package => "Package",
                TemplateEffectOverlayLayerDto.BehindText => "BehindText",
                TemplateEffectOverlayLayerDto.AboveText => "AboveText",
                _ => throw new ArgumentException("图元层无效。", nameof(input)),
            });
    }

    private static AuthorDraftConfigurationDocument DefaultConfiguration(AuthorDraftDocument document) => new(document.Name,
        document.PackageId, document.CurrentSourceRevision, document.ExportId, EffectParameterOverrides.Empty.Json,
        nameof(ScriptRelativeTarget.Application), nameof(ScriptReadScope.BoundSources), null, null, nameof(EffectOverlayLayer.Package));

    private static ScriptConfiguration BuildConfiguration(EffectPackageContents package, AuthorDraftConfigurationDocument document)
    {
        if (!string.Equals(package.PackageId, document.PackageId, StringComparison.Ordinal)
            || !string.Equals(package.Revision, document.SourceRevision, StringComparison.Ordinal))
            throw new ArgumentException("配置固定包身份与源码修订不一致。", nameof(document));
        ulong? seed = document.FixedSeed is null ? null : ulong.Parse(document.FixedSeed, NumberStyles.None, CultureInfo.InvariantCulture);
        return ScriptConfiguration.Create("author-draft", document.Name, package, document.ExportId,
            EffectParameterOverrides.Parse(document.ParametersJson), Enum.Parse<ScriptRelativeTarget>(document.RelativeTarget),
            Enum.Parse<ScriptReadScope>(document.ReadScope), document.BranchInput, seed,
            Enum.Parse<EffectOverlayLayer>(document.OverlayLayer));
    }

    private static AuthorDraftConfigurationDto ToConfigurationDto(ScriptConfiguration configuration) => new(
        configuration.Name, configuration.Package.PackageId, configuration.Package.Revision, configuration.Package.Revision,
        configuration.ExportId, configuration.Parameters.Json, configuration.Target switch
        {
            ScriptRelativeTarget.Line => TemplateEffectTargetDto.Line,
            ScriptRelativeTarget.TokenFamily => TemplateEffectTargetDto.TokenFamily,
            _ => TemplateEffectTargetDto.Application,
        }, configuration.ReadScope switch
        {
            ScriptReadScope.Project => TemplateScriptReadScopeDto.Project,
            ScriptReadScope.BranchInput => TemplateScriptReadScopeDto.BranchInput,
            _ => TemplateScriptReadScopeDto.BoundSources,
        }, configuration.BranchInput, configuration.FixedSeed?.ToString(CultureInfo.InvariantCulture), configuration.OverlayLayer switch
        {
            EffectOverlayLayer.BehindText => TemplateEffectOverlayLayerDto.BehindText,
            EffectOverlayLayer.AboveText => TemplateEffectOverlayLayerDto.AboveText,
            _ => TemplateEffectOverlayLayerDto.Package,
        }, configuration.Kind.ToString(), [.. configuration.BindingKinds]);

    private static AuthorDraftSampleOptionsDto BuildSampleOptions(ScriptConfiguration configuration)
    {
        // Build targets through the same complete template projection used by
        // validation and preview.  Constructing an empty StyleTemplate here
        // loses material properties and can fail package-closure validation.
        TemplateMaterialSlot[] materialSlots = configuration.Kind == ScriptConfigurationKind.Material
            ? ScriptSampleBuilder.MaterialSlots(configuration)
            : [TemplateMaterialSlot.UnsungFill];
        TemplateSampleOptionDto[] samples = [.. new[] { (TemplatePreviewLimits.SingleLineSampleId, false), (TemplatePreviewLimits.MultiLineSampleId, true) }
            .Select(item =>
            {
                TemplateSampleTarget[] targets = [.. materialSlots.SelectMany(slot => ScriptSampleBuilder.Targets(configuration, slot, item.Item2))
                    .GroupBy(target => target.Key, StringComparer.Ordinal).Select(group => group.First())];
                return new TemplateSampleOptionDto(item.Item1, item.Item2, TemplateSampleScene.DurationUs.ToString(CultureInfo.InvariantCulture),
                    [.. targets.Select(target => new TemplateSampleTargetDto(target.Key,
                        new(BindingName(target.Binding.Kind), target.Binding.Id, [.. target.Binding.Ids], target.Binding.IncludeDescendants)))]);
            })];
        string[] slots = configuration.Kind == ScriptConfigurationKind.Material
            ? [.. ScriptSampleBuilder.MaterialSlots(configuration).Select(slot => slot switch
            {
                TemplateMaterialSlot.UnsungFill => "unsungFill", TemplateMaterialSlot.SungFill => "sungFill", _ => "stroke",
            })] : [];
        return new(samples, slots);
    }

    private static string BindingName(ProjectEffectBindingKind kind) => kind switch
    {
        ProjectEffectBindingKind.Project => "project", ProjectEffectBindingKind.Branch => "branch",
        ProjectEffectBindingKind.Lines => "lines", ProjectEffectBindingKind.Tokens => "tokens",
        ProjectEffectBindingKind.Clips => "clips", ProjectEffectBindingKind.Node => "node",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static AuthorDraftDocument CreateDocument(string draftId, string owner, string name, EffectPackageContents package, string exportId)
    {
        AuthorDraftConfigurationDocument configuration = new(name, package.PackageId, package.Revision, exportId,
            EffectParameterOverrides.Empty.Json, nameof(ScriptRelativeTarget.Application), nameof(ScriptReadScope.BoundSources), null, null,
            nameof(EffectOverlayLayer.Package));
        AuthorDraftDocument document = new(draftId, owner, name, package.PackageId, package.Revision, package.Revision, exportId, null, "", null, null, configuration, 1, NewOwnershipRevision());
        return document with { DraftRevision = ComputeDraftRevision(document, null) };
    }

    private string DocumentPath(string draftId) => Path.Combine(_authorRoot!, draftId, "draft.json");

    private bool CanAdoptAuthorSource(AuthorDraftDocument document, EffectPackageContents package) =>
        !package.Files.Any(file => file.Role == "csharp")
        || ReadAuthorSourceHistory(document).Revisions.Any(revision => revision.SourceRevision == package.Revision
            && revision.Compilation is { Succeeded: true });

    private static void ValidateAuthorCompilationEntries(EffectPackageDefinition definition, ScriptCompilationResult compilation)
    {
        if (!compilation.Succeeded) return;
        using ScriptModule module = new(compilation.PeImage.AsMemory(), compilation.PdbImage.AsMemory());
        foreach (EffectControllerDefinition controller in definition.Controllers)
        {
            try
            {
                module.ValidateEntry<MakoKaraGenerator.Scripting.V2.IPureKaraokeEffect>(controller.EntryType);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new EffectPackageException("PackageEntryInvalid", "exports." + controller.Id + ".entryType",
                "真实 DLL 入口缺失或不符合此包声明的 SDK 构造合同。", error); }
        }
    }
    private string PackageDirectory(string draftId, string revision)
    {
        if (!ValidDraftId(draftId) || !ValidRevision(revision))
            throw new ArgumentException("源码修订无效。", nameof(revision));
        return Path.Combine(_authorRoot!, draftId, "packages", revision[7..]);
    }
    private Task<EffectPackageContents> LoadPackageAsync(string draftId, string revision, CancellationToken token)
        => EffectPackageStorage.ReadSupportedDirectoryAsync(PackageDirectory(draftId, revision), cancellationToken: token);

    /// <summary>持久作者草稿保护客户端身份；显式删除后释放保护。</summary>
    public bool HasAuthorDraft(string clientId)
    {
        if (_authorRoot is null || string.IsNullOrWhiteSpace(clientId) || !Directory.Exists(_authorRoot)) return false;
        foreach (string directory in Directory.EnumerateDirectories(_authorRoot, "draft-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string path = Path.Combine(directory, "draft.json");
                if (!File.Exists(path)) continue;
                AuthorDraftDocument? document = JsonSerializer.Deserialize<AuthorDraftDocument>(File.ReadAllBytes(path), AuthorJson);
                if (document is not null && string.Equals(document.OwnerClientId, clientId, StringComparison.Ordinal)) return true;
            }
            catch (IOException) { return true; }
            catch (JsonException) { return true; }
        }
        return false;
    }

    /// <summary>停止时取消长编译；已接受回执仍由共享排水器等待。</summary>
    internal void StopAuthorWorkflows()
    {
        Volatile.Write(ref _authorStopping, 1);
        _authorStop?.Cancel();
    }

    private (int Drafts, long Bytes) GetAuthorUsage(string clientId)
    {
        int drafts = 0; long bytes = 0;
        if (_authorRoot is null || !Directory.Exists(_authorRoot)) return (0, 0);
        foreach (string directory in Directory.EnumerateDirectories(_authorRoot, "draft-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string path = Path.Combine(directory, "draft.json");
                if (!File.Exists(path)) continue;
                AuthorDraftDocument? document = JsonSerializer.Deserialize<AuthorDraftDocument>(File.ReadAllBytes(path), AuthorJson);
                if (document?.OwnerClientId != clientId) continue;
                drafts++;
                bytes += Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            }
            catch (IOException) { return (MaxAuthorDraftsPerClient, MaxAuthorStorageBytesPerClient); }
            catch (JsonException) { return (MaxAuthorDraftsPerClient, MaxAuthorStorageBytesPerClient); }
        }
        return (drafts, bytes);
    }

    private static long PackageSize(EffectPackageContents package)
        => package.Manifest.Length + package.Files.Sum(file => (long)file.Content.Length);
    private static bool ValidRevision(string? revision)
        => !string.IsNullOrWhiteSpace(revision) && revision.Length == 71 && revision.StartsWith("sha256:", StringComparison.Ordinal)
            && revision[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    private static bool ValidDraftId(string? draftId)
        => !string.IsNullOrWhiteSpace(draftId) && draftId.Length == 38 && draftId.StartsWith("draft-", StringComparison.Ordinal)
            && draftId[6..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // Caller owns the storage gate, including deletion and the quota snapshot.
    private bool TryPersistAuthorDocument(AuthorDraftDocument document, EffectPackageContents? package = null,
        AuthorCompilationSummaryDto? compilation = null)
    {
        string path = DocumentPath(document.DraftId);
        string directory = Path.GetDirectoryName(path)!;
        string? packageDirectory = package is null ? null : PackageDirectory(document.DraftId, package.Revision);
        bool draftExisted = Directory.Exists(directory);
        bool packageExisted = packageDirectory is not null && Directory.Exists(packageDirectory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, AuthorJson);
        string historyPath = AuthorSourceHistoryPath(document.DraftId);
        byte[]? previousHistory = File.Exists(historyPath) ? File.ReadAllBytes(historyPath) : null;
        byte[] history = BuildAuthorSourceHistory(document, package, packageExisted, compilation);
        bool historyChanged = previousHistory is null || !history.AsSpan().SequenceEqual(previousHistory);
        bool historyPublished = false;
        long delta = bytes.Length - (File.Exists(path) ? new FileInfo(path).Length : 0)
            + (package is not null && !packageExisted ? PackageSize(package) : 0)
            + history.Length - (previousHistory?.Length ?? 0);
        if (delta > MaxAuthorStorageBytesPerClient - GetAuthorUsage(document.OwnerClientId).Bytes) return false;
        try
        {
            if (package is not null) EffectPackageStorage.Publish(packageDirectory!, package);
            Directory.CreateDirectory(directory);
            if (historyChanged) { SaveAuthorHistoryBytes(historyPath, history); historyPublished = true; }
            SaveDocument(document, bytes);
            return true;
        }
        catch
        {
            if (historyPublished)
            {
                if (previousHistory is null) TryDelete(historyPath);
                else SaveAuthorHistoryBytes(historyPath, previousHistory);
            }
            // Only remove this attempt's candidate; an already published revision is shared by this draft's history.
            string? candidate = !draftExisted ? directory : !packageExisted ? packageDirectory : null;
            if (candidate is not null && Directory.Exists(candidate)) Directory.Delete(candidate, recursive: true);
            throw;
        }
    }

    private void SaveDocument(AuthorDraftDocument document, byte[] bytes)
    {
        string path = DocumentPath(document.DraftId); string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".draft-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    private static byte[] UpdateManifest(EffectPackageContents package, Dictionary<string, ReadOnlyMemory<byte>> files)
    {
        JsonNode root = JsonNode.Parse(package.Manifest.ToArray()) ?? throw new EffectPackageException("PackageManifestInvalid", "manifest.json", "清单不是合法 JSON。");
        JsonArray entries = root["files"]?.AsArray() ?? throw new EffectPackageException("PackageManifestInvalid", "manifest.json.files", "清单缺少文件数组。");
        foreach (JsonNode? node in entries)
        {
            string path = node?["path"]?.GetValue<string>() ?? throw new EffectPackageException("PackageManifestInvalid", "manifest.json.files", "文件路径缺失。");
            if (!files.TryGetValue(path, out ReadOnlyMemory<byte> bytes)) throw new EffectPackageException("PackageFileMissing", path, "源码修订缺少文件。");
            node["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        }
        return JsonSerializer.SerializeToUtf8Bytes(root, AuthorJson);
    }

    private static string ComputeDraftRevision(AuthorDraftDocument document, string? ignored)
    {
        string value = string.Join("\n", document.DraftId, document.Name, document.PackageId, document.BaseSourceRevision,
            document.CurrentSourceRevision, document.ExportId, document.AdoptedSourceRevision ?? "", document.Configuration is null ? "" :
            string.Join("\n", document.Configuration.Name, document.Configuration.PackageId, document.Configuration.SourceRevision,
                document.Configuration.ExportId, document.Configuration.ParametersJson, document.Configuration.RelativeTarget,
                document.Configuration.ReadScope, document.Configuration.BranchInput ?? "", document.Configuration.FixedSeed ?? "",
                document.Configuration.OverlayLayer));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    private static async Task<byte[]> ReadAssetBytesAsync(StoredAsset asset, CancellationToken token)
    {
        await using FileStream stream = new(asset.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (stream.Length != asset.Size) throw new IOException("Fixed asset changed.");
        byte[] bytes = new byte[checked((int)asset.Size)]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256) throw new IOException("Fixed asset changed.");
        return bytes;
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static BackendOperationResult<T> AuthorReject<T>(BackendErrorCode code, string message, string? field = null, string? failureCode = null)
        => BackendOperationResult<T>.Rejected(new(code, message, field, FailureCode: failureCode));
}

internal sealed class AuthorCompilationBudgetException : Exception;
