using System.Collections.Concurrent;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>创建结果，成功和中立拒绝互斥。</summary>
public sealed class ProjectSessionCreateResult
{
    internal ProjectSessionCreateResult(ProjectSession session) => Session = session;
    internal ProjectSessionCreateResult(ProjectSession session, ProjectSessionSummaryResponse summary) => (Session, Summary) = (session, summary);
    internal ProjectSessionCreateResult(BackendErrorResponse error) => Error = error;
    internal ProjectSessionCreateResult(ProjectSessionSummaryResponse summary) => Summary = summary;
    public ProjectSessionSummaryResponse? Summary { get; }

    /// <summary>成功注册的会话；摘要须通过注册表的锁内查询捕获。</summary>
    public ProjectSession? Session { get; }
    /// <summary>新建被拒绝时的协议错误。</summary>
    public BackendErrorResponse? Error { get; }
    /// <summary>是否已成功注册会话。</summary>
    public bool Accepted => Session is not null || Summary is not null;
}

/// <summary>Hosting 命令执行的成功或协议拒绝结果。</summary>
public sealed class ProjectCommandExecutionResult
{
    private ProjectCommandExecutionResult(ProjectCommandResponse? response, BackendErrorResponse? error) =>
        (Response, Error) = (response, error);

    /// <summary>成功命令的只读传输投影。</summary>
    public ProjectCommandResponse? Response { get; }
    /// <summary>命令被拒绝时的协议错误。</summary>
    public BackendErrorResponse? Error { get; }
    /// <summary>是否已成功处理命令，包含无变化结果。</summary>
    public bool Accepted => Response is not null;

    internal static ProjectCommandExecutionResult FromResponse(ProjectCommandResponse response) => new(response, null);
    internal static ProjectCommandExecutionResult Rejected(BackendErrorResponse error) => new(null, error);
}

