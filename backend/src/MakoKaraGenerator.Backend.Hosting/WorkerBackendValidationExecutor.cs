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

/// <summary>已保存工程图片正式验证的独立 Worker 能力。</summary>
public interface IBackendImageValidationExecutor
{
    bool ImageValidationAvailable { get; }
    Task<ValidationReportDto> ExecuteImageAsync(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>统一固定Runtime输入的正式验证执行器，返回前须排水实际Worker。</summary>
public interface IBackendRuntimeValidationExecutor
{
    bool RuntimeValidationAvailable { get; }
    Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>每个任务独立拥有 Worker，完成或失败均等待私有进程及管道释放。</summary>
public sealed class WorkerBackendValidationExecutor : IBackendValidationExecutor, IBackendPackageValidationExecutor, IBackendImageValidationExecutor, IBackendRuntimeValidationExecutor,
    IBackendRenderBackendExecutor<IBackendValidationExecutor>
{
    private readonly WorkerProcessOptions _options;
    private readonly TimeSpan _timeout;
    private readonly object _packageCapabilityGate = new();
    private bool? _packageValidationAvailable;
    private readonly object _imageCapabilityGate = new();
    private bool? _imageValidationAvailable;
    private readonly object _runtimeCapabilityGate = new();
    private bool? _runtimeValidationAvailable;
    public WorkerBackendValidationExecutor(string workerPath, TimeSpan? operationTimeout = null)
        : this(new WorkerProcessOptions(workerPath), operationTimeout) { }
    public WorkerBackendValidationExecutor(WorkerProcessOptions options, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _options = options; _timeout = operationTimeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }
    public IBackendValidationExecutor WithRenderBackend(string renderBackend)
    {
        if (renderBackend is not ("cpu" or "gpu")) throw new ArgumentException("渲染后端无效。", nameof(renderBackend));
        return new WorkerBackendValidationExecutor(_options with { RenderBackend = renderBackend }, _timeout);
    }
    public bool PackageValidationAvailable
    {
        get
        {
            lock (_packageCapabilityGate)
            {
                if (_packageValidationAvailable is { } known) return known;
                // Keep the probe inside the gate: the result is process/deployment scoped,
                // and concurrent capability requests must not launch duplicate Workers.
                bool available;
                WorkerProcessProxy? proxy = null;
                try
                {
                    proxy = new WorkerProcessProxy(_options);
                    proxy.GetPackageValidationCapabilitiesAsync(_timeout).GetAwaiter().GetResult();
                    available = true;
                }
                catch (Exception error) when (error is WorkerProtocolException or OperationCanceledException or TimeoutException
                    or IOException or InvalidOperationException or ObjectDisposedException or Win32Exception
                    or FileNotFoundException or DirectoryNotFoundException)
                { available = false; }
                finally
                {
                    try { proxy?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception cleanup) when (cleanup is IOException or InvalidOperationException or ObjectDisposedException
                        or Win32Exception or OperationCanceledException) { }
                }
                return _packageValidationAvailable ??= available;
            }
        }
    }

    public bool ImageValidationAvailable
    {
        get
        {
            lock (_imageCapabilityGate)
            {
                if (_imageValidationAvailable is { } known) return known;
                WorkerProcessProxy? proxy = null;
                bool available;
                try
                {
                    proxy = new WorkerProcessProxy(_options);
                    proxy.GetImageValidationCapabilitiesAsync(_timeout).GetAwaiter().GetResult();
                    available = true;
                }
                catch (Exception error) when (error is WorkerProtocolException or OperationCanceledException or TimeoutException
                    or IOException or InvalidOperationException or ObjectDisposedException or Win32Exception
                    or FileNotFoundException or DirectoryNotFoundException)
                { available = false; }
                finally
                {
                    try { proxy?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception cleanup) when (cleanup is IOException or InvalidOperationException or ObjectDisposedException
                        or Win32Exception or OperationCanceledException) { }
                }
                return _imageValidationAvailable ??= available;
            }
        }
    }
    public bool RuntimeValidationAvailable
    {
        get
        {
            lock (_runtimeCapabilityGate)
            {
                if (_runtimeValidationAvailable is { } known) return known;
                WorkerProcessProxy? proxy = null;
                bool available;
                try
                {
                    proxy = new WorkerProcessProxy(_options);
                    proxy.GetRuntimeValidationCapabilitiesAsync(_timeout).GetAwaiter().GetResult();
                    available = true;
                }
                catch (Exception error) when (error is WorkerProtocolException or OperationCanceledException or TimeoutException
                    or IOException or InvalidOperationException or ObjectDisposedException or Win32Exception
                    or FileNotFoundException or DirectoryNotFoundException)
                { available = false; }
                finally
                {
                    try { proxy?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception cleanup) when (cleanup is IOException or InvalidOperationException or ObjectDisposedException
                        or Win32Exception or OperationCanceledException) { }
                }
                return _runtimeValidationAvailable ??= available;
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

    public Task<ValidationReportDto> ExecutePackageAsync(ProjectSnapshot snapshot, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!PackageValidationAvailable)
            throw new RenderSessionFailureException("WorkerPackageUnavailable", "固定 Worker 未通过 package validation 能力协商。");
        return ExecuteCoreAsync(snapshot, framesPerOutput, PackageProjectSnapshotCodec.Profile, progress, cancellationToken);
    }

    public Task<ValidationReportDto> ExecuteImageAsync(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ImageValidationAvailable)
            throw new RenderSessionFailureException("WorkerImageValidationUnavailable", "固定 Worker 未通过 image validation 能力协商。");
        byte[] fixedBytes = imageBytes.ToArray();
        return ExecuteCoreAsync(snapshot, framesPerOutput, ProjectImageValidationWorkerContract.ValidationProfile,
            progress, cancellationToken, fixedBytes);
    }

    public async Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!RuntimeValidationAvailable)
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
        IProgress<BackendJobProgressDto>? progress, CancellationToken cancellationToken, byte[]? imageBytes = null)
    {
        byte[] bytes = profile == PackageProjectSnapshotCodec.Profile
            ? PackageProjectSnapshotCodec.Encode(snapshot)
            : profile == ProjectImageValidationWorkerContract.ValidationProfile
            ? ProjectImagePreviewSnapshotCodec.Encode(snapshot, imageBytes ?? throw new ArgumentNullException(nameof(imageBytes)))
            : StaticProjectSnapshotCodec.Encode(snapshot, profile);
        int maximum = profile == ProjectImageValidationWorkerContract.ValidationProfile
            ? ProjectImageValidationWorkerContract.MaximumInputBytes : WorkerValidationContract.MaximumSnapshotBytes;
        if (bytes.Length > maximum)
            throw new RenderSessionFailureException("WorkerValidationInputRejected", "固定来源超过验证封包预算。");
        WorkerProcessProxy proxy = new(_options);
        Exception? primary = null;
        try
        {
            return profile == ProjectImageValidationWorkerContract.ValidationProfile
                    ? await proxy.ValidateImageProjectAsync(bytes, framesPerOutput, _timeout, progress, cancellationToken).ConfigureAwait(false)
                    : await proxy.ValidateProjectAsync(bytes, framesPerOutput, _timeout, progress, profile, cancellationToken).ConfigureAwait(false);
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
