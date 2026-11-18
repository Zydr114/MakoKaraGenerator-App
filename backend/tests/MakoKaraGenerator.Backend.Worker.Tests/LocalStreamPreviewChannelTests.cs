using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class LocalStreamPreviewChannelTests
{
    [Fact]
    public void 分片与粘包帧解析严格守卫字节预算且拒绝句柄传递()
    {
        var endpoint = new FakeStreamEndpoint();
        using var channel = new LocalStreamPreviewChannel(endpoint);
        Assert.True(channel.TryAccept(100));

        byte[] first = Encoding.UTF8.GetBytes("{\"type\":\"a\"}");
        byte[] second = Encoding.UTF8.GetBytes("{\"type\":\"b\",\"v\":2}");
        byte[] combined = new byte[4 + first.Length + 4 + second.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(combined.AsSpan(0, 4), (uint)first.Length);
        Buffer.BlockCopy(first, 0, combined, 4, first.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(combined.AsSpan(4 + first.Length, 4), (uint)second.Length);
        Buffer.BlockCopy(second, 0, combined, 8 + first.Length, second.Length);

        // 先喂入前 2 字节帧头（半包）
        endpoint.EnqueueIncoming(combined.AsSpan(0, 2));
        Assert.Null(channel.TryReceive());

        // 再喂入剩余全部（包含第一帧完整体 + 第二帧完整体）
        endpoint.EnqueueIncoming(combined.AsSpan(2));
        Assert.Equal("{\"type\":\"a\"}", Encoding.UTF8.GetString(channel.TryReceive()!));
        Assert.Equal("{\"type\":\"b\",\"v\":2}", Encoding.UTF8.GetString(channel.TryReceive()!));
        Assert.Null(channel.TryReceive());

        // 拒绝附带句柄
        using var fakeHandle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Throws<WorkerProtocolException>(() => channel.TrySend(first, fakeHandle));

        // 拒绝超过 16 KiB 的非法帧头
        byte[] oversizedHeader = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeader, GpuStreamWorkerContract.MaximumBytes + 1u);
        endpoint.EnqueueIncoming(oversizedHeader);
        Assert.Throws<WorkerProtocolException>(() => channel.TryReceive());
    }

    [Fact]
    public async Task LinuxUds流通道校验PeerPid并跑通Shm会话拆分耗时遥测()
    {
        if (!OperatingSystem.IsLinux()) return;

        string dir = Path.Combine(Path.GetTempPath(), "mkg-uds-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // 1. 错误的期望 PID 必须拒绝并抛出 WorkerGpuConsumerMismatch
            string badSock = Path.Combine(dir, "bad.sock");
            using (var mismatchChannel = new LocalStreamPreviewChannel(badSock))
            using (var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await client.ConnectAsync(new UnixDomainSocketEndPoint(badSock));
                var ex = Assert.Throws<WorkerProtocolException>(() => mismatchChannel.TryAccept(Environment.ProcessId + 99999));
                Assert.Equal("WorkerGpuConsumerMismatch", ex.Code);
            }

            // 2. 匹配当前进程 PID，跑通完整 GpuStreamSession + ShmPreviewPort 闭环
            string sockPath = Path.Combine(dir, "shm.sock");
            string handshake = new('f', 64);
            using var cpuDevice = new CpuSkiaRenderDevice();
            using var renderer = new SkiaRenderPipeline(null, null, null, null, false, cpuDevice, false);
            using var manager = new PreviewPortSessionManager(cpuDevice);
            var target = new RenderTarget { PixelWidth = 128, PixelHeight = 72 };
            PreviewPoolInfo pool = manager.InitializeOutput(
                target,
                maximumAllocationBytes: 8 * 1024 * 1024,
                new PreviewOutputOptions(PreviewTransportKind.Shm, PreviewPixelFormat.Rgba8888));

            var open = new GpuStreamWorkerOpen(
                "shm-stream", "host-1", "1", 128, 72, true, 8 * 1024 * 1024,
                Environment.ProcessId, sockPath, handshake,
                Transport: "shm", BackendApi: "cpu", PixelFormat: "rgba8888");

            var channel = new LocalStreamPreviewChannel(sockPath);
            IPreviewPort port = manager.RequireCurrentPort(target);
            await using var session = new GpuStreamSession(
                open,
                pool,
                channel,
                (timeUs, _, _) => ValueTask.FromResult(renderer.RenderSyntheticToPreview(target, timeUs, port, PreviewPixelFormat.Rgba8888)),
                synthetic: true);

            using var consumer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await consumer.ConnectAsync(new UnixDomainSocketEndPoint(sockPath));

            await SendJsonAsync(consumer, new
            {
                type = "hello",
                protocolVersion = 1,
                consumerId = "desktop-1",
                consumerEpoch = "1",
                streamId = "shm-stream",
                hostInstanceId = "host-1",
                producerEpoch = GpuStreamProtocol.Decimal(pool.ProducerEpoch),
                handshake,
                senderMonotonicNs = "1000000",
            });

            JsonElement helloAccepted = await ReadJsonAsync(consumer);
            Assert.Equal("hello-accepted", helloAccepted.GetProperty("type").GetString());
            Assert.Equal("shm", helloAccepted.GetProperty("transport").GetString());
            Assert.Equal("cpu", helloAccepted.GetProperty("api").GetString());
            Assert.Equal("rgba8888", helloAccepted.GetProperty("pixelFormat").GetString());
            Assert.Equal(pool.ShmResourceName, helloAccepted.GetProperty("shmName").GetString());
            // 单消费者连接建立后监听 socket 节点已被立即移除
            Assert.False(File.Exists(sockPath));

            JsonElement clockPing = await ReadJsonAsync(consumer);
            Assert.Equal("clock-ping", clockPing.GetProperty("type").GetString());
            string echoNs = clockPing.GetProperty("workerMonotonicNs").GetString()!;
            await SendJsonAsync(consumer, new
            {
                type = "clock-pong",
                echoWorkerMonotonicNs = echoNs,
                senderReceiveMonotonicNs = "2000000",
                senderMonotonicNs = "2001000",
            });

            JsonElement calibrated = await ReadJsonAsync(consumer);
            Assert.Equal("clock-calibrated", calibrated.GetProperty("type").GetString());

            await SendJsonAsync(consumer, new
            {
                type = "set-playback-state",
                protocolVersion = 1,
                streamId = "shm-stream",
                hostInstanceId = "host-1",
                producerEpoch = GpuStreamProtocol.Decimal(pool.ProducerEpoch),
                inputGeneration = "1",
                consumerEpoch = "1",
                controlSeq = 1L,
                mode = "paused",
                mediaTimeUs = 5000L,
                rate = 1.0,
                targetFps = 24,
                timelineResetId = "init",
                clockSample = new { mediaTimeUs = 5000L, senderMonotonicNs = "2002000" },
            });

            JsonElement applied = await ReadJsonAsync(consumer);
            Assert.Equal("control-applied", applied.GetProperty("type").GetString());

            JsonElement frame = await ReadJsonAsync(consumer);
            Assert.Equal("frame", frame.GetProperty("type").GetString());
            Assert.Equal("shm", frame.GetProperty("transport").GetString());
            Assert.Equal("cpu", frame.GetProperty("api").GetString());
            Assert.Equal("rgba8888", frame.GetProperty("pixelFormat").GetString());
            Assert.Equal(512u, frame.GetProperty("strideBytes").GetUInt32());
            Assert.Equal(JsonValueKind.Null, frame.GetProperty("diagnostics").ValueKind);
            Assert.Equal(1UL, frame.GetProperty("counters").GetProperty("cpuPixelReadbacks").GetUInt64());
            Assert.True(frame.GetProperty("timings").GetProperty("renderDurationUs").GetInt64() >= 0);
            Assert.True(frame.GetProperty("timings").GetProperty("portCaptureDurationUs").GetInt64() >= 0);

            await SendJsonAsync(consumer, new
            {
                type = "release",
                protocolVersion = 1,
                streamId = "shm-stream",
                hostInstanceId = "host-1",
                producerEpoch = GpuStreamProtocol.Decimal(pool.ProducerEpoch),
                inputGeneration = "1",
                consumerEpoch = "1",
                timelineEpoch = frame.GetProperty("timelineEpoch").GetString(),
                poolGeneration = frame.GetProperty("poolGeneration").GetString(),
                slotId = frame.GetProperty("slotId").GetUInt32(),
                slotEpoch = frame.GetProperty("slotEpoch").GetString(),
                frameSeq = frame.GetProperty("frameSeq").GetInt64(),
            });

            Assert.True(await session.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(session.ReadState().Drained);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static async Task SendJsonAsync<T>(Socket socket, T message)
    {
        byte[] body = GpuStreamWorkerContract.Encode(message);
        byte[] framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(0, 4), (uint)body.Length);
        Buffer.BlockCopy(body, 0, framed, 4, body.Length);
        await socket.SendAsync(framed, SocketFlags.None);
    }

    private static async Task<JsonElement> ReadJsonAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await ReadExactAsync(socket, header, cts.Token);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        byte[] body = new byte[len];
        await ReadExactAsync(socket, body, cts.Token);
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, token);
            if (n == 0) throw new EndOfStreamException("Unexpected EOF on UDS stream.");
            offset += n;
        }
    }

    private sealed class FakeStreamEndpoint : ILocalStreamEndpoint
    {
        private readonly Queue<byte> _incoming = new();
        public bool TryAcceptPeer(int expectedPid) => true;

        public void EnqueueIncoming(ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes) _incoming.Enqueue(b);
        }

        public int TryRead(Span<byte> destination)
        {
            int count = 0;
            while (count < destination.Length && _incoming.Count > 0)
            {
                destination[count++] = _incoming.Dequeue();
            }

            return count;
        }

        public int TryWrite(ReadOnlySpan<byte> source) => source.Length;
        public void Dispose() { }
    }
}
