namespace MakoKaraGenerator.Backend.Worker;

internal sealed class GpuClockCalibration
{
    /// <summary>一次流内最多发起的校准次数；对端一时繁忙使往返过长时重试，用尽后才回报未校准。</summary>
    internal const int MaximumAttempts = 8;
    private long? _sent;
    private int _attempts;
    internal bool Calibrated { get; private set; }
    /// <summary>上一次校准不确定度过大且还有剩余次数时为真；调用方应重新发起一次 ping。</summary>
    internal bool ShouldRetry => !Calibrated && _sent is null && _attempts < MaximumAttempts;
    internal long WorkerMinusSenderNs { get; private set; }
    internal long UncertaintyNs { get; private set; }
    internal void Begin(long workerNs) { _sent = workerNs; _attempts++; }
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
