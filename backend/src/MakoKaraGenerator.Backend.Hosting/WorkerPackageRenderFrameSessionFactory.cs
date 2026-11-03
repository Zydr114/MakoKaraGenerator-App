using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>用独立受管理 Worker 持有固定 package snapshot 的 Runtime 会话。</summary>
/// <remarks>该工厂支持预览专用 package 路由，不改变旧静态工厂或正式任务 allowlist。</remarks>
public sealed class WorkerPackageRenderFrameSessionFactory : IRenderFrameSessionFactory, IBackendPreviewProfileCapability, IBackendImagePreviewFactory, IBackendRuntimePreviewFactory,
    IBackendPreviewRenderBackendFactory
{
    private readonly WorkerProcessOptions _options;
    private readonly TimeSpan _timeout;

    public string RenderBackend => _options.RenderBackend ?? "cpu";

    private WorkerPackageRenderFrameSessionFactory ForRenderBackend(string renderBackend) =>
        new(_options with { RenderBackend = renderBackend }, _timeout);

    public IRenderFrameSessionFactory WithRenderBackend(string renderBackend) => ForRenderBackend(renderBackend);

    /// <summary>创建 package Worker 工厂；每次私有操作有独立 deadline。</summary>
    public WorkerPackageRenderFrameSessionFactory(WorkerProcessOptions options, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _options = options;
        _timeout = operationTimeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    /// <summary>实际探测完整包能力及运行隔离；不向 Worker 发送包 open 字节。</summary>
    public async ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
    {
        if (!ProjectSnapshotSourceProfile.IsPackage(profile) && profile != BackendSnapshotProfiles.ProjectImagePreview && profile != ProjectRuntimeInputWorkerContract.Profile)
            return new(BackendErrorCode.CommandNotSupported, "当前包预览来源能力不受支持。", "source");
        try
        {
            await using WorkerProcessProxy proxy = new(_options);
            if (profile == ProjectRuntimeInputWorkerContract.Profile)
                await proxy.GetRuntimeInputCapabilitiesAsync(_timeout, cancellationToken).ConfigureAwait(false);
            else if (profile == BackendSnapshotProfiles.ProjectImagePreview)
                await proxy.GetImagePreviewCapabilitiesAsync(_timeout, cancellationToken).ConfigureAwait(false);
            else
            {
                var capabilities = await proxy.GetPackageCapabilitiesAsync(_timeout, cancellationToken).ConfigureAwait(false);
                if (profile == PackagePresetProjectSnapshotCodec.Profile && capabilities.SourceProfiles?.Contains(profile, StringComparer.Ordinal) != true)
                    throw new WorkerProtocolException("包 Worker 未声明混合预设来源。", "WorkerPackageProfileMismatch");
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerProtocolException)
        {
            return new(BackendErrorCode.PreviewUnavailable, "当前预览Worker未通过完整包来源能力握手。", "worker",
                FailureCode: profile == BackendSnapshotProfiles.ProjectImagePreview ? "WorkerImagePreviewProfileMismatch" : "WorkerPackageProfileMismatch");
        }
        catch (Exception)
        {
            return new(BackendErrorCode.PreviewUnavailable, "当前包预览Worker不可用。", "worker",
                FailureCode: profile == BackendSnapshotProfiles.ProjectImagePreview ? "WorkerImagePreviewUnavailable" : "WorkerPackageUnavailable");
        }
    }

    /// <summary>编码固定 package 源并建立一次持久 Worker Runtime 会话。</summary>
    public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = ProjectSnapshotSourceProfile.Encode(snapshot);
        return new(CreateCoreAsync(snapshot, bytes, PackageRenderWorkerContract.OpenKind, cancellationToken));
    }

    /// <summary>绑定已授权图片 bytes；原包入口仍不接受 Image 背景。</summary>
    public IRenderFrameSessionFactory Bind(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] fixedBytes = imageBytes.ToArray();
        _ = ProjectImagePreviewSnapshotCodec.Encode(snapshot, fixedBytes);
        return new ImageFactory(this, fixedBytes);
    }

    private sealed class ImageFactory(WorkerPackageRenderFrameSessionFactory owner, byte[] fixedBytes)
        : IRenderFrameSessionFactory, IBackendPreviewProfileCapability, IBackendPreviewRenderBackendFactory
    {
        public string RenderBackend => owner.RenderBackend;
        public IRenderFrameSessionFactory WithRenderBackend(string renderBackend) => new ImageFactory(owner.ForRenderBackend(renderBackend), fixedBytes);
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
            => owner.EnsureProfileAsync(profile, cancellationToken);

        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = ProjectImagePreviewSnapshotCodec.Encode(snapshot, fixedBytes);
            return new(owner.CreateCoreAsync(snapshot, bytes, ProjectImagePreviewWorkerContract.OpenKind, cancellationToken));
        }
    }

    public IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input) => new RuntimeFactory(this, input);

    private sealed class RuntimeFactory(WorkerPackageRenderFrameSessionFactory owner, BackendPreparedProjectRuntimeInput input)
        : IRenderFrameSessionFactory, IBackendPreviewProfileCapability, IBackendPreviewRenderBackendFactory
    {
        public string RenderBackend => owner.RenderBackend;
        public IRenderFrameSessionFactory WithRenderBackend(string renderBackend) => new RuntimeFactory(owner.ForRenderBackend(renderBackend), input);
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
            => owner.EnsureProfileAsync(profile, cancellationToken);
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (!ReferenceEquals(snapshot, input.Snapshot)) throw new PreparedFrameException("WorkerRuntimeInputsMismatch", "固定工程输入对象不匹配。");
            IDisposable reference = input.AcquireReference();
            return new(owner.CreateCoreAsync(snapshot, input.SourceBytes.ToArray(), ProjectRuntimeInputWorkerContract.OpenKind, cancellationToken, reference));
        }
    }

    private async Task<IRenderFrameSession> CreateCoreAsync(ProjectSnapshot snapshot, byte[] bytes, string openKind, CancellationToken token, IDisposable? retention = null)
    {
        PackageRenderDiagnosticsMapper mapper = new(snapshot);
        WorkerProcessProxy proxy = new(_options);
        try
        {
            StaticWorkerSessionHandle handle;
            try { handle = openKind == ProjectRuntimeInputWorkerContract.OpenKind
                ? await proxy.OpenRuntimeInputSessionAsync(bytes, _timeout, token).ConfigureAwait(false)
                : openKind == ProjectImagePreviewWorkerContract.OpenKind
                ? await proxy.OpenImagePreviewSessionAsync(bytes, _timeout, token).ConfigureAwait(false)
                : await proxy.OpenPackageSessionAsync(bytes, _timeout, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new RenderSessionFailureException("WorkerPackageTimedOut", "包 Worker 打开会话超过 deadline。", innerException: new TimeoutException()); }
            token.ThrowIfCancellationRequested();
            return new Session(snapshot, proxy, handle, _timeout, mapper, retention);
        }
        catch (Exception error)
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); retention?.Dispose(); }
            catch (Exception cleanup)
            {
                error.Data["WorkerCleanupFailure"] = cleanup;
                BackendPreviewFailedCreation.Attach(error, BackendPreviewFailedCreation.Worker(proxy, retention));
            }
            if (error is RenderSessionFailureException) throw;
            if (error is WorkerProtocolException failure)
                throw new RenderSessionFailureException(failure.Code, failure.Message,
                    failure.Diagnostics is null ? null : WorkerRenderFrameSessionFactory.DecodeDiagnostics(failure.Diagnostics), failure,
                    failure.PackageDiagnostics is null ? [] : DecodeSourceFailure(mapper, failure.PackageDiagnostics));
            if (error is PreparedFrameException prepared)
                throw new RenderSessionFailureException(prepared.Code, prepared.Message, innerException: prepared);
            if (error is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerPackageUnavailable", "包 Worker 不可用。", innerException: error);
            throw;
        }
    }

    private static System.Collections.Immutable.ImmutableArray<RenderSourceDiagnostic> DecodeSourceFailure(PackageRenderDiagnosticsMapper mapper, WorkerPackageDiagnosticsDto source)
    {
        try { return mapper.DecodeSource(source); }
        catch (WorkerProtocolException invalid) { throw new RenderSessionFailureException(invalid.Code, invalid.Message, innerException: invalid); }
    }

    private sealed class Session(ProjectSnapshot snapshot, WorkerProcessProxy proxy, StaticWorkerSessionHandle handle, TimeSpan timeout, PackageRenderDiagnosticsMapper mapper, IDisposable? retention) : IRenderFrameSession, IBackendPreviewCancellationRecoverable
    {
        public bool CanReuseAfterCancellation => !_failed && !_disposed;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _closing = new();
        private Task? _disposeTask;
        private bool _disposed;
        private bool _failed;
        private bool _resetPending;
        private long _sequence;

        public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(request.Project, snapshot)) throw new PreparedFrameException("WorkerPackageSessionInputsMismatch", "渲染请求不属于固定 package 快照。");
            if (request.LayoutLineId is not null || request.PreparedFrame is not null || !Enum.IsDefined(request.Quality))
                throw new PreparedFrameException("WorkerPackageRequestUnsupported", "包 Worker 不接受布局试样、已准备帧或未知质量。");
            RenderTarget target = request.ResolveTarget();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.CancellationToken, _closing.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failed) throw new PreparedFrameException("WorkerPackageSessionFailed", "包 Worker 会话已失败，请重新建立固定输入会话。");
                PackageRenderWorkerContract.ValidateFrameRequest(new(handle.SessionId, handle.SourceSha256, target.PixelWidth, target.PixelHeight,
                    request.TimeUs, request.TransparentBackground, checked(_sequence + 1), (int)request.Quality));
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    if (_resetPending)
                    {
                        var prepared = handle.PreparedInput ?? throw new InvalidOperationException("包 Worker 会话缺少固定脚本输入。");
                        await proxy.ClosePackageSessionForReopenAsync(handle, checked(++_sequence), timeout, CancellationToken.None).ConfigureAwait(false);
                        handle = await proxy.OpenPreparedSessionAsync(prepared, timeout, CancellationToken.None).ConfigureAwait(false);
                        _sequence = 0;
                        _resetPending = false;
                    }
                    if (linked.IsCancellationRequested) throw new WorkerInvocationNotStartedException(linked.Token);
                    var input = new StaticWorkerFrameRequest(handle.SessionId, handle.SourceSha256, target.PixelWidth, target.PixelHeight,
                        request.TimeUs, request.TransparentBackground, checked(++_sequence), (int)request.Quality);
                    WorkerMessage result;
                    try { result = await proxy.RenderPackageSessionAsync(input, timeout, linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !request.CancellationToken.IsCancellationRequested && !_closing.IsCancellationRequested)
                    { throw new RenderSessionFailureException("WorkerPackageTimedOut", "包 Worker 渲染超过 deadline。", innerException: new TimeoutException()); }
                    linked.Token.ThrowIfCancellationRequested();
                    RuntimeDiagnosticSnapshot diagnostics = WorkerRenderFrameSessionFactory.DecodeDiagnostics(result.Diagnostics!);
                    if (diagnostics.HasError) throw new WorkerProtocolException("Error 诊断禁止成功帧。", "WorkerRuntimeDiagnosticError", result.Diagnostics, result.PackageDiagnostics);
                    var package = mapper.DecodeFrame(result.PackageDiagnostics!);
                    return new(result.Payload, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs)
                    { DiagnosticSnapshot = diagnostics, ImageEffectDiagnostics = package.Image, MaterialDiagnostics = package.Material };
                }
                catch (WorkerCooperativeCancellationException) when (linked.IsCancellationRequested && !_closing.IsCancellationRequested)
                {
                    _resetPending = true;
                    throw;
                }
                catch (WorkerInvocationNotStartedException) when (linked.IsCancellationRequested && !_closing.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    _failed = true;
                    try { await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanup) { error.Data["WorkerCleanupFailure"] = cleanup; }
                    if (error is RenderSessionFailureException failure) throw failure;
                    if (error is WorkerProtocolException failure2)
                    {
                        (System.Collections.Immutable.ImmutableArray<RenderImageEffectDiagnostic> Image,
                            System.Collections.Immutable.ImmutableArray<RenderMaterialDiagnostic> Material) package;
                        try
                        {
                            package = failure2.PackageDiagnostics is null ? ([], []) : mapper.DecodeFrame(failure2.PackageDiagnostics);
                        }
                        catch (WorkerProtocolException invalid)
                        { throw new RenderSessionFailureException(invalid.Code, invalid.Message, innerException: invalid); }
                        throw new RenderSessionFailureException(failure2.Code, failure2.Message,
                            failure2.Diagnostics is null ? null : WorkerRenderFrameSessionFactory.DecodeDiagnostics(failure2.Diagnostics), failure2)
                        { ImageEffectDiagnostics = package.Image, MaterialDiagnostics = package.Material };
                    }
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposed = true; _closing.Cancel();
                if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled) _disposeTask = DisposeCoreAsync();
                return new(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                Exception? primary = null;
                try
                {
                    if (!_failed)
                    {
                        _failed = true;
                        await proxy.ClosePackageSessionAsync(handle, checked(++_sequence), timeout).ConfigureAwait(false);
                    }
                }
                catch (Exception error) { primary = error; }
                try { await proxy.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    if (primary is null) primary = cleanup;
                    else primary.Data["WorkerCleanupFailure"] = cleanup;
                }
                if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
                retention?.Dispose();
            }
            finally { _gate.Release(); }
        }
    }
}
