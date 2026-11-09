using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class GpuStreamSessionTests
{
    private static readonly GpuStreamWorkerOpen Open = new("stream", "host", "1", 16, 16, true, 1024 * 1024, 123, "/unused", new('a', 64));
    private static readonly GpuDeviceIdentity Device = new("fixture", 1, 2, 226, 128, new('0', 32));
    private static readonly GpuFramePoolInfo Pool = new(1, 2, 16, 16, 3, 12288, Device);
    private static GpuRenderFrame Frame(Lease lease, long time = 0) => new(new() { PixelWidth = 16, PixelHeight = 16 }, time, lease);

    [Fact]
    public async Task NativeStateMeasuresRenderMailboxTargetsAndActualPoolOccupancy()
    {
        using var channel = new Channel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<GpuRenderFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new GpuStreamSession(Open, Pool with { RetainedAllocationBytes = 24576 }, channel, (time, seq, _) =>
        {
            if (seq == 1) { started.SetResult(); return new(ready.Task); }
            return ValueTask.FromResult(Frame(new Lease(seq), time));
        }, false);
        channel.ReadState();
        var empty = await channel.Wait("state", 1);
        Assert.Equal(0, empty.GetProperty("executingRender").GetInt32());
        Assert.Equal(0, empty.GetProperty("pendingControls").GetInt32());
        Assert.Equal(0, empty.GetProperty("peaks").GetProperty("executingRender").GetInt32());
        Assert.Equal(3, empty.GetProperty("slotCount").GetInt32());
        Assert.Equal(Pool.AllocationBytes, empty.GetProperty("actualAllocationBytes").GetUInt64());
        Assert.Equal(24576ul, empty.GetProperty("retainedAllocationBytes").GetUInt64());

        channel.Control(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        channel.ControlAndReadState(2, "seek", 100);
        var pending = await channel.Wait("state", 2);
        Assert.Equal(1, pending.GetProperty("executingRender").GetInt32());
        Assert.Equal(1, pending.GetProperty("pendingControls").GetInt32());
        Assert.Equal(0, pending.GetProperty("outstanding").GetInt32());
        await channel.Wait("control-applied", 2);
        channel.ReadState();
        var target = await channel.Wait("state", 3);
        Assert.Equal(0, target.GetProperty("pendingControls").GetInt32());
        Assert.Equal(1, target.GetProperty("pendingRenderTargets").GetInt32());

        ready.SetResult(Frame(new Lease(1)));
        var frames = new List<JsonElement> { await channel.Wait("frame", 1) };
        for (int seq = 3; seq <= 4; seq++)
        {
            channel.Control(seq, "seek", seq * 100);
            frames.Add(await channel.Wait("frame", seq - 1));
        }
        channel.Control(5, "seek", 500);
        await channel.Wait("control-applied", 5);
        channel.ReadState();
        var full = await channel.Wait("state", 4);
        Assert.Equal(0, full.GetProperty("executingRender").GetInt32());
        Assert.Equal(0, full.GetProperty("pendingControls").GetInt32());
        Assert.Equal(1, full.GetProperty("pendingRenderTargets").GetInt32());
        Assert.Equal(3, full.GetProperty("outstanding").GetInt32());
        var peaks = full.GetProperty("peaks");
        Assert.Equal(1, peaks.GetProperty("executingRender").GetInt32());
        Assert.Equal(1, peaks.GetProperty("pendingControls").GetInt32());
        Assert.Equal(1, peaks.GetProperty("pendingRenderTargets").GetInt32());
        Assert.Equal(3, peaks.GetProperty("outstanding").GetInt32());

        channel.Release(frames[0]);
        frames.Add(await channel.Wait("frame", 4));
        channel.ReadState();
        var resumed = await channel.Wait("state", 5);
        Assert.Equal(0, resumed.GetProperty("pendingRenderTargets").GetInt32());
        Assert.Equal(3, resumed.GetProperty("outstanding").GetInt32());
        foreach (var frame in frames.Skip(1)) channel.Release(frame);
        channel.ReadState();
        var drained = await channel.Wait("state", 6);
        Assert.Equal(0, drained.GetProperty("outstanding").GetInt32());
        Assert.Equal(3, drained.GetProperty("peaks").GetProperty("outstanding").GetInt32());
        Assert.True(await stream.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task StopWaitsForExactOldTimelineRelease()
    {
        using var channel = new Channel();
        var leases = new ConcurrentQueue<Lease>();
        await using var stream = new GpuStreamSession(Open, Pool, channel, (time, seq, _) =>
        {
            var lease = new Lease(seq); leases.Enqueue(lease); return ValueTask.FromResult(Frame(lease, time));
        }, false);
        channel.Control(1, "first", 100);
        var first = await channel.Wait("frame", 1);
        channel.Control(2, "seek", 50);
        var second = await channel.Wait("frame", 2);
        Assert.Equal("1", first.GetProperty("timelineEpoch").GetString());
        Assert.Equal("2", second.GetProperty("timelineEpoch").GetString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task<bool> stopping = stream.StopAsync(timeout.Token);
        await Task.Delay(15, timeout.Token);
        Assert.False(stopping.IsCompleted);
        channel.Release(first); channel.Release(second);
        Assert.True(await stopping);
        Assert.All(leases, lease => Assert.True(lease.Released));
        Assert.True(stream.ReadState().Drained);
        Assert.Equal("stopped", stream.ReadState().State);
    }

    [Fact]
    public async Task NewControlDiscardsOldReadyAndKeepsCapturedIdentity()
    {
        using var channel = new Channel();
        var ready = new TaskCompletionSource<GpuRenderFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldLease = new Lease(1); var newLease = new Lease(2);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (time, seq, _) =>
        {
            if (seq == 1) { started.TrySetResult(); return new(ready.Task); }
            return ValueTask.FromResult(Frame(newLease, time));
        }, false);
        channel.Control(1, "first", 100);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        channel.Control(2, "seek", 50);
        await channel.Wait("control-applied", 2);
        ready.SetResult(Frame(oldLease, 100));
        var published = await channel.Wait("frame", 1);
        Assert.Equal(2, published.GetProperty("controlSeq").GetInt64());
        Assert.Equal(50, published.GetProperty("timeUs").GetInt64());
        Assert.Equal(2, published.GetProperty("frameSeq").GetInt64());
        Assert.Equal("seek", published.GetProperty("timelineResetId").GetString());
        Assert.True(oldLease.Disposed); Assert.False(oldLease.Exported);
        Assert.True(long.Parse(published.GetProperty("producerReadyMonotonicNs").GetString()!, CultureInfo.InvariantCulture) >=
            long.Parse(published.GetProperty("renderStartMonotonicNs").GetString()!, CultureInfo.InvariantCulture));
        channel.Release(published);
        Assert.True(await stream.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ClockCorrectionKeepsInflightFrameWithItsCapturedControl()
    {
        using var channel = new Channel { AutoReleaseFrames = true };
        var ready = new TaskCompletionSource<GpuRenderFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(1);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (time, seq, _) =>
        {
            if (seq == 1) { started.TrySetResult(); return new(ready.Task); }
            return ValueTask.FromResult(Frame(new Lease(seq), time));
        }, false);
        channel.Control(1, mode: "playing");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        channel.Control(2, time: 1000, mode: "playing");
        await channel.Wait("control-applied", 2);
        ready.SetResult(Frame(lease, 0));
        var frame = await channel.Wait("frame", 1);
        Assert.Equal(1, frame.GetProperty("controlSeq").GetInt64());
        Assert.Equal("1", frame.GetProperty("timelineEpoch").GetString());
        Assert.True(lease.Exported);
        Assert.True(await stream.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task PauseDiscardsInflightPlayingFrameOnSameTimeline()
    {
        using var channel = new Channel { AutoReleaseFrames = true };
        var ready = new TaskCompletionSource<GpuRenderFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(1);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (time, seq, _) =>
        {
            if (seq == 1) { started.TrySetResult(); return new(ready.Task); }
            return ValueTask.FromResult(Frame(new Lease(seq), time));
        }, false);
        channel.Control(1, mode: "playing");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        channel.Control(2, time: 100);
        await channel.Wait("control-applied", 2);
        ready.SetResult(Frame(lease));
        var frame = await channel.Wait("frame", 1);
        Assert.Equal(2, frame.GetProperty("controlSeq").GetInt64());
        Assert.Equal("1", frame.GetProperty("timelineEpoch").GetString());
        Assert.False(lease.Exported); Assert.True(lease.Disposed);
        Assert.True(await stream.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ErrorDiagnosticCancelsReadyBeforeExport()
    {
        using var channel = new Channel(); var lease = new Lease(1);
        var diagnostics = RuntimeDiagnosticSnapshot.FromArray([new(new("fixture-instance"), "artifact", "render", 0,
            new(DiagnosticSeverity.Error, "FixtureError", "author error"))]);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (_, _, _) =>
            ValueTask.FromResult(new GpuRenderFrame(new() { PixelWidth = 16, PixelHeight = 16 }, 0, lease)
            { DiagnosticSnapshot = diagnostics }), false);
        channel.Control(1);
        await channel.Wait("failed", 1);
        Assert.True(await stream.StopAsync(CancellationToken.None));
        Assert.True(stream.ReadState().Drained);
        Assert.False(lease.Exported); Assert.True(lease.Disposed);
        Assert.DoesNotContain(channel.Packets, packet => packet.GetProperty("type").GetString() == "frame");
        Assert.Equal("WorkerRuntimeDiagnosticError", stream.ReadState().FailureCode);
    }

    [Fact]
    public async Task AuthorErrorDrainsEarlierLeaseWithoutQuarantine()
    {
        using var channel = new Channel(); var first = new Lease(1); var failed = new Lease(2);
        var actual = RuntimeDiagnosticSnapshot.FromArray([new(new("fixture-instance"), "artifact", "render", 0,
            new(DiagnosticSeverity.Error, "FixtureError", "author error"))]);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (time, seq, _) => ValueTask.FromResult(
            seq == 1 ? Frame(first, time) : new GpuRenderFrame(new() { PixelWidth = 16, PixelHeight = 16 }, time, failed)
            { DiagnosticSnapshot = actual }), false);
        channel.Control(1); var frame = await channel.Wait("frame", 1);
        channel.Control(2, "seek", 100);
        await channel.Wait("control-applied", 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (stream.ReadState().FailureCode is null) await Task.Delay(2, timeout.Token);
        Assert.DoesNotContain(channel.Packets, packet => packet.GetProperty("type").GetString() == "failed");
        Assert.False(first.Released); Assert.False(first.Quarantined);
        channel.Release(frame);
        var failure = await channel.Wait("failed", 1);
        Assert.True(failure.GetProperty("drained").GetBoolean());
        Assert.Equal(1, failure.GetProperty("diagnostics").GetProperty("errorCount").GetInt32());
        Assert.True(await stream.StopAsync(timeout.Token));
        Assert.True(first.Released); Assert.False(first.Quarantined);
        Assert.False(failed.Exported); Assert.True(failed.Disposed);
        Assert.Equal("failed", stream.ReadState().State); Assert.True(stream.ReadState().Drained);
    }

    [Fact]
    public async Task PendingExportHasDeadlineAndIsQuarantined()
    {
        using var channel = new Channel { BlockFrames = true }; var lease = new Lease(1);
        await using var stream = new GpuStreamSession(Open, Pool, channel,
            (_, _, _) => ValueTask.FromResult(Frame(lease)), false, TimeSpan.FromMilliseconds(30));
        channel.Control(1);
        await channel.Wait("failed", 1);
        Assert.False(await stream.StopAsync(CancellationToken.None));
        Assert.True(lease.Exported); Assert.True(lease.Quarantined); Assert.False(lease.Released);
        Assert.True(lease.ExportedHandle!.IsClosed);
        Assert.Equal("WorkerGpuConsumerTimeout", stream.ReadState().FailureCode);
        Assert.False(stream.ReadState().Drained);
    }

    [Fact]
    public async Task HardRenderFailureReportsWrappedEngineCodeAndRootType()
    {
        // 桌面日志此前只能看到笼统 WorkerGpuStreamFailed；包装内的引擎代码、根异常类型与引擎文本必须随 failed 送出。
        using var channel = new Channel();
        await using var stream = new GpuStreamSession(Open, Pool, channel, (_, _, _) => throw new InvalidOperationException("wrapper",
            new ImageGraphException("GraphRequiredInputUnavailable", "glass-media", "background", "missing")), false);
        channel.Control(1);
        var failure = await channel.Wait("failed", 1);
        Assert.False(failure.GetProperty("drained").GetBoolean());
        Assert.Equal("GraphRequiredInputUnavailable", failure.GetProperty("code").GetString());
        Assert.Equal("ImageGraphException", failure.GetProperty("failureType").GetString());
        Assert.Contains("glass-media background", failure.GetProperty("failureDetail").GetString());
        Assert.False(await stream.StopAsync(CancellationToken.None));
        Assert.Equal("GraphRequiredInputUnavailable", stream.ReadState().FailureCode);
    }

    [Fact]
    public async Task ProtocolFailureCancelsInflightRender()
    {
        using var channel = new Channel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new GpuStreamSession(Open, Pool, channel, async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            throw new InvalidOperationException();
        }, false);
        channel.Control(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        channel.Incoming.Enqueue("{}"u8.ToArray());
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(await stream.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task PausedLatestFrameRetriesWhenNoSlotWasTemporarilyFree()
    {
        using var channel = new Channel(); int attempts = 0; var lease = new Lease(1);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (time, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new GpuPreviewException("GpuPreviewNoFreeSlot", "busy");
            return ValueTask.FromResult(Frame(lease, time));
        }, false);
        channel.Control(1, time: 500);
        var frame = await channel.Wait("frame", 1);
        Assert.Equal(500, frame.GetProperty("timeUs").GetInt64());
        Assert.Equal(2, attempts);
        channel.Release(frame);
        Assert.True(await stream.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task MismatchedReleaseFailsWithoutReturningSlotToProducer()
    {
        using var channel = new Channel(); var lease = new Lease(1);
        await using var stream = new GpuStreamSession(Open, Pool, channel, (_, _, _) => ValueTask.FromResult(Frame(lease)), false);
        channel.Control(1); var frame = await channel.Wait("frame", 1);
        channel.Release(frame, slotEpoch: "999");
        await channel.Wait("failed", 1);
        Assert.False(await stream.StopAsync(CancellationToken.None));
        Assert.True(lease.Quarantined); Assert.False(lease.Released);
    }

    private sealed class Lease(ulong sequence) : GpuFrameLease(new(1, 2, sequence, sequence, (uint)((sequence - 1) % 3)))
    {
        internal bool Exported, Released, Quarantined, Disposed;
        internal SafeFileHandle? ExportedHandle;
        public override GpuFrameExport ExportForDelivery()
        {
            if (Exported) throw new InvalidOperationException();
            Exported = true; ExportedHandle = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
            return new(new(Token, 16, 16, 64, 0, 4096, 1024, 0, Device, 1, 0, 1), ExportedHandle);
        }
        public override void ReleaseAfterConsumerCompletion(GpuFrameToken token)
        { Assert.Equal(Token, token); Assert.True(Exported); Assert.False(Released); Released = true; }
        public override void Quarantine() => Quarantined = true;
        public override void Dispose() => Disposed = true;
    }
    private sealed class Channel : IGpuStreamChannel
    {
        private readonly object _receiveGate = new();
        internal readonly ConcurrentQueue<byte[]> Incoming = new();
        internal readonly ConcurrentQueue<JsonElement> Packets = new();
        internal bool BlockFrames, AutoReleaseFrames;
        public Channel() => Incoming.Enqueue(GpuStreamWorkerContract.Encode(new
        {
            type = "hello", protocolVersion = 1, consumerId = "fixture", consumerEpoch = "1", Open.StreamId,
            Open.HostInstanceId, producerEpoch = "1", Open.Handshake, senderMonotonicNs = "1",
        }));
        public bool TryAccept(int expectedPid) => expectedPid == 123;
        public byte[]? TryReceive()
        {
            lock (_receiveGate) return Incoming.TryDequeue(out var bytes) ? bytes : null;
        }
        public bool TrySend(byte[] packet, SafeFileHandle? descriptor = null)
        {
            using var document = JsonDocument.Parse(packet);
            if (descriptor is not null && BlockFrames) return false;
            JsonElement outgoing = document.RootElement.Clone();
            Packets.Enqueue(outgoing);
            if (outgoing.GetProperty("type").GetString() == "clock-ping")
            {
                string sender = GpuStreamProtocol.Decimal(GpuStreamProtocol.MonotonicNs());
                Incoming.Enqueue(GpuStreamWorkerContract.Encode(new { type = "clock-pong",
                    echoWorkerMonotonicNs = outgoing.GetProperty("workerMonotonicNs").GetString(),
                    senderReceiveMonotonicNs = sender, senderMonotonicNs = sender }));
            }
            if (descriptor is not null && AutoReleaseFrames) Release(outgoing);
            return true;
        }
        public void Dispose() { }
        internal void ControlAndReadState(long seq, string reset, long time)
        {
            lock (_receiveGate) { Control(seq, reset, time); ReadState(); }
        }
        internal void ReadState() => Incoming.Enqueue(GpuStreamWorkerContract.Encode(new
        {
            type = "read-state", protocolVersion = 1, Open.StreamId, Open.HostInstanceId,
            producerEpoch = "1", Open.InputGeneration, consumerEpoch = "1",
        }));
        internal void Control(long seq, string reset = "initial", long time = 0, string mode = "paused") => Incoming.Enqueue(GpuStreamWorkerContract.Encode(new
        {
            type = "set-playback-state", protocolVersion = 1, Open.StreamId, Open.HostInstanceId, producerEpoch = "1",
            Open.InputGeneration, consumerEpoch = "1", controlSeq = seq, mode, mediaTimeUs = time, rate = 1,
            targetFps = 30, timelineResetId = reset, clockSample = new { mediaTimeUs = time, senderMonotonicNs = GpuStreamProtocol.Decimal(GpuStreamProtocol.MonotonicNs()) },
        }));
        internal void Release(JsonElement frame, string? slotEpoch = null) => Incoming.Enqueue(GpuStreamWorkerContract.Encode(new
        {
            type = "release", protocolVersion = 1, Open.StreamId, Open.HostInstanceId, producerEpoch = "1",
            Open.InputGeneration, consumerEpoch = "1", timelineEpoch = frame.GetProperty("timelineEpoch").GetString(),
            poolGeneration = "2", slotId = frame.GetProperty("slotId").GetUInt32(),
            slotEpoch = slotEpoch ?? frame.GetProperty("slotEpoch").GetString(), frameSeq = frame.GetProperty("frameSeq").GetInt64(),
        }));
        internal async Task<JsonElement> Wait(string type, int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var matching = Packets.Where(packet => packet.GetProperty("type").GetString() == type).ToArray();
                if (matching.Length >= count) return matching[count - 1];
                await Task.Delay(2, timeout.Token);
            }
        }
    }
}
