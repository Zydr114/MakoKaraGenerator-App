using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.TestSupport;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerProcessProxyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Protocol_round_trips_hash_and_rejects_oversized_payload()
    {
        byte[] payload = [1, 2, 3, 4];
        WorkerMessage message = new(StaticRenderWorkerContract.CapabilitiesKind, "request", WorkerProtocol.ComputeSha256(payload), payload);
        await using MemoryStream stream = new();
        await WorkerProtocol.WriteAsync(stream, message);
        stream.Position = 0;
        WorkerMessage actual = (await WorkerProtocol.ReadAsync(stream))!;
        Assert.Equal(message.Kind, actual.Kind);
        Assert.Equal(message.RequestId, actual.RequestId);
        Assert.Equal(message.InputSha256, actual.InputSha256);
        Assert.Equal(payload, actual.Payload);

        byte[] tooLarge = new byte[WorkerProtocol.MaxPayloadBytes + 1];
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await WorkerProtocol.WriteAsync(new MemoryStream(), new(StaticRenderWorkerContract.CapabilitiesKind, "too-large", WorkerProtocol.ComputeSha256(tooLarge), tooLarge)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WorkerProtocol.MaxMessageBytes + 1)]
    public async Task Protocol_rejects_malformed_length_before_body_allocation(int length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.ReadAsync(new MemoryStream(prefix)));
    }

    [Fact]
    public async Task Protocol_rejects_truncated_prefix_body_bad_json_and_input_hash()
    {
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.ReadAsync(new MemoryStream([1, 0])));
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.ReadAsync(new MemoryStream([2, 0, 0, 0, 123])));
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.ReadAsync(new MemoryStream([1, 0, 0, 0, 123])));
        WorkerMessage badHash = new(StaticRenderWorkerContract.CapabilitiesKind, "bad", new string('0', 64), StaticRenderWorkerContract.EncodeCapabilitiesRequest());
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(badHash));
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.WriteAsync(new MemoryStream(), badHash));
    }

    [Fact]
    public async Task Protocol_graph_pixel_checks_only_belong_to_session_open_requests()
    {
        byte[] payload = [1];
        WorkerMessage Message(string kind) => new(kind, "pixel-checks", WorkerProtocol.ComputeSha256(payload), payload) { GraphPixelChecks = true };
        foreach (string kind in (string[])[PackageRenderWorkerContract.OpenKind, ProjectRuntimeInputWorkerContract.OpenKind])
            WorkerProtocol.Validate(Message(kind));
        foreach (string kind in (string[])[PackageRenderWorkerContract.RenderKind, WorkerExportContract.RequestKind, RuntimeValidationWorkerContract.RequestKind])
            Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(Message(kind)));
        await using MemoryStream stream = new();
        await WorkerProtocol.WriteAsync(stream, Message(ProjectRuntimeInputWorkerContract.OpenKind)); stream.Position = 0;
        Assert.True((await WorkerProtocol.ReadAsync(stream))!.GraphPixelChecks);
    }

    [Fact]
    public async Task Stop_cancels_an_active_call_before_reaping_the_worker()
    {
        using TemporaryDirectory temp = new();
        string script = temp.FilePath("blocking-probe.py");
        string marker = temp.FilePath("entered");
        await File.WriteAllTextAsync(script, BlockingWorkerProbe, Encoding.UTF8);
        await using WorkerProcessProxy proxy = new(HostingTestPlatformHelpers.PythonOptions(script, [marker], TimeSpan.FromMilliseconds(200)));
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        Task<StaticWorkerCapabilitiesDto> active = proxy.GetStaticCapabilitiesAsync(TimeSpan.FromSeconds(30));
        await WaitForFileAsync(marker, TimeSpan.FromSeconds(5));
        await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.False(IsProcessAlive(processId));
        Assert.Null(proxy.ProcessId);
    }

    [Fact]
    public async Task Concurrent_disposal_reaps_active_worker_and_rejects_queued_calls()
    {
        using TemporaryDirectory temp = new();
        string script = temp.FilePath("blocking-probe.py");
        string marker = temp.FilePath("entered");
        await File.WriteAllTextAsync(script, BlockingWorkerProbe, Encoding.UTF8);
        WorkerProcessProxy proxy = new(HostingTestPlatformHelpers.PythonOptions(script, [marker], TimeSpan.FromMilliseconds(200)));
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        Task<StaticWorkerCapabilitiesDto> active = proxy.GetStaticCapabilitiesAsync(TimeSpan.FromSeconds(30));
        await WaitForFileAsync(marker, TimeSpan.FromSeconds(5));
        Task<StaticWorkerCapabilitiesDto> queued = proxy.GetStaticCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Task firstDispose = proxy.DisposeAsync().AsTask();
        Task secondDispose = proxy.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => proxy.StartAsync());
        Assert.False(IsProcessAlive(processId));
    }

    [Fact]
    public async Task Failed_cleanup_retains_owned_process_until_successful_retry()
    {
        await using WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        var processField = typeof(WorkerProcessProxy).GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var drainField = typeof(WorkerProcessProxy).GetField("_stderrDrain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Process process = Assert.IsType<Process>(processField.GetValue(proxy));
        Task originalDrain = Assert.IsAssignableFrom<Task>(drainField.GetValue(proxy));
        drainField.SetValue(proxy, Task.FromException(new IOException("injected drain failure")));
        await Assert.ThrowsAsync<IOException>(() => proxy.StopAsync());
        Assert.Same(process, processField.GetValue(proxy));
        await originalDrain;
        drainField.SetValue(proxy, Task.CompletedTask);
        await proxy.StopAsync();
        Assert.Null(processField.GetValue(proxy));
    }

    [Fact]
    public async Task Failed_open_cleanup_keeps_input_and_quota_until_retry_confirms_termination()
    {
        await using var pool = new GpuWorkerSparePool<WorkerProcessProxy>("cleanup-fixture", NewProxy,
            (_, _) => Task.CompletedTask, async worker => { await worker.StopAsync(); await worker.DisposeAsync(); }, _ => true);
        var workerLease = await pool.AcquireAsync();
        WorkerProcessProxy proxy = workerLease.Worker;
        await proxy.StartAsync();
        var drainField = typeof(WorkerProcessProxy).GetField("_stderrDrain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task originalDrain = Assert.IsAssignableFrom<Task>(drainField.GetValue(proxy));
        drainField.SetValue(proxy, Task.FromException(new IOException("injected drain failure")));
        await Assert.ThrowsAsync<IOException>(() => proxy.StopAsync());
        GpuPreviewStreamRegistry registry = new();
        IDisposable reservation = registry.TryReserve("a", "consumer-a")!;
        using IDisposable second = registry.TryReserve("b", "consumer-b")!;
        CountingLease lease = new();
        var input = new BackendPreviewPinnedInput(new("project", "source", "1"), null!, null!, lease);
        using TemporaryDirectory directory = new();
        var failure = new GpuStreamOpenCleanupException(workerLease, directory.Path, new InvalidOperationException("open"), new IOException("cleanup"));
        registry.QuarantineOpen(reservation, null, null, input, failure);
        reservation.Dispose();
        await Assert.ThrowsAsync<AggregateException>(() => registry.RetryFailedOpensAsync());
        Assert.Equal(0, lease.Count);
        Assert.Null(registry.TryReserve("c", "consumer-c"));
        await originalDrain;
        drainField.SetValue(proxy, Task.CompletedTask);
        await registry.RetryFailedOpensAsync();
        await registry.RetryFailedOpensAsync();
        Assert.Equal(1, lease.Count);
        Assert.Null(registry.TryReserve("c", "consumer-c"));
    }

    private sealed class CountingLease : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }

    [Fact]
    public async Task Frame_deadline_reaps_unresponsive_process_tree_and_proxy_restarts()
    {
        using TemporaryDirectory temp = new();
        string script = temp.FilePath("worker-probe.py");
        string marker = temp.FilePath("first-call");
        string childPidFile = temp.FilePath("child-pid");
        await File.WriteAllTextAsync(script, UnresponsiveWorkerProbe, Encoding.UTF8);
        await using WorkerProcessProxy proxy = new(HostingTestPlatformHelpers.PythonOptions(script, [marker, childPidFile], TimeSpan.FromMilliseconds(80)));
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.GetStaticCapabilitiesAsync(TimeSpan.FromSeconds(2)));

        Assert.True(File.Exists(childPidFile), "Probe did not enter the request and spawn its owned child.");
        int childId = int.Parse(await File.ReadAllTextAsync(childPidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(IsProcessAlive(processId));
        // Kill(entireProcessTree) waits for the owned leader, while the kernel may
        // finish/reap descendants just after that leader exits. Bound observation.
        Stopwatch released = Stopwatch.StartNew();
        while (IsProcessAlive(childId) && released.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        Assert.False(IsProcessAlive(childId), $"Owned child {childId} survived deadline recovery.");
        Assert.Null(proxy.ProcessId);
        StaticWorkerCapabilitiesDto next = await proxy.GetStaticCapabilitiesAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsScene, next.SourceProfiles);
        int restarted = Assert.IsType<int>(proxy.ProcessId);
        Assert.NotEqual(processId, restarted);
        await proxy.StopAsync();
        Assert.False(IsProcessAlive(restarted));
        output.WriteLine($"Deadline probe PID={processId}, owned child PID={childId}; both released after timeout. Restarted PID={restarted}; StopAsync completed and released.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(sw.Elapsed < timeout, $"Marker {path} was not written in time.");
            await Task.Delay(10);
        }
    }

    private const string BlockingWorkerProbe = """
        import json, struct, sys, time
        marker = sys.argv[1]
        def send(message):
            body = json.dumps(dict(protocolVersion='worker-1', payload='', delayMilliseconds=0, cancelled=False, **message)).encode()
            sys.stdout.buffer.write(struct.pack('<i', len(body)) + body)
            sys.stdout.buffer.flush()
        while True:
            prefix = sys.stdin.buffer.read(4)
            if not prefix: break
            length = struct.unpack('<i', prefix)[0]
            message = json.loads(sys.stdin.buffer.read(length))
            kind, request_id = message['kind'], message['requestId']
            if kind == 'hello':
                send(dict(kind='hello', requestId=request_id))
            elif kind == 'shutdown':
                break
            elif kind == 'cancel':
                continue
            else:
                open(marker, 'w').close()
                time.sleep(60)
        """;

    // This transport fixture deliberately ignores cancellation and shutdown on its
    // first call. Actual rendering/pixels are verified separately with Backend.Worker.
    private const string UnresponsiveWorkerProbe = """
        import base64, hashlib, json, os, struct, subprocess, sys, time
        marker, child_pid_file = sys.argv[1:]
        def send(message):
            body = json.dumps(dict(protocolVersion='worker-1', payload='', delayMilliseconds=0, cancelled=False, **message)).encode()
            sys.stdout.buffer.write(struct.pack('<i', len(body)) + body)
            sys.stdout.buffer.flush()
        while True:
            prefix = sys.stdin.buffer.read(4)
            if not prefix:
                break
            length = struct.unpack('<i', prefix)[0]
            message = json.loads(sys.stdin.buffer.read(length))
            kind, request_id = message['kind'], message['requestId']
            if kind == 'hello':
                send(dict(kind='hello', requestId=request_id))
            elif kind == 'shutdown':
                break
            elif kind == 'static-session-capabilities-v1':
                if not os.path.exists(marker):
                    open(marker, 'w').close()
                    child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    with open(child_pid_file, 'w') as output:
                        output.write(str(child.pid))
                    time.sleep(60)
                else:
                    raw = json.dumps(dict(sourceProfiles=['static-lyrics-scene-v1'])).encode()
                    response = dict(kind='static-session-capabilities-result-v1', requestId=request_id, inputSha256=message['inputSha256'], outputSha256=hashlib.sha256(raw).hexdigest())
                    body = json.dumps(dict(protocolVersion='worker-1', payload=base64.b64encode(raw).decode(), delayMilliseconds=0, cancelled=False, **response)).encode()
                    sys.stdout.buffer.write(struct.pack('<i', len(body)) + body)
                    sys.stdout.buffer.flush()
        """;

    [Fact]
    public async Task Canceled_stop_reaps_started_worker_before_propagating()
    {
        await using WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.StopAsync(cancellation.Token));
            Assert.False(IsProcessAlive(processId), $"Worker process {processId} survived canceled StopAsync.");
            Assert.Null(proxy.ProcessId);
        }
        finally
        {
            await proxy.DisposeAsync();
        }
    }

    private static WorkerProcessProxy NewProxy()
    {
        string worker = WorkerBinaryLocator.LocateBackendWorkerDll();
        Assert.True(File.Exists(worker), $"先构建 Worker：{worker}");
        return new(new WorkerProcessOptions("dotnet", [worker], TimeSpan.FromSeconds(2)));
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
