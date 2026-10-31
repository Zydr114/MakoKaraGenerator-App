using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>持有同一固定输入 Worker 和真实已分配的 GPU 预览池。</summary>
public sealed class GpuStreamWorkerSession : IAsyncDisposable
{
    private readonly WorkerProcessProxy _proxy;
    private readonly StaticWorkerSessionHandle _session;
    private readonly string _directory;
    private readonly string _inputGeneration;
    private readonly object _disposeGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _sequence = 1;
    private Task? _disposeTask;
    private bool _terminationConfirmed;

    private GpuStreamWorkerSession(WorkerProcessProxy proxy, StaticWorkerSessionHandle session,
        string directory, string inputGeneration, string brokerPath, string handshake, GpuStreamWorkerOpened opened)
    { _proxy = proxy; _session = session; _directory = directory; _inputGeneration = inputGeneration; BrokerPath = brokerPath; Handshake = handshake; Opened = opened; }

    /// <summary>实际池参数与 Worker 身份。</summary>
    public GpuStreamWorkerOpened Opened { get; }
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
    /// <summary>仅供可信桌面 consumer 领取的私有 socket 路径。</summary>
    public string BrokerPath { get; }
    /// <summary>仅供可信桌面 consumer 领取的一次性通道凭证。</summary>
    public string Handshake { get; }

    /// <summary>使用现有固定输入创建独立 Worker，成功前不会返回未初始化池。</summary>
    public static async Task<GpuStreamWorkerSession> OpenAsync(WorkerProcessOptions options,
        BackendPreparedProjectRuntimeInput input, string streamId, string hostInstanceId, string inputGeneration,
        int width, int height, bool transparent, int mainPid, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("GPU preview v1 requires Linux.");
        const ulong maximumAllocationBytes = 64UL * 1024 * 1024;
        string directory = Path.Combine(Path.GetTempPath(), "mkg-gpu-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = Path.Combine(directory, "broker.sock");
        string handshake = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        WorkerProcessProxy proxy = new(options with { RenderBackend = "gpu", EnableGpuPreview = true,
            EnableGpuPreviewBenchmark = Environment.GetEnvironmentVariable("MAKO_BACKEND_GPU_PREVIEW_BENCHMARK") == "1" });
        try
        {
            var session = await proxy.OpenRuntimeInputSessionAsync(input.SourceBytes, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            var request = new GpuStreamWorkerOpen(streamId, hostInstanceId, inputGeneration, width, height,
                transparent, maximumAllocationBytes, mainPid, path, handshake);
            var opened = await proxy.OpenGpuStreamAsync(session, request, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            return new(proxy, session, directory, inputGeneration, path, handshake, opened);
        }
        catch (Exception openFailure)
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            { throw new GpuStreamOpenCleanupException(proxy, directory, openFailure, cleanupFailure); }
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

    /// <summary>停止并要求真实排空；失败仍结束所属 Worker 并释放本地引用。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
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
        await _proxy.StopAsync().ConfigureAwait(false);
        Volatile.Write(ref _terminationConfirmed, true);
    }

    private async Task DisposeCoreAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            await _proxy.CloseGpuStreamAsync(_session,
                new(Opened.StreamId, Opened.ProducerEpoch, _inputGeneration), checked(++_sequence), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await _proxy.ClosePackageSessionAsync(_session, checked(++_sequence), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { await _proxy.DisposeAsync().ConfigureAwait(false); Volatile.Write(ref _terminationConfirmed, true); }
        catch (Exception error) { failure ??= error; }
        try { Directory.Delete(_directory, true); }
        catch (IOException error) { failure ??= error; }
        catch (UnauthorizedAccessException error) { failure ??= error; }
        _operationGate.Release();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

/// <summary>启动失败且无法确认子进程终止时，保留代理所有权供宿主隔离。</summary>
public sealed class GpuStreamOpenCleanupException : Exception
{
    private readonly WorkerProcessProxy _proxy;
    private readonly string _directory;
    internal GpuStreamOpenCleanupException(WorkerProcessProxy proxy, string directory, Exception openFailure, Exception cleanupFailure)
        : base("GPU Worker 启动失败且清理状态未知。", new AggregateException(openFailure, cleanupFailure))
    { _proxy = proxy; _directory = directory; }

    /// <summary>再次等待所属进程清理；失败不代表进程已经终止。</summary>
    public async Task RetryCleanupAsync()
    {
        await _proxy.StopAsync().ConfigureAwait(false);
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
    }
}
