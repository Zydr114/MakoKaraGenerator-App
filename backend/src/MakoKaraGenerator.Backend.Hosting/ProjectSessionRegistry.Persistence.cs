using System.Collections.Concurrent;
using System.Globalization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private ProjectPersistenceOptions? _persistence;
    private ProjectDirectoryHandles? _handles;
    private BackendJobLedger? _ledger;
    private Func<string, IProjectSaveStorage>? _storageFactory;
    private Func<string, CancellationToken, Task<LoadedWaveform?>>? _loadAudio;
    private readonly ConcurrentDictionary<string, BackendJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _jobGate = new();
    private CancellationTokenSource? _persistenceStop;
    private Task _autoSaveLoop = Task.CompletedTask;
    private bool _persistenceStopping;
    private readonly HashSet<string> _recovering = new(StringComparer.Ordinal);

    /// <summary>可信组合根登记持久 profile 和允许的工程根；只允许在接受请求前调用一次。</summary>
    public void ConfigurePersistence(ProjectPersistenceOptions options,
        Func<string, IProjectSaveStorage>? storageFactory = null,
        Func<string, CancellationToken, Task<LoadedWaveform?>>? loadAudio = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        if (_persistence is not null) throw new InvalidOperationException("持久组合已经配置。");
        _persistence = options with { StateRoot = Path.GetFullPath(options.StateRoot),
            AllowedProjectRoots = options.AllowedProjectRoots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
        Directory.CreateDirectory(_persistence.StateRoot);
        _profileLease = ProjectWriteLease.Acquire(Path.Combine(_persistence.StateRoot, ".backend-profile"));
        RemoveStalePinnedInputs();
        _handles = new(_persistence); _storageFactory = storageFactory;
        _ledger = new(_hostInstanceId, _persistence, _timeProvider);
        AudioWaveformLoader loader = AudioWaveformLoader.CreateForSelfContainedFiles();
        _loadAudio = loadAudio ?? ((path, token) => loader.LoadAsync(path, cancellationToken: token));
        _persistenceStop = new();
        _autoSaveLoop = RunAutoSaveAsync(_persistenceStop.Token);
    }

    public Task<BackendOperationResult<FileHandleResponse>> CreateFileHandleAsync(string clientId,
        FileHandleRequest request, CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, "file-handles:create", request with { Metadata = null },
            () => Task.FromResult(_handles is null || _persistenceStopping
                ? Reject<FileHandleResponse>(BackendErrorCode.CommandNotSupported, "工程目录尚未配置或服务正在停止。")
                : _handles.Create(clientId, request)), cancellationToken);

    public Task<BackendOperationResult<JobResponse>> StartOpenAsync(string clientId, OpenProjectSessionRequest request,
        CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, "project-sessions:open", request with { Metadata = null },
            () => StartOpenCoreAsync(clientId, request), cancellationToken);

    private Task<BackendOperationResult<JobResponse>> StartOpenCoreAsync(string clientId, OpenProjectSessionRequest request)
    {
        if (request.Access is not ("read" or "readWrite")) return Task.FromResult(Reject<JobResponse>(BackendErrorCode.InvalidRequest, "打开访问模式无效。"));
        try
        {
            ProjectDirectoryHandles.Handle handle = Handles.Get(clientId, request.ProjectDirectoryHandleId, request.Access == "readWrite");
            return Task.FromResult(StartJob(clientId, "open", null, job => OpenCoreAsync(clientId, handle.Path, request.Access == "read", null, job),
                failureMapper: _audioPreparationConfigured ? AudioPreparationFailure : null,
                cancelOnShutdown: _audioPreparationConfigured));
        }
        catch (Exception error) { return Task.FromResult(PersistenceFailure<JobResponse>(error)); }
    }

    public Task<BackendOperationResult<JobResponse>> StartRecoverAsync(string clientId, RecoverProjectSessionRequest request,
        CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, "project-sessions:recover", request with { Metadata = null },
            () =>
            {
                lock (_jobGate)
                {
                    RecoveryEntry? entry = RecoveryEntries().FirstOrDefault(item => item.Id == request.RecoveryId);
                    if (entry is null) return Task.FromResult(Reject<JobResponse>(BackendErrorCode.ResourceNotFound, "恢复候选不存在。"));
                    if (!_recovering.Add(entry.Id)) return Task.FromResult(Reject<JobResponse>(BackendErrorCode.ActiveJobs, "恢复候选正在使用。"));
                    BackendOperationResult<JobResponse> accepted = StartJob(clientId, "recover", null, async job =>
                    {
                        try { await OpenCoreAsync(clientId, entry.Inspection.Session.BackupDirectoryPath, false, entry, job).ConfigureAwait(false); }
                        finally { lock (_jobGate) _recovering.Remove(entry.Id); }
                    }, failureMapper: _audioPreparationConfigured ? AudioPreparationFailure : null,
                        cancelOnShutdown: _audioPreparationConfigured);
                    if (!accepted.Accepted) _recovering.Remove(entry.Id);
                    return Task.FromResult(accepted);
                }
            }, cancellationToken);

    private async Task OpenCoreAsync(string clientId, string directory, bool readOnly, RecoveryEntry? recovery, BackendJob job)
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("打开候选");
        workspace.EnableWriteLeaseOwnership();
        ProjectSession? published = null; ProjectSession? candidateSession = null; Exception? primary = null;
        IDisposable? audioReference = null; IDisposable? backgroundReference = null;
        string candidateId = "project-" + Guid.NewGuid().ToString("N");
        string fixedDirectory = Path.Combine(_persistence!.StateRoot, "sessions", candidateId, "loaded-input");
        try
        {
            job.Cancellation.ThrowIfCancellationRequested();
            ProjectDirectoryHandles.EnsureNoLinks(directory);
            ProjectLoadResult loaded;
            if (recovery is not null)
            {
                loaded = await Task.Run(() => ProjectStore.Load(directory), job.Cancellation).ConfigureAwait(false);
                if (loaded.Project is { } recovered)
                {
                    string? original = recovery.Inspection.Session.ProjectDirectoryPath;
                    if (original is not null && !Handles.IsAuthorized(original))
                        throw new ResourceStoreException(BackendErrorCode.PathNotAllowed, "恢复原目录不在启动授权范围内。");
                    workspace.Recover(recovered, original);
                }
            }
            else if (readOnly)
            {
                loaded = await Task.Run(() => ProjectStore.Load(directory), job.Cancellation).ConfigureAwait(false);
                if (loaded.Project is { } project) workspace = ProjectWorkspace.Adopt(project, directory);
            }
            else loaded = await Task.Run(() => workspace.Open(directory), job.Cancellation).ConfigureAwait(false);
            if (loaded.Project is null)
            { job.Finish(null, new(BackendErrorCode.ProjectLoadFailed, "工程版本或闭包无效，未创建会话。")); return; }
            job.Cancellation.ThrowIfCancellationRequested();
            Project projectInput = workspace.Project;
            Project fixedProject = await PinInputAsync(projectInput, directory, fixedDirectory).ConfigureAwait(false);
            if (!workspace.PinPreparedMediaPaths(projectInput, fixedProject.AudioPath, fixedProject.Background.Path))
                throw new IOException("打开候选身份已经改变。");
            projectInput = workspace.Project;
            job.Cancellation.ThrowIfCancellationRequested();
            string audioId = string.Empty; long duration = 0;
            LoadedWaveform? preparedWaveform = null;
            StoredAsset? preparedAudio = null;
            if (projectInput.AudioPath is { } audioPath)
            {
                StoredAsset audio = await _resources.ImportProjectAudioAsync(clientId, audioPath, job.Cancellation).ConfigureAwait(false);
                audioReference = _resources.AcquireReference(clientId, audio.AssetId);
                if (projectInput.AudioMedia is { } media && (media.Hash != audio.Sha256 || media.ByteLength != audio.Size))
                    throw new ResourceStoreException(BackendErrorCode.AudioPreparationFailed, "固定工程音频与授权资源声明不一致。");
                using AudioPreparationOperation? audioOperation = ReserveAudioPreparation(clientId, job.Cancellation);
                LoadedWaveform? waveform;
                try
                {
                    if (audioOperation is not null) await audioOperation.WaitAsync().ConfigureAwait(false);
                    CancellationToken preparationToken = audioOperation?.Token ?? job.Cancellation;
                    VerifyFixedAudioSource(clientId, audio, preparationToken);
                    waveform = audioOperation is null
                        ? await _loadAudio!(audio.StoragePath, preparationToken).ConfigureAwait(false)
                        : await PrepareManagedAudioAsync(audio, preparationToken).ConfigureAwait(false);
                    preparationToken.ThrowIfCancellationRequested();
                    VerifyFixedAudioSource(clientId, audio, preparationToken);
                }
                catch (OperationCanceledException error) when (audioOperation is { TimedOut: true })
                { throw AudioPreparationOperation.TimeoutFailure(error); }
                if (waveform is null) throw new ResourceStoreException(BackendErrorCode.AudioPreparationFailed, "工程音轨无法准备。");
                audioId = audio.AssetId; duration = waveform.Peaks.DurationUs;
                preparedWaveform = waveform; preparedAudio = audio;
            }
            StoredAsset? preparedBackground = null;
            if (projectInput.Background is { HasMedia: true, Path: { } backgroundPath, Media: { } backgroundMedia })
            {
                await using FileStream input = new(backgroundPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                preparedBackground = await _resources.UploadAsync(clientId, backgroundMedia.OriginalName, null, input, job.Cancellation).ConfigureAwait(false);
                backgroundReference = _resources.AcquireReference(clientId, preparedBackground.AssetId);
                if (preparedBackground.Sha256 != backgroundMedia.Hash || preparedBackground.Size != backgroundMedia.ByteLength)
                    throw new ResourceStoreException(BackendErrorCode.TaskExecutionFailed, "固定背景资源与工程声明不一致。");
            }
            job.Cancellation.ThrowIfCancellationRequested();
            if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
            ProjectSession session = candidateSession = new(candidateId, _hostInstanceId, clientId, workspace,
                audioId, duration, _timeProvider, _commandRetention, _events, audioReference);
            // Move the original resource lease before any new fallible candidate setup.
            audioReference = null;
            if (preparedBackground is not null)
            {
                session.BindBackground(preparedBackground, projectInput.Background.Path!, backgroundReference!,
                    () => _resources.AcquireReference(clientId, preparedBackground.AssetId));
                backgroundReference = null;
            }
            if (preparedWaveform is not null && preparedAudio is not null)
                session.BindWaveform(_waveforms, preparedWaveform, preparedAudio);
            session.IsReadOnly = readOnly; session.SavedRevision = recovery is null ? 0 : null;
            session.PinnedInputDirectory = fixedDirectory;
            InitializeSessionPersistence(session);
            if (!await RegisterSessionAsync(session).ConfigureAwait(false))
            { throw new ResourceStoreException(BackendErrorCode.SessionConflict, "工程会话配额已满或注册失败。"); }
            published = session;
            if (recovery is not null && AutoSaveSession.Read(recovery.StateRoot) == recovery.Inspection.Session)
            {
                try { AutoSaveSession.Clear(recovery.StateRoot); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("恢复成功后的标记清理失败：{0}", error); }
            }
            job.Finish(await session.ReadSummaryAsync(CancellationToken.None).ConfigureAwait(false), diagnostics: ToDiagnostics(loaded.Report.Issues));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // Registration can throw after membership publication (for example event publication).
            // Published sessions retain ownership; failures afterwards must not roll them back.
            if (published is null && candidateSession is not null && _sessions.ContainsKey(candidateSession.ProjectSessionId))
                published = candidateSession;
            try
            {
                try { backgroundReference?.Dispose(); audioReference?.Dispose(); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["OpenAudioReferenceCleanupFailure"] = cleanup; }
            }
            finally
            {
                if (published is null)
                {
                    try
                    {
                        try { candidateSession?.DisposeResources(); }
                        catch (Exception cleanup) when (primary is not null) { primary.Data["OpenCandidateCleanupFailure"] = cleanup; }
                    }
                    finally
                    {
                        try
                        {
                            try { workspace.ReleaseWriteLease(); }
                            catch (Exception cleanup) when (primary is not null) { primary.Data["OpenWriteLeaseCleanupFailure"] = cleanup; }
                        }
                        finally
                        {
                            try { await ReleasePinnedInputAsync(fixedDirectory).ConfigureAwait(false); }
                            catch (Exception cleanup) when (primary is not null) { primary.Data["OpenPinnedInputCleanupFailure"] = cleanup; }
                        }
                    }
                }
            }
        }
    }

    public Task<BackendOperationResult<JobResponse>> StartSaveAsync(string clientId, string sessionId, SaveProjectRequest request,
        CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, $"project-sessions:{sessionId}:save", request with { Metadata = null },
            () => StartSaveCoreAsync(clientId, sessionId, request), cancellationToken);

    private async Task<BackendOperationResult<JobResponse>> StartSaveCoreAsync(string clientId, string sessionId, SaveProjectRequest request)
    {
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
        string target; ProjectSaveSnapshot snapshot; ProjectWriteLeaseReservation reservation; long revision;
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision) is { } error) return BackendOperationResult<JobResponse>.Rejected(error);
            if (session.SaveOutstanding) return Reject<JobResponse>(BackendErrorCode.ActiveJobs, "工程已有保存任务。");
            target = ResolveSaveTarget(clientId, session, request.TargetHandleId);
            ProjectDirectoryHandles.EnsureNoLinks(target);
            InitializeSessionPersistence(session);
            snapshot = session.Workspace.CaptureSave(_timeProvider.GetUtcNow());
            reservation = session.Workspace.PrepareSaveLease(target);
            revision = session.ProjectRevision;
            session.SaveOutstanding = true;
        }
        catch (Exception error) { return PersistenceFailure<JobResponse>(error); }
        finally { session.Writer.Release(); }
        BackendOperationResult<JobResponse> accepted = StartJob(clientId, "save", sessionId,
            job => SaveCoreAsync(session, snapshot, revision, target, reservation, job), target);
        if (!accepted.Accepted)
        {
            reservation.Dispose();
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try { session.SaveOutstanding = false; }
            finally { session.Writer.Release(); }
        }
        return accepted;
    }

    private static async Task SaveCoreAsync(ProjectSession session, ProjectSaveSnapshot snapshot, long revision, string target,
        ProjectWriteLeaseReservation reservation, BackendJob job)
    {
        using (reservation)
        {
            try
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                // Capture happened inside Writer. IO is owned by the coordinator and never holds Writer.
                ProjectSaveResult saved = await session.Saves!.SaveAsync(snapshot, target, reservation).ConfigureAwait(false);
                ProjectSessionSummaryResponse? summary = null; BackendErrorResponse? failure = null;
                await session.Writer.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (saved.Error is not null) failure = new(BackendErrorCode.ProjectLoadFailed, "保存失败，原工程入口保持。");
                    else if (!session.Workspace.AcceptSave(snapshot, target, saved.Issues, reservation))
                        failure = new(BackendErrorCode.SessionConflict, "保存已落盘，但工作区身份已经改变。");
                    else
                    {
                        session.SavedRevision = revision;
                        session.PublishEvent("project.saved");
                        summary = session.BuildSummary();
                    }
                }
                finally { session.Writer.Release(); }
                job.Finish(summary, failure, savedRevision: summary is not null ? Revision(revision) : null,
                    diagnostics: ToDiagnostics(saved.Issues));
            }
            finally
            {
                await session.Writer.WaitAsync().ConfigureAwait(false);
                try { session.SaveOutstanding = false; }
                finally { session.Writer.Release(); }
            }
        }
    }

    public Task<BackendOperationResult<JobResponse>> StartCloseAsync(string clientId, string sessionId, CloseProjectRequest request,
        CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata, $"project-sessions:{sessionId}:close", request with { Metadata = null },
            () => StartCloseCoreAsync(clientId, sessionId, request), cancellationToken);

    private async Task<BackendOperationResult<JobResponse>> StartCloseCoreAsync(string clientId, string sessionId, CloseProjectRequest request)
    {
        if (request.Policy is not ("requireClean" or "save" or "discard") || request.ActiveJobs is not ("reject" or "wait" or "cancelAndWait"))
            return Reject<JobResponse>(BackendErrorCode.InvalidRequest, "关闭策略或活动任务处置无效。");
        if (!TryGet(clientId, sessionId, out ProjectSession session)) return Reject<JobResponse>(BackendErrorCode.SessionNotFound, "工程会话不存在。");
        BackendJob[] active; string? target;
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CheckPersistenceMutation(session, request.ExpectedRevision, allowReadOnly: true) is { } error)
                return BackendOperationResult<JobResponse>.Rejected(error);
            active = ActiveJobs(sessionId);
            if (session.SaveOutstanding && active.Length == 0) return Reject<JobResponse>(BackendErrorCode.ActiveJobs, "保存请求正在登记任务，请读取后重试。");
            if (request.ActiveJobs == "reject" && active.Length > 0) return Reject<JobResponse>(BackendErrorCode.ActiveJobs, "工程仍有活动任务，请明确处置。");
            if (request.Policy == "requireClean" && session.Workspace.IsDirty) return Reject<JobResponse>(BackendErrorCode.DirtySession, "工程有未保存改动，请明确保存或放弃。");
            if (session.IsReadOnly && request.Policy == "save") return Reject<JobResponse>(BackendErrorCode.ReadOnlySession, "只读工程不能保存。");
            target = request.Policy == "save" ? ResolveSaveTarget(clientId, session, request.TargetHandleId) : null;
            session.ClosePending = true;
        }
        catch (Exception error) { return PersistenceFailure<JobResponse>(error); }
        finally { session.Writer.Release(); }
        BackendOperationResult<JobResponse> result = StartJob(clientId, "close", sessionId,
            job => CloseCoreAsync(session, request, target, active, job), target);
        if (!result.Accepted)
        {
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try { session.ClosePending = false; }
            finally { session.Writer.Release(); }
        }
        return result;
    }

    private async Task CloseCoreAsync(ProjectSession session, CloseProjectRequest request, string? target,
        BackendJob[] active, BackendJob job)
    {
        bool closed = false;
        try
        {
            if (request.ActiveJobs == "cancelAndWait")
                lock (_jobGate) foreach (BackendJob pending in active) pending.Cancel();
            await Task.WhenAll(active.Select(pending => pending.Completion)).ConfigureAwait(false);
            job.Cancellation.ThrowIfCancellationRequested();
            await session.Writer.WaitAsync().ConfigureAwait(false);
            ProjectSaveSnapshot? snapshot = null; ProjectWriteLeaseReservation? reservation = null; long revision;
            try
            {
                session.IsClosing = true;
                revision = session.ProjectRevision;
                if (request.Policy == "requireClean" && session.Workspace.IsDirty)
                { job.Finish(null, new(BackendErrorCode.DirtySession, "工程尚有未保存改动，关闭未完成。")); return; }
                InitializeSessionPersistence(session);
                if (target is not null)
                {
                    ProjectDirectoryHandles.EnsureNoLinks(target);
                    snapshot = session.Workspace.CaptureSave(_timeProvider.GetUtcNow());
                    reservation = session.Workspace.PrepareSaveLease(target);
                }
            }
            finally { session.Writer.Release(); }
            using (reservation)
            {
                if (!job.EnterCommitting()) throw new OperationCanceledException(job.Cancellation);
                if (snapshot is not null)
                {
                    ProjectSaveResult saved = await session.Saves!.SaveAsync(snapshot, target!, reservation).ConfigureAwait(false);
                    if (saved.Error is not null) { job.Finish(null, new(BackendErrorCode.ProjectLoadFailed, "关闭前保存失败，会话保留。")); return; }
                    await session.Writer.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!session.Workspace.AcceptSave(snapshot, target!, saved.Issues, reservation))
                            throw new IOException("关闭保存工作区身份失效。");
                        session.SavedRevision = revision;
                    }
                    finally { session.Writer.Release(); }
                }
                await session.Saves!.StopAsync(clearRecovery: true).ConfigureAwait(false);
                await session.DrainPinnedInputReadsAsync().ConfigureAwait(false);
                ProjectSessionSummaryResponse summary; string? savedRevisionText;
                await session.Writer.WaitAsync().ConfigureAwait(false);
                try
                {
                    session.IsClosed = true; closed = true;
                    session.PublishEvent("project.closed");
                    // Return the cursor of the close event itself, matching the
                    // summary delivered through the client event journal.
                    summary = session.BuildSummary();
                    session.Workspace.ReleaseWriteLease();
                    await session.DisposeResourcesAsync().ConfigureAwait(false);
                    savedRevisionText = session.SavedRevision is { } savedRevision ? Revision(savedRevision) : null;
                }
                finally { session.Writer.Release(); }
                await RemoveClosedSessionAsync(session).ConfigureAwait(false);
                BackendDiagnosticDto[]? cleanupWarnings = null;
                try { await ReleasePinnedInputAsync(session.PinnedInputDirectory).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { cleanupWarnings = [new("PinnedInputCleanupFailed", "Warning", "会话已关闭，固定输入清理失败，将在下一次独占profile启动时重试。", null)]; }
                job.Finish(summary, savedRevision: savedRevisionText, diagnostics: cleanupWarnings);
            }
        }
        finally
        {
            if (!closed)
            {
                await session.Writer.WaitAsync().ConfigureAwait(false);
                try { session.IsClosing = false; session.ClosePending = false; session.Saves?.Resume(); }
                finally { session.Writer.Release(); }
            }
        }
    }

    public RecoveryCandidateResponse[] ReadRecoveryCandidates(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return [.. RecoveryEntries().Select(entry => new RecoveryCandidateResponse(entry.Id, entry.Inspection.Session.ProjectName,
            entry.Inspection.Session.SavedAtUtc, entry.Inspection.Session.ProjectDirectoryPath is not null))];
    }

    public RecoveredJobResponse[] ReadRecoveredJobs(string clientId)
    { ArgumentException.ThrowIfNullOrWhiteSpace(clientId); return _ledger?.ReadRecovered() ?? []; }

    private ProjectDirectoryHandles Handles => _handles ?? throw new ResourceStoreException(BackendErrorCode.CommandNotSupported, "工程根未配置。");
    private string ResolveSaveTarget(string clientId, ProjectSession session, string? handleId) => handleId is not null
        ? Handles.Get(clientId, handleId, write: true).Path
        : session.Workspace.DirectoryPath ?? throw new ResourceStoreException(BackendErrorCode.InvalidRequest, "未保存工程需要目标目录句柄。");
    private BackendErrorResponse? CheckPersistenceMutation(ProjectSession session, string expectedRevision, bool allowReadOnly = false)
    {
        if (_persistenceStopping || session.ClosePending || session.IsClosing || session.IsClosed) return new(BackendErrorCode.SessionClosing, "工程会话正在关闭。");
        if (!allowReadOnly && session.IsReadOnly) return new(BackendErrorCode.ReadOnlySession, "工程会话只读。");
        if (!long.TryParse(expectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision < 0)
            return new(BackendErrorCode.InvalidRequest, "expectedRevision必须是非负十进制修订。");
        return revision != session.ProjectRevision ? new(BackendErrorCode.RevisionConflict, "工程修订已经改变，请先读取当前状态。") : null;
    }
    private void InitializeSessionPersistence(ProjectSession session)
    {
        if (session.Saves is not null || _persistence is null) return;
        string root = Path.Combine(_persistence.StateRoot, "sessions", session.ProjectSessionId);
        session.PersistenceStateRoot = root;
        session.Saves = _storageFactory is null ? new(root) : new(_storageFactory(root));
        if (!session.IsReadOnly) session.Workspace.EnableWriteLeaseOwnership();
    }
    private async Task RunAutoSaveAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new(_persistence!.AutoSaveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                foreach (ProjectSession session in _sessions.Values)
                {
                    await session.Writer.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (session.IsClosed || session.IsClosing || session.ClosePending || session.IsReadOnly) continue;
                        InitializeSessionPersistence(session);
                        ProjectWriteLeaseReservation? lease = session.Workspace.DirectoryPath is { } path ? session.Workspace.PrepareSaveLease(path) : null;
                        session.Saves!.RequestAutoSave(session.Workspace.CaptureSave(_timeProvider.GetUtcNow()), lease);
                    }
                    finally { session.Writer.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    internal async ValueTask DisposePersistenceAsync()
    {
        // Closing admission and cancelling every formal task share the gate with
        // executor start, so a released permit cannot revive a queued task.
        lock (_jobGate)
        {
            _persistenceStopping = true;
            foreach (BackendJob job in _jobs.Values.Where(value => value.CancelOnShutdown)) job.Cancel();
        }
        if (_persistence is null) return;
        if (_persistenceStop is not null) await _persistenceStop.CancelAsync().ConfigureAwait(false);
        await _autoSaveLoop.ConfigureAwait(false);
        await Task.WhenAll(_jobs.Values.Select(job => job.Completion)).ConfigureAwait(false);
        foreach (ProjectSession session in _sessions.Values)
        {
            await session.Writer.WaitAsync().ConfigureAwait(false);
            try
            {
                session.IsClosing = true;
                InitializeSessionPersistence(session);
                if (!session.IsReadOnly && session.Workspace.IsDirty)
                {
                    ProjectWriteLeaseReservation? lease = session.Workspace.DirectoryPath is { } path ? session.Workspace.PrepareSaveLease(path) : null;
                    session.Saves!.RequestAutoSave(session.Workspace.CaptureSave(_timeProvider.GetUtcNow()), lease);
                }
            }
            finally { session.Writer.Release(); }
            if (session.Saves is not null)
            {
                await session.Saves.DrainAsync().ConfigureAwait(false);
                await session.Saves.StopAsync(clearRecovery: false).ConfigureAwait(false);
            }
            session.Workspace.ReleaseWriteLease();
            await session.DrainPinnedInputReadsAsync().ConfigureAwait(false);
            await ReleasePinnedInputAsync(session.PinnedInputDirectory).ConfigureAwait(false);
        }
        foreach (BackendJob job in _jobs.Values) job.Dispose();
        _persistenceStop?.Dispose();
        _profileLease?.Dispose(); _profileLease = null;
    }

    private sealed record RecoveryEntry(string Id, string StateRoot, AutoSaveInspection Inspection);
    private IEnumerable<RecoveryEntry> RecoveryEntries()
    {
        if (_persistence is null) yield break;
        string sessions = Path.Combine(_persistence.StateRoot, "sessions");
        if (!Directory.Exists(sessions)) yield break;
        foreach (string root in Directory.EnumerateDirectories(sessions))
        {
            string name = Path.GetFileName(root);
            if (_sessions.ContainsKey(name) || new DirectoryInfo(root).LinkTarget is not null) continue;
            AutoSaveInspection? inspection = new ProjectAutoSave(root).InspectRecovery();
            if (inspection is not null) yield return new("recovery-" + name, root, inspection);
        }
    }
    private static string Revision(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static BackendDiagnosticDto[] ToDiagnostics(IEnumerable<Core.Diagnostics.ProjectIssue> issues) =>
        [.. issues.Select(issue => new BackendDiagnosticDto(issue.Code.ToString(), issue.Severity.ToString(),
            $"工程持久诊断：{issue.Code}", issue.Location is { } location && !Path.IsPathRooted(location) ? location : null))];
    private static BackendOperationResult<T> Reject<T>(BackendErrorCode code, string message) => BackendOperationResult<T>.Rejected(new(code, message));
    private static BackendOperationResult<T> PersistenceFailure<T>(Exception error) => error switch
    {
        ResourceStoreException resource => BackendOperationResult<T>.Rejected(new(resource.Code, resource.Message, resource.Field)),
        ProjectWriteLeaseConflictException => Reject<T>(BackendErrorCode.WriteLeaseConflict, "工程目录已经有写者。"),
        IOException or ArgumentException => Reject<T>(BackendErrorCode.PathNotAllowed, "工程目录无效或包含链接。"),
        _ => Reject<T>(BackendErrorCode.InternalError, "持久操作未能接受，请查询当前状态。"),
    };
}
