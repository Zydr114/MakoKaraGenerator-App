using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Worker;

internal sealed record GpuHello(string Type, int ProtocolVersion, string ConsumerId, string ConsumerEpoch,
    string StreamId, string HostInstanceId, string ProducerEpoch, string Handshake, string SenderMonotonicNs);
internal sealed record GpuClockPong(string Type, string EchoWorkerMonotonicNs,
    string SenderReceiveMonotonicNs, string SenderMonotonicNs);
internal sealed record GpuClockSample(long MediaTimeUs, string SenderMonotonicNs);
internal sealed record GpuControlPacket(string Type, int ProtocolVersion, string StreamId, string HostInstanceId,
    string ProducerEpoch, string InputGeneration, string ConsumerEpoch, long ControlSeq, string Mode,
    long MediaTimeUs, double Rate, int TargetFps, string TimelineResetId, GpuClockSample ClockSample);
internal sealed record GpuIdentityPacket(string Type, int ProtocolVersion, string StreamId, string HostInstanceId,
    string ProducerEpoch, string InputGeneration, string ConsumerEpoch);
internal sealed record GpuReleasePacket(string Type, int ProtocolVersion, string StreamId, string HostInstanceId,
    string ProducerEpoch, string InputGeneration, string ConsumerEpoch, string TimelineEpoch, string PoolGeneration,
    uint SlotId, string SlotEpoch, long FrameSeq);

internal static class GpuStreamProtocol
{
    internal const long MaximumSafeInteger = 9_007_199_254_740_991;
    internal static string Decimal(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    internal static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static long MonotonicNs() => checked((long)((Int128)Stopwatch.GetTimestamp() * 1_000_000_000 / Stopwatch.Frequency));
    internal static ulong CanonicalUInt64(string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) || Decimal(number) != value)
            throw Invalid();
        return number;
    }
    internal static long CanonicalClock(string value)
    {
        ulong number = CanonicalUInt64(value);
        if (number > long.MaxValue) throw Invalid();
        return (long)number;
    }
    internal static string Type(ReadOnlySpan<byte> packet)
    {
        using JsonDocument document = JsonDocument.Parse(packet.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw Invalid();
        return type.GetString() ?? throw Invalid();
    }
    internal static byte[] HelloAccepted(GpuStreamWorkerOpen open, GpuFramePoolInfo pool, GpuHello hello,
        long workerMonotonicNs, string provider) => GpuStreamWorkerContract.Encode(new
    {
        type = "hello-accepted", protocolVersion = 1, hello.ConsumerId, hello.ConsumerEpoch,
        open.StreamId, open.HostInstanceId, producerEpoch = Decimal(pool.ProducerEpoch), open.InputGeneration,
        poolGeneration = Decimal(pool.PoolGeneration), workerMonotonicNs = Decimal(workerMonotonicNs),
        echoSenderMonotonicNs = hello.SenderMonotonicNs, provider, width = pool.Width, height = pool.Height,
        slotCount = pool.SlotCount, allocationBytes = pool.AllocationBytes,
        gpu = new { name = pool.Device.Name, vendorId = pool.Device.VendorId, deviceId = pool.Device.DeviceId,
            drmRenderMajor = pool.Device.DrmRenderMajor, drmRenderMinor = pool.Device.DrmRenderMinor,
            deviceUUID = pool.Device.DeviceUuid },
    });
    internal static byte[] Frame(GpuStreamWorkerOpen open, GpuFrameDescriptor descriptor, string consumerEpoch,
        long timelineEpoch, long controlSeq, string timelineResetId, long timeUs, string provider,
        long renderStartMonotonicNs, long producerReadyMonotonicNs, GpuStreamDiagnosticSummary? diagnostics = null)
    {
        if (descriptor.Token.FrameSequence > MaximumSafeInteger || timeUs is < 0 or > MaximumSafeInteger)
            throw Invalid();
        return GpuStreamWorkerContract.Encode(new
        {
            type = "frame", protocolVersion = 1, producerPid = Environment.ProcessId,
            open.StreamId, open.HostInstanceId, producerEpoch = Decimal(descriptor.Token.ProducerEpoch),
            open.InputGeneration, timelineEpoch = Decimal(timelineEpoch), consumerEpoch,
            poolGeneration = Decimal(descriptor.Token.PoolGeneration), slotId = descriptor.Token.SlotId,
            slotEpoch = Decimal(descriptor.Token.SlotEpoch), frameSeq = (long)descriptor.Token.FrameSequence,
            controlSeq, timelineResetId, timeUs, width = descriptor.Width, height = descriptor.Height,
            renderStartMonotonicNs = Decimal(renderStartMonotonicNs), producerReadyMonotonicNs = Decimal(producerReadyMonotonicNs),
            format = "BGRA_8888", fourcc = "AR24", modifier = Decimal(descriptor.Modifier),
            planes = new[] { new { fdIndex = 0, stride = descriptor.PlaneStride,
                offset = descriptor.PlaneOffset, size = descriptor.AllocationSize } },
            allocationSize = descriptor.AllocationSize, queriedSubresourceSize = descriptor.QueriedSubresourceSize,
            origin = "top-left", alphaMode = "premultiplied", colorSpace = "srgb", sync = "queue-idle-foreign-release",
            provider, diagnostics,
            gpu = new { name = descriptor.Device.Name, vendorId = descriptor.Device.VendorId,
                deviceId = descriptor.Device.DeviceId, drmRenderMajor = descriptor.Device.DrmRenderMajor,
                drmRenderMinor = descriptor.Device.DrmRenderMinor, deviceUUID = descriptor.Device.DeviceUuid },
            counters = new { gpuBlits = descriptor.GpuBlits, cpuPixelReadbacks = descriptor.CpuPixelReadbacks,
                queueIdleWaits = descriptor.QueueIdleWaits },
        });
    }
    internal static WorkerProtocolException Invalid() => new("GPU 流私有消息无效。", "WorkerGpuStreamProtocolInvalid");
}
