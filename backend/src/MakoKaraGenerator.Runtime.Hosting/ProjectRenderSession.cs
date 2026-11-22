using System.Diagnostics;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Runtime.Hosting;

internal sealed class ProjectRenderSession(ProjectSnapshot snapshot, SkiaRenderPipeline renderer, IEvaluatedFrameSource? source,
    SceneImageEffectResources? resources, IReadOnlyList<CompiledPackageGraphExports> natives, IReadOnlyList<SkiaGraphSurface> inputs,
    ImageSurfaceBudgetSession? budget, IDisposable? fixedImages, ISkiaRenderDevice? device = null, bool enableGpuPreviewBenchmark = false,
    SceneSvgEffectResources? svgResources = null, IReadOnlyList<PackageSvgResources>? svgOwners = null,
    bool? enablePreviewStream = null) : IPreviewRenderSession, ISceneMeasurementSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _telemetrySession = Guid.NewGuid().ToString("N");
    private readonly PreviewPortSessionManager? _previewPorts =
        (enablePreviewStream ?? (device is VulkanSkiaRenderDevice { SupportsGpuFrames: true })) && device is not null
            ? new PreviewPortSessionManager(device)
            : null;
    private long _telemetrySequence;
    private bool _disposed;

    public bool SupportsPreviewFrames => !_disposed && _previewPorts is { IsHealthy: true };
    public string BackendApi => device?.Info.Backend ?? PreviewWireConventions.ApiCpu;

    public async ValueTask<PreviewPoolInfo> InitializePreviewOutputAsync(
        RenderTarget target,
        ulong maximumAllocationBytes,
        PreviewOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_previewPorts is null || !_previewPorts.IsHealthy)
                throw new GpuPreviewException("GpuPreviewUnavailable", "This session did not enable continuous preview output.");
            return _previewPorts.InitializeOutput(target, maximumAllocationBytes, options);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
        => new(Task.Run(() => RenderCoreAsync(request, cancellationToken), cancellationToken));

    private async Task<RenderFrame> RenderCoreAsync(RenderRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, request.CancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(request.Project, snapshot)) throw new PreparedFrameException("RenderSessionInputsMismatch", "像素会话不属于当前快照。");
            var target = request.ResolveTarget();
            using var frameScope = PerformanceTelemetry.BeginFrame(_telemetrySession,
                device?.Info.Backend == "vulkan" ? "gpu" : "cpu", Interlocked.Increment(ref _telemetrySequence), target.PixelWidth, target.PixelHeight,
                capture: request.Inspect);
            try
            {
                PreparedRenderFrame? prepared;
                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))
                    prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,
                        request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, target, linked.Token).ConfigureAwait(false);
                RenderFrame frame = renderer.Render(request with { PreparedFrame = prepared, CancellationToken = linked.Token });
                frameScope.SetOutcome("ok");
                if (request.Inspect) Inspect(frame, prepared, frameScope.Capture());
                return frame;
            }
            catch (OperationCanceledException) { frameScope.SetOutcome("cancelled"); throw; }
        }
        finally { _gate.Release(); }
    }

    // 检查投影只服务调试显示：生成失败时不附加，像素帧照常返回。
    private void Inspect(RenderFrame frame, PreparedRenderFrame? prepared, PerformanceEvent? cost)
    {
        try
        {
            var effects = (source as IEvaluatedFrameInspectionSource)?.DescribeEffects() ?? [];
            frame.AttachInspection(RenderFrameInspection.Build(snapshot, prepared, effects, cost, sessionOpenMs: null));
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    public ValueTask<PreviewRenderFrame> RenderPreviewAsync(RenderRequest request, CancellationToken cancellationToken = default)
        => new(Task.Run(() => RenderPreviewCoreAsync(request, cancellationToken), cancellationToken));

    private async Task<PreviewRenderFrame> RenderPreviewCoreAsync(RenderRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, request.CancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_previewPorts is null || !_previewPorts.IsHealthy)
                throw new GpuPreviewException("GpuPreviewUnavailable", "This render session did not enable continuous preview output.");
            if (!ReferenceEquals(request.Project, snapshot))
                throw new PreparedFrameException("RenderSessionInputsMismatch", "预览会话不属于当前快照。");
            var target = request.ResolveTarget();
            IPreviewPort port = _previewPorts.RequireCurrentPort(target);
            using var frameScope = PerformanceTelemetry.BeginFrame(_telemetrySession,
                device?.Info.Backend == "vulkan" ? "gpu" : "cpu", Interlocked.Increment(ref _telemetrySequence), target.PixelWidth, target.PixelHeight);
            try
            {
                long prepareStart = Stopwatch.GetTimestamp();
                PreparedRenderFrame? prepared;
                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))
                    prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,
                        request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, target, linked.Token).ConfigureAwait(false);
                PreviewRenderFrame frame = renderer.RenderToPreview(
                    request with { PreparedFrame = prepared, CancellationToken = linked.Token },
                    port,
                    prepareStart);
                frameScope.SetOutcome("ok");
                return frame;
            }
            catch (OperationCanceledException) { frameScope.SetOutcome("cancelled"); throw; }
        }
        finally { _gate.Release(); }
    }

    public ValueTask<PreviewRenderFrame> RenderSyntheticPreviewAsync(
        RenderTarget target,
        long timeUs,
        ulong sequence,
        PreviewPixelFormat? pixelFormat = null,
        CancellationToken cancellationToken = default)
        => new(Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(target);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!enableGpuPreviewBenchmark)
                    throw new GpuPreviewException("GpuPreviewSyntheticDisabled", "Synthetic preview output requires the explicit benchmark launch flag.");
                if (_previewPorts is null || !_previewPorts.IsHealthy)
                    throw new GpuPreviewException("GpuPreviewUnavailable", "This render session did not enable continuous preview output.");
                cancellationToken.ThrowIfCancellationRequested();
                IPreviewPort port = _previewPorts.RequireCurrentPort(target);
                _ = sequence;
                return renderer.RenderSyntheticToPreview(target, timeUs, port, pixelFormat);
            }
            finally { _gate.Release(); }
        }, cancellationToken));

    public async ValueTask<MeasuredRenderFrame> MeasureAsync(RenderRequest request, bool layoutReference,
        string? editingLineId = null, string? editingCueId = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request.CancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(request.Project, snapshot))
                throw new PreparedFrameException("SceneMeasurementUnsupported", "测量需要同一固定源会话。");
            var target = request.ResolveTarget();
            using var frameScope = PerformanceTelemetry.BeginFrame(_telemetrySession,
                device?.Info.Backend == "vulkan" ? "gpu" : "cpu", Interlocked.Increment(ref _telemetrySequence), target.PixelWidth, target.PixelHeight);
            try
            {
                PreparedRenderFrame? prepared;
                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))
                    prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,
                        request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, target, linked.Token).ConfigureAwait(false);
                MeasuredRenderFrame measured = await Task.Run(() => renderer.MeasureAndRender(request with { PreparedFrame = prepared, CancellationToken = linked.Token },
                    layoutReference, editingLineId, editingCueId), linked.Token).ConfigureAwait(false);
                frameScope.SetOutcome("ok");
                return measured;
            }
            catch (OperationCanceledException) { frameScope.SetOutcome("cancelled"); throw; }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true;
            await ReleaseAsync(source, renderer, resources, fixedImages, inputs, natives, budget, device, svgResources, svgOwners, _previewPorts).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal static async ValueTask ReleaseAsync(IEvaluatedFrameSource? source, SkiaRenderPipeline? renderer,
        SceneImageEffectResources? resources, IDisposable? fixedImages, IReadOnlyList<SkiaGraphSurface> inputs,
        IReadOnlyList<CompiledPackageGraphExports> natives, ImageSurfaceBudgetSession? budget, ISkiaRenderDevice? device = null,
        SceneSvgEffectResources? svgResources = null, IReadOnlyList<PackageSvgResources>? svgOwners = null,
        IDisposable? previewPorts = null)
    {
        List<Exception> failures = [];
        try { if (source is not null) await source.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        // External lease delivery/completion can run outside the session gate.
        // Every synchronous shared-device owner is retired under its same gate.
        lock (device?.SynchronizationRoot ?? new object())
        {
            Release(previewPorts);
            Release(renderer); Release(svgResources);
            if (svgOwners is not null) foreach (var svg in svgOwners.Reverse()) Release(svg);
            Release(resources); Release(fixedImages);
            foreach (var input in inputs) Release(input);
            foreach (var native in natives) Release(native);
            Release(budget);
            Release(device);
        }
        if (failures.Count > 0)
        {
            failures[0].Data["RuntimeCleanupFailures"] = failures.ToArray();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        void Release(IDisposable? value)
        {
            try { value?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
    }
}
