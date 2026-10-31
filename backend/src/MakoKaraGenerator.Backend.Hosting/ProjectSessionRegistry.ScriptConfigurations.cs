using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private ScriptConfigurationRepository? _scriptConfigurations;
    private readonly SemaphoreSlim _scriptConfigurationIo = new(1, 1);

    public void ConfigureScriptConfigurations()
    {
        if (_scriptConfigurations is not null) throw new InvalidOperationException("脚本配置库已经配置。");
        if (_persistence is null) throw new InvalidOperationException("脚本配置库需要先配置持久profile。");
        _scriptConfigurations = new(ScriptConfigurationLibrary.ResolvePath(_persistence.StateRoot));
    }

    public BackendOperationResult<ScriptConfigurationListResponse> ReadScriptConfigurations(string? expectedRevision = null)
    {
        if (!BeginTemplateOperation()) return TemplateClosing<ScriptConfigurationListResponse>();
        try
        {
        if (!TryReadScriptLibrary(out var library, out var error)) return BackendOperationResult<ScriptConfigurationListResponse>.Rejected(error!);
        if (expectedRevision is not null && RevisionText(library.Revision) != expectedRevision)
            return ScriptConfigurationReject<ScriptConfigurationListResponse>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "expectedRevision");
        return BackendOperationResult<ScriptConfigurationListResponse>.Success(new(RevisionText(library.Revision), [.. library.Configurations.Select(Summary)]));
        }
        finally { EndTemplateOperation(); }
    }

    public BackendOperationResult<ScriptConfigurationDetailResponse> ReadScriptConfiguration(string id, string expectedRevision)
    {
        if (!BeginTemplateOperation()) return TemplateClosing<ScriptConfigurationDetailResponse>();
        try
        {
        if (!TryReadScriptLibrary(out var library, out var error)) return BackendOperationResult<ScriptConfigurationDetailResponse>.Rejected(error!);
        if (RevisionText(library.Revision) != expectedRevision)
            return ScriptConfigurationReject<ScriptConfigurationDetailResponse>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "expectedRevision");
        var item = library.Configurations.FirstOrDefault(x => x.Id == id);
        if (item is null) return ScriptConfigurationReject<ScriptConfigurationDetailResponse>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId");
        return BackendOperationResult<ScriptConfigurationDetailResponse>.Success(new(RevisionText(library.Revision), ToDto(item),
            CreateEffectPackageInspection(item.Package, new("configuration", item.Package.PackageId, item.Package.Revision,
                ConfigurationId: item.Id, ConfigurationRevision: RevisionText(library.Revision)))));
        }
        finally { EndTemplateOperation(); }
    }

    public async Task<BackendOperationResult<ScriptConfigurationCommandResponse>> ExecuteScriptConfigurationCommandAsync(
        string clientId, ScriptConfigurationCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (_scriptConfigurations is null) return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.TaskUnavailable, "脚本配置库尚未配置。", "scriptConfigurations");
        if (request is null || request.Metadata is null || string.IsNullOrWhiteSpace(request.ExpectedRevision) || request.Command is null)
            return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, "配置库命令请求不完整。", "command");
        try
        {
            request = JsonSerializer.Deserialize<ScriptConfigurationCommandRequest>(JsonSerializer.SerializeToUtf8Bytes(request, AuthorJson), AuthorJson)!;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, "配置库命令形状非法。", "command"); }
        return await Receipts.ExecuteAsync(clientId, request.Metadata, "POST /api/v1/script-configurations/commands",
            new { request.ExpectedRevision, request.Command }, async () =>
        {
            if (!BeginTemplateOperation()) return TemplateClosing<ScriptConfigurationCommandResponse>();
            await _scriptConfigurationIo.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var current = _scriptConfigurations.Read();
                if (current.VersionMismatch || current.Issues.Count != 0) return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.ProjectLoadFailed, "配置库无效。", "scriptConfigurations");
                if (RevisionText(current.Revision) != request.ExpectedRevision)
                    return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "expectedRevision");
                var items = current.Configurations.ToList(); string id;
                switch (request.Command)
                {
                    case ScriptConfigurationCreateCommandDto create:
                        id = "configuration-" + Guid.NewGuid().ToString("N"); items.Add(await ResolveInput(clientId, id, create.Configuration, CancellationToken.None)); break;
                    case ScriptConfigurationReplaceCommandDto replace:
                        id = replace.ConfigurationId; int index = items.FindIndex(x => x.Id == id);
                        if (index < 0) return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId");
                        items[index] = await ResolveInput(clientId, id, replace.Configuration, CancellationToken.None); break;
                    case ScriptConfigurationCopyCommandDto copy:
                        var source = items.FirstOrDefault(x => x.Id == copy.ConfigurationId);
                        if (source is null) return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId");
                        id = "configuration-" + Guid.NewGuid().ToString("N"); items.Add(ScriptConfiguration.Create(id, copy.Name, source.Package, source.ExportId, source.Parameters, source.Target, source.ReadScope, source.BranchInput, source.FixedSeed, source.OverlayLayer)); break;
                    case ScriptConfigurationRenameCommandDto rename:
                        id = rename.ConfigurationId; int renameIndex = items.FindIndex(x => x.Id == id);
                        if (renameIndex < 0) return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId");
                        var old = items[renameIndex]; items[renameIndex] = ScriptConfiguration.Create(id, rename.Name, old.Package, old.ExportId, old.Parameters, old.Target, old.ReadScope, old.BranchInput, old.FixedSeed, old.OverlayLayer); break;
                    case ScriptConfigurationDeleteCommandDto delete:
                        id = delete.ConfigurationId; int deleteIndex = items.FindIndex(x => x.Id == id);
                        if (deleteIndex < 0) return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId");
                        items.RemoveAt(deleteIndex); break;
                    default: return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, "未知配置命令。", "command.type");
                }
                bool changed = !current.Configurations.SequenceEqual(items);
                var saved = changed ? await _scriptConfigurations.SaveAsync(current.Revision, items, CancellationToken.None).ConfigureAwait(false) : current;
                return BackendOperationResult<ScriptConfigurationCommandResponse>.Success(new(RevisionText(current.Revision), RevisionText(saved.Revision), changed, id));
            }
            catch (LibraryRevisionConflictException) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "expectedRevision"); }
            catch (ScriptConfigurationSourceException ex) { return BackendOperationResult<ScriptConfigurationCommandResponse>.Rejected(ex.Error); }
            catch (LibraryBusyException) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.CommandRejected, "配置库正在提交。", "scriptConfigurations", "LibraryBusy"); }
            catch (EffectPackageException ex) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, ex.Message, ex.Path, ex.Code); }
            catch (ArgumentException ex) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, ex.Message, "configuration"); }
            catch (FormatException ex) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, ex.Message, "configuration.fixedSeed"); }
            catch (OverflowException ex) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, ex.Message, "configuration.fixedSeed"); }
            catch (JsonException ex) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.InvalidRequest, ex.Message, "configuration.parametersJson"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ScriptConfigurationReject<ScriptConfigurationCommandResponse>(BackendErrorCode.ProjectLoadFailed, "配置来源或配置库无法读取或保存。", "configuration"); }
            finally { _scriptConfigurationIo.Release(); EndTemplateOperation(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScriptConfiguration> ResolveInput(string clientId, string id, ScriptConfigurationInputDto input, CancellationToken token)
    {
        if (input is null || input.Source is null) throw new ArgumentException("配置来源不能为空。");
        if (new[] { input.Source.DraftId is not null, input.Source.ConfigurationId is not null, input.Source.Package is not null }.Count(value => value) != 1)
            throw new ArgumentException("配置必须且只能选择draft、configuration或package一个来源。");
        if (!Enum.IsDefined(input.RelativeTarget) || !Enum.IsDefined(input.ReadScope) || !Enum.IsDefined(input.OverlayLayer))
            throw new ArgumentException("配置枚举非法。");
        EffectPackageContents package;
        string sourceRevision;
        if (!string.IsNullOrWhiteSpace(input.Source.ConfigurationId))
        {
            if (input.Source.DraftId is not null || input.Source.ExpectedDraftRevision is not null || input.Source.SourceRevision is not null || input.Source.Package is not null)
                throw new ArgumentException("配置来源字段必须与configuration一致。");
            if (_scriptConfigurations is null) throw new InvalidOperationException();
            var library = _scriptConfigurations.Read();
            if (library.VersionMismatch || library.Issues.Count != 0) throw new ScriptConfigurationSourceException(new(BackendErrorCode.ProjectLoadFailed, "配置库无效。", "configuration.source.configurationId"));
            if (RevisionText(library.Revision) != input.Source.ExpectedConfigurationRevision) throw new LibraryRevisionConflictException(_scriptConfigurations.Path);
            var source = library.Configurations.FirstOrDefault(x => x.Id == input.Source.ConfigurationId) ?? throw new ScriptConfigurationSourceException(new(BackendErrorCode.ResourceNotFound, "配置不存在。", "configuration.source.configurationId"));
            package = source.Package; sourceRevision = source.Package.Revision;
        }
        else if (!string.IsNullOrWhiteSpace(input.Source.DraftId))
        {
            if (input.Source.ExpectedConfigurationRevision is not null || input.Source.Package is not null || string.IsNullOrWhiteSpace(input.Source.SourceRevision))
                throw new ArgumentException("草稿来源必须显式提供sourceRevision且不含其他来源字段。");
            if (!ValidDraftId(input.Source.DraftId)) throw new ArgumentException("草稿身份无效。");
            sourceRevision = input.Source.SourceRevision;
            var gate = _authorDraftLocks.GetOrAdd(input.Source.DraftId, static _ => new(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!TryReadAuthorDraft(clientId, input.Source.DraftId, out var draft, out var err)) throw new ScriptConfigurationSourceException(err!);
                if (draft!.DraftRevision != input.Source.ExpectedDraftRevision) throw new ScriptConfigurationSourceException(new(BackendErrorCode.RevisionConflict, "草稿修订已经改变。", "configuration.source.expectedDraftRevision"));
                try { package = await LoadPackageAsync(draft.DraftId, sourceRevision, token).ConfigureAwait(false); }
                catch (DirectoryNotFoundException) { throw new ScriptConfigurationSourceException(new(BackendErrorCode.ResourceNotFound, "源码修订不存在。", "configuration.source.sourceRevision")); }
            }
            finally { gate.Release(); }
        }
        else if (input.Source.Package is not null)
        {
            if (input.Source.ExpectedConfigurationRevision is not null || input.Source.ExpectedDraftRevision is not null || input.Source.SourceRevision is not null)
                throw new ArgumentException("包来源不接受草稿或配置修订字段。");
            var resolved = await ResolveEffectPackageReferenceAsync(clientId, input.Source.Package, token).ConfigureAwait(false);
            if (!resolved.Accepted) throw new ScriptConfigurationSourceException(resolved.Error!);
            package = resolved.Response!; sourceRevision = package.Revision;
        }
        else throw new ArgumentException("配置必须引用draft、已有configuration或固定package。");
        ulong? seed = input.FixedSeed is null ? null : ulong.Parse(input.FixedSeed, NumberStyles.None, CultureInfo.InvariantCulture);
        return ScriptConfiguration.Create(id, input.Name, package, input.ExportId, EffectParameterOverrides.Parse(input.ParametersJson),
            Enum.Parse<ScriptRelativeTarget>(input.RelativeTarget.ToString()), Enum.Parse<ScriptReadScope>(input.ReadScope.ToString()), input.BranchInput, seed, Enum.Parse<EffectOverlayLayer>(input.OverlayLayer.ToString()));
    }

    private bool TryReadScriptLibrary(out ScriptConfigurationLibraryLoadResult result, out BackendErrorResponse? error)
    {
        if (_scriptConfigurations is null) { result = new([], []); error = new(BackendErrorCode.TaskUnavailable, "脚本配置库尚未配置。", "scriptConfigurations"); return false; }
        result = _scriptConfigurations.Read();
        error = !result.VersionMismatch && result.Issues.Count == 0 ? null : new(BackendErrorCode.ProjectLoadFailed, "脚本配置库无效。", "scriptConfigurations");
        return error is null;
    }
    private static string RevisionText(LibraryIndexRevision value) => value.Sha256 ?? "missing";
    private static ScriptConfigurationSummaryDto Summary(ScriptConfiguration x) => new(x.Id, x.Name, x.Kind.ToString(), x.Package.PackageId, x.Package.Revision, x.Package.Revision, x.ExportId, [.. x.BindingKinds]);
    private static ScriptConfigurationDto ToDto(ScriptConfiguration x) => new(x.Id, x.Name, x.Kind.ToString(), x.Package.PackageId, x.Package.Revision, x.Package.Revision, x.ExportId, x.Parameters.Json,
        Enum.Parse<TemplateEffectTargetDto>(x.Target.ToString()), Enum.Parse<TemplateScriptReadScopeDto>(x.ReadScope.ToString()), x.BranchInput, x.FixedSeed?.ToString(CultureInfo.InvariantCulture), Enum.Parse<TemplateEffectOverlayLayerDto>(x.OverlayLayer.ToString()), [.. x.BindingKinds]);
    private static BackendOperationResult<T> ScriptConfigurationReject<T>(BackendErrorCode code, string message, string? field = null, string? failureCode = null) => BackendOperationResult<T>.Rejected(new(code, message, field, FailureCode: failureCode));
    private sealed class ScriptConfigurationSourceException(BackendErrorResponse error) : Exception(error.Message)
    { public BackendErrorResponse Error { get; } = error; }

    private async Task<BackendOperationResult<AuthorDraftResponse>> CreateAuthorDraftFromConfigurationAsync(
        string clientId, AuthorDraftCreateRequest request, CancellationToken token)
    {
        if (request.PackageAssetId is not null || string.IsNullOrWhiteSpace(request.ConfigurationId)
            || string.IsNullOrWhiteSpace(request.ExpectedConfigurationRevision))
            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "配置来源与包资源来源互斥，且必须提供精确配置库修订。", "configurationId");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256)
            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "草稿名称不能为空且不得超过256字符。", "name");
        if (!TryReadScriptLibrary(out var library, out var error)) return BackendOperationResult<AuthorDraftResponse>.Rejected(error!);
        if (RevisionText(library.Revision) != request.ExpectedConfigurationRevision)
            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "expectedConfigurationRevision");
        var source = library.Configurations.FirstOrDefault(x => x.Id == request.ConfigurationId);
        if (source is null) return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceNotFound, "配置不存在。", "configurationId");
        if (!string.IsNullOrWhiteSpace(request.ExportId) && request.ExportId != source.ExportId)
            return AuthorReject<AuthorDraftResponse>(BackendErrorCode.InvalidRequest, "配置来源的exportId必须匹配固定配置。", "exportId");
        await _authorStorageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (GetAuthorUsage(clientId).Drafts >= MaxAuthorDraftsPerClient)
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.RequestQuotaExceeded, "客户端作者草稿数量达到上限。", "drafts");
            var document = CreateDocument("draft-" + Guid.NewGuid().ToString("N"), clientId, request.Name.Trim(), source.Package, source.ExportId);
            var config = ToDto(source);
            document = document with { Configuration = new(request.Name.Trim(), source.Package.PackageId, source.Package.Revision,
                source.ExportId, source.Parameters.Json, source.Target.ToString(), source.ReadScope.ToString(), source.BranchInput,
                config.FixedSeed, source.OverlayLayer.ToString()) };
            document = document with { DraftRevision = ComputeDraftRevision(document, null) };
            if (!TryPersistAuthorDocument(document, source.Package))
                return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ResourceQuotaExceeded, "客户端作者草稿存储达到上限。", "drafts");
            return BackendOperationResult<AuthorDraftResponse>.Success(ToAuthorResponse(document));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return AuthorReject<AuthorDraftResponse>(BackendErrorCode.ProjectLoadFailed, "固定配置无法保存为作者草稿。", "configurationId"); }
        finally { _authorStorageGate.Release(); }
    }
}