/// <summary>
/// Hosting 组合根：固定资源、单写者编辑、目录授权以及保存、重开和恢复。
/// 独立服务会话不修改桌面 Application 的工作区。
/// </summary>
public sealed partial class ProjectSessionRegistry : IDisposable, IAsyncDisposable
{
    private readonly string _hostInstanceId;
    private readonly ResourceStore _resources;
    private readonly NewProjectPreparationService _preparation;
    private readonly WaveformCache _waveforms;
    private readonly TimeProvider _timeProvider;
    private readonly ProjectCommandRetentionOptions _commandRetention;
    private readonly ConcurrentDictionary<string, ProjectSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);
    internal readonly SemaphoreSlim _membershipGate = new(1, 1);
    private readonly ClientLifecycleOptions _lifecycleOptions;
    private readonly ClientEventJournal _events;
    private readonly object _shutdownGate = new();
    private Task? _shutdown;
    // Set before draining accepted receipts so trusted in-process callers cannot
    // publish a new session while shutdown is releasing the existing graph.
    private int _stopping;
    private int _disposed;
    /// <summary>HTTP组合根的凭证注册表；可信进程内调用可省略。</summary>
    public ClientRegistry? Clients { get; }
    /// <summary>所有具名B2副作用共用的client级有限回执。</summary>
    public ClientRequestReceiptStore Receipts { get; }

    /// <summary>组合固定资源、无 GUI 新建服务、可注入时钟与有限命令回执策略。</summary>
    public ProjectSessionRegistry(string hostInstanceId, ResourceStore resources, NewProjectPreparationService preparation,
        TimeProvider? timeProvider = null, ProjectCommandRetentionOptions? commandRetention = null,
        ClientRegistry? clients = null, ClientLifecycleOptions? lifecycleOptions = null, WaveformCache? waveformCache = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(preparation);
        _hostInstanceId = hostInstanceId;
        _resources = resources;
        _preparation = preparation;
        _waveforms = waveformCache ?? new();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commandRetention = commandRetention ?? new();
        _commandRetention.Validate();
        Clients = clients;
        _lifecycleOptions = lifecycleOptions ?? clients?.Options ?? new();
        _lifecycleOptions.Validate();
        _events = new(_hostInstanceId, _lifecycleOptions.MaxEventsPerClient, _timeProvider,
            clients is null ? null : clients.IsClientActive);
        Receipts = new(_hostInstanceId, _commandRetention, _timeProvider, clients);
    }

    /// <summary>当前后端进程身份。</summary>
    public string HostInstanceId => _hostInstanceId;

    /// <summary>命令元信息使用的当前服务时钟。</summary>
    public DateTimeOffset ServerTimeUtc => _timeProvider.GetUtcNow();

    /// <summary>公布给客户端的最大命令重试窗口秒数。</summary>
    public int MaxCommandRetryWindowSeconds => _commandRetention.MaxRetryWindowSeconds;

    /// <summary>旧能力字段的兼容别名；实际为client共享有效回执数量上限。</summary>
    public int MaxCommandReceiptsPerSession => _commandRetention.MaxReceiptsPerSession;

    /// <summary>可信字节调用同样转为流式准备，再以完整SHA和元信息统一去重发布。</summary>
    public async Task<BackendOperationResult<AssetResponse>> UploadAssetAsync(string clientId, RequestMetadataDto? metadata,
        string fileName, string? contentType, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(fileName))
            return BackendOperationResult<AssetResponse>.Rejected(new(BackendErrorCode.InvalidRequest, "上传文件名不能为空。"));
        if (content.Length > _resources.MaxAssetBytes)
            return BackendOperationResult<AssetResponse>.Rejected(new(BackendErrorCode.ResourceTooLarge, "资源超过单文件大小上限。"));
        byte[] fixedBytes = [.. content];
        try
        {
            using ResourceStore.PreparedUpload upload = await _resources.PrepareUploadAsync(clientId, fileName, contentType,
                new MemoryStream(fixedBytes, writable: false), Receipts.HasUnexpiredReceipt(clientId, metadata?.RequestId), cancellationToken).ConfigureAwait(false);
            return await UploadAssetAsync(clientId, metadata, upload, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceStoreException error)
        {
            return BackendOperationResult<AssetResponse>.Rejected(new(error.Code, error.Message, error.Field));
        }
    }

    /// <summary>HTTP先在资源存储中完成流式固定；接受后只转移候选，不重读或复制完整文件。</summary>
    public Task<BackendOperationResult<AssetResponse>> UploadAssetAsync(string clientId, RequestMetadataDto? metadata,
        ResourceStore.PreparedUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (!upload.BelongsTo(_resources) || upload.OwnerClientId != clientId)
            return Task.FromResult(BackendOperationResult<AssetResponse>.Rejected(new(BackendErrorCode.ResourceUnauthorized, "上传候选不属于当前客户端。")));
        string fileName = upload.FileName, mime = upload.ContentType, hash = upload.Sha256;
        return Receipts.ExecuteAsync(clientId, metadata, "POST /api/v1/assets", new { fileName, mime, hash }, () =>
        {
            try
            {
                if (upload.ReplayOnly)
                    return Task.FromResult(BackendOperationResult<AssetResponse>.Rejected(new(BackendErrorCode.RequestExpired, "上传重试回执已不可用。")));
                StoredAsset asset = upload.Publish();
                return Task.FromResult(BackendOperationResult<AssetResponse>.Success(new(asset.AssetId, asset.Sha256, asset.Size, asset.ContentType, asset.FileName, asset.ExpiresAtUtc)));
            }
            catch (ResourceStoreException error)
            {
                return Task.FromResult(BackendOperationResult<AssetResponse>.Rejected(new(error.Code, error.Message, error.Field)));
            }
        }, cancellationToken);
    }

    /// <summary>无传输元信息的直接用例入口用于进程内可信调用；HTTP必须提供Metadata。</summary>
    public async Task<ProjectSessionCreateResult> CreateAsync(string clientId, CreateProjectSessionRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Metadata is null) return await CreateCoreAsync(clientId, request, cancellationToken).ConfigureAwait(false);
        BackendOperationResult<ProjectSessionSummaryResponse> result = await Receipts.ExecuteAsync(clientId,
            request?.Metadata, "POST /api/v1/project-sessions", request is null ? null : request with { Metadata = null }, async () =>
            {
                ProjectSessionCreateResult created = await CreateCoreAsync(clientId, request, CancellationToken.None).ConfigureAwait(false);
                if (!created.Accepted) return BackendOperationResult<ProjectSessionSummaryResponse>.Rejected(created.Error!);
                return BackendOperationResult<ProjectSessionSummaryResponse>.Success(
                    (await ReadSummaryAsync(clientId, created.Session!.ProjectSessionId).ConfigureAwait(false))!);
            }, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null) return new(result.Error);
        return TryGet(clientId, result.Response!.ProjectSessionId, out ProjectSession session)
            ? new(session, result.Response) : new(result.Response);
    }

    /// <summary>使用客户端授权的固定资源创建一个未保存的独立工程会话。</summary>
    private async Task<ProjectSessionCreateResult> CreateCoreAsync(
        string clientId, CreateProjectSessionRequest? request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        using IDisposable? clientPin = Clients?.TryPinClient(clientId);
        if (Clients is not null && clientPin is null)
            return Reject(BackendErrorCode.UnauthorizedClient, "客户端已不存在或正在回收。");
        if (Volatile.Read(ref _stopping) != 0)
            return Reject(BackendErrorCode.SessionClosing, "服务正在停止接收新的工程会话。");
        if (request is null) return Reject(BackendErrorCode.InvalidRequest, "新建工程请求不能为空。");
        // Validate request-only values before reserving a managed preparation slot. These checks
        // have no resource or project side effects and must retain the original InvalidRequest
        // contract even when the Worker is unavailable or its queue is full.
        if (string.IsNullOrWhiteSpace(request.Name)) return Reject(BackendErrorCode.InvalidRequest, "工程名称不能为空。", nameof(request.Name));
        if (request.AudioAssetId is not null && string.IsNullOrWhiteSpace(request.AudioAssetId))
            return Reject(BackendErrorCode.InvalidRequest, "音频资源 ID 不能为空。", nameof(request.AudioAssetId));
        if (request.LyricsAssetId is not null && string.IsNullOrWhiteSpace(request.LyricsAssetId))
            return Reject(BackendErrorCode.InvalidRequest, "歌词资源 ID 不能为空。", nameof(request.LyricsAssetId));
        if (!TryMapComposition(request.Composition, out Composition composition, out string? compositionError))
            return Reject(BackendErrorCode.InvalidRequest, compositionError!, nameof(request.Composition));
        if (!Enum.IsDefined(typeof(Core.Lyrics.TokenSplitRule), request.SplitRule))
            return Reject(BackendErrorCode.InvalidRequest, "SplitRule 超出协议范围。", nameof(request.SplitRule));
        AudioPreparationOperation? audioOperation = null;
        bool createEntered = false;
        ProjectSession? candidateSession = null; Exception? primary = null;
        try
        {
            if (request.AudioAssetId is not null) audioOperation = ReserveAudioPreparation(clientId, cancellationToken);
            if (audioOperation is not null)
            {
                cancellationToken = audioOperation.Token;
                await audioOperation.WaitAsync().ConfigureAwait(false);
            }
            await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            createEntered = true;
            if (Volatile.Read(ref _stopping) != 0)
                return Reject(BackendErrorCode.SessionClosing, "服务正在停止接收新的工程会话。");
            if (_sessions.Values.Count(session => session.OwnerClientId == clientId && !session.IsClosed) >= _lifecycleOptions.MaxSessionsPerClient)
                return Reject(BackendErrorCode.SessionQuotaExceeded, "客户端工程会话已达到上限。");
            using IDisposable? audioPreparationLease = request.AudioAssetId is { } audioId ? _resources.AcquireReference(clientId, audioId) : null;
            using IDisposable? lyricsPreparationLease = request.LyricsAssetId is { } lyricsId ? _resources.AcquireReference(clientId, lyricsId) : null;
            StoredAsset? audio = request.AudioAssetId is { } authorizedAudioId ? _resources.GetAuthorized(clientId, authorizedAudioId) : null;
            StoredAsset? lyrics = request.LyricsAssetId is { } authorizedLyricsId ? _resources.GetAuthorized(clientId, authorizedLyricsId) : null;
            if (audio is not null && audio.ContentType.StartsWith("text/", StringComparison.Ordinal))
                return Reject(BackendErrorCode.InvalidRequest, "音频资源必须是音频 MIME。", nameof(request.AudioAssetId));
            if (lyrics is not null && !lyrics.ContentType.StartsWith("text/", StringComparison.Ordinal) && lyrics.ContentType != "application/ttml+xml"
                && !lyrics.FileName.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase) && !lyrics.FileName.EndsWith(".ttml", StringComparison.OrdinalIgnoreCase))
                return Reject(BackendErrorCode.InvalidRequest, "歌词资源必须是文本、LRC 或 TTML 文件。", nameof(request.LyricsAssetId));
            if (lyrics is not null && !File.Exists(lyrics.StoragePath))
                return Reject(BackendErrorCode.ResourceNotFound, "歌词资源已不可用。", nameof(request.LyricsAssetId));
            if (audio is not null && !File.Exists(audio.StoragePath))
                return Reject(BackendErrorCode.ResourceNotFound, "音频资源已不可用。", nameof(request.AudioAssetId));

            LyricsImportSource? source = lyrics is null ? null : new(lyrics.FileName,
                await File.ReadAllBytesAsync(lyrics.StoragePath, cancellationToken).ConfigureAwait(false));
            LyricsImportOptions options = new()
            {
                ImporterId = request.ImporterId,
                EncodingName = request.EncodingName,
                SplitRule = (Core.Lyrics.TokenSplitRule)request.SplitRule,
                EstimateSplitTimes = request.EstimateSplitTimes,
            };
            if (audio is not null) VerifyFixedAudioSource(clientId, audio, cancellationToken);
            OptionalNewProjectPreparationResult prepared = audioOperation is null
                ? await _preparation.PrepareOptionalAsync(request.Name.Trim(), composition, audio?.StoragePath, source, options, cancellationToken).ConfigureAwait(false)
                : await NewProjectPreparationService.PrepareOptionalUsingAsync(request.Name.Trim(), composition, audio!.StoragePath, source, options,
                    (_, token) => PrepareManagedAudioAsync(audio, token), cancellationToken).ConfigureAwait(false);
            if (prepared.Error is { } error)
            {
                return Reject(Map(error.Code), PublicPreparationMessage(error.Code), error.Field);
            }

            if (audio is not null) VerifyFixedAudioSource(clientId, audio, cancellationToken);
            // The fixed upload already carries the content identity. Bind it before publishing revision zero;
            // adopting analysis later must never repair the audio declaration as a second edit.
            Project project = prepared.Project!;
            if (audio is not null) project = project with { AudioMedia = new(audio.Sha256, audio.Size, audio.FileName) };
            ProjectWorkspace workspace = ProjectWorkspace.Adopt(project, isSaved: false);
            ProjectSession session = candidateSession = new(
                "project-" + Guid.NewGuid().ToString("N"), _hostInstanceId, clientId, workspace,
                audio?.AssetId ?? string.Empty, prepared.Waveform?.Peaks.DurationUs ?? 0, _timeProvider, _commandRetention, _events,
                audio is null ? null : _resources.AcquireReference(clientId, audio.AssetId));
            if (audio is not null) session.BindWaveform(_waveforms, prepared.Waveform!, audio);
            if (!await RegisterSessionAsync(session, cancellationToken).ConfigureAwait(false))
            {
                primary = new ResourceStoreException(BackendErrorCode.SessionConflict, "无法注册工程会话。");
                return Reject(BackendErrorCode.SessionConflict, "无法注册工程会话。");
            }
            return new(session);
        }
        catch (ResourceStoreException exception)
        {
            primary = exception;
            return Reject(exception.Code, exception.Message, exception.Field);
        }
        catch (FileNotFoundException error)
        {
            primary = error;
            return Reject(BackendErrorCode.ResourceNotFound, "工程资源已不可用。",
                request.AudioAssetId is null ? nameof(request.LyricsAssetId) : nameof(request.AudioAssetId));
        }
        catch (OperationCanceledException error) when (audioOperation is { TimedOut: true })
        {
            primary = AudioPreparationOperation.TimeoutFailure(error);
            return new(AudioPreparationFailure(primary));
        }
        catch (RenderSessionFailureException error) when (audioOperation is not null)
        {
            primary = error;
            return new(AudioPreparationFailure(error));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                if (candidateSession is not null && !_sessions.ContainsKey(candidateSession.ProjectSessionId))
                {
                    try { candidateSession.DisposeResources(); }
                    catch (Exception cleanup) when (primary is not null) { primary.Data["CreateCandidateCleanupFailure"] = cleanup; }
                }
            }
            finally
            {
                if (createEntered) _createGate.Release();
                audioOperation?.Dispose();
            }
        }
    }

    /// <summary>按客户端归属及服务身份查找会话，不暴露可变工程状态。</summary>
    public bool TryGet(string clientId, string sessionId, out ProjectSession session)
    {
        if (Volatile.Read(ref _disposed) == 0 && _sessions.TryGetValue(sessionId, out session!) && !session.IsClosed && string.Equals(session.OwnerClientId, clientId, StringComparison.Ordinal)
            && string.Equals(session.HostInstanceId, _hostInstanceId, StringComparison.Ordinal)) return true;
        session = null!;
        return false;
    }

    /// <summary>在会话唯一写者内执行具名工程命令。</summary>
    public async Task<ProjectCommandExecutionResult> ExecuteCommandAsync(
        string clientId, string sessionId, ProjectCommandRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "命令请求不能为空。"));
        if (ValidateCommandIntent(request.Command) is { } intentError)
            return ProjectCommandExecutionResult.Rejected(intentError);
        ProjectCommandRequest canonicalRequest = ProjectSession.CanonicalCommand(request);
        BackendOperationResult<ProjectCommandResponse> result = await Receipts.ExecuteAsync(clientId,
            new(canonicalRequest.RequestId, canonicalRequest.HostInstanceId, canonicalRequest.ValidUntilUtc),
            "POST /api/v1/project-sessions/" + sessionId + "/commands",
            canonicalRequest, async () =>
            {
                if (!TryGet(clientId, sessionId, out ProjectSession session))
                    return BackendOperationResult<ProjectCommandResponse>.Rejected(MissingSessionError(sessionId));
                if (canonicalRequest.Command is ApplyProjectEditsCommandDto)
                {
                    var edits = await ExecuteApplyEditsAsync(clientId, session, canonicalRequest).ConfigureAwait(false);
                    return edits.Accepted ? BackendOperationResult<ProjectCommandResponse>.Success(edits.Response!)
                        : BackendOperationResult<ProjectCommandResponse>.Rejected(edits.Error!);
                }
                if (canonicalRequest.Command is SetBackgroundAssetCommandDto or ClearBackgroundCommandDto or SetBackgroundFitCommandDto or SetBackgroundPlaybackCommandDto)
                {
                    ProjectCommandExecutionResult background = await ExecuteBackgroundCommandAsync(clientId, session, canonicalRequest).ConfigureAwait(false);
                    return background.Accepted ? BackendOperationResult<ProjectCommandResponse>.Success(background.Response!)
                        : BackendOperationResult<ProjectCommandResponse>.Rejected(background.Error!);
                }
                if (canonicalRequest.Command?.Type is "audio.replaceAsset" or "audio.clear")
                {
                    ProjectCommandExecutionResult audio = await ExecuteBasicAudioCommandAsync(clientId, session, canonicalRequest).ConfigureAwait(false);
                    return audio.Accepted ? BackendOperationResult<ProjectCommandResponse>.Success(audio.Response!)
                        : BackendOperationResult<ProjectCommandResponse>.Rejected(audio.Error!);
                }
                if (canonicalRequest.Command?.Type is "lyrics.importAsset" or "lyrics.appendText")
                {
                    ProjectCommandExecutionResult imported = await ExecuteImportCommandAsync(clientId, session, canonicalRequest).ConfigureAwait(false);
                    return imported.Accepted ? BackendOperationResult<ProjectCommandResponse>.Success(imported.Response!)
                        : BackendOperationResult<ProjectCommandResponse>.Rejected(imported.Error!);
                }
                if (canonicalRequest.Command is SetLeadInAttachmentCommandDto)
                {
                    ProjectCommandExecutionResult attached = await ExecuteLeadInAttachmentAsync(clientId, session, canonicalRequest).ConfigureAwait(false);
                    return attached.Accepted ? BackendOperationResult<ProjectCommandResponse>.Success(attached.Response!)
                        : BackendOperationResult<ProjectCommandResponse>.Rejected(attached.Error!);
                }
                StyleTemplate? assignmentSnapshot = null;
                if (canonicalRequest.Command is ApplyPresetApplicationCommandDto or UpdatePresetApplicationCommandDto)
                {
                    if (!BeginTemplateOperation())
                        return BackendOperationResult<ProjectCommandResponse>.Rejected(new(BackendErrorCode.SessionClosing, "模板库服务正在停止。", "command"));
                    try
                    {
                        await _templateIo.WaitAsync().ConfigureAwait(false);
                        try { assignmentSnapshot = CaptureAssignmentTemplate(canonicalRequest.Command); }
                        finally { _templateIo.Release(); }
                    }
                    finally { EndTemplateOperation(); }
                }
                ProjectCommandExecutionResult command = await session.ExecuteCommandAsync(canonicalRequest, CancellationToken.None,
                    (current, jobId) => PrepareAnalysisAdoption(clientId, current, jobId),
                    _ => assignmentSnapshot).ConfigureAwait(false);
                return command.Accepted ? BackendOperationResult<ProjectCommandResponse>.Success(command.Response!)
                    : BackendOperationResult<ProjectCommandResponse>.Rejected(command.Error!);
            }, cancellationToken).ConfigureAwait(false);
        return result.Accepted ? ProjectCommandExecutionResult.FromResponse(result.Response!)
            : ProjectCommandExecutionResult.Rejected(result.Error!);
    }

    private static BackendErrorResponse? ValidateCommandIntent(ProjectCommandDto? command)
    {
        if (command is { } rubyIntent && ProjectSession.CheckRubyIntent(rubyIntent) is { } rubyError)
            return rubyError;
        if (command is ResplitLineCommandDto { Segments.Length: > TokenEditingLimits.MaxSegments })
            return new(BackendErrorCode.ResourceTooLarge,
                "segments 超过单次操作的项数预算。", "command.segments");
        if (command is { } styleIntent && ProjectSession.CheckStyleIntentBudget(styleIntent) is { } styleError)
            return styleError;
        if (command is { } localIntent && ProjectSession.CheckLineLocalTransformIntent(localIntent) is { } localError)
            return localError;
        if (command is { } propertiesIntent && ProjectSession.CheckOrchestrationPropertiesIntent(propertiesIntent) is { } propertiesError)
            return propertiesError;
        // Bind the receipt and queued execution to one detached payload before awaiting the writer.
        if (command is { } nodeIntent && ProjectSession.CheckNodeCreationIntent(nodeIntent) is { } nodeError)
            return nodeError;
        if (command is { } leadInEditingIntent && ProjectSession.CheckLeadInEditingIntent(leadInEditingIntent) is { } leadInEditingError)
            return leadInEditingError;
        if (command is { } completionIntent && ProjectSession.CheckLayoutAndLineAnimationIntent(completionIntent) is { } completionError)
            return completionError;
        if (command is { } presetIntent && ProjectSession.CheckPresetApplicationIntent(presetIntent) is { } presetError)
            return presetError;
        if (command is { } clipMembersIntent && ProjectSession.CheckClipMembersIntent(clipMembersIntent) is { } clipMembersError)
            return clipMembersError;
        if (command is SetLeadInAttachmentCommandDto attachmentIntent && ProjectSession.CheckLeadInAttachmentIntent(attachmentIntent) is { } attachmentError)
            return attachmentError;
        return null;
    }

    private StyleTemplate? CaptureAssignmentTemplate(ProjectCommandDto command)
    {
        string? library = command switch
        {
            ApplyPresetApplicationCommandDto value => value.TemplateLibraryId,
            UpdatePresetApplicationCommandDto value => value.TemplateLibraryId,
            _ => null,
        };
        string? id = command switch
        {
            ApplyPresetApplicationCommandDto value => value.TemplateId,
            UpdatePresetApplicationCommandDto value => value.TemplateId,
            _ => null,
        };
        string? revision = command switch
        {
            ApplyPresetApplicationCommandDto value => value.TemplateRevision,
            UpdatePresetApplicationCommandDto value => value.TemplateRevision,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(revision)) return null;
        if (library is not ("builtin" or "personal")) return null;
        TemplateLibraryCatalog catalog = library == "builtin" ? TemplateLibraryCatalog.BuiltIn : TemplateLibraryCatalog.Personal;
        {
            TemplateLibrarySnapshot snapshot;
            try { snapshot = _templateLibrary?.Read(catalog) ?? throw new InvalidOperationException(); }
            catch { return null; }
            string actual = snapshot.Revision.Sha256 ?? "missing";
            if (!string.Equals(actual, revision, StringComparison.Ordinal)) return null;
            var template = snapshot.Templates.FirstOrDefault(item => item.Id == id);
            if (template is null || !TemplateRuntimeSupported(template)) return null;
            return template;
        }
    }

    /// <summary>读取和命令提交处于同一写者闸门内的原子摘要。</summary>
    public async Task<ProjectSessionSummaryResponse?> ReadSummaryAsync(
        string clientId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return null;
        return await session.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取绑定精确工程修订的完整预设应用投影。</summary>
    public async Task<BackendOperationResult<ProjectPresetApplicationsResponse>> ReadPresetApplicationsAsync(
        string clientId, string sessionId, string? expectedRevision, CancellationToken cancellationToken = default)
    {
        if (!TryGet(clientId, sessionId, out ProjectSession session))
            return BackendOperationResult<ProjectPresetApplicationsResponse>.Rejected(MissingSessionError(sessionId));
        return await session.ReadPresetApplicationsAsync(expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取当前会话的历史查询，和写入共用同一闸门。</summary>
    public async Task<ProjectHistoryResponse?> ReadHistoryAsync(
        string clientId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return null;
        return await session.ReadHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取绑定精确工程修订的歌词行投影。</summary>
    public async Task<LyricsLinesResponse?> ReadLyricsAsync(
        string clientId, string sessionId, string branchId, CancellationToken cancellationToken = default)
    {
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return null;
        return await session.ReadLyricsAsync(branchId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>不区分不存在与无权限的中立会话错误。</summary>
    public static BackendErrorResponse MissingSessionError(string sessionId) =>
        new(BackendErrorCode.SessionNotFound, "工程会话不存在或不属于当前客户端。", nameof(sessionId));

    /// <summary>安全停止先等已接受操作，再结束持有引用和写入闸门。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    /// <summary>等已接受命令与保存结束后释放工程和资源引用。</summary>
    public ValueTask ShutdownAsync() => DisposeAsync();
    /// <summary>幂等等待回执、保存和恢复写入，然后释放资源。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_shutdownGate) return new(_shutdown ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        StopAuthorWorkflows();
        Receipts.StopAccepting();
        StopTemplateLibraryAccepting();
        try
        {
            await StopSceneMeasurementsAsync().ConfigureAwait(false);
            await StopAudioPreparationAsync().ConfigureAwait(false);
            await Receipts.DrainAsync().ConfigureAwait(false);
            await DrainTemplateLibraryAsync().ConfigureAwait(false);
            ClearTemplateTransfers();
            await DisposePersistenceAsync().ConfigureAwait(false);
        }
        finally
        {
            ClearLyricsExports();
            foreach (ProjectSession session in _sessions.Values)
            {
                ReleasePackageAssetsForSession(session.ProjectSessionId);
                session.DisposeResources();
            }
            lock (_packageAssetGate) _packageAssets?.Dispose();
            Volatile.Write(ref _disposed, 1);
            // SemaphoreSlim只有托管等待者；不在结束时Dispose仍可能已排队的查询。
            // 服务入口停止接受并等待HTTP读取响应后，由GC收敛这些短闸门。
        }
    }

    internal async Task<bool> RegisterSessionAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stopping) != 0) return false;
        await _membershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0) return false;
            if (_sessions.Values.Count(item => item.OwnerClientId == session.OwnerClientId && !item.IsClosed) >= _lifecycleOptions.MaxSessionsPerClient)
                return false;
            if (!_sessions.TryAdd(session.ProjectSessionId, session)) return false;
            session.PublishEvent("project.opened");
            return true;
        }
        finally { _membershipGate.Release(); }
    }

    internal async Task RemoveClosedSessionAsync(ProjectSession session)
    {
        await _membershipGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _sessions.TryRemove(session.ProjectSessionId, out _);
            ReleasePackageAssetsForSession(session.ProjectSessionId);
            RemoveSessionLyricsExports(session.ProjectSessionId);
        }
        finally { _membershipGate.Release(); }
    }

    /// <summary>读取当前client的有界事件重放。</summary>
    public ClientEventsResponse ReadEvents(string clientId, string? afterCursor) => _events.ReadAfter(clientId, afterCursor);
    /// <summary>等待下一次client事件，断开只取消等待。</summary>
    public Task<ClientEventsResponse> WaitForEventsAsync(string clientId, string? afterCursor, CancellationToken cancellationToken = default) =>
        _events.WaitForEventsAsync(clientId, afterCursor, cancellationToken);

    /// <summary>成员资格与全部工程写者统一锁定后捕获摘要和事件游标。</summary>
    public async Task<ClientStateResponse> ReadClientStateAsync(string clientId, CancellationToken cancellationToken = default)
    {
        await _membershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<ProjectSession> locked = [];
        try
        {
            ProjectSession[] sessions = [.. _sessions.Values.Where(session => session.OwnerClientId == clientId && !session.IsClosed)
                .OrderBy(session => session.ProjectSessionId, StringComparer.Ordinal)];
            foreach (ProjectSession session in sessions)
            {
                await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
                locked.Add(session);
            }
            DateTimeOffset expires = DateTimeOffset.MaxValue;
            bool dormant = false;
            Clients?.TryReadLease(clientId, out expires, out dormant);
            lock (_jobGate)
            {
                string cursor = _events.Cursor(clientId);
                ProjectSessionSummaryResponse[] summaries = [.. sessions.Where(session => !session.IsClosed)
                    .Select(session => session.BuildSummary() with { EventCursor = cursor })];
                return new(clientId, _hostInstanceId, dormant, expires, cursor, summaries, BuildJobSummaries(clientId));
            }
        }
        finally
        {
            foreach (ProjectSession session in locked) session.Writer.Release();
            _membershipGate.Release();
        }
    }

    private static ProjectSessionCreateResult Reject(BackendErrorCode code, string message, string? field = null) =>
        new(new BackendErrorResponse(code, message, field));

    private static BackendErrorCode Map(NewProjectPreparationErrorCode code) => code switch
    {
        NewProjectPreparationErrorCode.EmptyLyrics => BackendErrorCode.EmptyLyrics,
        NewProjectPreparationErrorCode.LyricsImportFailed => BackendErrorCode.LyricsImportFailed,
        NewProjectPreparationErrorCode.NoAudioStream => BackendErrorCode.NoAudioStream,
        NewProjectPreparationErrorCode.AudioPreparationFailed => BackendErrorCode.AudioPreparationFailed,
        _ => BackendErrorCode.InvalidRequest,
    };

    private static string PublicPreparationMessage(NewProjectPreparationErrorCode code) => code switch
    {
        NewProjectPreparationErrorCode.EmptyLyrics => "歌词资源没有可导入行。",
        NewProjectPreparationErrorCode.LyricsImportFailed => "歌词资源无法识别或解析。",
        NewProjectPreparationErrorCode.NoAudioStream => "音频资源没有可用音轨。",
        NewProjectPreparationErrorCode.AudioPreparationFailed => "音频资源探测或解码失败。",
        _ => "新建工程请求不合法。",
    };

    private static bool TryMapComposition(CompositionDto? dto, out Composition composition, out string? error)
    {
        dto ??= new CompositionDto();
        composition = new Composition();
        error = null;
        if (dto.CanvasWidth <= 0 || dto.CanvasHeight <= 0 || dto.FrameRateNumerator <= 0 || dto.FrameRateDenominator <= 0
            || dto.OutputDurationUs is <= 0)
        {
            error = "画布、帧率和输出时长必须为正，时长也可以留空。";
            return false;
        }
        try
        {
            composition = Composition.Create(dto.CanvasWidth, dto.CanvasHeight,
                new FrameRate(dto.FrameRateNumerator, dto.FrameRateDenominator),
                new RgbaColor(dto.BackgroundRed, dto.BackgroundGreen, dto.BackgroundBlue, dto.BackgroundAlpha),
                dto.OutputDurationUs);
            return true;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
