using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Application;
using System.Security.Cryptography;

namespace MakoKaraGenerator.Backend.Server;

/// <summary>HTTP可信组合：固定项目输入、单层client回执、资源引用和租约清理。</summary>
internal sealed partial class PreviewBackendService : IAsyncDisposable
{
    private readonly ProjectSessionRegistry _projects;
    private readonly BackendPreviewSessionRegistry _previews;
    private readonly ResourceStore _resources;
    private readonly ClientRegistry _clients;
    private readonly IRenderFrameSessionFactory? _factory;
    private readonly BackendRenderBackendConfiguration? _renderBackends;
    private readonly string _defaultRenderBackend;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _membership = new(1, 1);
    private bool _stopping;
    private Task? _dispose;
    private readonly object _disposeGate = new();
    public PreviewRuntimeInfoResponse RuntimeInfo { get; }

    internal PreviewBackendService(ProjectSessionRegistry projects, BackendPreviewSessionRegistry previews,
        ResourceStore resources, ClientRegistry clients, IRenderFrameSessionFactory? factory, PreviewRuntimeInfoResponse runtimeInfo,
        BackendRenderBackendConfiguration? renderBackends = null)
    {
        _projects = projects; _previews = previews; _resources = resources; _clients = clients;
        _factory = factory; RuntimeInfo = runtimeInfo;
        _renderBackends = renderBackends;
        _defaultRenderBackend = renderBackends?.DefaultBackend
            ?? (factory as IBackendPreviewRenderBackendFactory)?.RenderBackend ?? "cpu";
        _previews.Updated += PublishEvent;
    }

    private void PublishEvent(BackendPreviewSessionEvent update)
    {
        PreviewSessionResponse session = update.Status.Session;
        _projects.PublishPreviewEvent(update.ClientId, new(session.PreviewId, session.PreviewGeneration,
            session.Source.SourceRevision, session.State, session.LatestRequestSeq,
            session.State == "ready" ? session.LatestFrame : null, update.Status.Failure, update.Status.RenderFailureCode,
            session.RenderBackend));
    }

    internal Task<BackendOperationResult<SceneMeasurementResponse>> MeasureSceneAsync(string client, string sessionId,
        SceneMeasurementRequest request, CancellationToken cancellationToken = default) =>
        _projects.MeasureSceneAsync(client, sessionId, request, _previews, cancellationToken);

