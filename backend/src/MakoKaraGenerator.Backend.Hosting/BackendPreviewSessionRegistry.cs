using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>可信组合根捕获的固定输入；不是客户端提交的裸工作区。</summary>
public sealed record BackendPreviewPinnedInput(PreviewSourceDto Source, ProjectSnapshot Snapshot, IRenderFrameSessionFactory Factory,
    IDisposable? RetentionLease = null)
{
    /// <summary>本代次固定的公开设备选择，不受后续请求或进程环境影响。</summary>
    public string RenderBackend { get; init; } = "cpu";

    /// <summary>仅可信组合根完成实际能力握手和完整编码预算后设置；不是客户端输入。</summary>
    public string? VerifiedProfile { get; init; }

    public BackendPreparedProjectRuntimeInput? PreparedInput { get; init; }

    public ProjectSession? CapturedProjectSession { get; init; }

    /// <summary>固定编辑候选身份和反馈；随会话换代次整体替换。</summary>
    public ProjectEditCandidateDto? EditCandidate { get; init; }

    /// <summary>可信组合根计算的包资源闭包摘要。</summary>
    public PreviewPackageClosureDto? PackageClosure { get; init; }
}

/// <summary>像素转PNG的窄边界。实现必须编码此帧，禁止返回占位图。</summary>
public interface IBackendPreviewPngEncoder
{
    byte[] Encode(BackendPreviewFrame frame);
}

public sealed record BackendPreviewRegistryOptions
{
    public int MaxSessions { get; init; } = 32;
    public int MaxSessionsPerClient { get; init; } = 4;
    public PreviewLimitsDto Limits { get; init; } = new(1920, 1080, 1, 2, 16 * 1024 * 1024, 30, 128);
    public int MaxDiagnosticPages { get; init; } = 32;
}

public sealed record BackendPreviewSessionStatus(PreviewSessionResponse Session, BackendErrorResponse? Failure = null,
    string? RenderFailureCode = null);
public sealed record BackendPreviewSessionEvent(string ClientId, BackendPreviewSessionStatus Status);
public sealed record BackendPreviewSessionResult(BackendPreviewSessionStatus? Status, BackendErrorResponse? Error)
{
    public bool Accepted => Error is null && Status is not null;
}
public sealed record BackendPreviewFrameRequestResult(PreviewFrameAcceptedResponse? Response, BackendErrorResponse? Error)
{
    public bool Accepted => Error is null && Response is not null;
}

/// <summary>
/// 服务所有的有界预览会话。复用协调器的单执行者和最新槽；编码在锁外执行，
/// 发布前再次核对会话、代次、来源和序号。当前合同严格拒绝脚本输入。
/// </summary>
public sealed class BackendPreviewSessionRegistry : IAsyncDisposable
{
    private sealed class Entry(string clientId, PreviewSessionResponse session, BackendPreviewPinnedInput input)
    {
        internal string ClientId { get; } = clientId;
        internal PreviewSessionResponse Session = session;
        internal BackendPreviewPinnedInput? Input = input;
        internal BackendPreviewCoordinator Coordinator { get; } = new();
        internal ConditionalWeakTable<BackendPreviewRequest, PreviewFrameRequest> Requests { get; } = new();
        internal long LastAcceptedSequence;
        internal long LastPublishedSequence;
        internal long? LastRequestedTime;
        internal long? LastPublishedTime;
        internal BackendErrorResponse? Failure;
        internal string? RenderFailureCode;
        internal Task? Closing;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly PreviewFrameHandleStore _frames;
    private readonly PreviewDiagnosticsStore _diagnostics;
    private readonly IBackendPreviewPngEncoder _encoder;
    private readonly BackendPreviewRegistryOptions _options;
    private readonly TimeProvider _clock;
    private long _nextGeneration;
    private bool _stopping;
    private Task? _disposeTask;
    private readonly Channel<BackendPreviewSessionEvent> _notifications = Channel.CreateBounded<BackendPreviewSessionEvent>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _notificationPump;

    /// <summary>有界异步状态提示；GET状态仍为权威值。观察者不运行在渲染执行者或注册表闸门内。</summary>
    public event Action<BackendPreviewSessionEvent>? Updated;

    public BackendPreviewSessionRegistry(PreviewFrameHandleStore frames, PreviewDiagnosticsStore diagnostics,
        IBackendPreviewPngEncoder encoder, BackendPreviewRegistryOptions? options = null, TimeProvider? clock = null)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _options = options ?? new();
        _clock = clock ?? TimeProvider.System;
        PreviewLimitsDto limits = _options.Limits;
        if (_options.MaxSessions <= 0 || _options.MaxSessionsPerClient <= 0 || _options.MaxDiagnosticPages is <= 0 or > 32
            || limits is null || limits.MaxWidth <= 0 || limits.MaxHeight <= 0 || limits.MaxInFlightRequests != 1
            || limits.MaxRetainedFrames <= 0 || limits.MaxRetainedFrameBytes <= 0
            || limits.MaxRawFrameBytes <= 0 || limits.MaxRawFrameBytes > PreviewFrameContract.MaxFrameBytes
            || limits.FrameLeaseLifetimeSeconds <= 0 || limits.MaxDiagnosticsPageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _notificationPump = Task.Run(PumpNotificationsAsync);
    }

