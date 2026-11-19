using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>持有同一固定输入 Worker 和真实已分配的连续预览池（DMA-BUF 或 SHM）。</summary>
public sealed class GpuStreamWorkerSession : IAsyncDisposable
{
    private readonly WorkerProcessProxy _proxy;
    private readonly IStreamWorkerLease _workerLease;
    private readonly StaticWorkerSessionHandle _session;
    private readonly string _directory;
    private readonly string _inputGeneration;
    private readonly string? _shmToken;
    private readonly object _disposeGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _sequence = 1;
    private Task? _disposeTask;
    private bool _terminationConfirmed;

    private GpuStreamWorkerSession(WorkerProcessProxy proxy, IStreamWorkerLease workerLease, StaticWorkerSessionHandle session,
        string directory, string inputGeneration, string brokerPath, string handshake, string? shmToken, GpuStreamWorkerOpened opened)
    {
        _proxy = proxy;
        _workerLease = workerLease;
        _session = session;
        _directory = directory;
        _inputGeneration = inputGeneration;
        _shmToken = shmToken;
        BrokerPath = brokerPath;
        Handshake = handshake;
        Opened = opened;
    }

    // 保留 8 参私有构造函数，兼容既有反射隔离测试。
    private GpuStreamWorkerSession(WorkerProcessProxy proxy, GpuWorkerSparePool<WorkerProcessProxy>.Lease workerLease,
        StaticWorkerSessionHandle session, string directory, string inputGeneration, string brokerPath, string handshake,
        GpuStreamWorkerOpened opened)
        : this(proxy, new GpuWorkerPool.SparePoolLeaseAdapter(workerLease), session, directory, inputGeneration,
            brokerPath, handshake, shmToken: null, opened)
    {
    }

    /// <summary>实际池参数与 Worker 身份。</summary>
    public GpuStreamWorkerOpened Opened { get; }
    internal string WorkerAdmission => _workerLease.Admission;
    /// <summary>所属进程及其子进程已由代理回收，不表示消费者已完成。</summary>
    public bool TerminationConfirmed => Volatile.Read(ref _terminationConfirmed);
    /// <summary>仅表示所属 Worker 进程仍存在，不代表播放或帧状态。</summary>
    public bool IsWorkerAlive
    {
        get
        {
            try { return _proxy.ProcessId == Opened.WorkerPid; }
            catch (InvalidOperationException) { return false; }
        }
    }
    /// <summary>仅供可信桌面 consumer 领取的私有通道端点标识。</summary>
    public string BrokerPath { get; }
    /// <summary>仅供可信桌面 consumer 领取的一次性通道凭证。</summary>
    public string Handshake { get; }

    /// <summary>按显式传输、环境变量与后端 API 解析真实传输模式，严守零静默降级。</summary>
    public static PreviewTransportKind ResolveEffectiveTransport(string? backendApi, PreviewTransportKind? transport = null,
        string? envOverride = null)
    {
        string normalizedApi = PreviewWireConventions.NormalizeBackendApi(backendApi ?? PreviewWireConventions.ApiVulkan);
        string? raw = envOverride ?? Environment.GetEnvironmentVariable("MAKO_PREVIEW_TRANSPORT");
        PreviewTransportKind? requested = transport ?? (string.IsNullOrWhiteSpace(raw) ? null : PreviewWireConventions.ParseTransport(raw.Trim()));

        if (requested is { } chosen)
        {
            if (chosen == PreviewTransportKind.DmaBuf)
            {
                if (normalizedApi != PreviewWireConventions.ApiVulkan)
                {
                    throw new GpuPreviewException(
                        "PreviewTransportUnsupported",
                        $"显式指定了 DMA-BUF 传输模式，仅支持 vulkan 渲染设备，不支持 '{normalizedApi}'；已拒绝静默降级。");
                }
                if (!OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException("DMA-BUF preview requires Linux.");
                }
            }
            return chosen;
        }

        if (normalizedApi == PreviewWireConventions.ApiCpu)
        {
            return PreviewTransportKind.Shm;
        }

        return OperatingSystem.IsLinux() ? PreviewTransportKind.DmaBuf : PreviewTransportKind.Shm;
    }

