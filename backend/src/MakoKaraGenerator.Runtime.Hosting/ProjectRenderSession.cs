using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Runtime.Hosting;

internal sealed class ProjectRenderSession(ProjectSnapshot snapshot, SkiaRenderPipeline renderer, IEvaluatedFrameSource? source,
    SceneImageEffectResources? resources, IReadOnlyList<CompiledPackageGraphExports> natives, IReadOnlyList<SkiaGraphSurface> inputs,
    ImageSurfaceBudgetSession? budget, IDisposable? fixedImages, ISkiaRenderDevice? device = null, bool enableGpuPreviewBenchmark = false) : IGpuRenderFrameSession, ISceneMeasurementSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _telemetrySession = Guid.NewGuid().ToString("N");
    private long _telemetrySequence;
    private bool _disposed;
    public bool SupportsGpuFrames => !_disposed && device is VulkanSkiaRenderDevice { SupportsGpuFrames: true };
    public async ValueTask<GpuFramePoolInfo> InitializeGpuOutputAsync(RenderTarget target, ulong maximumAllocationBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (device is not VulkanSkiaRenderDevice gpu || !SupportsGpuFrames)
                throw new GpuPreviewException("GpuPreviewUnavailable", "This session did not enable GPU shared output.");
            return gpu.InitializeGpuOutput(target, maximumAllocationBytes);
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
                device?.Info.Backend == "vulkan" ? "gpu" : "cpu", Interlocked.Increment(ref _telemetrySequence), target.PixelWidth, target.PixelHeight);
            try
            {
                PreparedRenderFrame? prepared;
                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))
                    prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,
                        request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, target, linked.Token).ConfigureAwait(false);
                RenderFrame frame = renderer.Render(request with { PreparedFrame = prepared, CancellationToken = linked.Token });
                frameScope.SetOutcome("ok");
                return frame;
            }
            catch (OperationCanceledException) { frameScope.SetOutcome("cancelled"); throw; }
        }
        finally { _gate.Release(); }
    }

    public ValueTask<GpuRenderFrame> RenderGpuAsync(RenderRequest request, CancellationToken cancellationToken = default)
        => new(Task.Run(() => RenderGpuCoreAsync(request, cancellationToken), cancellationToken));

    private async Task<GpuRenderFrame> RenderGpuCoreAsync(RenderRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, request.CancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!SupportsGpuFrames) throw new GpuPreviewException("GpuPreviewUnavailable", "This render session did not enable GPU shared output.");
            if (!ReferenceEquals(request.Project, snapshot)) throw new PreparedFrameException("RenderSessionInputsMismatch", "GPU 会话不属于当前快照。");
            var target = request.ResolveTarget();
            using var frameScope = PerformanceTelemetry.BeginFrame(_telemetrySession, "gpu",
                Interlocked.Increment(ref _telemetrySequence), target.PixelWidth, target.PixelHeight);
            try
            {
                PreparedRenderFrame? prepared;
                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))
                    prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,
                        request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, target, linked.Token).ConfigureAwait(false);
                var frame = renderer.RenderGpu(request with { PreparedFrame = prepared, CancellationToken = linked.Token });
                frameScope.SetOutcome("ok");
                return frame;
            }
            catch (OperationCanceledException) { frameScope.SetOutcome("cancelled"); throw; }
        }
        finally { _gate.Release(); }
    }

    public ValueTask<GpuRenderFrame> RenderSyntheticGpuAsync(RenderTarget target, long timeUs, ulong sequence,
        CancellationToken cancellationToken = default)
        => new(Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(target);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!enableGpuPreviewBenchmark)
                    throw new GpuPreviewException("GpuPreviewSyntheticDisabled", "Synthetic GPU output requires the explicit benchmark launch flag.");
                cancellationToken.ThrowIfCancellationRequested();
                return renderer.RenderSyntheticGpu(target, timeUs, sequence);
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
            await ReleaseAsync(source, renderer, resources, fixedImages, inputs, natives, budget, device).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal static async ValueTask ReleaseAsync(IEvaluatedFrameSource? source, SkiaRenderPipeline? renderer,
        SceneImageEffectResources? resources, IDisposable? fixedImages, IReadOnlyList<SkiaGraphSurface> inputs,
        IReadOnlyList<CompiledPackageGraphExports> natives, ImageSurfaceBudgetSession? budget, ISkiaRenderDevice? device = null)
    {
        List<Exception> failures = [];
        try { if (source is not null) await source.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        // External lease delivery/completion can run outside the session gate.
        // Every synchronous shared-device owner is retired under its same gate.
        lock (device?.SynchronizationRoot ?? new object())
        {
            Release(renderer); Release(resources); Release(fixedImages);
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