    public int Count { get { lock (_gate) return _sessions.Count; } }
    public string[] ActiveClientIds { get { lock (_gate) return [.. _sessions.Values.Select(entry => entry.ClientId).Distinct(StringComparer.Ordinal)]; } }

    public BackendPreviewSessionResult Create(string clientId, PreviewSessionRequest request, BackendPreviewPinnedInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (request is null || request.PreviewKind is not ("project" or "presetCandidate" or "template" or "editCandidate" or "authorDraft")
            || request.Committable != (request.PreviewKind == "project")
            || request.RenderBackend is not (null or "cpu" or "gpu")
            || request.RenderBackend is not null && request.RenderBackend != input.RenderBackend
            || request.Source is null
            || (request.PreviewKind == "project" && request.Source.Kind != "project")
            || (request.PreviewKind == "presetCandidate" && request.Source.Kind != "presetCandidate")
            || (request.PreviewKind == "template" && request.Source.Kind != "template")
            || (request.PreviewKind == "authorDraft" && request.Source.Kind != "authorDraft")
            || (request.PreviewKind == "editCandidate" && (request.Source.Kind != "editCandidate" || input.EditCandidate is null)))
            return Rejected(BackendErrorCode.InvalidRequest, "预览类型、可提交标志和来源类型必须匹配。");
        BackendErrorResponse? validation = ValidateInput(request?.Source, request?.Size, request?.AlphaMode, input);
        if (validation is not null) return new(null, validation);
        lock (_gate)
        {
            if (_stopping) return Rejected(BackendErrorCode.SessionClosing, "预览服务正在关闭。");
            if (_sessions.Count >= _options.MaxSessions
                || _sessions.Values.Count(item => item.ClientId == clientId) >= _options.MaxSessionsPerClient)
                return Rejected(BackendErrorCode.SessionQuotaExceeded, "预览会话达到保留上限。");
            string id = "preview-" + Guid.NewGuid().ToString("N");
            PreviewSessionResponse response = new(id, input.Source, NextGeneration(), request!.Size,
                request.AlphaMode, "idle", _options.Limits, PreviewKind: request.PreviewKind,
                Committable: request.Committable, PackageClosure: input.PackageClosure, EditCandidate: input.EditCandidate,
                RenderBackend: input.RenderBackend);
            Entry entry = new(clientId, response, input);
            BackendErrorResponse? activation = Activate(entry, response);
            if (activation is not null) return new(null, activation);
            entry.Input = ShareInput(input);
            entry.Coordinator.Updated += update => Receive(entry, update);
            _sessions.Add(id, entry);
            Notify(entry);
            return Success(entry);
        }
    }

    public BackendPreviewSessionResult Read(string clientId, string previewId)
    {
        lock (_gate) return TryFind(clientId, previewId, out Entry? entry, out BackendErrorResponse? error)
            ? Success(entry!) : new(null, error);
    }

    /// <summary>在预览闸门内固定读取编辑候选快照；只复制不可变输入引用，不转移或释放预览租约。</summary>
    internal BackendOperationResult<BackendPreviewPinnedInput> ReadEditCandidateInput(string clientId, string projectSessionId,
        string previewId, string expectedPreviewGeneration, string candidateRevision)
    {
        lock (_gate)
        {
            if (!TryFind(clientId, previewId, out Entry? entry, out BackendErrorResponse? findError))
                return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(findError!);
            if (_stopping || entry!.Closing is not null || entry.Session.State is "closing" or "closed" || entry.Input is null)
                return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(new(BackendErrorCode.SessionClosing, "预览会话正在关闭。", "previewId"));
            if (entry.Session.PreviewKind != "editCandidate" || entry.Session.Source.Kind != "editCandidate"
                || entry.Session.Source.SourceId != projectSessionId || entry.Input.EditCandidate is null)
                return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(new(BackendErrorCode.InvalidRequest,
                    "测量只能读取同一工程的编辑候选预览。", "previewId"));
            if (entry.Session.PreviewGeneration != expectedPreviewGeneration)
                return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(new(BackendErrorCode.PreviewGenerationConflict,
                    "候选预览代次已经改变。", "expectedPreviewGeneration"));
            if (entry.Input.EditCandidate.CandidateRevision != candidateRevision
                || entry.Session.Source.SourceRevision != candidateRevision)
                return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(new(BackendErrorCode.RevisionConflict,
                    "候选修订已经改变。", "candidateRevision"));
            return BackendOperationResult<BackendPreviewPinnedInput>.Success(AcquireInput(entry.Input));
        }
    }

