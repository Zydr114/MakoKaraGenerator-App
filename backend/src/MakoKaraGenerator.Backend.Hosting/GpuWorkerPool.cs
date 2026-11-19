using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>连续预览流 Worker 的一次性进程租约（支持 GPU 预热备用池与 CPU 按需冷启动）。</summary>
internal interface IStreamWorkerLease : IAsyncDisposable
{
    WorkerProcessProxy Worker { get; }
    string Admission { get; }
    Task RetryTerminationAsync();
}

/// <summary>同一 Host 的连续预览流 Worker 所有者；Vulkan 复用预热备用池，CPU 按需启动轻量 Worker。</summary>
public sealed class GpuWorkerPool : IAsyncDisposable
{
    private readonly WorkerProcessOptions _options;
    private readonly WorkerProcessOptions _cpuOptions;
    private readonly GpuWorkerSparePool<WorkerProcessProxy> _pool;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly GpuDeploymentIdentity _deployment;
    private readonly object _onDemandGate = new();
    private readonly HashSet<OnDemandSlot> _onDemandSlots = [];
    private bool _disposed;

    public GpuWorkerPool(WorkerProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool benchmark = Environment.GetEnvironmentVariable("MAKO_BACKEND_GPU_PREVIEW_BENCHMARK") == "1";
        _options = options with { RenderBackend = "gpu", EnableGpuPreview = true, EnableGpuPreviewBenchmark = benchmark };
        _options.Validate();
        _cpuOptions = options with { RenderBackend = "cpu", EnableGpuPreview = true, EnableGpuPreviewBenchmark = benchmark };
        _cpuOptions.Validate();
        string executable = _options.Arguments?.FirstOrDefault(argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            ?? _options.FileName;
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        string deploymentRoot = Path.GetDirectoryName(directory)!;
        _deployment = new(directory, Path.Combine(deploymentRoot, "deployment.json"),
            Path.Combine(deploymentRoot, "gpu-preview-manifest.json"));
        _pool = new(ConfigurationIdentity(), () => new WorkerProcessProxy(_options),
            async (worker, token) => { _ = await worker.WarmGpuAsync(token).ConfigureAwait(false); },
            async worker => { await worker.StopAsync().ConfigureAwait(false); await worker.DisposeAsync().ConfigureAwait(false); },
            worker => worker.ProcessId is { } pid && worker.GpuWarmupProcessIdentity?.ProcessId == pid);
    }

    public GpuWorkerSparePoolSnapshot Read() => _pool.Read();
    internal (int Files, long Bytes) DeploymentClosureSize => (_deployment.LastFileCount, _deployment.LastTotalBytes);
    internal GpuWorkerDiagnostic[] ReadPrivateDiagnostics() => _pool.ReadPrivateDiagnostics((worker, state, current, ready) =>
        new GpuWorkerDiagnostic(state, current, ready, worker.ProcessId, worker.GpuWarmupProcessIdentity, worker.GpuWarmup));

    public async Task<GpuWorkerSparePool<WorkerProcessProxy>.Lease> AcquireAsync(CancellationToken token = default)
    {
        // A deployment/configuration content change invalidates unused preparation
        // before admission. Used Workers remain single-use and are drained normally.
        await _configurationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string configuration = ConfigurationIdentity();
            await _pool.InvalidateAsync(configuration).ConfigureAwait(false);
            var lease = await _pool.AcquireAsync(token).ConfigureAwait(false);
            try
            {
                if (ConfigurationIdentity() == configuration) return lease;
                throw new WorkerProtocolException("GPU Worker 取用期间部署配置已经改变。", GpuWorkerWarmupContract.FailureCode);
            }
            catch (Exception admissionFailure)
            {
                // A deleted/unreadable/symlinked closure is also a failed admission;
                // hashing errors must never strand the newly bound process lease.
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch (Exception drainFailure) { throw new AggregateException(admissionFailure, drainFailure); }
                throw;
            }
        }
        finally { _configurationGate.Release(); }
    }

