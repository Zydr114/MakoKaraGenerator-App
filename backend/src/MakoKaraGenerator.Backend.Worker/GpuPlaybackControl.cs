using System.Globalization;

namespace MakoKaraGenerator.Backend.Worker;

// The control reader and render loop share this mailbox; it never queues frames.
internal sealed record GpuPlaybackState(long ControlSeq, string Mode, long MediaTimeUs,
    double Rate, int TargetFps, string TimelineResetId, long ClockMediaTimeUs, string SenderMonotonicNs);

internal sealed class GpuPlaybackControl
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private readonly object _gate = new();
    private GpuPlaybackState? _latestAccepted;
    private GpuPlaybackState? _pending;
    private GpuPlaybackState? _executing;
    private bool _closed;
    private string? _appliedResetId;
    private long _timelineEpoch;
    private int _peakPending;

    internal (int Pending, int PeakPending) ReadQueueState()
    {
        lock (_gate) return (_pending is null ? 0 : 1, _peakPending);
    }

    public bool Offer(GpuPlaybackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ControlSeq is < 1 or > MaxSafeInteger || state.Mode is not ("paused" or "playing") ||
            state.MediaTimeUs < 0 || state.ClockMediaTimeUs < 0 || !double.IsFinite(state.Rate) ||
            state.Rate <= 0 || state.Rate > 4 || state.TargetFps is < 1 or > 30 ||
            string.IsNullOrEmpty(state.TimelineResetId) || state.TimelineResetId.Length > 128 ||
            !ulong.TryParse(state.SenderMonotonicNs, NumberStyles.None, CultureInfo.InvariantCulture, out ulong ns) ||
            ns.ToString(CultureInfo.InvariantCulture) != state.SenderMonotonicNs)
            throw new ArgumentException("Invalid bounded GPU playback state.", nameof(state));
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("GPU playback control is closed.");
            if (_latestAccepted is { } last && state.ControlSeq <= last.ControlSeq)
            {
                if (state == last) return false;
                throw new InvalidOperationException("Stale or conflicting GPU playback control.");
            }
            _latestAccepted = _pending = state;
            _peakPending = Math.Max(_peakPending, _pending is null ? 0 : 1);
            return true;
        }
    }

    public bool TryBegin(out GpuPlaybackState? state)
    {
        lock (_gate)
        {
            if (_closed || _executing is not null || _pending is null) { state = null; return false; }
            state = _pending;
            _pending = null;
            _executing = state;
            return true;
        }
    }

    public long Complete(GpuPlaybackState state)
    {
        lock (_gate)
        {
            if (_executing != state) throw new InvalidOperationException("GPU control completion does not match the executing state.");
            _executing = null;
            if (_appliedResetId != state.TimelineResetId)
            {
                _timelineEpoch = checked(_timelineEpoch + 1);
                _appliedResetId = state.TimelineResetId;
            }
            return _timelineEpoch;
        }
    }

    public void Close()
    {
        lock (_gate) { _closed = true; _pending = null; }
    }
}
