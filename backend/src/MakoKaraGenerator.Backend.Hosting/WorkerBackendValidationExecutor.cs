using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>任务宿主的中立正式验证执行器；固定快照由调用方捕获。</summary>
public interface IBackendValidationExecutor
{
    Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>包工程正式验证的独立能力；旧静态验证实现无需实现此接口。</summary>
public interface IBackendPackageValidationExecutor
{
    /// <summary>能力协商已通过的 package validation 状态；读取时不得将未知 Worker 当作已支持。</summary>
    bool PackageValidationAvailable { get; }
    Task<ValidationReportDto> ExecutePackageAsync(ProjectSnapshot snapshot, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>统一固定Runtime输入的正式验证执行器，返回前须排水实际Worker。</summary>
public interface IBackendRuntimeValidationExecutor
{
    bool RuntimeValidationAvailable { get; }
    Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Worker 正式验证能力的不可变探测快照。</summary>
public sealed record WorkerBackendValidationCapabilities(
    bool PackageValidationAvailable,
    bool RuntimeValidationAvailable);

/// <summary>每个任务独立拥有 Worker，完成或失败均等待私有进程及管道释放。</summary>
public sealed class WorkerBackendValidationExecutor : IBackendValidationExecutor, IBackendPackageValidationExecutor, IBackendRuntimeValidationExecutor,
    IBackendRenderBackendExecutor<IBackendValidationExecutor>
{
    private readonly WorkerProcessOptions _options;
    private readonly TimeSpan _timeout;
    private readonly CapabilityCache _capabilities;

    public WorkerBackendValidationExecutor(string workerPath, TimeSpan? operationTimeout = null)
        : this(new WorkerProcessOptions(workerPath), operationTimeout) { }

    public WorkerBackendValidationExecutor(WorkerProcessOptions options, TimeSpan? operationTimeout = null)
        : this(options, operationTimeout, new CapabilityCache()) { }

    private WorkerBackendValidationExecutor(WorkerProcessOptions options, TimeSpan? operationTimeout, CapabilityCache capabilities)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _options = options; _timeout = operationTimeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _capabilities = capabilities;
    }

    public IBackendValidationExecutor WithRenderBackend(string renderBackend)
    {
        if (renderBackend is not ("cpu" or "gpu")) throw new ArgumentException("渲染后端无效。", nameof(renderBackend));
        return new WorkerBackendValidationExecutor(_options with { RenderBackend = renderBackend }, _timeout, _capabilities);
    }

    /// <summary>异步探测并缓存 Worker 正式验证能力快照；与 <see cref="WithRenderBackend"/> 派生实例共享该缓存。</summary>
    public Task<WorkerBackendValidationCapabilities> ProbeCapabilitiesAsync(CancellationToken cancellationToken = default)
        => ProbeCapabilitiesAsync(null, cancellationToken);

    /// <summary>复用已有探测代理完成正式验证能力握手；调用方保留代理所有权。</summary>
    public Task<WorkerBackendValidationCapabilities> ProbeCapabilitiesAsync(WorkerProcessProxy? sharedProxy, CancellationToken cancellationToken = default)
        => _capabilities.GetOrProbeAsync(token => ProbeCapabilitiesCoreAsync(sharedProxy, token), cancellationToken);

    public bool PackageValidationAvailable => _capabilities.Snapshot?.PackageValidationAvailable ?? false;

    public bool RuntimeValidationAvailable => _capabilities.Snapshot?.RuntimeValidationAvailable ?? false;

    private async Task<WorkerBackendValidationCapabilities> ProbeCapabilitiesCoreAsync(WorkerProcessProxy? sharedProxy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkerProcessProxy proxy = sharedProxy ?? new(_options);
        try
        {
            bool packageAvailable = await ProbeCapabilityAsync(
                token => proxy.GetPackageValidationCapabilitiesAsync(_timeout, token), cancellationToken).ConfigureAwait(false);
            bool runtimeAvailable = await ProbeCapabilityAsync(
                token => proxy.GetRuntimeValidationCapabilitiesAsync(_timeout, token), cancellationToken).ConfigureAwait(false);
            return new(packageAvailable, runtimeAvailable);
        }
        finally
        {
            if (sharedProxy is null)
            {
                try { await proxy.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (cleanup is IOException or InvalidOperationException or ObjectDisposedException
                    or Win32Exception or OperationCanceledException) { }
            }
        }
    }

    private static async Task<bool> ProbeCapabilityAsync<T>(
        Func<CancellationToken, Task<T>> probe, CancellationToken cancellationToken)
    {
        try
        {
            await probe(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is WorkerProtocolException or OperationCanceledException or TimeoutException
            or IOException or InvalidOperationException or ObjectDisposedException or Win32Exception
            or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private sealed class CapabilityCache
    {
        private readonly object _gate = new();
        private Task<WorkerBackendValidationCapabilities>? _inFlight;
        private volatile WorkerBackendValidationCapabilities? _snapshot;

        public WorkerBackendValidationCapabilities? Snapshot => _snapshot;

        public Task<WorkerBackendValidationCapabilities> GetOrProbeAsync(
            Func<CancellationToken, Task<WorkerBackendValidationCapabilities>> probe,
            CancellationToken cancellationToken)
        {
            if (_snapshot is { } cached) return Task.FromResult(cached);
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<WorkerBackendValidationCapabilities>? owner = null;
            Task<WorkerBackendValidationCapabilities> task;
            lock (_gate)
            {
                if (_snapshot is { } doubleChecked) return Task.FromResult(doubleChecked);
                if (_inFlight is null)
                {
                    owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight = owner.Task;
                }
                task = _inFlight;
            }
            if (owner is not null) _ = RunProbeAsync(probe, owner);
            return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
        }

        private async Task RunProbeAsync(
            Func<CancellationToken, Task<WorkerBackendValidationCapabilities>> probe,
            TaskCompletionSource<WorkerBackendValidationCapabilities> owner)
        {
            try
            {
                WorkerBackendValidationCapabilities probed = await probe(CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _snapshot = probed;
                    _inFlight = null;
                }
                owner.TrySetResult(probed);
            }
            catch (Exception error)
            {
                lock (_gate) { _inFlight = null; }
                owner.TrySetException(error);
            }
        }
    }
    public async Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var composition = snapshot.Composition;
        try { WorkerValidationContract.ValidateGeometryAndTiming(composition.CanvasWidth, composition.CanvasHeight, composition.OutputDurationUs,
                composition.FrameRate.Numerator, composition.FrameRate.Denominator, framesPerOutput); }
        catch (WorkerProtocolException failure)
        { throw new RenderSessionFailureException(failure.Code, failure.Message, innerException: failure); }
        return await ExecuteCoreAsync(snapshot, framesPerOutput, StaticProjectSnapshotCodec.SelectProfile(snapshot), progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ValidationReportDto> ExecutePackageAsync(ProjectSnapshot snapshot, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        WorkerBackendValidationCapabilities capabilities = await ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.PackageValidationAvailable)
            throw new RenderSessionFailureException("WorkerPackageUnavailable", "固定 Worker 未通过 package validation 能力协商。");
        return await ExecuteCoreAsync(snapshot, framesPerOutput, PackageProjectSnapshotCodec.Profile, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        WorkerBackendValidationCapabilities capabilities = await ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.RuntimeValidationAvailable)
            throw new RenderSessionFailureException("WorkerRuntimeValidationUnavailable", "固定Worker未通过统一Runtime验证能力协商。");
        byte[] fixedBytes = sourceBytes.ToArray();
        _ = ProjectRuntimeInputCodec.ValidateEnvelope(fixedBytes);
        WorkerProcessProxy proxy = new(_options);
        Exception? primary = null;
        try
        {
            return await proxy.ValidateRuntimeProjectAsync(fixedBytes, framesPerOutput, _timeout, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is WorkerProtocolException protocol)
                throw new RenderSessionFailureException(protocol.Code, "固定Runtime Worker验证失败。", innerException: protocol);
            if (failure is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerUnavailable", "固定Runtime验证Worker不可用。", innerException: failure);
            throw;
        }
        finally
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerCleanupFailure"] = cleanup; }
        }
    }

    private async Task<ValidationReportDto> ExecuteCoreAsync(ProjectSnapshot snapshot, int framesPerOutput, string profile,
        IProgress<BackendJobProgressDto>? progress, CancellationToken cancellationToken)
    {
        byte[] bytes = profile == PackageProjectSnapshotCodec.Profile
            ? PackageProjectSnapshotCodec.Encode(snapshot)
            : StaticProjectSnapshotCodec.Encode(snapshot, profile);
        if (bytes.Length > WorkerValidationContract.MaximumSnapshotBytes)
            throw new RenderSessionFailureException("WorkerValidationInputRejected", "固定来源超过验证封包预算。");
        WorkerProcessProxy proxy = new(_options);
        Exception? primary = null;
        try
        {
            return await proxy.ValidateProjectAsync(bytes, framesPerOutput, _timeout, progress, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is WorkerProtocolException protocol)
                throw new RenderSessionFailureException(protocol.Code, "固定静态 Worker 验证失败。",
                    protocol.Diagnostics is null ? null : WorkerRenderFrameSessionFactory.DecodeDiagnostics(protocol.Diagnostics), protocol);
            if (failure is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerUnavailable", "固定静态验证 Worker 不可用。", innerException: failure);
            throw;
        }
        finally
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerCleanupFailure"] = cleanup; }
        }
    }
}
