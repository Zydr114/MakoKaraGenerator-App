using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>同一 Host 的 GPU stream Worker 所有者；活动、备用、开启和未知退出共享两个硬名额。</summary>
public sealed class GpuWorkerPool : IAsyncDisposable
{
    private readonly WorkerProcessOptions _options;
    private readonly GpuWorkerSparePool<WorkerProcessProxy> _pool;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly GpuDeploymentIdentity _deployment;

    public GpuWorkerPool(WorkerProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options with { RenderBackend = "gpu", EnableGpuPreview = true,
            EnableGpuPreviewBenchmark = Environment.GetEnvironmentVariable("MAKO_BACKEND_GPU_PREVIEW_BENCHMARK") == "1" };
        _options.Validate();
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

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
    public Task RetryShutdownDrainAsync() => _pool.RetryShutdownDrainAsync();
}

internal sealed record GpuWorkerDiagnostic(string State, bool CurrentConfiguration, bool Ready,
    int? ProcessId, ProcessIdentity? OwnedIdentity, GpuWorkerWarmupResult? Preparation);
