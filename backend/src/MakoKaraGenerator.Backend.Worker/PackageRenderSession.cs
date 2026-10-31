using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>每个 package Worker 进程最多持有一个固定包 Runtime 会话。</summary>
internal sealed class PackageRenderSession(string? renderBackend = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRenderFrameSession? _session;
    private ProjectSnapshot? _snapshot;
    private ProjectRuntimeInput? _runtimeInput;
    private Guid _id;
    private string? _sourceHash;
    private long _sequence;

    public async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (request.Kind is PackageRenderWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or ProjectRuntimeInputWorkerContract.OpenKind)
            {
                if (_session is not null) throw new WorkerProtocolException("Worker 已持有固定会话。", "WorkerSessionAlreadyOpen");
                var input = PackageRenderWorkerContract.DecodeOpen(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, 0);
                ProjectSnapshot snapshot;
                Func<IBackgroundFrameSourceResolver>? imageResolverFactory = null;
                ProjectRuntimeInput? runtimeInput = null;
                if (request.Kind == ProjectRuntimeInputWorkerContract.OpenKind)
                {
                    runtimeInput = await ProjectRuntimeInput.OpenAsync(input.SnapshotBytes, token).ConfigureAwait(false);
                    snapshot = runtimeInput.Snapshot;
                }
                else if (request.Kind == ProjectImagePreviewWorkerContract.OpenKind)
                {
                    var image = ProjectImagePreviewSnapshotCodec.Decode(input.SnapshotBytes);
                    snapshot = image.Snapshot;
                    imageResolverFactory = () => new MemoryImageBackgroundFrameSourceResolver(image.LogicalPath, image.ImageBytes, token);
                }
                else
                {
                    snapshot = ProjectSnapshotSourceProfile.Decode(input.SnapshotBytes);
                    if (!ProjectSnapshotSourceProfile.IsPackage(ProjectSnapshotSourceProfile.Select(snapshot)))
                        throw new SnapshotContractException("SnapshotVersionMismatch", "包会话必须携带包来源。");
                }
                var factory = runtimeInput?.CreateFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
                    Path.Combine(Path.GetTempPath(), $"makokara-worker-package-{Environment.ProcessId}"), ReplayReason.Initial, renderBackend: renderBackend) ?? new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
                    Path.Combine(Path.GetTempPath(), $"makokara-worker-package-{Environment.ProcessId}"),
                    new FfmpegVideoFrameProvider(), ReplayReason.Initial, imageResolverFactory, renderBackend: renderBackend);
                IRenderFrameSession session;
                try { session = await factory.CreateAsync(snapshot, token).ConfigureAwait(false); }
                catch (ProjectEffectCompilationException failure)
                {
                    var source = PackageRenderDiagnosticsMapper.EncodeSource(failure.Diagnostics.Select(d => new RenderSourceDiagnostic(
                        d.PackageId, d.Revision, d.InstanceId, d.ExportId, d.Source.Code, d.Source.Message, d.Source.IsError, d.Source.Path, d.Source.Line, d.Source.Column)));
                    runtimeInput?.Dispose();
                    throw new WorkerProtocolException("包源码编译失败。", "WorkerPackageCompilationFailed", packageDiagnostics: source);
                }
                catch { runtimeInput?.Dispose(); throw; }
                try { token.ThrowIfCancellationRequested(); }
                catch { try { await session.DisposeAsync().ConfigureAwait(false); } finally { runtimeInput?.Dispose(); } throw; }
                _id = input.SessionId; _sourceHash = input.SourceSha256; _sequence = 0; _snapshot = snapshot; _session = session; _runtimeInput = runtimeInput;
                return SessionResult(request);
            }
            if (request.Kind == PackageRenderWorkerContract.RenderKind)
            {
                StaticWorkerFrameRequest input = PackageRenderWorkerContract.DecodeFrame(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, input.Sequence);
                ValidateOwned(input.SessionId, input.SourceSha256, input.Sequence);
                if (request.FrameTransparentBackground != input.TransparentBackground || request.FrameQuality != input.Quality)
                    throw new WorkerProtocolException("包帧透明模式或质量与固定封包不匹配。", "WorkerPackageProtocolInvalid");
                _sequence = input.Sequence;
                using RenderFrame frame = await _session!.RenderAsync(RenderRequest.At(_snapshot!, input.TimeUs) with
                {
                    Target = new() { PixelWidth = input.Width, PixelHeight = input.Height },
                    TransparentBackground = input.TransparentBackground, Quality = (RenderQuality)input.Quality, CancellationToken = token,
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                WorkerPackageDiagnosticsDto packageDiagnostics = PackageRenderDiagnosticsMapper.EncodeFrame(frame);
                WorkerDiagnosticsSnapshotDto diagnostics = StaticRenderSession.EncodeDiagnostics(frame.DiagnosticSnapshot);
                if (diagnostics.HasError) throw new WorkerProtocolException("实际 Runtime Error 诊断禁止像素发布。", "WorkerRuntimeDiagnosticError", diagnostics, packageDiagnostics);
                if (frame.Format != PixelFormat.Bgra8888Premultiplied) throw new WorkerProtocolException("包 Runtime 像素格式不符合合同。", "WorkerPackageProtocolInvalid");
                byte[] pixels = frame.PixelMemory.ToArray();
                WorkerMessage result = new(PackageRenderWorkerContract.FrameResultKind, request.RequestId, request.InputSha256, pixels,
                    outputSha256: WorkerProtocol.ComputeSha256(pixels), frameWidth: frame.Width, frameHeight: frame.Height,
                    frameStride: frame.Stride, frameTimeUs: frame.TimeUs, frameFormat: RenderFrameV1WorkerResult.RequiredPixelFormat,
                    sessionId: _id.ToString("N"), sourceSha256: _sourceHash, frameSequence: input.Sequence,
                    frameTransparentBackground: input.TransparentBackground, diagnostics: diagnostics, frameQuality: input.Quality, packageDiagnostics: packageDiagnostics);
                PackageRenderWorkerContract.ValidateFrameResult(input, result);
                return result;
            }
            if (request.Kind == PackageRenderWorkerContract.CloseKind)
            {
                var input = PackageRenderWorkerContract.DecodeClose(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, input.Sequence);
                ValidateOwned(input.SessionId, input.SourceSha256, input.Sequence);
                IRenderFrameSession session = _session!; _session = null; _snapshot = null;
                try { await session.DisposeAsync().ConfigureAwait(false); } finally { _runtimeInput?.Dispose(); _runtimeInput = null; }
                return SessionResult(request);
            }
            throw new WorkerProtocolException("包 Worker 收到未知会话操作。", "WorkerPackageProtocolInvalid");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { IRenderFrameSession? session = _session; _session = null; _snapshot = null; try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); } finally { _runtimeInput?.Dispose(); _runtimeInput = null; } }
        finally { _gate.Release(); }
    }

    private void ValidateOwned(Guid id, string hash, long sequence)
    {
        if (_session is null || _id != id || _sourceHash != hash) throw new WorkerProtocolException("包请求不属于本进程固定会话。", "WorkerPackageSessionConflict");
        if (sequence <= _sequence) throw new WorkerProtocolException("包会话请求序号已过期。", "WorkerPackageRequestOutOfOrder");
    }
    private static void ValidateMetadata(WorkerMessage message, Guid id, string hash, long sequence)
    {
        if (message.SessionId != id.ToString("N") || message.SourceSha256 != hash || message.FrameSequence != sequence)
            throw new WorkerProtocolException("包会话消息字段与受哈希保护的封包不匹配。", "WorkerPackageProtocolInvalid");
    }
    private static WorkerMessage SessionResult(WorkerMessage request) => new(PackageRenderWorkerContract.SessionResultKind, request.RequestId,
        request.InputSha256, sessionId: request.SessionId, sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence);
}