    /// <summary>在闸门内取得原固定输入的整组独立引用，不重新读取工程或作者草稿。</summary>
    public BackendOperationResult<BackendPreviewPinnedInput> CaptureRenderBackendInput(string clientId, string previewId,
        string expectedPreviewGeneration)
    {
        lock (_gate)
        {
            if (!TryFind(clientId, previewId, out Entry? entry, out BackendErrorResponse? error))
                return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(error!);
            if (!TryExpected(clientId, previewId, expectedPreviewGeneration, entry!.Session.Source.SourceRevision,
                out entry, out error)) return BackendOperationResult<BackendPreviewPinnedInput>.Rejected(error!);
            return BackendOperationResult<BackendPreviewPinnedInput>.Success(AcquireInput(entry!.Input!));
        }
    }

    /// <summary>只提交设备切换；代次、原快照对象和完整来源身份必须仍与捕获时一致。</summary>
    public BackendPreviewSessionResult SwitchRenderBackend(string clientId, string previewId, string expectedPreviewGeneration,
        BackendPreviewPinnedInput input)
    {
        lock (_gate)
        {
            if (!TryExpected(clientId, previewId, expectedPreviewGeneration, input.Source.SourceRevision,
                out Entry? entry, out BackendErrorResponse? error)) return new(null, error);
            if (entry!.Input is null || entry.Input.Source != input.Source
                || !ReferenceEquals(entry.Input.Snapshot, input.Snapshot) || entry.Input.EditCandidate != input.EditCandidate
                || entry.Input.PackageClosure != input.PackageClosure)
                return Rejected(BackendErrorCode.PreviewGenerationConflict, "固定预览输入已经改变。");
            error = ValidateInput(input.Source, entry.Session.Size, entry.Session.AlphaMode, input);
            if (error is not null) return new(null, error);
            if (entry.Session.RenderBackend == input.RenderBackend)
            {
                input.RetentionLease?.Dispose();
                return Success(entry);
            }
            return CommitUpdate(entry, entry.Session with { RenderBackend = input.RenderBackend }, input);
        }
    }

    /// <summary>更新需要旧来源修订与代次；新来源必须与已捕获固定输入完全一致。</summary>
    public BackendPreviewSessionResult Update(string clientId, string previewId, string expectedSourceRevision,
        PreviewInputRequest request, BackendPreviewPinnedInput input)
    {
        if (request?.RenderBackend is not (null or "cpu" or "gpu")
            || request?.RenderBackend is not null && request.RenderBackend != input.RenderBackend)
            return Rejected(BackendErrorCode.InvalidRequest, "渲染后端必须与固定输入一致。");
        BackendErrorResponse? validation = ValidateInput(request?.Source, request?.Size, request?.AlphaMode, input);
        if (validation is not null) return new(null, validation);
        lock (_gate)
        {
            if (!TryExpected(clientId, previewId, request!.ExpectedPreviewGeneration, expectedSourceRevision,
                out Entry? entry, out BackendErrorResponse? error)) return new(null, error);
            if (entry!.Session.PreviewKind is "presetCandidate" or "template")
                return new(null, new(BackendErrorCode.CommandNotSupported,
                    "固定候选或模板试样会话不可更新输入；请创建新的预览会话。", "previewKind"));
            if (entry!.Session.PreviewKind == "editCandidate" && (input.Source.Kind != "editCandidate"
                || input.EditCandidate is null || input.Source.SourceId != entry.Session.Source.SourceId)
                || entry.Session.PreviewKind != "editCandidate" && input.Source.Kind == "editCandidate")
                return new(null, new(BackendErrorCode.InvalidRequest, "编辑候选只能由同一工程的候选来源替换。"));
            PreviewSessionResponse next = entry!.Session with
            {
                Source = input.Source, PreviewGeneration = NextGeneration(), Size = request.Size,
                AlphaMode = request.AlphaMode, State = "idle", LatestFrame = null,
                LatestRequestSeq = null, DiagnosticsCursor = null, EditCandidate = input.EditCandidate,
                PackageClosure = input.PackageClosure, RenderBackend = input.RenderBackend,
            };
            return CommitUpdate(entry, next, input, nextGeneration: false);
        }
    }

    private BackendPreviewSessionResult CommitUpdate(Entry entry, PreviewSessionResponse next,
        BackendPreviewPinnedInput input, bool nextGeneration = true)
    {
            next = next with { PreviewGeneration = nextGeneration ? NextGeneration() : next.PreviewGeneration,
                State = "idle", LatestFrame = null, LatestRequestSeq = null, DiagnosticsCursor = null };
            BackendErrorResponse? error = Activate(entry, next);
            if (error is not null) return new(null, error);
            IDisposable? oldLease = entry.Input!.RetentionLease;
            Reset(entry, next);
            entry.Input = ShareInput(input);
            entry.Coordinator.Invalidate();
            try { oldLease?.Dispose(); }
            catch (Exception) { Fail(entry, new(BackendErrorCode.InternalError, "旧预览输入租约释放失败。")); }
            Notify(entry);
            return Success(entry);
    }