    /// <summary>若 Worker 进程已死亡，幂等清理可能尚未被消费者映射解链的 SHM 具名文件。</summary>
    internal void CleanupUnopenedShmIfDead()
    {
        if (!IsWorkerAlive)
        {
            ProjectRenderSessionFactory.TryCleanupUnopenedShm(_shmToken);
        }
    }

    /// <summary>使用现有固定输入创建独立 Worker，成功前不会返回未初始化池。</summary>
    public static Task<GpuStreamWorkerSession> OpenAsync(GpuWorkerPool pool,
        BackendPreparedProjectRuntimeInput input, string streamId, string hostInstanceId, string inputGeneration,
        int width, int height, bool transparent, int mainPid, CancellationToken token = default) =>
        OpenAsync(pool, input, streamId, hostInstanceId, inputGeneration, width, height, transparent, mainPid,
            transport: null, pixelFormat: PreviewPixelFormat.Bgra8888, strideBytes: 0, backendApi: null, token: token);

    /// <summary>按显式传输与像素格式创建独立连续预览流 Worker。</summary>
    public static async Task<GpuStreamWorkerSession> OpenAsync(GpuWorkerPool pool,
        BackendPreparedProjectRuntimeInput input, string streamId, string hostInstanceId, string inputGeneration,
        int width, int height, bool transparent, int mainPid,
        PreviewTransportKind? transport, PreviewPixelFormat pixelFormat = PreviewPixelFormat.Bgra8888,
        uint strideBytes = 0, string? backendApi = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(input);

        string effectiveApi = PreviewWireConventions.NormalizeBackendApi(backendApi ?? PreviewWireConventions.ApiVulkan);
        PreviewTransportKind effectiveTransport = ResolveEffectiveTransport(effectiveApi, transport);

        const ulong maximumAllocationBytes = 64UL * 1024 * 1024;
        string directory = Path.Combine(Path.GetTempPath(), "mkg-gpu-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
        string endpointPath = AllocateEndpointIdentifier(directory, effectiveTransport);
        string handshake = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string? shmToken = effectiveTransport == PreviewTransportKind.Shm
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
            : null;

        var workerLease = await pool.AcquireStreamWorkerAsync(effectiveApi, token).ConfigureAwait(false);
        WorkerProcessProxy proxy = workerLease.Worker;
        try
        {
            CreatePrivateDirectory(directory);
            var session = await proxy.OpenRuntimeInputSessionAsync(input.SourceBytes, TimeSpan.FromSeconds(45),
                input.GraphPixelChecks, token).ConfigureAwait(false);
            var request = new GpuStreamWorkerOpen(
                streamId,
                hostInstanceId,
                inputGeneration,
                width,
                height,
                transparent,
                maximumAllocationBytes,
                mainPid,
                endpointPath,
                handshake,
                Transport: effectiveTransport.ToWireName(),
                BackendApi: effectiveApi,
                PixelFormat: pixelFormat.ToWireName(),
                StrideBytes: strideBytes,
                PreferredGpuDevice: null,
                ShmToken: shmToken);
            var opened = await proxy.OpenGpuStreamAsync(session, request, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            return new(proxy, workerLease, session, directory, inputGeneration, endpointPath, handshake, shmToken, opened);
        }
        catch (Exception openFailure)
        {
            try { await workerLease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            { throw new GpuStreamOpenCleanupException(workerLease, directory, openFailure, cleanupFailure, shmToken); }
            ProjectRenderSessionFactory.TryCleanupUnopenedShm(shmToken);
            DeleteDirectory();
            throw;
        }

        void DeleteDirectory()
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string AllocateEndpointIdentifier(string directory, PreviewTransportKind transport)
    {
        if (transport == PreviewTransportKind.Shm && OperatingSystem.IsWindows())
        {
            return @"\\.\pipe\mkg-shm-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }

        return Path.Combine(directory, "broker.sock");
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>停止并要求真实排空；若进程已确认终止，协议或临时目录清理失败只记录诊断，不再永久阻塞后续释放。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
                return new(_disposeTask = DisposeCoreAsync());
            if (_disposeTask.IsFaulted && !TerminationConfirmed)
                return new(_disposeTask = RetryTerminationAsync());
            return new(_disposeTask);
        }
    }

    /// <summary>读取生产者权威状态，和关闭消息按同一序列串行。</summary>
    public async Task<GpuStreamWorkerState> ReadAsync(CancellationToken token = default)
    {
        await _operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_disposeGate) ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return await _proxy.ReadGpuStreamAsync(_session, new(Opened.StreamId, Opened.ProducerEpoch, _inputGeneration),
                checked(++_sequence), TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>重试所属进程回收，不改变排空结果。</summary>
    public async Task RetryTerminationAsync()
    {
        await _workerLease.RetryTerminationAsync().ConfigureAwait(false);
        ProjectRenderSessionFactory.TryCleanupUnopenedShm(_shmToken);
        if (!string.IsNullOrWhiteSpace(_directory) && Path.IsPathRooted(_directory))
        {
            try { Directory.Delete(_directory, true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Volatile.Write(ref _terminationConfirmed, true);
    }

    private async Task DisposeCoreAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        Exception? protocolFailure = null;
        Exception? terminationFailure = null;
        try
        {
            try
            {
                await _proxy.CloseGpuStreamAsync(_session,
                    new(Opened.StreamId, Opened.ProducerEpoch, _inputGeneration), checked(++_sequence), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                await _proxy.ClosePackageSessionAsync(_session, checked(++_sequence), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (Exception error) { protocolFailure = error; }
            try
            {
                await _workerLease.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _terminationConfirmed, true);
            }
            catch (Exception error) { terminationFailure = error; }
            ProjectRenderSessionFactory.TryCleanupUnopenedShm(_shmToken);
            if (terminationFailure is not null)
            {
                await TryDeleteDirectoryWithRetryAsync(_directory, logFailure: false).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(terminationFailure).Throw();
            }
            if (protocolFailure is not null)
            {
                Console.Error.WriteLine(
                    $"BACKEND_GPU_WORKER_CLOSE_PROTOCOL_FAILED type={protocolFailure.GetType().Name} inner={protocolFailure.InnerException?.GetType().Name ?? "none"}");
            }
            await TryDeleteDirectoryWithRetryAsync(_directory, logFailure: true).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static async Task TryDeleteDirectoryWithRetryAsync(string directory, bool logFailure)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        const int maxAttempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(attempt * 25).ConfigureAwait(false);
                    continue;
                }
                if (logFailure)
                {
                    Console.Error.WriteLine(
                        $"BACKEND_GPU_WORKER_DIR_CLEANUP_FAILED type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"}");
                }
                return;
            }
        }
    }
}

/// <summary>启动失败且无法确认子进程终止时，保留代理所有权供宿主隔离。</summary>
public sealed class GpuStreamOpenCleanupException : Exception
{
    private readonly IStreamWorkerLease _workerLease;
    private readonly string _directory;
    private readonly string? _shmToken;

    internal GpuStreamOpenCleanupException(IStreamWorkerLease workerLease, string directory,
        Exception openFailure, Exception cleanupFailure, string? shmToken = null)
        : base("GPU Worker 启动失败且清理状态未知。", new AggregateException(openFailure, cleanupFailure))
    {
        _workerLease = workerLease;
        _directory = directory;
        _shmToken = shmToken;
    }

    internal GpuStreamOpenCleanupException(GpuWorkerSparePool<WorkerProcessProxy>.Lease workerLease, string directory,
        Exception openFailure, Exception cleanupFailure, string? shmToken = null)
        : this(new GpuWorkerPool.SparePoolLeaseAdapter(workerLease), directory, openFailure, cleanupFailure, shmToken)
    {
    }

    /// <summary>再次等待所属进程清理；失败不代表进程已经终止。</summary>
    public async Task RetryCleanupAsync()
    {
        await _workerLease.RetryTerminationAsync().ConfigureAwait(false);
        ProjectRenderSessionFactory.TryCleanupUnopenedShm(_shmToken);
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