    /// <summary>按渲染后端获取连续预览 Worker：vulkan 走预热备用池，cpu 按需冷启动且纳入统一进程回收。</summary>
    internal async Task<IStreamWorkerLease> AcquireStreamWorkerAsync(string backendApi, CancellationToken token = default)
    {
        string normalized = PreviewWireConventions.NormalizeBackendApi(backendApi);
        if (normalized == PreviewWireConventions.ApiVulkan)
        {
            return new SparePoolLeaseAdapter(await AcquireAsync(token).ConfigureAwait(false));
        }

        if (normalized != PreviewWireConventions.ApiCpu)
        {
            throw new GpuPreviewException("PreviewBackendUnsupported", $"当前连续预览流不支持渲染后端 '{normalized}'。");
        }

        OnDemandSlot slot;
        lock (_onDemandGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            slot = new OnDemandSlot(new WorkerProcessProxy(_cpuOptions));
            _onDemandSlots.Add(slot);
        }

        try
        {
            await slot.Worker.StartAsync(token).ConfigureAwait(false);
            return new OnDemandWorkerLease(this, slot);
        }
        catch
        {
            try { await RetireOnDemandAsync(slot).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    /// <summary>availability 复用同配置、存活的已准备 stream Worker，不增开 helper。</summary>
    public async Task<RenderBackendDeviceInfoDto> VerifyDeviceAsync(CancellationToken token = default)
    {
        await _configurationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string configuration = ConfigurationIdentity();
            await _pool.InvalidateAsync(configuration).ConfigureAwait(false);
            var device = await _pool.InspectReadyAsync((worker, cancellation) =>
                worker.ReadGpuDeviceProofAsync(cancellation), token).ConfigureAwait(false);
            if (ConfigurationIdentity() != configuration)
                throw new WorkerProtocolException("GPU 设备检查期间部署配置已经改变。", GpuWorkerWarmupContract.FailureCode);
            return device;
        }
        finally { _configurationGate.Release(); }
    }

    private string ConfigurationIdentity()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            _options.FileName, _options.Arguments, _options.CancellationGracePeriod,
            _options.ExportMediaFileSizeLimitBytes, _options.RenderBackend,
            _options.EnableGpuPreview, _options.EnableGpuPreviewBenchmark,
            ScriptCompilationIdentity = _options.ScriptCompilationService?.Identity,
        }));
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(_deployment.Read()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public async ValueTask DisposeAsync()
    {
        OnDemandSlot[]? onDemand = null;
        if (_onDemandGate is not null && _onDemandSlots is not null)
        {
            lock (_onDemandGate)
            {
                _disposed = true;
                onDemand = _onDemandSlots.ToArray();
            }
        }

        List<Exception>? errors = null;
        try { await _pool.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { (errors ??= []).Add(error); }

        if (onDemand is { Length: > 0 })
        {
            foreach (OnDemandSlot slot in onDemand)
            {
                try { await RetireOnDemandAsync(slot).ConfigureAwait(false); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        }

        if (errors is not null)
        {
            throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
        }
    }

    public async Task RetryShutdownDrainAsync()
    {
        List<Exception>? errors = null;
        try { await _pool.RetryShutdownDrainAsync().ConfigureAwait(false); }
        catch (Exception error) { (errors ??= []).Add(error); }

        if (_onDemandGate is not null && _onDemandSlots is not null)
        {
            OnDemandSlot[] remaining;
            lock (_onDemandGate) remaining = _onDemandSlots.ToArray();
            foreach (OnDemandSlot slot in remaining)
            {
                try { await RetireOnDemandAsync(slot).ConfigureAwait(false); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        }

        if (errors is not null)
        {
            throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
        }
    }

    private Task RetireOnDemandAsync(OnDemandSlot slot)
    {
        lock (_onDemandGate)
        {
            if (!_onDemandSlots.Contains(slot)) return Task.CompletedTask;
            if (slot.Retirement is { IsCompleted: false }) return slot.Retirement;
            return slot.Retirement = Task.Run(async () =>
            {
                await slot.Worker.StopAsync().ConfigureAwait(false);
                await slot.Worker.DisposeAsync().ConfigureAwait(false);
                lock (_onDemandGate) _onDemandSlots.Remove(slot);
            });
        }
    }

    internal sealed class SparePoolLeaseAdapter(GpuWorkerSparePool<WorkerProcessProxy>.Lease inner) : IStreamWorkerLease
    {
        public WorkerProcessProxy Worker => inner.Worker;
        public string Admission => inner.Admission;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public Task RetryTerminationAsync() => inner.RetryTerminationAsync();
    }

    private sealed class OnDemandSlot(WorkerProcessProxy worker)
    {
        internal readonly WorkerProcessProxy Worker = worker;
        internal Task? Retirement;
    }

    private sealed class OnDemandWorkerLease(GpuWorkerPool owner, OnDemandSlot slot) : IStreamWorkerLease
    {
        public WorkerProcessProxy Worker => slot.Worker;
        public string Admission => "cpu-on-demand";
        public ValueTask DisposeAsync() => new(owner.RetireOnDemandAsync(slot));
        public Task RetryTerminationAsync() => owner.RetireOnDemandAsync(slot);
    }
}

internal sealed record GpuWorkerDiagnostic(string State, bool CurrentConfiguration, bool Ready,
    int? ProcessId, ProcessIdentity? OwnedIdentity, GpuWorkerWarmupResult? Preparation);
