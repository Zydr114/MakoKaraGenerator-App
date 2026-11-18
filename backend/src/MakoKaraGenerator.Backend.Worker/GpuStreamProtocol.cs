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

    internal static PreviewPoolInfo ToPreviewPoolInfo(GpuFramePoolInfo pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return new PreviewPoolInfo(
            PreviewTransportKind.DmaBuf,
            PreviewWireConventions.ApiVulkan,
            pool.ProducerEpoch,
            pool.PoolGeneration,
            pool.Width,
            pool.Height,
            PreviewPixelFormat.Bgra8888,
            checked((uint)(pool.Width * 4)),
            pool.SlotCount,
            pool.AllocationBytes,
            PreviewDeviceIdentity.FromGpuDevice(pool.Device))
        {
            RetainedAllocationBytes = pool.RetainedAllocationBytes,
        };
    }

    internal static PreviewFrameDescriptor ToPreviewDescriptor(GpuFrameDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new PreviewFrameDescriptor(
            PreviewTransportKind.DmaBuf,
            PreviewWireConventions.ApiVulkan,
            PreviewFrameToken.FromGpuToken(descriptor.Token),
            descriptor.Width,
            descriptor.Height,
            PreviewPixelFormat.Bgra8888,
            descriptor.PlaneStride,
            descriptor.PlaneOffset,
            descriptor.AllocationSize,
            descriptor.AllocationSize,
            PreviewDeviceIdentity.FromGpuDevice(descriptor.Device),
            new PreviewFrameCounters(descriptor.GpuBlits, descriptor.CpuPixelReadbacks, descriptor.QueueIdleWaits),
            DmaBufModifier: descriptor.Modifier,
            QueriedSubresourceBytes: descriptor.QueriedSubresourceSize);
    }

    internal static byte[] HelloAccepted(GpuStreamWorkerOpen open, GpuFramePoolInfo pool, GpuHello hello,
        long workerMonotonicNs, string provider) =>
        HelloAccepted(open, ToPreviewPoolInfo(pool), hello, workerMonotonicNs, provider);

    internal static byte[] HelloAccepted(GpuStreamWorkerOpen open, PreviewPoolInfo pool, GpuHello hello,
        long workerMonotonicNs, string provider)
    {
        if (pool.TransportKind == PreviewTransportKind.Shm)
        {
            if (string.IsNullOrEmpty(pool.ShmResourceName))
                throw Invalid();
            return GpuStreamWorkerContract.Encode(new
            {
                type = "hello-accepted",
                protocolVersion = 1,
                hello.ConsumerId,
                hello.ConsumerEpoch,
                open.StreamId,
                open.HostInstanceId,
                producerEpoch = Decimal(pool.ProducerEpoch),
                open.InputGeneration,
                poolGeneration = Decimal(pool.PoolGeneration),
                workerMonotonicNs = Decimal(workerMonotonicNs),
                echoSenderMonotonicNs = hello.SenderMonotonicNs,
                provider,
                transport = pool.TransportKind.ToWireName(),
                api = pool.BackendApi,
                pixelFormat = pool.PixelFormat.ToWireName(),
                strideBytes = pool.StrideBytes,
                shmName = pool.ShmResourceName,
                width = pool.Width,
                height = pool.Height,
                slotCount = pool.SlotCount,
                allocationBytes = pool.AllocationBytes,
                device = new
                {
                    api = pool.Device.BackendApi,
                    name = pool.Device.Name,
                    vendorId = pool.Device.VendorId,
                    deviceId = pool.Device.DeviceId,
                },
            });
        }

        return GpuStreamWorkerContract.Encode(new
        {
            type = "hello-accepted",
            protocolVersion = 1,
            hello.ConsumerId,
            hello.ConsumerEpoch,
            open.StreamId,
            open.HostInstanceId,
            producerEpoch = Decimal(pool.ProducerEpoch),
            open.InputGeneration,
            poolGeneration = Decimal(pool.PoolGeneration),
            workerMonotonicNs = Decimal(workerMonotonicNs),
            echoSenderMonotonicNs = hello.SenderMonotonicNs,
            provider,
            transport = pool.TransportKind.ToWireName(),
            api = pool.BackendApi,
            pixelFormat = pool.PixelFormat.ToWireName(),
            strideBytes = pool.StrideBytes,
            width = pool.Width,
            height = pool.Height,
            slotCount = pool.SlotCount,
            allocationBytes = pool.AllocationBytes,
            gpu = new
            {
                name = pool.Device.Name,
                vendorId = pool.Device.VendorId ?? 0,
                deviceId = pool.Device.DeviceId ?? 0,
                drmRenderMajor = pool.Device.DrmRenderMajor ?? 0,
                drmRenderMinor = pool.Device.DrmRenderMinor ?? 0,
                deviceUUID = pool.Device.DeviceUuid ?? string.Empty,
            },
        });
    }

    internal static byte[] Frame(GpuStreamWorkerOpen open, GpuFrameDescriptor descriptor, string consumerEpoch,
        long timelineEpoch, long controlSeq, string timelineResetId, long timeUs, string provider,
        long renderStartMonotonicNs, long producerReadyMonotonicNs, GpuStreamDiagnosticSummary? diagnostics = null,
        PreviewFrameTimings? timings = null) =>
        Frame(open, ToPreviewDescriptor(descriptor),
            timings ?? new PreviewFrameTimings(Math.Max(0, (producerReadyMonotonicNs - renderStartMonotonicNs) / 1_000), 0),
            consumerEpoch, timelineEpoch, controlSeq, timelineResetId, timeUs, provider,
            renderStartMonotonicNs, producerReadyMonotonicNs, diagnostics);

    internal static byte[] Frame(GpuStreamWorkerOpen open, PreviewFrameDescriptor descriptor, PreviewFrameTimings timings,
        string consumerEpoch, long timelineEpoch, long controlSeq, string timelineResetId, long timeUs, string provider,
        long renderStartMonotonicNs, long producerReadyMonotonicNs, GpuStreamDiagnosticSummary? diagnostics = null)
    {
        if (descriptor.Token.FrameSequence > MaximumSafeInteger || timeUs is < 0 or > MaximumSafeInteger ||
            timings.RenderDurationUs < 0 || timings.PortCaptureDurationUs < 0)
            throw Invalid();

        if (descriptor.TransportKind == PreviewTransportKind.Shm)
        {
            return GpuStreamWorkerContract.Encode(new
            {
                type = "frame",
                protocolVersion = 1,
                producerPid = Environment.ProcessId,
                open.StreamId,
                open.HostInstanceId,
                producerEpoch = Decimal(descriptor.Token.ProducerEpoch),
                open.InputGeneration,
                timelineEpoch = Decimal(timelineEpoch),
                consumerEpoch,
                poolGeneration = Decimal(descriptor.Token.PoolGeneration),
                slotId = descriptor.Token.SlotId,
                slotEpoch = Decimal(descriptor.Token.SlotEpoch),
                frameSeq = (long)descriptor.Token.FrameSequence,
                controlSeq,
                timelineResetId,
                timeUs,
                width = descriptor.Width,
                height = descriptor.Height,
                renderStartMonotonicNs = Decimal(renderStartMonotonicNs),
                producerReadyMonotonicNs = Decimal(producerReadyMonotonicNs),
                transport = descriptor.TransportKind.ToWireName(),
                api = descriptor.BackendApi,
                pixelFormat = descriptor.PixelFormat.ToWireName(),
                strideBytes = descriptor.StrideBytes,
                slotOffsetBytes = descriptor.SlotOffsetBytes,
                slotSizeBytes = descriptor.SlotSizeBytes,
                allocationSize = descriptor.AllocationBytes,
                origin = "top-left",
                alphaMode = "premultiplied",
                colorSpace = "srgb",
                sync = "read-pixels-memory-barrier",
                provider,
                diagnostics,
                device = new
                {
                    api = descriptor.Device.BackendApi,
                    name = descriptor.Device.Name,
                    vendorId = descriptor.Device.VendorId,
                    deviceId = descriptor.Device.DeviceId,
                },
                timings = new
                {
                    renderDurationUs = timings.RenderDurationUs,
                    portCaptureDurationUs = timings.PortCaptureDurationUs,
                },
                counters = new
                {
                    gpuBlits = descriptor.Counters.GpuBlits,
                    cpuPixelReadbacks = descriptor.Counters.CpuPixelReadbacks,
                    queueIdleWaits = descriptor.Counters.QueueIdleWaits,
                },
            });
        }

        return GpuStreamWorkerContract.Encode(new
        {
            type = "frame",
            protocolVersion = 1,
            producerPid = Environment.ProcessId,
            open.StreamId,
            open.HostInstanceId,
            producerEpoch = Decimal(descriptor.Token.ProducerEpoch),
            open.InputGeneration,
            timelineEpoch = Decimal(timelineEpoch),
            consumerEpoch,
            poolGeneration = Decimal(descriptor.Token.PoolGeneration),
            slotId = descriptor.Token.SlotId,
            slotEpoch = Decimal(descriptor.Token.SlotEpoch),
            frameSeq = (long)descriptor.Token.FrameSequence,
            controlSeq,
            timelineResetId,
            timeUs,
            width = descriptor.Width,
            height = descriptor.Height,
            renderStartMonotonicNs = Decimal(renderStartMonotonicNs),
            producerReadyMonotonicNs = Decimal(producerReadyMonotonicNs),
            transport = descriptor.TransportKind.ToWireName(),
            api = descriptor.BackendApi,
            pixelFormat = descriptor.PixelFormat.ToWireName(),
            strideBytes = descriptor.StrideBytes,
            format = "BGRA_8888",
            fourcc = "AR24",
            modifier = Decimal(descriptor.DmaBufModifier ?? 0),
            planes = new[]
            {
                new
                {
                    fdIndex = 0,
                    stride = descriptor.StrideBytes,
                    offset = descriptor.SlotOffsetBytes,
                    size = descriptor.AllocationBytes,
                },
            },
            allocationSize = descriptor.AllocationBytes,
            queriedSubresourceSize = descriptor.QueriedSubresourceBytes ?? descriptor.AllocationBytes,
            origin = "top-left",
            alphaMode = "premultiplied",
            colorSpace = "srgb",
            sync = "queue-idle-foreign-release",
            provider,
            diagnostics,
            gpu = new
            {
                name = descriptor.Device.Name,
                vendorId = descriptor.Device.VendorId ?? 0,
                deviceId = descriptor.Device.DeviceId ?? 0,
                drmRenderMajor = descriptor.Device.DrmRenderMajor ?? 0,
                drmRenderMinor = descriptor.Device.DrmRenderMinor ?? 0,
                deviceUUID = descriptor.Device.DeviceUuid ?? string.Empty,
            },
            timings = new
            {
                renderDurationUs = timings.RenderDurationUs,
                portCaptureDurationUs = timings.PortCaptureDurationUs,
            },
            counters = new
            {
                gpuBlits = descriptor.Counters.GpuBlits,
                cpuPixelReadbacks = descriptor.Counters.CpuPixelReadbacks,
                queueIdleWaits = descriptor.Counters.QueueIdleWaits,
            },
        });
    }

    internal static WorkerProtocolException Invalid() => new("GPU 流私有消息无效。", "WorkerGpuStreamProtocolInvalid");
}