    private static BackendPreviewPinnedInput ShareInput(BackendPreviewPinnedInput input) => input with
        { RetentionLease = BackendPreviewRetentionLease.Own(input.RetentionLease) };

    private static BackendPreviewPinnedInput AcquireInput(BackendPreviewPinnedInput input) => input with
        { RetentionLease = ((BackendPreviewRetentionLease)input.RetentionLease!).AcquireReference() };

    /// <summary>seek原子换代次；playback同代次只向前移动，执行与最新待处理槽由协调器管理。</summary>
    public BackendPreviewFrameRequestResult RequestFrame(string clientId, string previewId,
        string expectedSourceRevision, PreviewFrameRequest request)
    {
        if (request is null || !TryNumber(request.RequestSeq, out long sequence) || sequence <= 0
            || !TryNumber(request.TimeUs, out long time) || request.Intent is not ("seek" or "playback"))
            return new(null, new(BackendErrorCode.InvalidRequest, "请求需要规范的序号、非负微秒及seek/playback意图。"));
        lock (_gate)
        {
            if (!TryExpected(clientId, previewId, request.PreviewGeneration, expectedSourceRevision,
                out Entry? entry, out BackendErrorResponse? error)) return new(null, error);
            Entry current = entry!;
            if (sequence <= current.LastAcceptedSequence)
                return new(null, new(BackendErrorCode.PreviewRequestOutOfOrder, "请求序号重复或倒退。", "requestSeq"));
            if (time > current.Input!.Snapshot.Composition.OutputDurationUs)
                return new(null, new(BackendErrorCode.InvalidRequest, "预览时间超过固定工程时长。", "timeUs"));
            if (request.Intent == "playback" && current.LastRequestedTime is { } previous && time < previous)
                return new(null, new(BackendErrorCode.PreviewRequestOutOfOrder, "回退必须显式seek并换代次。", "timeUs"));
            if (request.Intent == "seek")
            {
                PreviewSessionResponse next = current.Session with
                {
                    PreviewGeneration = NextGeneration(), State = "idle", LatestFrame = null,
                    LatestRequestSeq = null, DiagnosticsCursor = null, EditCandidate = current.Input!.EditCandidate,
                    PackageClosure = current.Input!.PackageClosure,
                };
                error = Activate(current, next);
                if (error is not null) return new(null, error);
                Reset(current, next);
                current.Coordinator.Invalidate();
            }
            current.LastAcceptedSequence = sequence;
            current.LastRequestedTime = time;
            current.Failure = null;
            current.RenderFailureCode = null;
            current.Session = current.Session with { LatestRequestSeq = request.RequestSeq, State = "queued" };
            BackendPreviewPinnedInput fixedInput = current.Input!;
            BackendPreviewRequest render = new()
            {
                SessionFactory = fixedInput.Factory,
                PreviewGeneration = long.Parse(current.Session.PreviewGeneration, CultureInfo.InvariantCulture),
                SourceRevision = fixedInput.Source.SourceRevision,
                RequestSequence = sequence,
                RenderRequest = RenderRequest.At(fixedInput.Snapshot, time) with
                {
                    Target = new() { PixelWidth = current.Session.Size.Width, PixelHeight = current.Session.Size.Height },
                    TransparentBackground = current.Session.AlphaMode == PreviewAlphaMode.Straight,
                },
            };
            current.Requests.Add(render, request with { PreviewGeneration = current.Session.PreviewGeneration });
            current.Coordinator.Submit(render);
            Notify(current);
            return new(new(previewId, current.Session.PreviewGeneration, request.RequestSeq, request.TimeUs, "accepted"), null);
        }
    }

    public Task<BackendPreviewSessionResult> CloseAsync(string clientId, string previewId,
        string expectedGeneration, string expectedSourceRevision, CancellationToken cancellationToken = default)
    {
        Task close;
        Entry current;
        lock (_gate)
        {
            if (!TryFind(clientId, previewId, out Entry? entry, out BackendErrorResponse? error))
                return Task.FromResult(new BackendPreviewSessionResult(null, error));
            current = entry!;
            if (current.Session.PreviewGeneration != expectedGeneration || current.Session.Source.SourceRevision != expectedSourceRevision)
                return Task.FromResult(Rejected(BackendErrorCode.PreviewGenerationConflict, "预览输入基线已改变。"));
            close = BeginClose(current);
        }
        return FinishCloseAsync(current, close, cancellationToken);
    }

    private static async Task<BackendPreviewSessionResult> FinishCloseAsync(Entry entry, Task close, CancellationToken token)
    {
        await close.WaitAsync(token).ConfigureAwait(false);
        return new(new(entry.Session, entry.Failure, entry.RenderFailureCode), null);
    }

