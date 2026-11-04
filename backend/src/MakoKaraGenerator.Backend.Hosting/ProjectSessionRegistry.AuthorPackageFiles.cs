using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private const int MaximumAuthorTextFileBytes = 4 * 1024 * 1024;
    private const int MaximumAuthorTextBytes = 16 * 1024 * 1024;
    private const int MaximumAuthorTextFiles = 256;
    private static readonly UTF8Encoding AuthorPackageUtf8 = new(false, true);

    /// <summary>读取精确草稿修订下完整包清单和全部文件描述；非文本原字节不经此接口返回。</summary>
    public BackendOperationResult<AuthorPackageFilesResponse> ReadAuthorPackageFiles(string clientId, string draftId,
        string expectedDraftRevision, string sourceRevision)
    {
        if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
            return BackendOperationResult<AuthorPackageFilesResponse>.Rejected(error!);
        SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
        gate.Wait(_authorStop?.Token ?? CancellationToken.None);
        try
        {
            if (!TryReadAuthorDraft(clientId, draftId, out document, out error))
                return BackendOperationResult<AuthorPackageFilesResponse>.Rejected(error!);
            return ReadAuthorPackageFilesLocked(document!, expectedDraftRevision, sourceRevision);
        }
        finally { gate.Release(); }
    }

    private BackendOperationResult<AuthorPackageFilesResponse> ReadAuthorPackageFilesLocked(AuthorDraftDocument document,
        string expectedDraftRevision, string sourceRevision)
    {
        if (!ValidRevision(expectedDraftRevision))
            return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.InvalidRequest, "草稿修订无效。", "expectedDraftRevision");
        if (!ValidRevision(sourceRevision))
            return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.InvalidRequest, "源码修订无效。", "sourceRevision");
        if (!string.Equals(document!.DraftRevision, expectedDraftRevision, StringComparison.Ordinal))
            return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
        try
        {
            EffectPackageContents package = LoadPackageAsync(document.DraftId, sourceRevision,
                _authorStop?.Token ?? CancellationToken.None).GetAwaiter().GetResult();
            long textBytes = package.Manifest.Length;
            int textFiles = 0;
            List<AuthorPackageFileDto> fileList = [];
            foreach (EffectPackageFile file in package.Files)
            {
                string? text = null;
                if (IsEditableAuthorRole(file.Role))
                {
                    textFiles++;
                    if (textFiles > MaximumAuthorTextFiles || file.Content.Length > MaximumAuthorTextFileBytes)
                        return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.ResourceTooLarge, "文本文件超过数量或单文件预算。", "files");
                    text = file.ReadUtf8();
                    textBytes += file.Content.Length;
                    if (textBytes > MaximumAuthorTextBytes)
                        return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.ResourceTooLarge, "文本及清单合计超过16MiB。", "files");
                }
                fileList.Add(new(file.Path, file.Role, file.Content.Length, file.Sha256, IsEditableAuthorRole(file.Role), text));
            }
            string manifestText = AuthorPackageUtf8.GetString(package.Manifest.AsSpan());
            AuthorPackageFilesResponse response = new(document.DraftId, document.DraftRevision, sourceRevision,
                package.PackageId, package.Revision, manifestText, [.. fileList]);
            if (JsonSerializer.SerializeToUtf8Bytes(response).LongLength > 32L * 1024 * 1024)
                return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.ResourceTooLarge, "包文件响应JSON超过32MiB。", "files");
            return BackendOperationResult<AuthorPackageFilesResponse>.Success(response);
        }
        catch (DirectoryNotFoundException)
        { return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "sourceRevision"); }
        catch (EffectPackageException packageError)
        { return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.CommandRejected, packageError.Message, packageError.Path, packageError.Code); }
        catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return AuthorReject<AuthorPackageFilesResponse>(BackendErrorCode.ProjectLoadFailed, "作者草稿固定包无法读取。", "sourceRevision", "AuthorDraftStorageFailed"); }
    }

    /// <summary>原子保存完整清单与 csharp/shader/shaderProgram/graph 文本，检查结构与配置后才发布新修订。</summary>
    public Task<BackendOperationResult<AuthorDraftResponse>> SaveAuthorPackageFilesAsync(string clientId, string draftId,
        AuthorPackageFilesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AuthorPackageTextFileDto[]? snapshotFiles = request.TextFiles?.Select(file => file is null
            ? null! : new AuthorPackageTextFileDto(file.Path, file.Text)).ToArray();
        AuthorPackageFilesRequest snapshot = request with { Metadata = null, TextFiles = snapshotFiles! };
        return RunAuthorMutationAsync(clientId, draftId, request.Metadata, "PUT /api/v1/drafts/package-files",
            snapshot, async document =>
        {
            if (!string.Equals(document.DraftRevision, snapshot.ExpectedDraftRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.RevisionConflict, "作者草稿修订已经改变，请重新读取草稿。", "expectedDraftRevision");
            if (snapshot.ManifestJson is null || snapshot.TextFiles is null)
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "必须提供清单和完整文本文件集合。", "textFiles");
            if (snapshot.TextFiles.Length > MaximumAuthorTextFiles)
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceTooLarge, "文本文件数量超过256。", "textFiles");
            CancellationToken lifetime = _authorStop?.Token ?? CancellationToken.None;
            try
            {
                byte[] manifestBytes = AuthorPackageUtf8.GetBytes(snapshot.ManifestJson);
                if (manifestBytes.Length > MaximumAuthorTextFileBytes)
                    return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceTooLarge, "清单超过4MiB。", "manifestJson");
                long textBytes = manifestBytes.Length;
                Dictionary<string, ReadOnlyMemory<byte>> texts = new(StringComparer.Ordinal);
                foreach (AuthorPackageTextFileDto? text in snapshot.TextFiles)
                {
                    if (text is null || string.IsNullOrWhiteSpace(text.Path) || text.Text is null)
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "文本文件必须提供路径和文本。", "textFiles");
                    EffectPackageContents.ValidatePath(text.Path);
                    byte[] bytes = AuthorPackageUtf8.GetBytes(text.Text);
                    if (bytes.Length > MaximumAuthorTextFileBytes)
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceTooLarge, "单个文本文件超过4MiB。", "textFiles");
                    textBytes += bytes.Length;
                    if (textBytes > MaximumAuthorTextBytes)
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceTooLarge, "文本及清单合计超过16MiB。", "textFiles");
                    if (!texts.TryAdd(text.Path, bytes))
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "文本文件路径重复。", "textFiles");
                }
                using JsonDocument json = JsonDocument.Parse(manifestBytes);
                EnsureUniqueAuthorPackageProperties(json.RootElement);
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || !json.RootElement.TryGetProperty("files", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                    return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "清单必须包含files数组。", "manifestJson");
                EffectPackageContents original = await LoadPackageAsync(document.DraftId, document.CurrentSourceRevision, lifetime).ConfigureAwait(false);
                if (!json.RootElement.TryGetProperty("packageId", out JsonElement packageId)
                    || packageId.ValueKind != JsonValueKind.String || packageId.GetString() != document.PackageId)
                    return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "packageId必须保持当前草稿的固定包身份。", "manifestJson.packageId");
                Dictionary<string, EffectPackageFile> fixedFiles = original.Files.Where(file => !IsEditableAuthorRole(file.Role))
                    .ToDictionary(file => file.Path, StringComparer.Ordinal);
                Dictionary<string, ReadOnlyMemory<byte>> contents = new(StringComparer.Ordinal);
                HashSet<string> seenFixedFiles = new(StringComparer.Ordinal);
                HashSet<string> seenTextFiles = new(StringComparer.Ordinal);
                JsonObject manifest = JsonNode.Parse(manifestBytes)!.AsObject();
                JsonArray mutableEntries = manifest["files"]!.AsArray();
                int index = 0;
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String
                        || !entry.TryGetProperty("role", out JsonElement roleValue) || roleValue.ValueKind != JsonValueKind.String)
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "文件声明必须包含path和role。", "manifestJson.files");
                    string path = pathValue.GetString()!, role = roleValue.GetString()!;
                    EffectPackageContents.ValidatePath(path);
                    ReadOnlyMemory<byte> bytes;
                    if (IsEditableAuthorRole(role))
                    {
                        if (!seenTextFiles.Add(path) || !texts.TryGetValue(path, out bytes))
                            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "textFiles须恰好匹配清单的全部可编辑文件。", "textFiles");
                    }
                    else
                    {
                        if (!seenFixedFiles.Add(path) || !fixedFiles.TryGetValue(path, out EffectPackageFile? prior)
                            || prior.Role != role || !entry.TryGetProperty("sha256", out JsonElement hashValue)
                            || hashValue.ValueKind != JsonValueKind.String || hashValue.GetString() != prior.Sha256)
                            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "非文本文件的path、role和sha256必须保持不变。", "manifestJson.files");
                        bytes = prior.Content.ToArray();
                    }
                    if (!contents.TryAdd(path, bytes))
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "清单文件路径重复。", "manifestJson.files");
                    mutableEntries[index++]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
                }
                if (seenTextFiles.Count != texts.Count || seenFixedFiles.Count != fixedFiles.Count)
                    return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "文本集合须完整，非文本文件不能删除或改变用途。", "textFiles");
                byte[] normalizedManifest = JsonSerializer.SerializeToUtf8Bytes(manifest);
                if (normalizedManifest.Length > MaximumAuthorTextFileBytes
                    || textBytes - manifestBytes.Length + normalizedManifest.Length > MaximumAuthorTextBytes)
                    return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceTooLarge, "派生清单及文本超过预算。", "manifestJson");
                EffectPackageContents package = EffectPackageContents.Create(normalizedManifest, contents, original.Profile,
                    cancellationToken: lifetime);
                _ = EffectPackageDefinition.Read(package, lifetime);
                AuthorDraftConfigurationDocument previous = document.Configuration ?? DefaultConfiguration(document);
                AuthorDraftConfigurationDocument configuration = previous;
                bool v2 = EffectPackageProfiles.IsV2(package.Profile);
                if (snapshot.Configuration is { } input)
                    configuration = NormalizeConfiguration(document, new(input.Name, document.PackageId, v2 ? previous.SourceRevision : package.Revision,
                        input.ExportId, input.ParametersJson, input.RelativeTarget, input.ReadScope, input.BranchInput,
                        input.FixedSeed, input.OverlayLayer));
                else if (!v2 && previous.SourceRevision == document.CurrentSourceRevision)
                    configuration = previous with { SourceRevision = package.Revision };
                EffectPackageContents configuredPackage = configuration.SourceRevision == package.Revision ? package
                    : await LoadPackageAsync(document.DraftId, configuration.SourceRevision, lifetime).ConfigureAwait(false);
                _ = BuildSampleOptions(BuildConfiguration(configuredPackage, configuration));
                bool changedSource = package.Revision != document.CurrentSourceRevision;
                AuthorDraftDocument updatedBase = document with
                {
                    CurrentSourceRevision = package.Revision,
                    Configuration = configuration,
                    Name = configuration.Name,
                    ExportId = configuration.ExportId,
                    LastCompilation = changedSource && v2 && document.LastCompilation is { } compilation
                        ? compilation with { Stale = compilation.SourceRevision != package.Revision }
                        : changedSource ? null : document.LastCompilation,
                    LastCompilationDiagnostics = changedSource && !v2 ? null : document.LastCompilationDiagnostics,
                };
                AuthorDraftDocument updated = updatedBase with { DraftRevision = ComputeDraftRevision(updatedBase, null) };
                if (updated == document)
                    return BackendOperationResult<AuthorDraftResponse>.Success(ToAuthorResponse(document));
                await _authorStorageGate.WaitAsync(lifetime).ConfigureAwait(false);
                try
                {
                    if (!TryPersistAuthorDocument(updated, changedSource ? package : null))
                        return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "textFiles");
                }
                finally { _authorStorageGate.Release(); }
                return BackendOperationResult<AuthorDraftResponse>.Success(ToAuthorResponse(updated));
            }
            catch (EffectPackageException packageError)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, packageError.Message, packageError.Path, packageError.Code); }
            catch (JsonException)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "清单JSON无效或含重复字段。", "manifestJson"); }
            catch (EncoderFallbackException)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "文本不是合法Unicode。", "textFiles"); }
            catch (ArgumentException configurationError)
            { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, configurationError.Message, "configuration", "AuthorConfigurationInvalid"); }
        }, cancellationToken);
    }

    private static bool IsEditableAuthorRole(string role) => role is "csharp" or "shader" or "shaderProgram" or "graph";

    private static void EnsureUniqueAuthorPackageProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("重复清单属性。");
                EnsureUniqueAuthorPackageProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in value.EnumerateArray()) EnsureUniqueAuthorPackageProperties(child);
    }
}