    public Task<BackendOperationResult<PreviewSessionResponse>> CreateAsync(string client, PreviewSessionRequest request) =>
        _projects.Receipts.ExecuteAsync(client, request.Metadata, "POST /api/v1/preview-sessions", request with { Metadata = null },
            () => MutateAsync(client, async () =>
            {
                if (request.Purpose != "preview") return Reject<PreviewSessionResponse>(BackendErrorCode.CommandNotSupported,
                    "当前静态项目预览只支持purpose=preview。");
                BackendErrorResponse? size = ValidateSize(request.Size);
                if (size is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(size);
                var backend = await PrepareRenderBackendAsync(request.RenderBackend).ConfigureAwait(false);
                if (!backend.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(backend.Error!);
                BackendPreviewInputCaptureResult captured = await CaptureAsync(client, request.Source, backend.Response!,
                    request.AlphaMode == PreviewAlphaMode.Straight).ConfigureAwait(false);
                if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
                BackendPreviewSessionResult result;
                try
                {
                    result = await _projects.RegisterPreparedPreviewAsync(client, captured.Input!,
                        () => _previews.Create(client, request, captured.Input!)).ConfigureAwait(false);
                }
                catch { captured.Input!.RetentionLease?.Dispose(); throw; }
                if (!result.Accepted) captured.Input!.RetentionLease?.Dispose();
                return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                    : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
            }));

    /// <summary>创建只读预设候选试看；不会调用工程命令或写入模板库。</summary>
    public Task<BackendOperationResult<PreviewSessionResponse>> CreatePresetApplicationPreviewAsync(
        string client, string projectSessionId, PresetApplicationPreviewRequest request) =>
        _projects.Receipts.ExecuteAsync(client, request.Metadata,
            "POST /api/v1/project-sessions/" + projectSessionId + "/preset-application-previews",
            request with { Metadata = null }, () => MutateAsync(client, async () =>
            {
                if (request.ExpectedProjectRevision is null)
                    return Reject<PreviewSessionResponse>(BackendErrorCode.InvalidRequest, "候选试看必须提供工程精确修订。");
                if (request.Source is not null && (request.TemplateLibraryId is not null || request.TemplateLibraryRevision is not null
                    || request.TemplateId is not null || request.TemplateRevision is not null))
                    return Reject<PreviewSessionResponse>(BackendErrorCode.InvalidRequest, "候选试看source不得与原库引用字段混用。");
                BackendErrorResponse? size = ValidateSize(request.Size);
                if (size is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(size);
                var backend = await PrepareRenderBackendAsync(request.RenderBackend).ConfigureAwait(false);
                if (!backend.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(backend.Error!);
                TemplatePreviewSourceDto source = request.Source ?? new("savedTemplate", request.TemplateLibraryId,
                    request.TemplateLibraryRevision, request.TemplateId, request.TemplateRevision);
                BackendOperationResult<StyleTemplate> template = await ResolveTemplatePreviewSourceAsync(client, source).ConfigureAwait(false);
                if (!template.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(template.Error!);
                BackendPreviewInputCaptureResult captured = await CaptureCandidateAsync(client, projectSessionId, request,
                    template.Response!, backend.Response!).ConfigureAwait(false);
                if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
                PreviewSessionRequest previewRequest = new(captured.Input!.Source, request.Size, request.AlphaMode,
                    Purpose: "preview", PreviewKind: "presetCandidate", Committable: false, RenderBackend: backend.Response);
                BackendPreviewSessionResult result;
                try { result = await _projects.RegisterPreparedPreviewAsync(client, captured.Input,
                    () => _previews.Create(client, previewRequest, captured.Input)).ConfigureAwait(false); }
                catch { captured.Input.RetentionLease?.Dispose(); throw; }
                if (!result.Accepted) captured.Input.RetentionLease?.Dispose();
                return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                    : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
            }));

    /// <summary>创建脱离工程的模板固定试样；输入快照只存于预览会话，不写入个人库。</summary>
    public Task<BackendOperationResult<PreviewSessionResponse>> CreateTemplatePreviewAsync(
        string client, TemplatePreviewSessionRequest request) =>
        _projects.Receipts.ExecuteAsync(client, request.Metadata,
            "POST /api/v1/template-preview-sessions", request with { Metadata = null }, () => MutateAsync(client, async () =>
            {
                if (request.SampleId is not (TemplatePreviewLimits.DefaultSampleId or TemplatePreviewLimits.SingleLineSampleId
                    or TemplatePreviewLimits.MultiLineSampleId))
                    return Reject<PreviewSessionResponse>(BackendErrorCode.InvalidRequest, "模板试样来源或样本版本不受支持。");
                if (_factory is null)
                    return Reject<PreviewSessionResponse>(BackendErrorCode.PreviewUnavailable,
                        RuntimeInfo.DisabledReason?.Message ?? "预览Worker未配置。");
                BackendErrorResponse? size = ValidateSize(request.Size);
                if (size is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(size);
                var backend = await PrepareRenderBackendAsync(request.RenderBackend).ConfigureAwait(false);
                if (!backend.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(backend.Error!);
                request = request with { RenderBackend = backend.Response };
                BackendOperationResult<StyleTemplate> template = await ResolveTemplatePreviewSourceAsync(client, request.Source).ConfigureAwait(false);
                if (!template.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(template.Error!);
                if (request.SampleId != TemplatePreviewLimits.DefaultSampleId)
                    return await CreateWorkshopTemplatePreviewAsync(client, request, template.Response!).ConfigureAwait(false);
                if (request.TargetKey is not null || request.MaterialSlot is not null)
                    return Reject<PreviewSessionResponse>(BackendErrorCode.InvalidRequest,
                        "default-v1保留旧固定样本；目标和材质焦点请选择工坊样本。");
                try
                {
                    ProjectSnapshot? snapshot = TemplateSample(template.Response!, out string? sampleError);
                    if (snapshot is null)
                        return Reject<PreviewSessionResponse>(BackendErrorCode.CommandNotSupported,
                            sampleError ?? "模板样本不支持当前模板类型。");
                    if (_factory is not IBackendPreviewProfileCapability)
                        return BackendOperationResult<PreviewSessionResponse>.Rejected(new(
                            BackendErrorCode.PreviewUnavailable, "当前预览Worker未提供来源能力握手。", "worker",
                            FailureCode: "WorkerStaticProfileMismatch"));
                    BackendPreviewPinnedInput sample = new(TemplateSourceIdentity(request.Source), snapshot,
                        SelectFactory(_factory!, backend.Response!)) { RenderBackend = backend.Response! };
                    BackendPreviewInputCaptureResult captured = await CompleteCaptureAsync(new(sample, null,
                        RuntimeInput: BackendProjectRuntimeInputLease.CreateDetached(snapshot)), null,
                        request.AlphaMode == PreviewAlphaMode.Straight).ConfigureAwait(false);
                    if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
                    BackendPreviewPinnedInput input = captured.Input!;
                    PreviewSessionRequest previewRequest = new(input.Source, request.Size, request.AlphaMode,
                        Purpose: "template-sample", PreviewKind: "template", Committable: false, RenderBackend: backend.Response);
                    BackendPreviewSessionResult result;
                    try { result = _previews.Create(client, previewRequest, input); }
                    catch { input.RetentionLease?.Dispose(); throw; }
                    if (!result.Accepted) input.RetentionLease?.Dispose();
                    return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                        : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
                }
                catch (SnapshotContractException error)
                { return Reject<PreviewSessionResponse>(BackendErrorCode.CommandNotSupported, "模板试样定义未通过固定输入能力校验：" + error.Code); }
            }));

    private static ProjectSnapshot? TemplateSample(StyleTemplate template, out string? error)
    {
        error = null;
        // Advanced effects need complete token coverage so the sample token owns its full glyph cluster.
        string sampleText = TemplateLibraryEditService.IsStatic(template) ? "Template sample" : "Template";
        LyricLine line = LyricLine.Create("sample", sampleText, TimeRange.Create(0, 2_500_000), TimingStatus.Imported,
            [LyricToken.Create("sample-token", "Template", TimeRange.Create(100_000, 1_200_000), TimingStatus.Imported)]);
        Project project = Project.Create("template-preview-sample",
            Composition.Create(640, 360, FrameRate.Fps30, new(18, 22, 32, 255), 3_000_000),
            LyricsDocument.Create([line]));
        string? token = template.Kind == StyleTemplateKind.Token ? "sample-token" : null;
        if (!TemplateLibraryEditService.IsStatic(template))
        {
            var inputs = template.EffectRecipes.Where(recipe => recipe.BranchInput is not null)
                .Select(recipe => recipe.BranchInput!).Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, _ => project.Lyrics.Branches[0].Id, StringComparer.Ordinal);
            ProjectEffectBinding target = token is null ? new(ProjectEffectBindingKind.Lines, ids: ["sample"])
                : new(ProjectEffectBindingKind.Tokens, ids: [token]);
            if (!PresetApplicationsEditor.TryApply(project, template, [target], inputs, out Project result, out string advancedReason))
            { error = advancedReason; return null; }
            return ProjectSnapshot.FromProject(result);
        }
        if (!StyleTemplateApplicator.TryPreview(project, template, "sample", token, false,
            out StyleTemplateApplyPreview applied, out string reason))
        { error = reason; return null; }
        project = applied.Result;
        return ProjectSnapshot.FromProject(project);
    }

    private static BackendErrorResponse? ValidateTemplateSource(TemplatePreviewSourceDto? source)
    {
        if (source is null)
            return new(BackendErrorCode.InvalidRequest, "模板试样必须提供来源。", "source");

        bool saved = source.Kind == "savedTemplate";
        bool draft = source.Kind == "draft";
        if (!saved && !draft)
            return new(BackendErrorCode.InvalidRequest, "模板试样来源必须是savedTemplate或draft。", "source.kind");

        if (saved)
        {
            if (string.IsNullOrWhiteSpace(source.LibraryId)
                || string.IsNullOrWhiteSpace(source.LibraryRevision)
                || string.IsNullOrWhiteSpace(source.TemplateId)
                || string.IsNullOrWhiteSpace(source.TemplateRevision))
                return new(BackendErrorCode.InvalidRequest, "savedTemplate必须完整提供库和模板精确修订。", "source");
            if (source.DraftRevision is not null || source.Definition is not null || source.DefinitionFormat is not null)
                return new(BackendErrorCode.InvalidRequest, "savedTemplate不得混入draft字段。", "source");
            return null;
        }

        if (string.IsNullOrWhiteSpace(source.DraftRevision) || source.Definition is null)
            return new(BackendErrorCode.InvalidRequest, "draft必须完整提供draftRevision和definition。", "source");
        if (source.DefinitionFormat is not (null or "static" or "full")
            || source.DefinitionFormat == "full" != (source.Definition is FullTemplateDefinitionDto))
            return new(BackendErrorCode.InvalidRequest, "完整草稿必须显式提供definitionFormat=full及完整定义。", "source.definitionFormat");
        if (source.LibraryId is not null || source.LibraryRevision is not null
            || source.TemplateId is not null || source.TemplateRevision is not null)
            return new(BackendErrorCode.InvalidRequest, "draft不得混入savedTemplate字段。", "source");
        return null;
    }

    public Task<BackendOperationResult<PreviewSessionResponse>> UpdateAsync(string client, string id, PreviewProjectInputRequest request) =>
        _projects.Receipts.ExecuteAsync(client, request.Metadata, "PUT /api/v1/preview-sessions/" + id + "/input",
            request with { Metadata = null }, () => MutateAsync(client, async () =>
            {
                BackendErrorResponse? size = ValidateSize(request.Size);
                if (size is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(size);
                if (request.Source is null)
                    return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.InvalidRequest,
                        "必须提供固定预览来源。", "source"));
                BackendPreviewSessionResult existing = _previews.Read(client, id);
                if (!existing.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(existing.Error!);
                if (existing.Status!.Session.PreviewKind is "presetCandidate" or "template" or "editCandidate")
                    return Reject<PreviewSessionResponse>(BackendErrorCode.CommandNotSupported,
                        "固定候选或模板试样输入不可更新；请创建新的预览会话。" );
                if (existing.Status.Session.PreviewKind == "authorDraft"
                    && (request.Source.Kind != "authorDraft" || request.Source.SourceId != existing.Status.Session.Source.SourceId))
                    return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.CommandNotSupported,
                        "作者预览换源必须保持同一草稿身份。", "source"));
                if (existing.Status.Session.PreviewKind == "project" && request.Source.Kind != "project")
                    return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.CommandNotSupported,
                        "工程预览换源必须保持工程来源类型。", "source"));
                if (existing.Status!.Session.PreviewGeneration != request.ExpectedPreviewGeneration
                    || existing.Status.Session.Source.SourceRevision != request.ExpectedSourceRevision)
                    return Reject<PreviewSessionResponse>(BackendErrorCode.PreviewGenerationConflict, "预览输入基线已改变。");
                var backend = await PrepareRenderBackendAsync(request.RenderBackend, existing.Status.Session.RenderBackend).ConfigureAwait(false);
                if (!backend.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(backend.Error!);
                BackendPreviewInputCaptureResult captured = await CaptureAsync(client, request.Source, backend.Response!,
                    existing.Status.Session.AlphaMode == PreviewAlphaMode.Straight).ConfigureAwait(false);
                if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
                BackendPreviewSessionResult result;
                try
                {
                    result = await _projects.RegisterPreparedPreviewAsync(client, captured.Input!,
                        () => _previews.Update(client, id, request.ExpectedSourceRevision,
                            new(request.ExpectedPreviewGeneration, request.Source, request.Size, request.AlphaMode,
                                RenderBackend: backend.Response), captured.Input!)).ConfigureAwait(false);
                }
                catch { captured.Input!.RetentionLease?.Dispose(); throw; }
                if (!result.Accepted) captured.Input!.RetentionLease?.Dispose();
                return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                    : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
            }));

    public Task<BackendOperationResult<PreviewFrameAcceptedResponse>> RequestFrameAsync(string client, string id, PreviewFrameCommandRequest request) =>
        _projects.Receipts.ExecuteAsync(client, request.Metadata, "POST /api/v1/preview-sessions/" + id + "/frame-requests",
            request with { Metadata = null }, () => MutateAsync(client, () =>
            {
                BackendPreviewFrameRequestResult result = _previews.RequestFrame(client, id, request.ExpectedSourceRevision,
                    new(request.PreviewGeneration, request.RequestSeq, request.TimeUs, request.Intent));
                return Task.FromResult(result.Accepted ? BackendOperationResult<PreviewFrameAcceptedResponse>.Success(result.Response!)
                    : BackendOperationResult<PreviewFrameAcceptedResponse>.Rejected(result.Error!));
            }));

    public Task<BackendOperationResult<PreviewSessionStateResponse>> CloseAsync(string client, string id, ClosePreviewSessionRequest request) =>
        _projects.Receipts.ExecuteAsync(client, request.Metadata, "POST /api/v1/preview-sessions/" + id + "/close",
            request with { Metadata = null }, () => MutateAsync(client, async () =>
            {
                BackendPreviewSessionResult result = await _previews.CloseAsync(client, id,
                    request.ExpectedPreviewGeneration, request.ExpectedSourceRevision).ConfigureAwait(false);
                return result.Accepted ? BackendOperationResult<PreviewSessionStateResponse>.Success(new(result.Status!.Session, result.Status.Failure, result.Status.RenderFailureCode))
                    : BackendOperationResult<PreviewSessionStateResponse>.Rejected(result.Error!);
            }));

    public BackendOperationResult<PreviewSessionStateResponse> Read(string client, string id)
    {
        BackendPreviewSessionResult result = _previews.Read(client, id);
        return result.Accepted ? BackendOperationResult<PreviewSessionStateResponse>.Success(new(result.Status!.Session, result.Status.Failure, result.Status.RenderFailureCode))
            : BackendOperationResult<PreviewSessionStateResponse>.Rejected(result.Error!);
    }

    private async Task<BackendOperationResult<T>> MutateAsync<T>(string client, Func<Task<BackendOperationResult<T>>> action,
        CancellationToken cancellationToken = default)
    {
        await _membership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || _shutdown.IsCancellationRequested) return Reject<T>(BackendErrorCode.SessionClosing, "预览服务正在关闭。");
            if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant)
                return Reject<T>(BackendErrorCode.ClientTokenExpired, "客户端租约已休眠，请恢复后重建预览。");
            return await action().ConfigureAwait(false);
        }
        finally { _membership.Release(); }
    }

    private async Task<BackendPreviewInputCaptureResult> CaptureAsync(string client, PreviewSourceDto? source, string renderBackend,
        bool transparentOutput = false)
    {
        if (source is null || source.Kind is not ("project" or "authorDraft") || string.IsNullOrWhiteSpace(source.SourceId)
            || string.IsNullOrWhiteSpace(source.SourceRevision))
            return new(null, new(BackendErrorCode.CommandNotSupported, "预览来源必须是固定项目或作者草稿修订。", "source"));
        if (_factory is null) return new(null, RuntimeInfo.DisabledReason ?? new(BackendErrorCode.CommandNotSupported, "预览Worker未配置。"));
        if (source.Kind == "authorDraft")
        {
            BackendOperationResult<AuthorPreviewCapture> author = await _projects.CaptureAuthorPreviewAsync(client, source,
                renderBackend, _shutdown.Token).ConfigureAwait(false);
            if (!author.Accepted) return new(null, author.Error!);
            try
            {
                IRenderFrameSessionFactory fixedFactory = SelectFactory(_factory, renderBackend);
                if (fixedFactory is IBackendPreviewProfileCapability capability
                    && await capability.EnsureProfileAsync(author.Response!.PreparedInput.SourceProfile).ConfigureAwait(false) is { } capabilityError)
                {
                    author.Response.PreparedInput.Dispose();
                    return new(null, capabilityError);
                }
                if (fixedFactory is IBackendRuntimePreviewFactory runtimeFactory)
                    fixedFactory = runtimeFactory.Bind(author.Response!.PreparedInput);
                else { author.Response!.PreparedInput.Dispose(); return new(null, new(BackendErrorCode.PreviewUnavailable, "当前预览工厂未提供作者Runtime输入能力。", "worker")); }
                if (author.Response.PreparedInput.SourceBytes.Length > StaticRenderWorkerContract.MaximumSnapshotBytes)
                {
                    author.Response.PreparedInput.Dispose();
                    return new(null, new(BackendErrorCode.ResourceTooLarge, "作者固定预览输入超过Worker预算。", "source"));
                }
                BackendPreviewPinnedInput input = new(source, author.Response.Snapshot, fixedFactory,
                    RetentionLease: author.Response.PreparedInput)
                { PreparedInput = author.Response.PreparedInput, VerifiedProfile = author.Response.PreparedInput.SourceProfile,
                    RenderBackend = renderBackend };
                return new(input, null);
            }
            catch
            { author.Response?.PreparedInput.Dispose(); throw; }
        }
        if (!_projects.TryGet(client, source.SourceId, out ProjectSession project))
            return new(null, new(BackendErrorCode.SessionNotFound, "预览来源工程不存在。", "sourceId"));
        IDisposable? reservation = null;
        BackendProjectImageReadLease? imageSource = null;
        try
        {
            BackendPreviewInputCaptureResult captured = await project.CapturePreviewInputAsync(client,
                source.SourceRevision, SelectFactory(_factory, renderBackend), captureResourceFactory: audioAssetId =>
                    CaptureResourceFactory(client, audioAssetId, out reservation, renderBackend), captureImageSource: false, captureRuntimeSource: true).ConfigureAwait(false);
            if (captured.Error is not null) return captured;
            IDisposable? ownedReservation = reservation; reservation = null;
            return await CompleteCaptureAsync(captured, ownedReservation, transparentOutput).ConfigureAwait(false);
        }
        catch (SnapshotContractException error)
        { return new(null, error.Code == "WorkerImagePreviewProfileMismatch"
            ? new(BackendErrorCode.PreviewUnavailable, "当前预览工厂未提供固定图片能力。", "worker", FailureCode: error.Code)
            : error.Code == "SnapshotSizeExceeded"
            ? new(BackendErrorCode.ResourceTooLarge, "固定来源超过快照字节预算。", imageSource is null ? "source" : "background", FailureCode: error.Code)
            : error.Code is "ProjectImageSourceChanged" or "ProjectImageSourceUnavailable"
            ? new(BackendErrorCode.CommandNotSupported,
                error.Code == "ProjectImageSourceChanged" ? "工程图片大小或内容已改变。" : "工程图片的已保存授权闭包不可用。",
                "background", FailureCode: error.Code)
            : new(BackendErrorCode.CommandNotSupported, "项目含当前预览合同不支持的输入。", "source", FailureCode: error.Code)); }
        catch (ResourceStoreException error)
        { return new(null, new(error.Code, "预览固定资源不可用。", "source")); }
        finally { imageSource?.Dispose(); reservation?.Dispose(); }
    }

    private async Task<BackendPreviewInputCaptureResult> CompleteCaptureAsync(BackendPreviewInputCaptureResult captured,
        IDisposable? reservation, bool transparentOutput = false)
    {
        BackendPreparedProjectRuntimeInput? prepared = null;
        using BackendProjectRuntimeInputLease? runtimeLease = captured.RuntimeInput;
        try
        {
            if (runtimeLease is null)
                return new(null, new(BackendErrorCode.CommandNotSupported, "固定工程输入授权不可用。", "source"));
            string renderBackend = (captured.Input!.Factory as IBackendPreviewRenderBackendFactory)?.RenderBackend
                ?? captured.Input.RenderBackend;
            if (renderBackend != "gpu" && ScriptBackendRequirement.RequiresVulkan(captured.Input.Snapshot))
                return new(null, new(BackendErrorCode.CommandNotSupported, "SDK v2 效果要求 GPU 渲染后端。", "renderBackend",
                    FailureCode: "PackageBackendUnsupported"));
            // 透明预览不读背景：媒体依赖固定为透明无媒体输入，与透明导出一致。
            runtimeLease.TransparentOutput = transparentOutput;
            var result = await runtimeLease.PrepareAsync().ConfigureAwait(false);
            if (!result.Accepted) return new(null, result.Error);
            prepared = result.Response!;
            string profile = prepared.SourceProfile;
            if (RuntimeInfo.SourceProfiles is not null && !RuntimeInfo.SourceProfiles.Contains(profile, StringComparer.Ordinal))
                return new(null, new(BackendErrorCode.PreviewUnavailable, "当前预览部署未公布所选来源能力。", "worker", FailureCode: "WorkerRuntimeInputProfileMismatch"));
            if (captured.Input!.Factory is IBackendPreviewProfileCapability capability
                && await capability.EnsureProfileAsync(profile).ConfigureAwait(false) is { } capabilityError)
                return new(null, capabilityError);
            IRenderFrameSessionFactory fixedFactory = captured.Input.Factory;
            if (profile == ProjectRuntimeInputWorkerContract.Profile)
            {
                if (fixedFactory is not IBackendRuntimePreviewFactory runtimeFactory)
                    return new(null, new(BackendErrorCode.PreviewUnavailable, "当前预览工厂未提供固定工程输入能力。", "worker", FailureCode: "WorkerRuntimeInputProfileMismatch"));
                fixedFactory = runtimeFactory.Bind(prepared);
            }
            if (prepared.SourceBytes.Length > StaticRenderWorkerContract.MaximumSnapshotBytes)
                return new(null, new(BackendErrorCode.ResourceTooLarge, "固定来源超过Worker输入预算。", "source"));
            BackendPreviewPinnedInput owned = captured.Input with { Factory = fixedFactory,
                RetentionLease = new CombinedRetention(prepared, reservation), PreparedInput = prepared, VerifiedProfile = profile,
                PackageClosure = PackageClosure(captured.Input.Snapshot),
                RenderBackend = (fixedFactory as IBackendPreviewRenderBackendFactory)?.RenderBackend ?? renderBackend };
            prepared = null; reservation = null;
            return new(owned, null);
        }
        catch (SnapshotContractException error)
        { return new(null, new(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "固定预览来源未通过能力或预算校验。", "source", FailureCode: error.Code)); }
        finally { captured.ImageSource?.Dispose(); prepared?.Dispose(); reservation?.Dispose(); }
    }

    private sealed class CombinedRetention(IDisposable first, IDisposable? second) : IDisposable
    {
        private readonly object _gate = new();
        private bool _firstDisposed, _secondDisposed;
        public void Dispose()
        {
            lock (_gate)
            {
                Exception? primary = null;
                if (!_firstDisposed)
                    try { first.Dispose(); _firstDisposed = true; } catch (Exception error) { primary = error; }
                if (!_secondDisposed)
                    try { second?.Dispose(); _secondDisposed = true; }
                    catch (Exception error) { if (primary is null) primary = error; else primary.Data["SecondaryCleanupFailure"] = error; }
                if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            }
        }
    }

    private async Task<BackendPreviewInputCaptureResult> CaptureCandidateAsync(string client,
        string projectSessionId, PresetApplicationPreviewRequest request,
        MakoKaraGenerator.Core.Projects.StyleTemplate template, string renderBackend)
    {
        if (_factory is null) return new(null, RuntimeInfo.DisabledReason ?? new(BackendErrorCode.PreviewUnavailable,
            "预览Worker未配置。", "worker"));
        if (!_projects.TryGet(client, projectSessionId, out ProjectSession project))
            return new(null, new(BackendErrorCode.SessionNotFound, "预览来源工程不存在。", "projectSessionId"));
        IDisposable? reservation = null;
        try
        {
            BackendPreviewInputCaptureResult captured = await project.CapturePresetCandidatePreviewInputAsync(client,
                request.ExpectedProjectRevision, template, request.Targets, request.BranchInputs, SelectFactory(_factory, renderBackend),
                captureResourceFactory: audioAssetId => CaptureResourceFactory(client, audioAssetId, out reservation, renderBackend)).ConfigureAwait(false);
            if (captured.Error is not null) return captured;
            IDisposable? ownedReservation = reservation; reservation = null;
            return await CompleteCaptureAsync(captured, ownedReservation).ConfigureAwait(false);
        }
        catch (SnapshotContractException error)
        {
            return new(null, error.Code == "SnapshotSizeExceeded"
                ? new(BackendErrorCode.ResourceTooLarge, "候选预设来源超过快照字节预算。", "targets")
                : new(BackendErrorCode.CommandNotSupported, "候选预设包含当前静态能力不支持的输入。", "templateId",
                    FailureCode: error.Code));
        }
        catch (ResourceStoreException error) { return new(null, new(error.Code, "预览固定资源不可用。", "source")); }
        finally { reservation?.Dispose(); }
    }

    private ReferencedFactory CaptureResourceFactory(string client, string audioAssetId, out IDisposable? reservation,
        string renderBackend)
    {
        reservation = string.IsNullOrWhiteSpace(audioAssetId) ? null : _resources.AcquireReference(client, audioAssetId);
        return new ReferencedFactory(SelectFactory(_factory!, renderBackend), string.IsNullOrWhiteSpace(audioAssetId)
            ? static () => null : () => _resources.AcquireReference(client, audioAssetId));
    }

    private static PreviewPackageClosureDto? PackageClosure(ProjectSnapshot snapshot)
    {
        if (snapshot.Effects.Packages.IsEmpty) return null;
        PreviewPackageClosureEntryDto[] packages = [.. snapshot.Effects.Packages
            .OrderBy(package => package.PackageId, StringComparer.Ordinal)
            .ThenBy(package => package.Revision, StringComparer.Ordinal)
            .Select(package => new PreviewPackageClosureEntryDto(package.PackageId, package.Revision,
                package.Files.Length, package.Files.Sum(file => (long)file.Content.Length),
                [.. package.Files.Select(file => file.Role).Distinct(StringComparer.Ordinal).OrderBy(role => role, StringComparer.Ordinal)]))];
        int fileCount = packages.Sum(package => package.FileCount);
        long totalBytes = packages.Sum(package => package.TotalBytes);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PreviewPackageClosureEntryDto package in packages)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(package.PackageId));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(package.Revision));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(package.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(package.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach (string role in package.Roles) hash.AppendData(System.Text.Encoding.UTF8.GetBytes(role));
        }
        return new("sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()), packages, fileCount, totalBytes);
    }

    private BackendErrorResponse? ValidateSize(PreviewSizeDto? size)
    {
        if (size is null || size.Width <= 0 || size.Height <= 0
            || size.Width > RuntimeInfo.MaxPixelWidth || size.Height > RuntimeInfo.MaxPixelHeight)
            return new(BackendErrorCode.InvalidRequest, "预览尺寸超过当前Worker公布的上限。", "size");
        try
        {
            if (checked((long)size.Width * size.Height * 4) > RuntimeInfo.MaxRawFrameBytes)
                return new(BackendErrorCode.ResourceTooLarge, "预览裸像素超过Worker协议预算。", "size");
        }
        catch (OverflowException) { return new(BackendErrorCode.ResourceTooLarge, "预览裸像素超出可表示预算。", "size"); }
        return null;
    }

    public async Task DrainClientAsync(string client)
    {
        await _membership.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await DrainGpuClientAsync(client).ConfigureAwait(false); }
            finally { await _previews.DrainClientAsync(client).ConfigureAwait(false); }
        }
        finally { _membership.Release(); }
    }

    public async Task DrainExpiredClientsAsync()
    {
        await _membership.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_gpuStreams is not null)
                foreach (string client in _gpuStreams.ActiveClientIds)
                    if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant)
                        try { await DrainGpuClientAsync(client).ConfigureAwait(false); }
                        catch (Exception) { Console.Error.WriteLine("GPU stream cleanup failed; capacity remains reserved."); }
            foreach (string client in _previews.ActiveClientIds)
                if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant)
                    try { await _previews.DrainClientAsync(client).ConfigureAwait(false); }
                    catch (Exception) { Console.Error.WriteLine("Preview cleanup failed; capacity remains reserved."); }
        }
        finally { _membership.Release(); }
    }

    public async Task RunLeaseCleanupAsync(CancellationToken stopping)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
                await DrainExpiredClientsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_dispose is null) _dispose = DisposeCoreAsync();
            else if (_dispose.IsFaulted || _dispose.IsCanceled) _dispose = RetryDisposeCoreAsync();
            return new(_dispose);
        }
    }

    private async Task RetryDisposeCoreAsync()
    {
        await _membership.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Exception> failures = [];
            // A prior stream close keeps its diagnostic fault. Retry authoritative
            // ownership drain directly rather than replaying that cached close task.
            if (_gpuWorkerPool is not null)
                try { await _gpuWorkerPool.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            if (_gpuStreams is not null)
            {
                try { await _gpuStreams.RetryFailedOpensAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
                try { _gpuStreams.ReleaseAllInputs(); }
                catch (Exception error) { failures.Add(error); }
            }
            try { await _previews.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally { _membership.Release(); }
    }

    private async Task DisposeCoreAsync()
    {
        StopAccepting();
        await _membership.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            List<Exception> failures = [];
            if (_gpuStreams is not null)
            {
                foreach (string client in _gpuStreams.ActiveClientIds)
                    try { await DrainGpuClientAsync(client).ConfigureAwait(false); }
                    catch (Exception error) { failures.Add(error); }
                try { await _gpuStreams.RetryFailedOpensAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
                try { _gpuStreams.ReleaseAllInputs(); }
                catch (Exception error) { failures.Add(error); }
            }
            if (_gpuWorkerPool is not null)
            {
                try { await _gpuWorkerPool.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
            try { await _previews.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            _previews.Updated -= PublishEvent;
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally { _membership.Release(); }
    }

    private static BackendOperationResult<T> Reject<T>(BackendErrorCode code, string message) =>
        BackendOperationResult<T>.Rejected(new(code, message));

    /// <summary>输入引用与正在执行的runtime引用独立；所有失败/取消出口归还运行引用。</summary>
    private sealed class ReferencedFactory(IRenderFrameSessionFactory inner, Func<IDisposable?> acquire)
        : IRenderFrameSessionFactory, IBackendPreviewProfileCapability, IBackendImagePreviewFactory, IBackendRuntimePreviewFactory,
            IBackendPreviewRenderBackendFactory
    {
        public string RenderBackend => (inner as IBackendPreviewRenderBackendFactory)?.RenderBackend ?? "cpu";
        public IRenderFrameSessionFactory WithRenderBackend(string renderBackend) =>
            new ReferencedFactory(SelectFactory(inner, renderBackend), acquire);
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken token = default) =>
            inner is IBackendPreviewProfileCapability capability
                ? capability.EnsureProfileAsync(profile, token)
                : ValueTask.FromResult<BackendErrorResponse?>(new(BackendErrorCode.PreviewUnavailable,
                    "当前预览Worker未提供所选来源能力握手。", "worker", FailureCode: profile == ProjectImagePreviewSnapshotCodec.Profile
                        ? "WorkerImagePreviewProfileMismatch" : profile == PackageProjectSnapshotCodec.Profile
                            ? "WorkerPackageProfileMismatch" : "WorkerStaticProfileMismatch"));

        public IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input) =>
            inner is IBackendRuntimePreviewFactory runtimeFactory ? new ReferencedFactory(runtimeFactory.Bind(input), acquire)
                : throw new SnapshotContractException("WorkerRuntimeInputProfileMismatch", "当前预览工厂未提供固定工程输入能力。");

        public IRenderFrameSessionFactory Bind(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes) =>
            inner is IBackendImagePreviewFactory imageFactory ? new ReferencedFactory(imageFactory.Bind(snapshot, imageBytes), acquire)
                : throw new SnapshotContractException("WorkerImagePreviewProfileMismatch", "当前预览工厂未提供固定图片能力。");

        public async ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            IDisposable? lease = acquire();
            IRenderFrameSession? session = null;
            try
            {
                token.ThrowIfCancellationRequested();
                session = await inner.CreateAsync(snapshot, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new ReferencedSession(session, lease);
            }
            catch (Exception error)
            {
                session ??= BackendPreviewFailedCreation.Get(error);
                if (session is not null)
                {
                    var referenced = new ReferencedSession(session, lease);
                    try { await referenced.DisposeAsync().ConfigureAwait(false); BackendPreviewFailedCreation.Attach(error, null); }
                    catch (Exception cleanup)
                    {
                        error.Data["PreviewCleanupFailure"] = cleanup;
                        BackendPreviewFailedCreation.Attach(error, referenced);
                    }
                }
                else if (lease is not null)
                {
                    try { lease.Dispose(); }
                    catch (Exception cleanup)
                    {
                        error.Data["PreviewCleanupFailure"] = cleanup;
                        BackendPreviewFailedCreation.Attach(error, new RetentionOnlySession(lease));
                    }
                }
                throw;
            }
        }
    }

    private sealed class RetentionOnlySession(IDisposable lease) : IRenderFrameSession
    {
        public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken token = default) =>
            ValueTask.FromException<RenderFrame>(new InvalidOperationException("创建失败的会话只能排水。"));
        public ValueTask DisposeAsync() { lease.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ReferencedSession(IRenderFrameSession inner, IDisposable? lease) : IRenderFrameSession, IBackendPreviewCancellationRecoverable
    {
        private Task? _dispose;
        private readonly object _gate = new();
        public bool CanReuseAfterCancellation => inner is IBackendPreviewCancellationRecoverable { CanReuseAfterCancellation: true };
        public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken token = default) => inner.RenderAsync(request, token);
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_dispose is null || _dispose.IsFaulted || _dispose.IsCanceled) _dispose = DisposeCoreAsync();
                return new(_dispose);
            }
        }
        private async Task DisposeCoreAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            lease?.Dispose();
        }
    }
}
