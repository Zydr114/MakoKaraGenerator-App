using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

// Exactly one render task, one latest control and at most three exported leases.
// The reader keeps running during GPU work and normal stop/drain.
internal sealed class GpuStreamSession : IAsyncDisposable
{
    private readonly GpuStreamWorkerOpen _open;
    private readonly GpuFramePoolInfo _pool;
    private readonly IGpuStreamChannel _channel;
    private readonly Func<long, ulong, CancellationToken, ValueTask<GpuRenderFrame>> _render;
    private readonly bool _synthetic;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GpuPlaybackControl _controls = new();
    private readonly GpuClockCalibration _clock = new();
    private readonly Dictionary<ulong, Published> _published = [];
    private readonly Task<bool> _completion;
    private int _stop;
    private GpuStreamWorkerState _state = new("opening", null, "0", "initial", "paused", null, null, false);
    private readonly long _leaseTimeoutNs;
    private int _disposed;
    private string _consumerEpoch = "1";
    private string? _failureCode;
    private bool _runtimeFailed;
    private long _barrierControlSeq;
    private GpuStreamDiagnosticSummary? _diagnostics;
    private long _timelineEpoch, _lastControlSeq, _framesSent, _framesReleased, _staleFrames;
    private GpuPlaybackState? _applied;
    private int _peakExecutingRender, _peakPendingRenderTargets, _peakOutstanding;
    private string ProducerEpoch => GpuStreamProtocol.Decimal(_pool.ProducerEpoch);
    private string Provider => _synthetic ? "synthetic-gpu" : "runtime";