    /// <summary>宿主的客户端租约清理入口；不是具名用户编辑命令。</summary>
    public Task DrainClientAsync(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        lock (_gate) return Task.WhenAll(_sessions.Values.Where(entry => entry.ClientId == clientId).ToArray().Select(BeginClose));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _stopping = true;
            _disposeTask = DisposeCoreAsync(Task.WhenAll(_sessions.Values.ToArray().Select(BeginClose)));
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task draining)
    {
        try { await draining.ConfigureAwait(false); }
        finally
        {
            _notifications.Writer.TryComplete();
            await _notificationPump.ConfigureAwait(false);
            Updated = null;
        }
    }

    private async Task PumpNotificationsAsync()
    {
        await foreach (BackendPreviewSessionEvent notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Action<BackendPreviewSessionEvent>? callbacks = Updated;
            if (callbacks is null) continue;
            foreach (Action<BackendPreviewSessionEvent> callback in callbacks.GetInvocationList().Cast<Action<BackendPreviewSessionEvent>>())
            {
                try { callback(notification); }
                catch (Exception) { /* A failed advisory observer cannot alter accepted pixels or stop other observers. */ }
            }
        }
    }

    private void Notify(Entry entry) => _notifications.Writer.TryWrite(new(entry.ClientId, new(entry.Session, entry.Failure, entry.RenderFailureCode)));

    private Task BeginClose(Entry entry)
    {
        if (entry.Closing is not null) return entry.Closing;
        entry.Session = entry.Session with { State = "closing", LatestFrame = null, DiagnosticsCursor = null };
        Notify(entry);
        _frames.Close(entry.ClientId, entry.Session.PreviewId);
        _diagnostics.Close(entry.ClientId, entry.Session.PreviewId);
        entry.Closing = CloseCoreAsync(entry);
        return entry.Closing;
    }

    private async Task CloseCoreAsync(Entry entry)
    {
        try { await entry.Coordinator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            lock (_gate) entry.Failure = new(BackendErrorCode.InternalError, "预览资源释放失败。");
        }
        finally
        {
            lock (_gate)
            {
                entry.Session = entry.Session with { State = "closed" };
                try { entry.Input?.RetentionLease?.Dispose(); }
                catch (Exception) { entry.Failure = new(BackendErrorCode.InternalError, "预览固定输入租约释放失败。"); }
                entry.Input = null;
                entry.Requests.Clear();
                _sessions.Remove(entry.Session.PreviewId);
                Notify(entry);
            }
        }
    }

    private void Receive(Entry entry, BackendPreviewUpdate update)
    {
        PreviewSessionResponse current;
        PreviewFrameRequest transport;
        lock (_gate)
        {
            if (!Current(entry, update) || !entry.Requests.TryGetValue(update.Request, out transport!)) return;
            current = entry.Session;
            if (update.State == BackendPreviewUpdateState.Preparing)
            {
                entry.Session = current with { State = "rendering" };
                Notify(entry);
                return;
            }
        }
        if (update.State == BackendPreviewUpdateState.Failed)
        {
            FailCurrent(entry, update, current.Limits, new(BackendErrorCode.InternalError, "预览渲染失败。"),
                update.FailureDiagnostics, update.FailureCode, update.FailureSourceDiagnostics, update.FailureImageEffectDiagnostics, update.FailureMaterialDiagnostics);
            return;
        }
        byte[] png;
        BackendDiagnosticDto[] diagnostics;
        try
        {
            BackendPreviewFrame frame = update.Frame ?? throw new InvalidOperationException("Missing ready frame.");
            if (frame.Target.PixelWidth != current.Size.Width || frame.Target.PixelHeight != current.Size.Height
                || frame.ActualTimeUs != update.Request.RenderRequest.TimeUs
                || frame.Format != PixelFormat.Bgra8888Premultiplied
                || frame.Pixels.LongLength != (long)current.Size.Width * current.Size.Height * 4)
                throw new InvalidOperationException("Frame output does not match pinned input.");
            if (frame.DiagnosticSnapshot.HasError)
                throw new RenderSessionFailureException("PreviewRuntimeDiagnosticError", "渲染存在实际Error报告。", frame.DiagnosticSnapshot)
                    { ImageEffectDiagnostics = frame.ImageEffectDiagnostics, MaterialDiagnostics = frame.MaterialDiagnostics };
            diagnostics = ProjectDiagnostics(frame.DiagnosticSnapshot, current.Limits, image: frame.ImageEffectDiagnostics, material: frame.MaterialDiagnostics);
            png = _encoder.Encode(frame) ?? throw new InvalidOperationException("Missing PNG content.");
        }
        catch (RenderSessionFailureException failure)
        {
            FailCurrent(entry, update, current.Limits, new(BackendErrorCode.InternalError, "预览渲染或诊断生成失败。"),
                failure.DiagnosticSnapshot, failure.Code, failure.SourceDiagnostics, failure.ImageEffectDiagnostics, failure.MaterialDiagnostics);
            return;
        }
        catch (Exception)
        {
            lock (_gate) if (Current(entry, update)) Fail(entry, new(BackendErrorCode.InternalError, "预览帧编码或诊断生成失败。"));
            return;
        }
        lock (_gate)
        {
            if (!Current(entry, update) || update.RequestSequence <= entry.LastPublishedSequence
                || entry.LastPublishedTime is { } lastTime && update.Frame!.ActualTimeUs < lastTime) return;
            string id = "frame-" + Guid.NewGuid().ToString("N");
            DateTimeOffset now = _clock.GetUtcNow();
            PreviewFrameMetadataDto metadata = new(id, current.PreviewId, current.PreviewGeneration,
                current.Source.SourceRevision, transport.RequestSeq, transport.TimeUs, current.Size.Width, current.Size.Height,
                current.AlphaMode, new("handle-" + Guid.NewGuid().ToString("N"), "image/png", png.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(png)), now.AddSeconds(current.Limits.FrameLeaseLifetimeSeconds),
                    "/api/v1/preview-frames/" + id + "/content"), RenderBackend: current.RenderBackend);
            BackendErrorResponse? error;
            try
            {
                error = PublishDiagnostics(entry, diagnostics);
                if (error is null) error = _frames.Publish(entry.ClientId, entry.Session, transport, metadata, png,
                    entry.LastPublishedSequence, now);
            }
            catch (Exception) { error = new(BackendErrorCode.InternalError, "预览帧发布失败。"); }
            if (error is not null) { Fail(entry, error); return; }
            entry.LastPublishedSequence = update.RequestSequence;
            entry.LastPublishedTime = update.Frame!.ActualTimeUs;
            entry.Failure = null;
            entry.RenderFailureCode = null;
            entry.Session = entry.Session with { State = "ready", LatestFrame = metadata, DiagnosticsCursor = null };
            Notify(entry);
        }
    }

