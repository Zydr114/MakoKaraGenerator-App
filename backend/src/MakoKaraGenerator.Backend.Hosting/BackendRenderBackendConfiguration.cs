using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>部署默认仅解析一次；每次操作固定选择并在隔离 Worker 内检查设备。</summary>
public sealed class BackendRenderBackendConfiguration : IDisposable
{
    private readonly WorkerProcessOptions? _workerOptions;
    private readonly SemaphoreSlim _cpuProbe = new(1, 1);
    private readonly SemaphoreSlim _gpuProbe = new(1, 1);
    private Func<CancellationToken, Task<RenderBackendDeviceInfoDto>>? _gpuStreamDevice;
    /// <summary>固定部署默认与隔离Worker启动配置；环境只在构造时读取。</summary>
    public BackendRenderBackendConfiguration(WorkerProcessOptions? workerOptions, string? configuredDefault = null)
    {
        _workerOptions = workerOptions;
        string? configured = configuredDefault ?? Environment.GetEnvironmentVariable("MAKO_RENDER_BACKEND");
        DefaultBackend = string.IsNullOrWhiteSpace(configured) || configured.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? "cpu"
            : configured.Equals("vulkan", StringComparison.OrdinalIgnoreCase) ? "gpu"
            : throw new InvalidOperationException("部署渲染后端必须为cpu或vulkan。");
    }

    /// <summary>本次Host启动固定的公开默认选择。</summary>
    public string DefaultBackend { get; }

    /// <summary>仅同一受管理部署可将探针替换为正式 Worker 自身的启动核验。</summary>
    public bool UsesWorkerOptions(WorkerProcessOptions? options) => options is not null && ReferenceEquals(_workerOptions, options);

    /// <summary>桌面 stream 配额启用后，所有 GPU availability 读取复用其已准备的原设备。</summary>
    public void ConfigureGpuStreamDevice(Func<CancellationToken, Task<RenderBackendDeviceInfoDto>> verify)
    {
        ArgumentNullException.ThrowIfNull(verify);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.CompareExchange(ref _gpuStreamDevice, verify, null) is not null)
            throw new InvalidOperationException("GPU stream 设备检查只能配置一次。");
    }
    /// <summary>以显式选择、现有会话选择、部署默认的顺序固定公开后端。</summary>
    public string Resolve(string? requested, string? fallback = null)
    {
        string resolved = requested ?? fallback ?? DefaultBackend;
        return WorkerRenderBackendContract.IsSupported(resolved) ? resolved
            : throw new ArgumentException("renderBackend必须为cpu或gpu。", nameof(requested));
    }

    /// <summary>在独立Worker内核验固定设备，失败只返回稳定安全原因。</summary>
    public async Task<BackendErrorResponse?> VerifyAsync(string backend, CancellationToken cancellationToken = default)
        => (await CheckAsync(backend, cancellationToken).ConfigureAwait(false)).UnavailableReason;

    /// <summary>读取两种固定选择的当前实际设备检查快照。</summary>
    public async Task<RenderBackendsResponse> ReadAsync(CancellationToken cancellationToken = default)
    {
        RenderBackendAvailabilityDto[] results = await Task.WhenAll(CheckAsync("cpu", cancellationToken),
            CheckAsync("gpu", cancellationToken)).ConfigureAwait(false);
        return new(DefaultBackend, results);
    }

    private async Task<RenderBackendAvailabilityDto> CheckAsync(string backend, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _ = Resolve(backend);
        string api = WorkerRenderBackendContract.Api(backend);
        if (_workerOptions is null) return Unavailable(WorkerRenderBackendContract.UnavailableCode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        SemaphoreSlim gate = backend == "cpu" ? _cpuProbe : _gpuProbe;
        bool entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            if (backend == "gpu" && _gpuStreamDevice is { } verify)
                return new(backend, true, api, await verify(deadline.Token).ConfigureAwait(false));
            await using var worker = new WorkerProcessProxy(_workerOptions with { RenderBackend = backend });
            await worker.StartAsync(deadline.Token).ConfigureAwait(false);
            return new(backend, true, api, worker.VerifiedRenderBackendDevice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerProtocolException error) { return Unavailable(WorkerRenderBackendContract.SafeFailureCode(error.Code)); }
        catch (Exception error) when (error is OperationCanceledException or IOException or Win32Exception
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return Unavailable(WorkerRenderBackendContract.UnavailableCode); }
        finally { if (entered) gate.Release(); }

        RenderBackendAvailabilityDto Unavailable(string code) => new(backend, false, api, UnavailableReason:
            new(BackendErrorCode.TaskUnavailable, "所选渲染后端当前不可用。", "renderBackend", FailureCode: code));
    }

    private int _disposed;

    /// <summary>释放并行设备探针的并发门。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cpuProbe.Dispose();
        _gpuProbe.Dispose();
    }
}
