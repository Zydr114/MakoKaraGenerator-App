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
    private Task? _disposeTask;

    private GpuStreamWorkerSession(WorkerProcessProxy proxy, StaticWorkerSessionHandle session,
        string directory, string inputGeneration, string brokerPath, string handshake, GpuStreamWorkerOpened opened)
    { _proxy = proxy; _session = session; _directory = directory; _inputGeneration = inputGeneration; BrokerPath = brokerPath; Handshake = handshake; Opened = opened; }

    /// <summary>实际池参数与 Worker 身份。</summary>
    public GpuStreamWorkerOpened Opened { get; }
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
        WorkerProcessProxy proxy = new(options with { RenderBackend = "gpu", EnableGpuPreview = true });
        try
        {
            var session = await proxy.OpenRuntimeInputSessionAsync(input.SourceBytes, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            var request = new GpuStreamWorkerOpen(streamId, hostInstanceId, inputGeneration, width, height,
                transparent, maximumAllocationBytes, mainPid, path, handshake);
            var opened = await proxy.OpenGpuStreamAsync(session, request, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            return new(proxy, session, directory, inputGeneration, path, handshake, opened);
        }
        catch
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); } catch { /* Preserve the open failure. */ }
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

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await _proxy.CloseGpuStreamAsync(_session,
                new(Opened.StreamId, Opened.ProducerEpoch, _inputGeneration), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await _proxy.ClosePackageSessionAsync(_session, 3, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { await _proxy.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        try { Directory.Delete(_directory, true); }
        catch (IOException error) { failure ??= error; }
        catch (UnauthorizedAccessException error) { failure ??= error; }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