    private BackendErrorResponse? PublishDiagnostics(Entry entry, BackendDiagnosticDto[] values)
    {
        List<PreviewDiagnosticsPageDto> pages = [];
        int size = entry.Session.Limits.MaxDiagnosticsPageSize;
        int count = Math.Max(1, (values.Length + size - 1) / size);
        string? cursor = null;
        for (int index = 0; index < count; index++)
        {
            string? next = index + 1 < count ? "cursor-" + Guid.NewGuid().ToString("N") : null;
            pages.Add(new(entry.Session.PreviewId, entry.Session.PreviewGeneration, entry.Session.Source.SourceRevision,
                values.Skip(index * size).Take(size).ToArray(), next, next is not null, cursor));
            cursor = next;
        }
        return _diagnostics.PublishBatch(entry.ClientId, entry.Session, pages);
    }

    private BackendDiagnosticDto[] ProjectDiagnostics(RuntimeDiagnosticSnapshot? snapshot, PreviewLimitsDto limits,
        ImmutableArray<RenderSourceDiagnostic> source = default, ImmutableArray<RenderImageEffectDiagnostic> image = default,
        ImmutableArray<RenderMaterialDiagnostic> material = default) =>
        BackendPreviewDiagnosticsMapper.Project(snapshot, source, image, material, limits, _options.MaxDiagnosticPages);

    // Projection and its UTF8 budget can reach 512KiB: perform them before entering the registry gate.
    private void FailCurrent(Entry entry, BackendPreviewUpdate update, PreviewLimitsDto limits, BackendErrorResponse failure,
        RuntimeDiagnosticSnapshot? actual, string? code, ImmutableArray<RenderSourceDiagnostic> source,
        ImmutableArray<RenderImageEffectDiagnostic> image, ImmutableArray<RenderMaterialDiagnostic> material)
    {
        if (code == "PreviewDiagnosticsBudgetExceeded")
            failure = new(BackendErrorCode.DiagnosticLimitExceeded, "完整实际诊断超过预览预算，未截断或伪造报告。", "diagnostics");
        BackendDiagnosticDto[] diagnostics;
        try
        {
            diagnostics = actual is null && source.IsDefaultOrEmpty && image.IsDefaultOrEmpty && material.IsDefaultOrEmpty
                ? [new(code ?? failure.Code.ToString(), "error", failure.Message, failure.Field, OriginKind: "transportFailure")]
                : ProjectDiagnostics(actual, limits, source, image, material);
        }
        catch (Exception invalid)
        {
            bool budget = invalid is RenderSessionFailureException { Code: "PreviewDiagnosticsBudgetExceeded" };
            failure = new(budget ? BackendErrorCode.DiagnosticLimitExceeded : BackendErrorCode.InternalError,
                budget ? "完整实际诊断超过预览预算，未截断或伪造报告。" : "实际诊断包含非法来源或字段。");
            code = budget ? "PreviewDiagnosticsBudgetExceeded" : "PreviewDiagnosticsInvalid";
            diagnostics = [new(code, "error", failure.Message, "diagnostics", OriginKind: "transportFailure")];
        }
        lock (_gate)
            if (Current(entry, update)) Fail(entry, failure, renderFailureCode: code, projected: diagnostics);
    }