    internal GpuStreamSession(GpuStreamWorkerOpen open, GpuFramePoolInfo pool, IGpuStreamChannel channel,
        Func<long, ulong, CancellationToken, ValueTask<GpuRenderFrame>> render, bool synthetic,
        TimeSpan? leaseTimeout = null)
    {
        ValidateOpen(open, pool);
        _open = open; _pool = pool; _channel = channel; _render = render; _synthetic = synthetic;
        _leaseTimeoutNs = checked((leaseTimeout ?? TimeSpan.FromSeconds(30)).Ticks * 100);
        _completion = Task.Run(RunAsync);
    }
    internal static void ValidateOpen(GpuStreamWorkerOpen open, GpuFramePoolInfo pool)
    {
        if (string.IsNullOrEmpty(open.StreamId) || open.StreamId.Length > 128 ||
            string.IsNullOrEmpty(open.HostInstanceId) || open.HostInstanceId.Length > 128 ||
            GpuStreamProtocol.CanonicalUInt64(open.InputGeneration) == 0 || open.ExpectedMainPid <= 0 ||
            string.IsNullOrEmpty(open.Handshake) || open.Handshake.Length != 64 ||
            open.Width != pool.Width || open.Height != pool.Height || pool.SlotCount != 3 ||
            pool.AllocationBytes > open.MaximumAllocationBytes || open.MaximumAllocationBytes > 64UL * 1024 * 1024)
            throw GpuStreamProtocol.Invalid();
    }
    internal GpuStreamWorkerOpened Opened => new(_open.StreamId, ProducerEpoch,
        GpuStreamProtocol.Decimal(_pool.PoolGeneration), _pool.Width, _pool.Height, (int)_pool.SlotCount,
        _pool.AllocationBytes, Environment.ProcessId, _pool.Device.VendorId, _pool.Device.DeviceId,
        _pool.Device.DrmRenderMajor, _pool.Device.DrmRenderMinor, _pool.Device.DeviceUuid);
    internal GpuStreamWorkerState ReadState() => Volatile.Read(ref _state);
    internal async Task<bool> StopAsync(CancellationToken token)
    {
        Interlocked.Exchange(ref _stop, 1);
        return await _completion.WaitAsync(token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _stop, 1);
        try { await _completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _lifetime.Cancel();
            await _completion.ConfigureAwait(false);
        }
        _lifetime.Dispose();
    }
    private sealed record Rendered(GpuRenderFrame Frame, long StartedNs, long ReadyNs,
        long ControlSeq, long TimelineEpoch, string TimelineResetId);
    private sealed class Published(Rendered rendered, GpuFrameExport exported, byte[] packet)
    {
        internal Rendered Rendered { get; } = rendered;
        internal GpuFrameExport? Exported { get; set; } = exported;
        internal byte[] Packet { get; } = packet;
        internal long ExportedNs { get; } = GpuStreamProtocol.MonotonicNs();
        internal long SentNs { get; set; }
    }
    private async Task<bool> RunAsync()
    {
        Task<Rendered>? rendering = null;
        Published? delivery = null;
        bool accepted = false, pausedDirty = false;
        long schedulingAnchor = 0, nextDeadline = 0, lastRenderTime = -1, lastRenderTimeline = -1;
        ulong attempts = 0;
        try
        {
            long openingDeadline = GpuStreamProtocol.MonotonicNs() + 30_000_000_000;
            while (!(accepted = _channel.TryAccept(_open.ExpectedMainPid)))
            {
                if (Volatile.Read(ref _stop) != 0) { UpdateState("stopped", true); return true; }
                if (GpuStreamProtocol.MonotonicNs() >= openingDeadline)
                    throw new WorkerProtocolException("GPU consumer 未在期限内连接。", "WorkerGpuConsumerTimeout");
                await Task.Delay(5, _lifetime.Token).ConfigureAwait(false);
            }
            byte[]? helloBytes = null;
            while ((helloBytes = _channel.TryReceive()) is null)
            {
                if (Volatile.Read(ref _stop) != 0) { UpdateState("stopped", true); return true; }
                if (GpuStreamProtocol.MonotonicNs() >= openingDeadline) throw GpuStreamProtocol.Invalid();
                await Task.Delay(2, _lifetime.Token).ConfigureAwait(false);
            }
            var hello = GpuStreamWorkerContract.Decode<GpuHello>(helloBytes);
            if (hello.Type != "hello" || hello.ProtocolVersion != 1 || hello.StreamId != _open.StreamId ||
                hello.HostInstanceId != _open.HostInstanceId || hello.ProducerEpoch != ProducerEpoch ||
                string.IsNullOrEmpty(hello.ConsumerId) || hello.ConsumerId.Length > 128 ||
                GpuStreamProtocol.CanonicalUInt64(hello.ConsumerEpoch) == 0 ||
                string.IsNullOrEmpty(hello.Handshake) || hello.Handshake.Length != 64 ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Handshake), Encoding.UTF8.GetBytes(_open.Handshake)))
                throw GpuStreamProtocol.Invalid();
            _ = GpuStreamProtocol.CanonicalClock(hello.SenderMonotonicNs);
            _consumerEpoch = hello.ConsumerEpoch;
            SendBytes(GpuStreamProtocol.HelloAccepted(_open, _pool, hello, GpuStreamProtocol.MonotonicNs(), Provider));
            long ping = GpuStreamProtocol.MonotonicNs(); _clock.Begin(ping);
            Send(new { type = "clock-ping", workerMonotonicNs = GpuStreamProtocol.Decimal(ping) });
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                for (int count = 0; count < 32; count++)
                {
                    byte[]? packet = _channel.TryReceive();
                    if (packet is null) break;
                    Process(packet, rendering is not null, pausedDirty);
                }
                long now = GpuStreamProtocol.MonotonicNs();
                UpdateState();
                bool stopping = Volatile.Read(ref _stop) != 0;
                if (!stopping && _controls.TryBegin(out var latest))
                {
                    if (latest!.Mode == "playing" && !_clock.Calibrated)
                        throw new WorkerProtocolException("播放时钟未建立。", "WorkerGpuClockUnavailable");
                    bool resetSchedule = _applied is null || _applied.Mode != latest.Mode ||
                        _applied.TargetFps != latest.TargetFps || _applied.Rate != latest.Rate || _applied.TimelineResetId != latest.TimelineResetId;
                    _timelineEpoch = _controls.Complete(latest);
                    _applied = latest; _lastControlSeq = latest.ControlSeq;
                    if (resetSchedule) { schedulingAnchor = nextDeadline = now; _barrierControlSeq = latest.ControlSeq; }
                    pausedDirty = latest.Mode == "paused";
                    ObservePendingRenderTarget(pausedDirty);
                    SendApplied(now);
                }
                if (rendering is { IsCompleted: true })
                {
                    Rendered? result = null;
                    try { result = await rendering.ConfigureAwait(false); }
                    catch (GpuPreviewException failure) when (failure.Code == "GpuPreviewNoFreeSlot")
                    { if (_applied?.Mode == "paused") { pausedDirty = true; ObservePendingRenderTarget(pausedDirty); } }
                    catch (Exception failure) when (RuntimeDiagnosticFailure(failure) is not null)
                    {
                        _diagnostics = GpuStreamDiagnostics.Summarize(RuntimeDiagnosticFailure(failure)!);
                        BeginRuntimeFailure("WorkerRuntimeDiagnosticError");
                        stopping = true;
                    }
                    rendering = null;
                    if (result is not null)
                    {
                        _diagnostics = GpuStreamDiagnostics.Summarize(result.Frame);
                        if (result.Frame.DiagnosticSnapshot.HasError)
                        {
                            result.Frame.Dispose();
                            BeginRuntimeFailure("WorkerRuntimeDiagnosticError");
                            stopping = true;
                        }
                        else if (stopping || result.TimelineEpoch != _timelineEpoch || result.ControlSeq < _barrierControlSeq)
                        { result.Frame.Dispose(); _staleFrames++; }
                        else
                        {
                            GpuFrameExport? exported = null;
                            try
                            {
                                exported = result.Frame.Lease.ExportForDelivery();
                                byte[] bytes = GpuStreamProtocol.Frame(_open, exported.Descriptor, _consumerEpoch,
                                    result.TimelineEpoch, result.ControlSeq, result.TimelineResetId, result.Frame.TimeUs,
                                    Provider, result.StartedNs, result.ReadyNs, _diagnostics);
                                delivery = new(result, exported, bytes);
                                _published.Add(exported.Descriptor.Token.FrameSequence, delivery);
                                _peakOutstanding = Math.Max(_peakOutstanding, _published.Count);
                            }
                            catch
                            { exported?.Dispose(); result.Frame.Dispose(); throw; }
                        }
                    }
                }
                if (delivery is not null && _channel.TrySend(delivery.Packet, delivery.Exported!.FileDescriptor))
                {
                    delivery.Exported.Dispose(); delivery.Exported = null;
                    delivery.SentNs = now; _framesSent++; delivery = null;
                }
                if (stopping && rendering is null && _published.Count == 0)
                {
                    _controls.Close();
                    if (_runtimeFailed)
                        Send(new { type = "failed", protocolVersion = 1, _open.StreamId, _open.HostInstanceId,
                            producerEpoch = ProducerEpoch, _open.InputGeneration, consumerEpoch = _consumerEpoch,
                            code = _failureCode, drained = true, diagnostics = _diagnostics });
                    else
                        Send(new { type = "stopped", protocolVersion = 1, _open.StreamId, _open.HostInstanceId,
                            producerEpoch = ProducerEpoch, _open.InputGeneration, consumerEpoch = _consumerEpoch,
                            frames = _framesSent, released = _framesReleased, drained = true });
                    UpdateState(_runtimeFailed ? "failed" : "stopped", true);
                    return true;
                }
                if (_published.Values.Any(frame => now - frame.ExportedNs > _leaseTimeoutNs))
                    throw new WorkerProtocolException("GPU consumer 未归还租约。", "WorkerGpuConsumerTimeout");
                if (!stopping && rendering is null && delivery is null && _published.Count < 3 && _applied is { } applied &&
                    ((applied.Mode == "paused" && pausedDirty) || (applied.Mode == "playing" && now >= nextDeadline)))
                {
                    long timeUs = _clock.MediaTime(applied, now);
                    long period = 1_000_000_000 / applied.TargetFps;
                    nextDeadline = schedulingAnchor + ((now - schedulingAnchor) / period + 1) * period;
                    if (applied.Mode == "paused" || lastRenderTimeline != _timelineEpoch || timeUs > lastRenderTime)
                    {
                        long capturedTimeline = _timelineEpoch, capturedControl = applied.ControlSeq;
                        string capturedReset = applied.TimelineResetId;
                        ulong attempt = checked(++attempts);
                        pausedDirty = false; lastRenderTime = timeUs; lastRenderTimeline = capturedTimeline;
                        rendering = Task.Run(async () =>
                        {
                            long started = GpuStreamProtocol.MonotonicNs();
                            GpuRenderFrame frame = await _render(timeUs, attempt, _lifetime.Token).ConfigureAwait(false);
                            return new Rendered(frame, started, GpuStreamProtocol.MonotonicNs(), capturedControl, capturedTimeline, capturedReset);
                        });
                        _peakExecutingRender = Math.Max(_peakExecutingRender, rendering is null ? 0 : 1);
                    }
                }
                await Task.Delay(2, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            _lifetime.Cancel();
            RuntimeDiagnosticSnapshot? actual = failure switch
            {
                EffectFailureException actualEffect => actualEffect.DiagnosticSnapshot,
                RenderSessionFailureException actualSession => actualSession.DiagnosticSnapshot,
                _ => null,
            };
            if (actual is not null) _diagnostics = GpuStreamDiagnostics.Summarize(actual);
            GpuStreamFailure detail = GpuStreamFailure.From(failure);
            _failureCode = detail.Code;
            if (rendering is not null)
            {
                try { (await rendering.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false)).Frame.Dispose(); }
                catch (TimeoutException)
                {
                    // A non-cooperative author is terminated by the host process budget. Any late Ready lease is cancelled.
                    _ = rendering.ContinueWith(task =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion) task.Result.Frame.Dispose();
                        else _ = task.Exception;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    _failureCode = "WorkerGpuRenderCancellationTimeout";
                }
                catch { }
            }
            foreach (Published frame in _published.Values)
            {
                try { frame.Rendered.Frame.Lease.Quarantine(); } catch { }
                frame.Exported?.Dispose();
                try { frame.Rendered.Frame.Dispose(); } catch { }
            }
            UpdateState("failed");
            if (accepted)
            {
                try { Send(new { type = "failed", protocolVersion = 1, _open.StreamId, _open.HostInstanceId,
                    producerEpoch = ProducerEpoch, _open.InputGeneration, consumerEpoch = _consumerEpoch,
                    code = _failureCode, failureType = detail.Type, failureSite = detail.Site, failureDetail = detail.Detail,
                    drained = false, diagnostics = _diagnostics }); } catch { }
            }
            return false;
        }
        finally { _controls.Close(); _channel.Dispose(); }
    }
    private static RuntimeDiagnosticSnapshot? RuntimeDiagnosticFailure(Exception failure) => failure switch
    {
        EffectFailureException { DiagnosticSnapshot: { HasError: true } actual } => actual,
        RenderSessionFailureException { DiagnosticSnapshot: { HasError: true } actual } => actual,
        _ => null,
    };
    private void BeginRuntimeFailure(string code)
    {
        _failureCode = code; _runtimeFailed = true;
        Interlocked.Exchange(ref _stop, 1);
    }
    private void Process(byte[] packet, bool executingRender, bool pendingRenderTarget)
    {
        switch (GpuStreamProtocol.Type(packet))
        {
            case "clock-pong":
                _clock.Complete(GpuStreamWorkerContract.Decode<GpuClockPong>(packet), GpuStreamProtocol.MonotonicNs());
                Send(new { type = "clock-calibrated", workerMinusSenderNs = GpuStreamProtocol.Decimal(_clock.WorkerMinusSenderNs),
                    uncertaintyNs = GpuStreamProtocol.Decimal(_clock.UncertaintyNs), calibrated = _clock.Calibrated });
                break;
            case "set-playback-state":
                var control = GpuStreamWorkerContract.Decode<GpuControlPacket>(packet);
                Identity(control.ProtocolVersion, control.StreamId, control.HostInstanceId, control.ProducerEpoch, control.InputGeneration, control.ConsumerEpoch);
                if (control.ClockSample is null || control.MediaTimeUs > GpuStreamProtocol.MaximumSafeInteger ||
                    control.ClockSample.MediaTimeUs > GpuStreamProtocol.MaximumSafeInteger) throw GpuStreamProtocol.Invalid();
                if (Volatile.Read(ref _stop) != 0) break;
                bool changed = _controls.Offer(new(control.ControlSeq, control.Mode, control.MediaTimeUs, control.Rate,
                    control.TargetFps, control.TimelineResetId, control.ClockSample.MediaTimeUs, control.ClockSample.SenderMonotonicNs));
                if (!changed && _lastControlSeq == control.ControlSeq) SendApplied(GpuStreamProtocol.MonotonicNs());
                break;
            case "release":
                var release = GpuStreamWorkerContract.Decode<GpuReleasePacket>(packet);
                Identity(release.ProtocolVersion, release.StreamId, release.HostInstanceId, release.ProducerEpoch, release.InputGeneration, release.ConsumerEpoch);
                if (release.FrameSeq < 1 || release.FrameSeq > GpuStreamProtocol.MaximumSafeInteger ||
                    !_published.TryGetValue((ulong)release.FrameSeq, out Published? frame)) throw GpuStreamProtocol.Invalid();
                GpuFrameToken token = frame.Rendered.Frame.Lease.Token;
                if (release.PoolGeneration != GpuStreamProtocol.Decimal(token.PoolGeneration) ||
                    release.SlotEpoch != GpuStreamProtocol.Decimal(token.SlotEpoch) || release.SlotId != token.SlotId ||
                    release.TimelineEpoch != GpuStreamProtocol.Decimal(frame.Rendered.TimelineEpoch) || frame.SentNs == 0)
                    throw GpuStreamProtocol.Invalid();
                frame.Rendered.Frame.Lease.ReleaseAfterConsumerCompletion(token);
                frame.Rendered.Frame.Dispose(); _published.Remove((ulong)release.FrameSeq); _framesReleased++;
                break;
            case "read-state":
            case "stop":
                var identity = GpuStreamWorkerContract.Decode<GpuIdentityPacket>(packet);
                Identity(identity.ProtocolVersion, identity.StreamId, identity.HostInstanceId, identity.ProducerEpoch, identity.InputGeneration, identity.ConsumerEpoch);
                if (identity.Type == "stop") Interlocked.Exchange(ref _stop, 1);
                else SendState(executingRender, pendingRenderTarget);
                break;
            default: throw GpuStreamProtocol.Invalid();
        }
    }
    private void Identity(int version, string stream, string host, string producer, string input, string consumer)
    {
        if (version != 1 || stream != _open.StreamId || host != _open.HostInstanceId || producer != ProducerEpoch ||
            input != _open.InputGeneration || consumer != _consumerEpoch) throw GpuStreamProtocol.Invalid();
    }
    private void SendApplied(long now) => Send(new { type = "control-applied", protocolVersion = 1,
        _open.StreamId, _open.HostInstanceId, producerEpoch = ProducerEpoch, _open.InputGeneration, consumerEpoch = _consumerEpoch,
        poolGeneration = GpuStreamProtocol.Decimal(_pool.PoolGeneration), controlSeq = _lastControlSeq,
        timelineEpoch = GpuStreamProtocol.Decimal(_timelineEpoch), timelineResetId = _applied!.TimelineResetId,
        mode = _applied.Mode, effectiveMediaTimeUs = _clock.MediaTime(_applied, now) });
    private void UpdateState(string? state = null, bool drained = false)
    {
        long? time = null;
        if (_applied is not null)
        {
            try { time = _clock.MediaTime(_applied, GpuStreamProtocol.MonotonicNs()); }
            catch (WorkerProtocolException) { /* A failed clock cannot prevent failure state publication. */ }
            catch (OverflowException) { }
        }
        Volatile.Write(ref _state, new(state ?? (Volatile.Read(ref _stop) != 0 ? "draining" : _applied?.Mode ?? "ready"),
            _lastControlSeq == 0 ? null : _lastControlSeq, GpuStreamProtocol.Decimal(_timelineEpoch),
            _applied?.TimelineResetId ?? "initial", _applied?.Mode ?? "paused", time, _failureCode, drained, _diagnostics));
    }
    private void ObservePendingRenderTarget(bool pending) =>
        _peakPendingRenderTargets = Math.Max(_peakPendingRenderTargets, pending ? 1 : 0);
    private void SendState(bool executingRender, bool pendingRenderTarget)
    {
        var controls = _controls.ReadQueueState();
        // These are Worker-local occupancy counts. A render task stays occupied until harvested;
        // pending targets describe the latest paused target, never missed playing deadlines.
        Send(new { type = "state", protocolVersion = 1, _open.StreamId, _open.HostInstanceId,
        producerEpoch = ProducerEpoch, _open.InputGeneration, consumerEpoch = _consumerEpoch,
        poolGeneration = GpuStreamProtocol.Decimal(_pool.PoolGeneration), timelineEpoch = GpuStreamProtocol.Decimal(_timelineEpoch),
        lastAppliedControlSeq = _lastControlSeq, mode = _applied?.Mode ?? "paused",
        state = Volatile.Read(ref _stop) != 0 ? "draining" : _applied?.Mode ?? "opening",
        outstanding = _published.Count, poolSize = _pool.SlotCount, slotCount = _pool.SlotCount,
        actualAllocationBytes = _pool.AllocationBytes, retainedAllocationBytes = _pool.RetainedAllocationBytes,
        executingRender = executingRender ? 1 : 0, pendingControls = controls.Pending,
        pendingRenderTargets = pendingRenderTarget && Volatile.Read(ref _stop) == 0 ? 1 : 0,
        peaks = new { executingRender = _peakExecutingRender, pendingControls = controls.PeakPending,
            pendingRenderTargets = _peakPendingRenderTargets, outstanding = _peakOutstanding },
        framesSent = _framesSent, framesReleased = _framesReleased,
        staleFrames = _staleFrames, clockCalibrated = _clock.Calibrated,
        clockUncertaintyNs = GpuStreamProtocol.Decimal(_clock.UncertaintyNs), provider = Provider, diagnostics = _diagnostics });
    }
    private void Send<T>(T packet) => SendBytes(GpuStreamWorkerContract.Encode(packet));
    private void SendBytes(byte[] packet)
    {
        if (!_channel.TrySend(packet))
            throw new WorkerProtocolException("GPU 控制回包背压超过有界窗口。", "WorkerGpuControlBackpressure");
    }
}
