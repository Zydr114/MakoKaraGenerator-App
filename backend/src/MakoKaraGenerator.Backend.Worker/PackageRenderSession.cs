using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>每个 package Worker 进程最多持有一个固定包 Runtime 会话。</summary>
internal sealed class PackageRenderSession(string? renderBackend = null, bool enableGpuPreview = false, bool enableGpuPreviewBenchmark = false, GpuWorkerPreparationState? gpuPreparation = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRenderFrameSession? _session;
    private ProjectSnapshot? _snapshot;
    private ProjectRuntimeInput? _runtimeInput;
    private Guid _id;
    private string? _sourceHash;
    private long _sequence;
    private double _openMs;
    private GpuStreamSession? _gpuStream;
    private GpuStreamWorkerOpen? _gpuOpen;

    public async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (request.Kind is PackageRenderWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or ProjectRuntimeInputWorkerContract.OpenKind)
            {
                if (_session is not null) throw new WorkerProtocolException("Worker 已持有固定会话。", "WorkerSessionAlreadyOpen");
                var preparedGpu = gpuPreparation?.Bind();
                var input = PackageRenderWorkerContract.DecodeOpen(request.Payload);
                var prepared = WorkerScriptInputs.Read(input.SnapshotBytes, token);
                string sourceHash = WorkerProtocol.ComputeSha256(prepared.Source.Span);
                ValidateMetadata(request, input.SessionId, sourceHash, 0);
                ProjectSnapshot snapshot;
                Func<IBackgroundFrameSourceResolver>? imageResolverFactory = null;
                ProjectRuntimeInput? runtimeInput = null;
                if (request.Kind == ProjectRuntimeInputWorkerContract.OpenKind)
                {
                    runtimeInput = await ProjectRuntimeInput.OpenAsync(prepared.Source, token, prepared.Compiler).ConfigureAwait(false);
                    snapshot = runtimeInput.Snapshot;
                }
                else if (request.Kind == ProjectImagePreviewWorkerContract.OpenKind)
                {
                    var image = ProjectImagePreviewSnapshotCodec.Decode(prepared.Source);
                    snapshot = image.Snapshot;
                    imageResolverFactory = () => new MemoryImageBackgroundFrameSourceResolver(image.LogicalPath, image.ImageBytes, token);
                }
                else
                {
                    snapshot = ProjectSnapshotSourceProfile.Decode(prepared.Source);
                    if (!ProjectSnapshotSourceProfile.IsPackage(ProjectSnapshotSourceProfile.Select(snapshot)))
                        throw new SnapshotContractException("SnapshotVersionMismatch", "包会话必须携带包来源。");
                }
                // 正式预览不为逐像素检查读回；Host 只对作者草稿请求开启。
                bool pixelChecks = request.GraphPixelChecks == true;
                var factory = runtimeInput?.CreateFactory(ReplayReason.Initial, renderBackend: renderBackend,
                    enableGpuPreview: enableGpuPreview, enableGpuPreviewBenchmark: enableGpuPreviewBenchmark, gpuPreparation: preparedGpu,
                    graphPixelChecks: pixelChecks) ?? new ProjectRenderSessionFactory(prepared.Compiler,
                    new FfmpegVideoFrameProvider(), ReplayReason.Initial, imageResolverFactory, renderBackend: renderBackend,
                    enableGpuPreview: enableGpuPreview, enableGpuPreviewBenchmark: enableGpuPreviewBenchmark, gpuPreparation: preparedGpu,
                    graphPixelChecks: pixelChecks);
                IRenderFrameSession session;
                long opened = System.Diagnostics.Stopwatch.GetTimestamp();
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
                _id = input.SessionId; _sourceHash = sourceHash; _sequence = 0; _snapshot = snapshot; _session = session; _runtimeInput = runtimeInput;
                _openMs = System.Diagnostics.Stopwatch.GetElapsedTime(opened).TotalMilliseconds;
                return SessionResult(request);
            }
            if (request.Kind is GpuStreamWorkerContract.OpenKind or GpuStreamWorkerContract.CloseKind or GpuStreamWorkerContract.ReadKind)
            {
                if (request.SessionId != _id.ToString("N") || request.SourceSha256 != _sourceHash || request.FrameSequence is not { } sequence)
                    throw GpuStreamProtocol.Invalid();
                ValidateOwned(_id, _sourceHash!, sequence);
                _sequence = sequence;
                if (request.Kind == GpuStreamWorkerContract.OpenKind)
                {
                    if (!enableGpuPreview || _gpuStream is not null || _runtimeInput is null ||
                        _session is not IPreviewRenderSession { SupportsPreviewFrames: true } previewSession)
                        throw new WorkerProtocolException("固定 Runtime 会话没有启用连续预览流。", "WorkerGpuStreamUnavailable");
                    var open = GpuStreamWorkerContract.Decode<GpuStreamWorkerOpen>(request.Payload);
                    if (open.Width is < 1 or > 4096 || open.Height is < 1 or > 4096 ||
                        open.MaximumAllocationBytes is 0 or > 64UL * 1024 * 1024 || string.IsNullOrEmpty(open.BrokerPath))
                        throw GpuStreamProtocol.Invalid();
                    PreviewTransportKind? requestedTransport;
                    PreviewPixelFormat requestedPixelFormat;
                    try
                    {
                        requestedTransport = open.Transport is null ? null : PreviewWireConventions.ParseTransport(open.Transport);
                        requestedPixelFormat = open.PixelFormat is null ? PreviewPixelFormat.Bgra8888 : PreviewWireConventions.ParsePixelFormat(open.PixelFormat);
                        if (open.BackendApi is not null &&
                            PreviewWireConventions.NormalizeBackendApi(open.BackendApi) != previewSession.BackendApi)
                        {
                            throw new WorkerProtocolException(
                                $"请求的渲染设备 '{open.BackendApi}' 与固定会话设备 '{previewSession.BackendApi}' 不一致。",
                                "WorkerGpuStreamUnavailable");
                        }
                    }
                    catch (GpuPreviewException error)
                    {
                        throw new WorkerProtocolException(error.Message, error.Code);
                    }

                    PreferredGpuDevice? preferredDevice = open.PreferredGpuDevice is { } preferred
                        ? new PreferredGpuDevice(preferred.VendorId, preferred.DeviceId, preferred.DrmRenderMajor,
                            preferred.DrmRenderMinor, preferred.DeviceLuidHex, preferred.MetalRegistryId)
                        : null;

                    RenderTarget target = new() { PixelWidth = open.Width, PixelHeight = open.Height };
                    PreviewPoolInfo pool = await previewSession.InitializePreviewOutputAsync(
                        target,
                        open.MaximumAllocationBytes,
                        new PreviewOutputOptions(requestedTransport, requestedPixelFormat, open.StrideBytes, preferredDevice, open.ShmToken),
                        token).ConfigureAwait(false);
                    GpuStreamSession.ValidateOpen(open, pool);
                    ProjectSnapshot snapshot = _snapshot!;
                    IGpuStreamChannel channel = pool.TransportKind == PreviewTransportKind.Shm
                        ? new LocalStreamPreviewChannel(open.BrokerPath)
                        : new GpuNativeChannel(open.BrokerPath);
                    try
                    {
                        _gpuStream = new(open, pool, channel, (timeUs, attempt, lifetime) => enableGpuPreviewBenchmark
                            ? previewSession.RenderSyntheticPreviewAsync(target, timeUs, attempt, requestedPixelFormat, lifetime)
                            : previewSession.RenderPreviewAsync(RenderRequest.At(snapshot, timeUs) with
                            {
                                Target = target, TransparentBackground = open.TransparentBackground,
                                Quality = RenderQuality.Standard, CancellationToken = lifetime,
                            }, lifetime), enableGpuPreviewBenchmark);
                    }
                    catch { channel.Dispose(); throw; }
                    _gpuOpen = open;
                    return GpuResult(request, GpuStreamWorkerContract.OpenResultKind, _gpuStream.Opened with
                    { WarmupBinding = pool.BackendApi == PreviewWireConventions.ApiVulkan ? gpuPreparation?.BindingProof() : null });
                }
                if (_gpuStream is null || _gpuOpen is null) throw GpuStreamProtocol.Invalid();
                if (request.Kind == GpuStreamWorkerContract.ReadKind)
                {
                    var read = GpuStreamWorkerContract.Decode<GpuStreamWorkerRead>(request.Payload);
                    ValidateGpuIdentity(read.StreamId, read.ProducerEpoch, read.InputGeneration);
                    return GpuResult(request, GpuStreamWorkerContract.ReadResultKind, _gpuStream.ReadState());
                }
                var close = GpuStreamWorkerContract.Decode<GpuStreamWorkerClose>(request.Payload);
                ValidateGpuIdentity(close.StreamId, close.ProducerEpoch, close.InputGeneration);
                bool drained = await _gpuStream.StopAsync(token).ConfigureAwait(false);
                return GpuResult(request, GpuStreamWorkerContract.CloseResultKind,
                    new GpuStreamWorkerClosed(close.StreamId, close.ProducerEpoch, drained));
            }
            if (request.Kind == PackageRenderWorkerContract.RenderKind)
            {
                if (_gpuStream is not null) throw new WorkerProtocolException("GPU 流持有固定 Runtime 会话。", "WorkerGpuStreamAlreadyOpen");
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
                    Inspect = input.Inspect,
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
                    frameTransparentBackground: input.TransparentBackground, diagnostics: diagnostics, frameQuality: input.Quality, packageDiagnostics: packageDiagnostics,
                    frameInspection: StaticRenderSession.EncodeInspection(input, frame, _openMs));
                PackageRenderWorkerContract.ValidateFrameResult(input, result);
                return result;
            }
            if (request.Kind == PackageRenderWorkerContract.CloseKind)
            {
                var input = PackageRenderWorkerContract.DecodeClose(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, input.Sequence);
                ValidateOwned(input.SessionId, input.SourceSha256, input.Sequence);
                if (_gpuStream is not null) await _gpuStream.DisposeAsync().ConfigureAwait(false);
                _gpuStream = null; _gpuOpen = null;
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
        try { if (_gpuStream is not null) await _gpuStream.DisposeAsync().ConfigureAwait(false); _gpuStream = null; _gpuOpen = null; IRenderFrameSession? session = _session; _session = null; _snapshot = null; try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); } finally { _runtimeInput?.Dispose(); _runtimeInput = null; } }
        finally { _gate.Release(); }
    }

    private void ValidateGpuIdentity(string stream, string producer, string input)
    {
        if (stream != _gpuOpen!.StreamId || input != _gpuOpen.InputGeneration || producer != _gpuStream!.Opened.ProducerEpoch)
            throw GpuStreamProtocol.Invalid();
    }
    private static WorkerMessage GpuResult<T>(WorkerMessage request, string kind, T value)
    {
        byte[] payload = GpuStreamWorkerContract.Encode(value);
        return new(kind, request.RequestId, request.InputSha256, payload, outputSha256: WorkerProtocol.ComputeSha256(payload),
            sessionId: request.SessionId, sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence);
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