    private void Fail(Entry entry, BackendErrorResponse failure, RuntimeDiagnosticSnapshot? actual = null, string? renderFailureCode = null,
        ImmutableArray<RenderSourceDiagnostic> source = default, ImmutableArray<RenderImageEffectDiagnostic> image = default,
        ImmutableArray<RenderMaterialDiagnostic> material = default, BackendDiagnosticDto[]? projected = null)
    {
        entry.Failure = failure;
        entry.RenderFailureCode = renderFailureCode;
        entry.Session = entry.Session with { State = "failed", LatestFrame = null, DiagnosticsCursor = null };
        BackendDiagnosticDto[] diagnostics;
        try
        {
            diagnostics = projected ?? (actual is null && source.IsDefaultOrEmpty && image.IsDefaultOrEmpty && material.IsDefaultOrEmpty ? [new(renderFailureCode ?? failure.Code.ToString(), "error", failure.Message, failure.Field,
                OriginKind: "transportFailure")] : ProjectDiagnostics(actual, entry.Session.Limits, source, image, material));
        }
        catch (Exception invalid)
        {
            bool budget = invalid is RenderSessionFailureException { Code: "PreviewDiagnosticsBudgetExceeded" };
            entry.Failure = new(budget ? BackendErrorCode.DiagnosticLimitExceeded : BackendErrorCode.InternalError,
                budget ? "完整实际诊断超过预览预算，未截断或伪造报告。" : "实际诊断包含非法来源或字段。");
            entry.RenderFailureCode = budget ? "PreviewDiagnosticsBudgetExceeded" : "PreviewDiagnosticsInvalid";
            diagnostics = [new(entry.RenderFailureCode, "error", entry.Failure.Message, "diagnostics", OriginKind: "transportFailure")];
        }
        BackendErrorResponse? publish;
        try { publish = PublishDiagnostics(entry, diagnostics); }
        catch (Exception) { publish = new(BackendErrorCode.InternalError, "诊断发布失败。", "diagnostics"); }
        if (publish is not null)
        {
            entry.Failure = publish;
            entry.RenderFailureCode = publish.Code == BackendErrorCode.DiagnosticLimitExceeded ? "PreviewDiagnosticsBudgetExceeded" : "PreviewDiagnosticsInvalid";
            // Replace an invalid actual report with an explicit transport failure, never retain a partial page set.
            PublishDiagnostics(entry, [new(entry.RenderFailureCode, "error", publish.Message, "diagnostics", OriginKind: "transportFailure")]);
        }
        Notify(entry);
    }

    private bool Current(Entry entry, BackendPreviewUpdate update)
        => entry.Closing is null && entry.Session.State != "closing" && entry.Session.State != "closed"
            && _sessions.TryGetValue(entry.Session.PreviewId, out Entry? actual) && ReferenceEquals(actual, entry)
            && entry.Session.PreviewGeneration == update.Request.PreviewGeneration.ToString(CultureInfo.InvariantCulture)
            && entry.Session.Source.SourceRevision == update.Request.SourceRevision
            && ReferenceEquals(entry.Input!.Factory, update.Request.SessionFactory)
            && ReferenceEquals(entry.Input!.Snapshot, update.Request.RenderRequest.Project)
            && update.RequestSequence <= entry.LastAcceptedSequence;

    private BackendErrorResponse? Activate(Entry entry, PreviewSessionResponse next)
    {
        BackendErrorResponse? error = _frames.Activate(entry.ClientId, next);
        if (error is not null) return error;
        error = _diagnostics.Activate(entry.ClientId, next);
        if (error is not null) _frames.Close(entry.ClientId, next.PreviewId);
        return error;
    }

    private static void Reset(Entry entry, PreviewSessionResponse next)
    {
        entry.Session = next;
        entry.Failure = null;
        entry.RenderFailureCode = null;
        entry.LastPublishedSequence = 0;
        entry.LastRequestedTime = null;
        entry.LastPublishedTime = null;
    }

