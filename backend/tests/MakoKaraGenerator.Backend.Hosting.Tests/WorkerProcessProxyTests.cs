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
        WorkerMessage message = new("render", "request", WorkerProtocol.ComputeSha256(payload), payload);
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
            await WorkerProtocol.WriteAsync(new MemoryStream(), new("render", "too-large", WorkerProtocol.ComputeSha256(tooLarge), tooLarge)));
    }

    [Fact]
    public async Task Real_worker_echoes_input_and_exits_cleanly()
    {
        await using WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        WorkerInvocationResult result = await proxy.InvokeAsync(new("render", [7, 8, 9]));
        Assert.Equal([7, 8, 9], result.Payload);
        Assert.Equal(WorkerProtocol.ComputeSha256([7, 8, 9]), result.InputSha256);
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
        Assert.True(processId > 0);
    }

    [Theory]
    [InlineData(false, 255, 56, 34, 12, 255)]
    [InlineData(false, 128, 28, 17, 6, 128)]
    [InlineData(true, 255, 0, 0, 0, 0)]
    public async Task Real_worker_renders_actual_runtime_pixels_and_independent_hashes(
        bool transparent, byte alpha, byte b, byte g, byte r, byte a)
    {
        await using WorkerProcessProxy proxy = NewProxy();
        RenderFrameV1WorkerRequest request = new(64, 36, 125_000, transparent, 12, 34, 56, alpha);

        WorkerRenderFrameResult result = await proxy.InvokeRenderFrameAsync(request, TimeSpan.FromSeconds(20));

        Assert.Equal(64, result.Width);
        Assert.Equal(36, result.Height);
        Assert.Equal(64 * 4, result.Stride);
        Assert.Equal(125_000, result.TimeUs);
        Assert.Equal(RenderFrameV1WorkerResult.RequiredPixelFormat, result.PixelFormat);
        Assert.Equal(64 * 36 * 4, result.Pixels.Length);
        byte[] expected = new byte[result.Pixels.Length];
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            expected[offset] = b; expected[offset + 1] = g; expected[offset + 2] = r; expected[offset + 3] = a;
        }
        Assert.Equal(expected, result.Pixels);
        Assert.Equal(WorkerProtocol.ComputeSha256(request.Encode()), result.InputSha256);
        Assert.NotEqual(result.InputSha256, result.OutputSha256);
        Assert.Equal(WorkerProtocol.ComputeSha256(result.Pixels), result.OutputSha256);
        int processId = Assert.IsType<int>(proxy.ProcessId);
        await proxy.StopAsync();
        Assert.False(IsProcessAlive(processId));
        output.WriteLine($"Backend.Worker PID={processId}, transparent={transparent}, alpha={alpha}; StopAsync completed, process released.");
    }

    [Fact]
    public async Task Cancellation_is_acknowledged_and_worker_remains_usable()
    {
        await using WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            proxy.InvokeAsync(new("render", [1], DelayMilliseconds: 1_000), cancellation.Token));
        Assert.Equal(processId, proxy.ProcessId);
        WorkerRenderFrameResult next = await proxy.InvokeRenderFrameAsync(new(8, 4, 0, false, 2, 4, 6), TimeSpan.FromSeconds(20));
        Assert.Equal(new byte[] { 6, 4, 2, 255 }, next.Pixels[..4]);
        Assert.Equal(processId, proxy.ProcessId);
        await proxy.StopAsync();
    }

    [Fact]
    public async Task Frame_result_round_trips_input_correlation_and_output_hash()
    {
        RenderFrameV1WorkerRequest request = new(1, 1, 42, false, 1, 2, 3);
        WorkerMessage message = ValidFrameMessage(request);
        await using MemoryStream stream = new();
        await WorkerProtocol.WriteAsync(stream, message);
        stream.Position = 0;

        RenderFrameV1WorkerResult result = RenderFrameV1WorkerResult.FromMessage(request, (await WorkerProtocol.ReadAsync(stream))!);

        Assert.Equal([3, 2, 1, 255], result.Pixels);
        Assert.Equal(WorkerProtocol.ComputeSha256(request.Encode()), result.InputSha256);
        Assert.Equal(WorkerProtocol.ComputeSha256(result.Pixels), result.OutputSha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(39)]
    public void Fixed_frame_input_rejects_magic_version_flags_and_reserved_bytes(int offset)
    {
        byte[] payload = new RenderFrameV1WorkerRequest(8, 4, 0, false, 1, 2, 3).Encode();
        payload[offset] ^= 0x80;
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerRequest.Decode(payload));
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(8, -1)]
    [InlineData(8, 1921)]
    [InlineData(8, int.MaxValue)]
    [InlineData(12, 0)]
    [InlineData(12, -1)]
    [InlineData(12, 1081)]
    [InlineData(12, int.MaxValue)]
    public void Fixed_frame_input_rejects_invalid_dimensions_as_protocol_errors(int offset, int value)
    {
        byte[] payload = new RenderFrameV1WorkerRequest(8, 4, 0, false, 1, 2, 3).Encode();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), value);
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerRequest.Decode(payload));
    }

    [Fact]
    public void Fixed_frame_input_enforces_length_nonnegative_time_and_actual_4MiB_budget()
    {
        RenderFrameV1WorkerRequest limit = new(1024, 1024, long.MaxValue, false, 1, 2, 3);
        Assert.Equal(limit, RenderFrameV1WorkerRequest.Decode(limit.Encode()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderFrameV1WorkerRequest(1024, 1025, 0, false, 1, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderFrameV1WorkerRequest(1920, 1080, 0, false, 1, 2, 3));
        byte[] bytes = limit.Encode();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 1025);
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerRequest.Decode(bytes));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), -1);
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerRequest.Decode(bytes));
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerRequest.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerRequest.Decode(new byte[41]));
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("stride")]
    [InlineData("time")]
    [InlineData("format")]
    [InlineData("length")]
    [InlineData("input-sha")]
    [InlineData("output-sha")]
    [InlineData("missing-metadata")]
    [InlineData("missing-output-sha")]
    [InlineData("cancelled")]
    [InlineData("kind")]
    [InlineData("error")]
    public void Frame_result_rejects_corruption_without_integer_overflow(string field)
    {
        RenderFrameV1WorkerRequest request = new(1, 1, 42, false, 1, 2, 3);
        WorkerMessage valid = ValidFrameMessage(request);
        WorkerMessage invalid = field switch
        {
            "width" => valid with { FrameWidth = int.MaxValue },
            "height" => valid with { FrameHeight = int.MaxValue },
            "stride" => valid with { FrameStride = int.MaxValue },
            "time" => valid with { FrameTimeUs = 43 },
            "format" => valid with { FrameFormat = "rgba8888" },
            "length" => valid with { Payload = [1], OutputSha256 = WorkerProtocol.ComputeSha256([1]) },
            "input-sha" => valid with { InputSha256 = new string('0', 64) },
            "output-sha" => valid with { OutputSha256 = new string('0', 64) },
            "missing-metadata" => valid with { FrameHeight = null },
            "missing-output-sha" => valid with { OutputSha256 = null },
            "cancelled" => valid with { Cancelled = true },
            "kind" => valid with { Kind = "hello" },
            "error" => valid with { Error = "contradictory failure" },
            _ => throw new ArgumentException(field),
        };
        Assert.Throws<WorkerProtocolException>(() => RenderFrameV1WorkerResult.FromMessage(request, invalid));
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
        WorkerMessage badHash = new("render-frame-v1", "bad", new string('0', 64), new RenderFrameV1WorkerRequest(8, 4, 0, false, 1, 2, 3).Encode());
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(badHash));
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.WriteAsync(new MemoryStream(), badHash));
    }

    [Fact]
    public async Task Protocol_graph_pixel_checks_only_belong_to_session_open_requests()
    {
        byte[] payload = [1];
        WorkerMessage Message(string kind) => new(kind, "pixel-checks", WorkerProtocol.ComputeSha256(payload), payload) { GraphPixelChecks = true };
        foreach (string kind in (string[])[PackageRenderWorkerContract.OpenKind, ProjectImagePreviewWorkerContract.OpenKind, ProjectRuntimeInputWorkerContract.OpenKind])
            WorkerProtocol.Validate(Message(kind));
        foreach (string kind in (string[])[PackageRenderWorkerContract.RenderKind, WorkerExportContract.RequestKind, RuntimeValidationWorkerContract.RequestKind])
            Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(Message(kind)));
        await using MemoryStream stream = new();
        await WorkerProtocol.WriteAsync(stream, Message(ProjectRuntimeInputWorkerContract.OpenKind)); stream.Position = 0;
        Assert.True((await WorkerProtocol.ReadAsync(stream))!.GraphPixelChecks);
    }

    [Fact]
    public async Task Real_worker_rejects_malformed_frame_input_and_proxy_recovers()
    {
        await using WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        byte[] payload = new RenderFrameV1WorkerRequest(8, 4, 0, false, 1, 2, 3).Encode();
        payload[32] = 1;

        await Assert.ThrowsAsync<WorkerProtocolException>(() => proxy.InvokeAsync(new("render-frame-v1", payload)));

        Assert.False(IsProcessAlive(processId));
        WorkerRenderFrameResult result = await proxy.InvokeRenderFrameAsync(new(8, 4, 0, true, 1, 2, 3), TimeSpan.FromSeconds(20));
        Assert.All(result.Pixels, value => Assert.Equal(0, value));
        await proxy.StopAsync();
    }

    [Fact]
    public async Task Stop_cancels_an_active_call_before_reaping_the_worker()
    {
        await using WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        Task<WorkerInvocationResult> active = proxy.InvokeAsync(new("render", [1], DelayMilliseconds: 60_000));
        await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.False(IsProcessAlive(processId));
        Assert.Null(proxy.ProcessId);
    }

    [Fact]
    public async Task Concurrent_disposal_reaps_active_worker_and_rejects_queued_calls()
    {
        WorkerProcessProxy proxy = NewProxy();
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        Task<WorkerInvocationResult> active = proxy.InvokeAsync(new("render", [1], DelayMilliseconds: 60_000));
        Task<WorkerInvocationResult> queued = proxy.InvokeAsync(new("render", [2]));
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
        string marker = temp.FilePath("first-frame");
        string childPidFile = temp.FilePath("child-pid");
        await File.WriteAllTextAsync(script, UnresponsiveWorkerProbe, Encoding.UTF8);
        await using WorkerProcessProxy proxy = new(HostingTestPlatformHelpers.PythonOptions(script, [marker, childPidFile], TimeSpan.FromMilliseconds(80)));
        await proxy.StartAsync();
        int processId = Assert.IsType<int>(proxy.ProcessId);
        RenderFrameV1WorkerRequest request = new(1, 1, 42, false, 1, 2, 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.InvokeRenderFrameAsync(request, TimeSpan.FromSeconds(2)));

        Assert.True(File.Exists(childPidFile), "Probe did not enter the frame request and spawn its owned child.");
        int childId = int.Parse(await File.ReadAllTextAsync(childPidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(IsProcessAlive(processId));
        // Kill(entireProcessTree) waits for the owned leader, while the kernel may
        // finish/reap descendants just after that leader exits. Bound observation.
        Stopwatch released = Stopwatch.StartNew();
        while (IsProcessAlive(childId) && released.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        Assert.False(IsProcessAlive(childId), $"Owned child {childId} survived deadline recovery.");
        Assert.Null(proxy.ProcessId);
        WorkerRenderFrameResult next = await proxy.InvokeRenderFrameAsync(request, TimeSpan.FromSeconds(5));
        Assert.Equal([3, 2, 1, 255], next.Pixels);
        Assert.Equal(WorkerProtocol.ComputeSha256(request.Encode()), next.InputSha256);
        int restarted = Assert.IsType<int>(proxy.ProcessId);
        Assert.NotEqual(processId, restarted);
        await proxy.StopAsync();
        Assert.False(IsProcessAlive(restarted));
        output.WriteLine($"Deadline probe PID={processId}, owned child PID={childId}; both released after timeout. Restarted PID={restarted}; StopAsync completed and released.");
    }

    private static WorkerMessage ValidFrameMessage(RenderFrameV1WorkerRequest request) => new(
        "result", "frame", WorkerProtocol.ComputeSha256(request.Encode()), [3, 2, 1, 255],
        outputSha256: WorkerProtocol.ComputeSha256([3, 2, 1, 255]), frameWidth: request.Width,
        frameHeight: request.Height, frameStride: request.Width * 4, frameTimeUs: request.TimeUs,
        frameFormat: RenderFrameV1WorkerResult.RequiredPixelFormat);

    // This transport fixture deliberately ignores cancellation and shutdown on its
    // first frame. Actual rendering/pixels are verified separately with Backend.Worker.
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
            elif kind == 'render-frame-v1':
                if not os.path.exists(marker):
                    open(marker, 'w').close()
                    child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    with open(child_pid_file, 'w') as output:
                        output.write(str(child.pid))
                    time.sleep(60)
                else:
                    payload = base64.b64decode(message['payload'])
                    width, height, timestamp = struct.unpack_from('<iiq', payload, 8)
                    pixels = bytes([payload[26], payload[25], payload[24], payload[27]]) * width * height
                    response = dict(kind='result', requestId=request_id, inputSha256=message['inputSha256'], outputSha256=hashlib.sha256(pixels).hexdigest(), frameWidth=width, frameHeight=height, frameStride=width*4, frameTimeUs=timestamp, frameFormat='bgra8888-premultiplied')
                    body = json.dumps(dict(protocolVersion='worker-1', payload=base64.b64encode(pixels).decode(), delayMilliseconds=0, cancelled=False, **response)).encode()
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
        string worker = Path.Combine(FindRepositoryRoot(), "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        Assert.True(File.Exists(worker), $"先构建 Worker：{worker}");
        return new(new WorkerProcessOptions("dotnet", [worker], TimeSpan.FromSeconds(2)));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
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
