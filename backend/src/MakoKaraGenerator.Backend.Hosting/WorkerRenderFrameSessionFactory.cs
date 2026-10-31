using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>可选的预览来源能力门禁；公共会话创建前由可信组合根调用。</summary>
public interface IBackendPreviewProfileCapability
{
    ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default);
}

/// <summary>用独立受管理 Worker 持有 snapshot-v1 静态歌词／场景的持久 Runtime 会话。</summary>
/// <remarks>Hosting 不依赖 Runtime.Hosting 或 Skia；包、分析和背景媒体由 codec 明确拒绝，音频声明不授权 IO。</remarks>
public sealed class WorkerRenderFrameSessionFactory : IRenderFrameSessionFactory, IBackendPreviewProfileCapability,
    IBackendPreviewRenderBackendFactory
{
    private readonly WorkerProcessOptions _options;
    private readonly TimeSpan _timeout;

    public string RenderBackend => _options.RenderBackend ?? "cpu";

    public IRenderFrameSessionFactory WithRenderBackend(string renderBackend) =>
        new WorkerRenderFrameSessionFactory(_options with { RenderBackend = renderBackend }, _timeout);

    /// <summary>创建受管理 Worker 工厂；每次 open／render／close 有独立且不超过一分钟的 deadline。</summary>
    public WorkerRenderFrameSessionFactory(WorkerProcessOptions options, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _options = options;
        _timeout = operationTimeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    /// <summary>在预览会话登记前确认当前 Worker 能消费所选静态来源。</summary>
    public async ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
    {
        if (!BackendSnapshotProfiles.IsSupported(profile))
            return new(BackendErrorCode.CommandNotSupported, "当前静态预览来源能力不受支持。", "source");
        try
        {
            await using WorkerProcessProxy proxy = new(_options);
            StaticWorkerCapabilitiesDto capabilities = await proxy.GetStaticCapabilitiesAsync(_timeout, cancellationToken).ConfigureAwait(false);
            if (!capabilities.SourceProfiles.Contains(profile, StringComparer.Ordinal))
                return new(BackendErrorCode.PreviewUnavailable, "当前预览Worker未提供所选静态来源能力。", "worker",
                    FailureCode: "WorkerStaticProfileMismatch");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkerProtocolException)
        {
            return new(BackendErrorCode.PreviewUnavailable, "当前预览Worker未通过静态来源能力握手。", "worker",
                FailureCode: "WorkerStaticProfileMismatch");
        }
        catch (Exception)
        {
            return new(BackendErrorCode.PreviewUnavailable, "当前预览Worker不可用。", "worker",
                FailureCode: "WorkerUnavailable");
        }
    }

    /// <summary>编码独立静态快照并只创建一次 Worker Runtime 会话。</summary>
    public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.SelectProfile(snapshot));
        return new(CreateCoreAsync(snapshot, bytes, cancellationToken));
    }

    private async Task<IRenderFrameSession> CreateCoreAsync(ProjectSnapshot snapshot, byte[] bytes, CancellationToken token)
    {
        WorkerProcessProxy proxy = new(_options);
        try
        {
            StaticWorkerSessionHandle handle = await proxy.OpenStaticSessionAsync(bytes, _timeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new Session(snapshot, proxy, handle, _timeout);
        }
        catch (Exception error)
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
            if (error is WorkerProtocolException failure)
                throw new RenderSessionFailureException(failure.Code, failure.Message,
                    failure.Diagnostics is null ? null : DecodeDiagnostics(failure.Diagnostics), failure);
            if (error is PreparedFrameException prepared)
                throw new RenderSessionFailureException(prepared.Code, prepared.Message, innerException: prepared);
            if (error is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerUnavailable", "静态工程 Worker 不可用。", innerException: error);
            throw;
        }
    }

    private sealed class Session(ProjectSnapshot snapshot, WorkerProcessProxy proxy, StaticWorkerSessionHandle handle,
        TimeSpan timeout) : IRenderFrameSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _closing = new();
        private Task? _disposeTask;
        private bool _disposed;
        private bool _failed;
        private long _sequence;

        public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(request.Project, snapshot))
                throw new PreparedFrameException("WorkerSessionInputsMismatch", "渲染请求不属于固定静态快照。");
            if (request.LayoutLineId is not null || request.PreparedFrame is not null || !Enum.IsDefined(request.Quality))
                throw new PreparedFrameException("WorkerRequestUnsupported", "本静态 timed profile 不接受布局试样、已准备帧或未知质量。");
            RenderTarget target = request.ResolveTarget();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.CancellationToken, _closing.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failed) throw new PreparedFrameException("WorkerSessionFailed", "Worker 会话已失败，请重新建立固定输入会话。");
                var input = new StaticWorkerFrameRequest(handle.SessionId, handle.SourceSha256, target.PixelWidth,
                    target.PixelHeight, request.TimeUs, request.TransparentBackground, checked(++_sequence), (int)request.Quality);
                // Rejected user geometry never invalidates or starts native rendering.
                StaticRenderWorkerContract.ValidateFrameRequest(input);
                try
                {
                    WorkerMessage result = await proxy.RenderStaticSessionAsync(input, timeout, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    RuntimeDiagnosticSnapshot diagnostics = DecodeDiagnostics(result.Diagnostics!);
                    if (diagnostics.HasError)
                        throw new WorkerProtocolException("Error 诊断禁止成功帧。", "WorkerRuntimeDiagnosticError", result.Diagnostics);
                    return new(result.Payload, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs)
                    { DiagnosticSnapshot = diagnostics };
                }
                catch (Exception error)
                {
                    _failed = true;
                    await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    if (error is WorkerProtocolException failure)
                        throw new RenderSessionFailureException(failure.Code, failure.Message,
                            failure.Diagnostics is null ? null : DecodeDiagnostics(failure.Diagnostics), failure);
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposed = true;
                _closing.Cancel();
                return new(_disposeTask ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                try
                {
                    if (!_failed)
                        await proxy.CloseStaticSessionAsync(handle, checked(++_sequence), timeout).ConfigureAwait(false);
                }
                finally { await proxy.DisposeAsync().ConfigureAwait(false); }
            }
            finally { _gate.Release(); }
            // Managed gates/tokens remain valid for already queued callers to observe closure.
        }
    }

    internal static RuntimeDiagnosticSnapshot DecodeDiagnostics(WorkerDiagnosticsSnapshotDto snapshot)
    {
        WorkerDiagnosticsSnapshotDto.Validate(snapshot);
        try
        {
            return RuntimeDiagnosticSnapshot.FromOccurrences(snapshot.Occurrences.Select(occurrence =>
            {
                WorkerRuntimeDiagnosticDto value = occurrence.Diagnostic;
                var diagnostic = new RuntimeDiagnostic(new(value.InstanceId), value.ArtifactHash, value.Stage, value.TimeUs,
                    new(Enum.Parse<DiagnosticSeverity>(value.Severity), value.Code, value.Message,
                        value.Location is null ? null : new(value.Location.Path, value.Location.Line, value.Location.Column)))
                { PackageIdentity = value.Package is null ? null : new(value.Package.PackageId, value.Package.Revision, value.Package.ExportId) };
                return new RuntimeDiagnosticOccurrence(occurrence.Epoch, occurrence.Sequence, diagnostic);
            }));
        }
        catch (ArgumentException error)
        { throw new WorkerProtocolException("Worker 诊断来源无法按实际 Runtime 语义还原：" + error.Message); }
    }
}