    private bool TryFind(string client, string id, out Entry? entry, out BackendErrorResponse? error)
    {
        if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(id)
            || !_sessions.TryGetValue(id, out entry) || entry.ClientId != client)
        { entry = null; error = new(BackendErrorCode.PreviewSessionNotFound, "预览会话不存在。"); return false; }
        error = null;
        return true;
    }

    private bool TryExpected(string client, string id, string generation, string revision, out Entry? entry, out BackendErrorResponse? error)
    {
        if (!TryFind(client, id, out entry, out error)) return false;
        if (_stopping || entry!.Closing is not null || entry.Session.State == "closing")
        { error = new(BackendErrorCode.SessionClosing, "预览会话正在关闭。"); return false; }
        if (entry.Session.PreviewGeneration != generation || entry.Session.Source.SourceRevision != revision)
        { error = new(BackendErrorCode.PreviewGenerationConflict, "预览输入基线已改变。"); return false; }
        return true;
    }

    private BackendErrorResponse? ValidateInput(PreviewSourceDto? source, PreviewSizeDto? size, PreviewAlphaMode? alpha,
        BackendPreviewPinnedInput? input)
    {
        if (input?.Snapshot is null || input.Factory is null || input.RenderBackend is not ("cpu" or "gpu")
            || source is null || source != input.Source
            || source.Kind is not ("project" or "presetCandidate" or "template" or "editCandidate" or "authorDraft") || string.IsNullOrWhiteSpace(source.SourceId)
            || string.IsNullOrWhiteSpace(source.SourceRevision) || source.SourceRevision.Length > 256
            || (source.Kind == "authorDraft" && (string.IsNullOrWhiteSpace(source.AuthorSourceRevision)
                || source.TemplateSample is null
                || source.TemplateSample.SampleId is not (TemplatePreviewLimits.SingleLineSampleId or TemplatePreviewLimits.MultiLineSampleId)
                || string.IsNullOrWhiteSpace(source.TemplateSample.TargetKey)
                || source.TemplateSample.MaterialSlot is not (null or "unsungFill" or "sungFill" or "stroke")))
            || (source.Kind != "authorDraft" && source.AuthorSourceRevision is not null)
            || (source.Kind is not ("authorDraft" or "template") && source.TemplateSample is not null)
            || size is null || size.Width <= 0 || size.Height <= 0 || size.Width > _options.Limits.MaxWidth
            || size.Height > _options.Limits.MaxHeight || alpha is null || !Enum.IsDefined(alpha.Value))
            return new(BackendErrorCode.InvalidRequest, "预览来源、固定输入或输出设置无效。");
        ProjectSnapshot snapshot = input.Snapshot;
        try
        {
            if (checked((long)size.Width * size.Height * 4) > _options.Limits.MaxRawFrameBytes)
                return new(BackendErrorCode.ResourceTooLarge, "预览裸像素超过公布字节预算。", "size");
        }
        catch (OverflowException) { return new(BackendErrorCode.ResourceTooLarge, "预览裸像素超出可表示预算。", "size"); }
        try
        {
            if (input.PreparedInput is { } prepared)
            {
                if (!ReferenceEquals(prepared.Snapshot, snapshot) || input.VerifiedProfile != prepared.SourceProfile)
                    return new(BackendErrorCode.CommandNotSupported, "固定工程输入身份或能力不匹配。", "source");
                return null;
            }
            if (snapshot.Background.Kind == BackgroundKind.Image)
            {
                if (source.Kind is not ("project" or "editCandidate") || input.VerifiedProfile != ProjectImagePreviewSnapshotCodec.Profile
                    || snapshot.Background.Media is null || !snapshot.Background.HasImage
                    || !snapshot.PresetApplications.Items.IsEmpty)
                    return new(BackendErrorCode.CommandNotSupported, "当前预览核心不接受未固定的图片背景输入。", "background");
                return null;
            }
            string profile = ProjectPreviewSnapshotProfile.Select(snapshot);
            if (ProjectSnapshotSourceProfile.IsPackage(profile))
            {
                if (source.Kind is not ("project" or "editCandidate" or "presetCandidate" or "authorDraft") || input.VerifiedProfile != profile)
                    return new(BackendErrorCode.CommandNotSupported, "当前预览核心不接受未隔离的脚本或包输入。", "source");
                _ = ProjectSnapshotSourceProfile.Encode(snapshot);
                return null;
            }
        }
        catch (SnapshotContractException error)
        { return new(BackendErrorCode.CommandNotSupported, "包预览固定来源不受支持。", "source", FailureCode: error.Code); }
        if (!snapshot.PresetApplications.Items.IsEmpty)
        {
            try { StaticProjectSnapshotCodec.ValidateSupported(snapshot, StaticProjectSnapshotCodec.PresetsProfile); }
            catch (SnapshotContractException)
            { return new(BackendErrorCode.CommandNotSupported, "当前公共预览只接受完整静态预设来源。", "source"); }
        }
        return null;
    }

    private string NextGeneration() => checked(++_nextGeneration).ToString(CultureInfo.InvariantCulture);
    private static bool TryNumber(string? value, out long result)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= 0 && result.ToString(CultureInfo.InvariantCulture) == value;
    private static BackendPreviewSessionResult Success(Entry entry) => new(new(entry.Session, entry.Failure, entry.RenderFailureCode), null);
    private static BackendPreviewSessionResult Rejected(BackendErrorCode code, string message) => new(null, new(code, message));
}
