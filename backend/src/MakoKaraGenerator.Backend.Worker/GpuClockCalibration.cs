namespace MakoKaraGenerator.Backend.Worker;

internal sealed class GpuClockCalibration
{
    private long? _sent;
    internal bool Calibrated { get; private set; }
    internal long WorkerMinusSenderNs { get; private set; }
    internal long UncertaintyNs { get; private set; }
    internal void Begin(long workerNs) => _sent = workerNs;
    internal void Complete(GpuClockPong packet, long receivedNs)
    {
        long sent = GpuStreamProtocol.CanonicalClock(packet.EchoWorkerMonotonicNs);
        long senderReceive = GpuStreamProtocol.CanonicalClock(packet.SenderReceiveMonotonicNs);
        long senderSend = GpuStreamProtocol.CanonicalClock(packet.SenderMonotonicNs);
        if (_sent != sent || receivedNs < sent || senderSend < senderReceive || receivedNs - sent > 1_000_000_000)
            throw GpuStreamProtocol.Invalid();
        long networkNs = (receivedNs - sent) - (senderSend - senderReceive);
        if (networkNs < 0) throw GpuStreamProtocol.Invalid();
        WorkerMinusSenderNs = checked((long)(((Int128)sent - senderReceive + receivedNs - senderSend) / 2));
        UncertaintyNs = (networkNs + 1) / 2;
        Calibrated = UncertaintyNs <= 25_000_000;
        _sent = null;
    }
    internal long MediaTime(GpuPlaybackState state, long workerNow)
    {
        if (state.Mode == "paused") return state.MediaTimeUs;
        if (!Calibrated) throw GpuStreamProtocol.Invalid();
        long sampled = checked(GpuStreamProtocol.CanonicalClock(state.SenderMonotonicNs) + WorkerMinusSenderNs);
        double elapsedUs = Math.Max(0, workerNow - sampled) / 1000d;
        double media = state.ClockMediaTimeUs + elapsedUs * state.Rate;
        if (!double.IsFinite(media) || media < 0 || media > GpuStreamProtocol.MaximumSafeInteger)
            throw GpuStreamProtocol.Invalid();
        return (long)media;
    }
}
