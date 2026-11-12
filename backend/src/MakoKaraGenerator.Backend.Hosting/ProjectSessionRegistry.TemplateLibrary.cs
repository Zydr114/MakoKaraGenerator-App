using System.Collections.Immutable;
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
    private TemplateLibraryEditService? _templateLibrary;
    // One full personal closure at a time; builtins do not wait on this I/O gate.
    private readonly SemaphoreSlim _templateIo = new(1, 1);
    private readonly object _templateOperationGate = new();
    private int _templateOperations;
    private bool _templateStopping;
    private TaskCompletionSource _templateIdle = CompletedTemplateOperation();

    /// <summary>可信组合根启用当前profile个人库，不自动采用桌面目录。</summary>
    public void ConfigureTemplateLibrary()
    {
        if (_templateLibrary is not null) throw new InvalidOperationException("模板库已经配置。");
        if (_persistence is null) throw new InvalidOperationException("模板库需要先配置持久profile。");
        string stateRoot = _persistence.StateRoot;
        _templateLibrary = new(new TemplateLibraryRepository(() => ResolveTemplateLibraryPath(stateRoot)));
    }
    /// <summary>固定目录描述；不读取个人磁盘入口。</summary>
    public TemplateLibrariesResponse ReadTemplateLibraries()
    {
        _ = _templateLibrary; // Discovery remains independent of personal I/O and configuration.
        return new([new("builtin", "内置模板", true), new("personal", "我的模板", false)]);
    }

    /// <summary>完整读取并验证目录后投影同一修订的一页摘要，分页不限制底层全库IO。</summary>
    public Task<BackendOperationResult<TemplateListResponse>> ReadTemplatesAsync(string libraryId, string? expectedRevision,
        int offset = 0, int limit = 50, CancellationToken cancellationToken = default) => ReadTemplateLibraryAsync(libraryId, expectedRevision,
        snapshot =>
        {
            if (offset < 0 || offset > snapshot.Templates.Length || limit is < 1 or > TemplateLibraryLimits.MaxPageSize)
                return TemplateReject<TemplateListResponse>(BackendErrorCode.InvalidRequest, "模板页范围非法。", "window");
            int end = Math.Min(snapshot.Templates.Length, checked(offset + limit));
            TemplateSummaryDto[] rows = [.. snapshot.Templates.Skip(offset).Take(end - offset).Select(TemplateSummary)];
            return BackendOperationResult<TemplateListResponse>.Success(TemplateLibraryResponseBudget.Check(new TemplateListResponse(
                libraryId, TemplateRevision(snapshot), offset, snapshot.Templates.Length,
                end < snapshot.Templates.Length ? end : null, rows)));
        }, cancellationToken);

    /// <summary>同一精确修订下读取静态完整定义；高级对象只投影执行可用性，不重建静态定义。</summary>
    public Task<BackendOperationResult<TemplateDetailResponse>> ReadTemplateAsync(string libraryId, string expectedRevision,
        string templateId, CancellationToken cancellationToken = default) => ReadTemplateAsync(libraryId, expectedRevision, templateId, null, cancellationToken);

    /// <summary>显式full读取可编辑完整定义及只读闭包说明，省略格式保留原静态响应。</summary>
    public Task<BackendOperationResult<TemplateDetailResponse>> ReadTemplateAsync(string libraryId, string expectedRevision,
        string templateId, string? definitionFormat, CancellationToken cancellationToken = default) => ReadTemplateLibraryAsync(libraryId, expectedRevision,
        snapshot =>
        {
            if (string.IsNullOrWhiteSpace(expectedRevision) || string.IsNullOrWhiteSpace(templateId))
                return TemplateReject<TemplateDetailResponse>(BackendErrorCode.InvalidRequest, "必须提供修订和模板身份。", "templateId");
            var template = snapshot.Templates.FirstOrDefault(template => template.Id == templateId);
            if (template is null) return TemplateReject<TemplateDetailResponse>(BackendErrorCode.ResourceNotFound, "模板不存在。", "templateId");
            if (definitionFormat is not null)
                return definitionFormat == "full"
                    ? BackendOperationResult<TemplateDetailResponse>.Success(TemplateLibraryResponseBudget.Check(
                        FullTemplateDetail(libraryId, TemplateRevision(snapshot), template)))
                    : TemplateReject<TemplateDetailResponse>(BackendErrorCode.InvalidRequest, "definitionFormat仅接受full。", "definitionFormat");
            bool available = TemplateLibraryEditService.IsStatic(template);
            StaticTemplateDefinitionDto? definition = available ? new(template.Name, (TemplateKind)template.Kind,
                ProjectSession.ExportTemplateLibraryStyle(template.Style), TemplateProperties(template)) : null;
            return BackendOperationResult<TemplateDetailResponse>.Success(TemplateLibraryResponseBudget.Check(new TemplateDetailResponse(
                libraryId, TemplateRevision(snapshot), TemplateSummary(template), TemplateDefinitionAvailability(template), definition)));
        }, cancellationToken);

    /// <summary>为只读候选试看固定同一库修订及模板内容；高级模板不降级为静态样式。</summary>
    public Task<BackendOperationResult<StyleTemplate>> ReadStaticTemplateForPreviewAsync(
        string libraryId, string expectedLibraryRevision, string templateId, string expectedTemplateRevision,
        CancellationToken cancellationToken = default) => ReadTemplateLibraryAsync(libraryId, expectedLibraryRevision,
        snapshot =>
        {
            if (string.IsNullOrWhiteSpace(templateId) || string.IsNullOrWhiteSpace(expectedTemplateRevision))
                return TemplateReject<StyleTemplate>(BackendErrorCode.InvalidRequest, "必须提供模板身份和精确模板修订。", "templateRevision");
            StyleTemplate? template = snapshot.Templates.FirstOrDefault(item => item.Id == templateId);
            if (template is null) return TemplateReject<StyleTemplate>(BackendErrorCode.ResourceNotFound, "模板不存在。", "templateId");
            if (!TemplateRuntimeSupported(template))
                return BackendOperationResult<StyleTemplate>.Rejected(new(BackendErrorCode.CommandNotSupported,
                    "候选试看不支持此类高级模板。", "templateId", FailureCode: "TemplatePreviewUnsupported"));
            string actual = TemplateSummary(template).TemplateRevision;
            return actual != expectedTemplateRevision
                ? BackendOperationResult<StyleTemplate>.Rejected(new(BackendErrorCode.RevisionConflict,
                    "模板内容修订已经改变，请重新读取模板详情。", "templateRevision"))
                : BackendOperationResult<StyleTemplate>.Success(template);
        }, cancellationToken);

    /// <summary>将请求内的完整静态草稿转换为一次试样会话使用的不可变模板；不写个人库。</summary>
    public static BackendOperationResult<StyleTemplate> CreateDraftTemplateForPreview(
        string draftRevision, StaticTemplateDefinitionDto definition)
    {
        if (string.IsNullOrWhiteSpace(draftRevision) || draftRevision.Length > TemplatePreviewLimits.MaxDraftRevisionLength)
            return TemplateReject<StyleTemplate>(BackendErrorCode.InvalidRequest, "草稿修订不能为空且长度受限。", "source.draftRevision");
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name) || definition.Style is null
            || definition.Properties is null)
            return TemplateReject<StyleTemplate>(BackendErrorCode.InvalidRequest, "草稿必须提供完整静态定义。", "source.definition");
        try
        {
            StyleTemplate template = StyleTemplate.Create("draft:" + draftRevision, definition.Name,
                (StyleTemplateKind)definition.Kind, ProjectSession.ImportTemplateLibraryStyle(definition.Style),
                LegacyStylePropertyMap.Import(definition.Properties));
            if (!TemplateLibraryEditService.IsStatic(template))
                return TemplateReject<StyleTemplate>(BackendErrorCode.CommandNotSupported,
                    "草稿含包材质或效果闭包，模板试样暂不开放。", "source.definition");
            return BackendOperationResult<StyleTemplate>.Success(template);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return TemplateReject<StyleTemplate>(BackendErrorCode.CommandRejected, "草稿静态定义未通过模板校验。", "source.definition"); }
    }

    /// <summary>独立库回执；准入先固定typed意图，修订、存在性和新身份只在执行owner中处理。</summary>
    public async Task<BackendOperationResult<TemplateLibraryCommandResponse>> ExecuteTemplateLibraryCommandAsync(string clientId,
        string libraryId, TemplateLibraryCommandRequest request, CancellationToken cancellationToken = default)
    {
        TemplateLibraryCommandRequest captured;
        try { captured = TemplateLibraryCommandCodec.Capture(request); }
        catch (TemplateLibraryBudgetExceededException) { return TemplateFailure<TemplateLibraryCommandResponse>(new TemplateLibraryResponseBudgetException()); }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return TemplateReject<TemplateLibraryCommandResponse>(BackendErrorCode.InvalidRequest, "模板命令形状或数值非法。", "command"); }
        return await Receipts.ExecuteAsync(clientId, captured.Metadata,
            "POST /api/v1/template-libraries/" + libraryId + "/commands", new { captured.ExpectedRevision, captured.Command }, async () =>
            {
                if (!BeginTemplateOperation()) return TemplateClosing<TemplateLibraryCommandResponse>();
                try
                {
                    if (libraryId != "personal") return libraryId == "builtin"
                        ? TemplateReject<TemplateLibraryCommandResponse>(BackendErrorCode.ReadOnlySession, "内置目录只读。", "libraryId")
                        : TemplateReject<TemplateLibraryCommandResponse>(BackendErrorCode.ResourceNotFound, "逻辑模板库不存在。", "libraryId");
                    if (_templateLibrary is null) return TemplateUnavailable<TemplateLibraryCommandResponse>();
                    // Resolve immutable packages before entering the personal IO gate; library references use that same gate.
                    FullTemplateDefinitionDto? fullInput = captured.Command switch
                    {
                        CreateTemplateCommandDto { Definition: FullTemplateDefinitionDto full } => full,
                        ReplaceTemplateCommandDto { Definition: FullTemplateDefinitionDto full } => full,
                        _ => null,
                    };
                    FullTemplateDefinition? fullDefinition = null;
                    if (fullInput is not null)
                    {
                        var resolved = await ResolveFullTemplateDefinitionAsync(clientId, fullInput).ConfigureAwait(false);
                        if (!resolved.Accepted) return BackendOperationResult<TemplateLibraryCommandResponse>.Rejected(resolved.Error!);
                        fullDefinition = resolved.Response!;
                    }
                    await _templateIo.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        return await Task.Run(async () =>
                        {
                            TemplateLibrarySnapshot? copySource = null;
                            if (captured.Command is CopyTemplateCommandDto copy)
                            {
                                if (!TryTemplateCatalog(copy.SourceLibraryId, out var sourceCatalog))
                                    return TemplateReject<TemplateLibraryCommandResponse>(BackendErrorCode.ResourceNotFound, "源模板库不存在。", "command.sourceLibraryId");
                                copySource = _templateLibrary.Read(sourceCatalog);
                                if (RevisionError(copy.SourceRevision, copySource) is { } sourceError)
                                    return BackendOperationResult<TemplateLibraryCommandResponse>.Rejected(sourceError with { Field = "command.sourceRevision" });
                            }
                            var destination = copySource?.Catalog == TemplateLibraryCatalog.Personal ? copySource : _templateLibrary.Read(TemplateLibraryCatalog.Personal);
                            if (RevisionError(captured.ExpectedRevision, destination) is { } revisionError)
                                return BackendOperationResult<TemplateLibraryCommandResponse>.Rejected(revisionError);
                            TemplateLibraryEdit edit = LibraryEdit(captured.Command, fullDefinition);
                            PreparedTemplateLibraryEdit prepared = _templateLibrary.Prepare(destination, edit, copySource);
                            // Every changed result must remain retrievable, including complete static detail.
                            var affected = prepared.Templates.FirstOrDefault(item => item.Id == prepared.TemplateId);
                            if (prepared.Changed && affected is not null) CheckTemplateCandidate(affected, prepared);
                            string previous = TemplateRevision(destination);
                            string upperRevision = prepared.Changed ? new string('f', 64) : previous;
                            var upper = new TemplateLibraryCommandResponse(libraryId, previous, upperRevision, prepared.Changed, prepared.TemplateId);
                            TemplateLibraryResponseBudget.Check(BackendOperationResult<TemplateLibraryCommandResponse>.Success(upper),
                                Receipts.MaxReceiptBytes, countValues: false, options: JsonSerializerOptions.Default);
                            var committed = await _templateLibrary.CommitPreparedAsync(prepared).ConfigureAwait(false);
                            // No fallible IO, validation, or cancellation check after the repository publication point.
                            return BackendOperationResult<TemplateLibraryCommandResponse>.Success(upper with { Revision = TemplateRevision(committed) });
                        }).ConfigureAwait(false);
                    }
                    finally { _templateIo.Release(); }
                }
                catch (Exception error) when (TemplateHandled(error)) { return TemplateFailure<TemplateLibraryCommandResponse>(error); }
                finally { EndTemplateOperation(); }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BackendOperationResult<T>> ReadTemplateLibraryAsync<T>(string libraryId, string? expectedRevision,
        Func<TemplateLibrarySnapshot, BackendOperationResult<T>> project, CancellationToken token)
    {
        if (!BeginTemplateOperation()) return TemplateClosing<T>();
        try
        {
            if (!TryTemplateCatalog(libraryId, out var catalog)) return TemplateReject<T>(BackendErrorCode.ResourceNotFound, "逻辑模板库不存在。", "libraryId");
            if (_templateLibrary is null) return TemplateUnavailable<T>();
            BackendOperationResult<T> Read()
            {
                var snapshot = _templateLibrary.Read(catalog);
                return expectedRevision is not null && RevisionError(expectedRevision, snapshot) is { } error
                    ? BackendOperationResult<T>.Rejected(error) : project(snapshot);
            }
            if (catalog == TemplateLibraryCatalog.BuiltIn) return Read();
            await _templateIo.WaitAsync(token).ConfigureAwait(false);
            try { return await Task.Run(Read, token).ConfigureAwait(false); }
            finally { _templateIo.Release(); }
        }
        catch (Exception error) when (TemplateHandled(error)) { return TemplateFailure<T>(error); }
        finally { EndTemplateOperation(); }
    }

    private static TemplateLibraryEdit LibraryEdit(TemplateLibraryCommandDto command, FullTemplateDefinition? fullDefinition) => command switch
    {
        CreateTemplateCommandDto create => new TemplateLibraryEdit.Create((StaticTemplateDefinition?)fullDefinition ?? LibraryDefinition(create.Definition)),
        ReplaceTemplateCommandDto replace => new TemplateLibraryEdit.Replace(replace.TemplateId, (StaticTemplateDefinition?)fullDefinition ?? LibraryDefinition(replace.Definition)),
        CopyTemplateCommandDto copy => new TemplateLibraryEdit.Copy(copy.TemplateId, copy.Name),
        RenameTemplateCommandDto rename => new TemplateLibraryEdit.Rename(rename.TemplateId, rename.Name),
        DeleteTemplateCommandDto delete => new TemplateLibraryEdit.Delete(delete.TemplateId),
        _ => throw new JsonException("未知模板意图。"),
    };
    private static StaticTemplateDefinition LibraryDefinition(StaticTemplateDefinitionDto definition) => new(definition.Name,
        (StyleTemplateKind)definition.Kind, ProjectSession.ImportTemplateLibraryStyle(definition.Style),
        LegacyStylePropertyMap.Import(definition.Properties));
    private static bool TryTemplateCatalog(string id, out TemplateLibraryCatalog catalog)
    { catalog = id == "builtin" ? TemplateLibraryCatalog.BuiltIn : TemplateLibraryCatalog.Personal; return id is "builtin" or "personal"; }
    private static string TemplateRevision(TemplateLibrarySnapshot snapshot) => snapshot.Revision.Sha256 ?? "missing";
    private static BackendErrorResponse? RevisionError(string expected, TemplateLibrarySnapshot snapshot) =>
        expected != "missing" && (expected.Length != 64 || expected.Any(c => !char.IsAsciiHexDigitLower(c)))
            ? new(BackendErrorCode.InvalidRequest, "修订必须是missing或小写64位摘要。", "expectedRevision")
            : expected != TemplateRevision(snapshot) ? new(BackendErrorCode.RevisionConflict, "模板库已改变，请重新读取。", "expectedRevision") : null;
    private static TemplateProperty[] TemplateProperties(StyleTemplate template) => LegacyStylePropertyMap.Export(template.Properties);
    private static bool TemplateRuntimeSupported(StyleTemplate template) => Enum.IsDefined(template.Kind);
    private static string TemplateDefinitionAvailability(StyleTemplate template) => TemplateLibraryEditService.IsStatic(template)
        ? "staticAvailable" : TemplateRuntimeSupported(template) ? "advancedRuntimeAvailable" : "advancedUnsupported";
    private static TemplateSummaryDto TemplateSummary(StyleTemplate template)
    {
        TemplateLibraryResponseBudget.Text(template.Id); TemplateLibraryResponseBudget.Text(template.Name);
        string hash = Convert.ToHexStringLower(SHA256.HashData(StyleTemplateLibrary.EncodeForIdentity([template])));
        return new(template.Id, template.Name, (TemplateKind)template.Kind, hash, TemplateProperties(template),
            !template.EffectRecipes.IsEmpty, !SourceMaterialCatalog.Collect(template.Style).IsEmpty, !template.Packages.IsEmpty);
    }
    private static void CheckTemplateCandidate(StyleTemplate template, PreparedTemplateLibraryEdit prepared)
    {
        var summary = TemplateSummary(template);
        int index = prepared.Templates.IndexOf(template);
        TemplateLibraryResponseBudget.Check(new TemplateListResponse("personal", new string('f', 64), index, prepared.Templates.Length,
            index + 1 < prepared.Templates.Length ? index + 1 : null, [summary]));
        // Advanced objects expose only truthful summaries, never a lossy full definition.
        StaticTemplateDefinitionDto? definition = TemplateLibraryEditService.IsStatic(template)
            ? new(template.Name, (TemplateKind)template.Kind, ProjectSession.ExportTemplateLibraryStyle(template.Style), TemplateProperties(template)) : null;
        TemplateLibraryResponseBudget.Check(new TemplateDetailResponse("personal", new string('f', 64), summary,
            TemplateDefinitionAvailability(template), definition));
        TemplateLibraryResponseBudget.Check(FullTemplateDetail("personal", new string('f', 64), template));
    }

    private static bool TemplateHandled(Exception error) => error is TemplateLibraryEditException or LibraryRevisionConflictException
        or LibraryBusyException or IOException or UnauthorizedAccessException or EffectPackageException or JsonException
        or ArgumentException or TemplateLibraryResponseBudgetException;
    private static BackendOperationResult<T> TemplateFailure<T>(Exception error)
    {
        BackendErrorCode code; string failure; string field;
        switch (error)
        {
            case TemplateLibraryResponseBudgetException: code = BackendErrorCode.ResourceTooLarge; failure = "TemplateBudgetExceeded"; field = "command"; break;
            case LibraryRevisionConflictException: code = BackendErrorCode.RevisionConflict; failure = "LibraryRevisionConflict"; field = "expectedRevision"; break;
            case LibraryBusyException: code = BackendErrorCode.SessionConflict; failure = "LibraryBusy"; field = "library"; break;
            case TemplateLibraryEditException domain:
                code = domain.Failure switch
                {
                    TemplateLibraryEditFailure.TemplateNotFound => BackendErrorCode.ResourceNotFound,
                    TemplateLibraryEditFailure.ReadOnlyLibrary => BackendErrorCode.ReadOnlySession,
                    TemplateLibraryEditFailure.InvalidDefinition => BackendErrorCode.CommandRejected,
                    _ => BackendErrorCode.CommandRejected,
                };
                failure = domain.Failure.ToString(); field = domain.Failure == TemplateLibraryEditFailure.TemplateNotFound ? "templateId" : "command"; break;
            default: code = BackendErrorCode.CommandRejected; failure = "LibraryInvalid"; field = "library"; break;
        }
        return BackendOperationResult<T>.Rejected(new(code, "模板库操作未完成；原库保留，请按错误代码处理。", field, FailureCode: failure));
    }
    private static BackendOperationResult<T> TemplateReject<T>(BackendErrorCode code, string message, string field) =>
        BackendOperationResult<T>.Rejected(new(code, message, field));
    private static BackendOperationResult<T> TemplateClosing<T>() => TemplateReject<T>(BackendErrorCode.SessionClosing, "模板库服务正在关闭。", "library");
    private static BackendOperationResult<T> TemplateUnavailable<T>() => TemplateReject<T>(BackendErrorCode.CommandNotSupported, "模板库尚未配置。", "library");
    private bool BeginTemplateOperation()
    {
        lock (_templateOperationGate)
        {
            if (_templateStopping || Volatile.Read(ref _stopping) != 0) return false;
            if (_templateOperations++ == 0) _templateIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }
    private void EndTemplateOperation()
    { lock (_templateOperationGate) { if (--_templateOperations == 0) _templateIdle.TrySetResult(); } }
    private void StopTemplateLibraryAccepting() { lock (_templateOperationGate) _templateStopping = true; }
    private Task DrainTemplateLibraryAsync() { lock (_templateOperationGate) return _templateIdle.Task; }
    private static TaskCompletionSource CompletedTemplateOperation()
    { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
}
