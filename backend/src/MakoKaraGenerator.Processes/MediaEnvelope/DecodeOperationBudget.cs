using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Frozen caller-owned total budget: native hard partitions plus actual live managed buffer capacity.</summary>
/// <remarks>This mechanism owns no media profile constant and makes no whole-Worker RSS claim.</remarks>
public sealed class DecodeOperationBudget
{
    /// <summary>Four pipe capacities, each verified at exactly one 4096-byte page.</summary>
    public const long RequiredPipeBackingPerProducer = 4 * 4096;
    /// <summary>Three independent 4096-byte discard buffers for terminal pipe tails.</summary>
    public const long DrainScratchPerProducer = 3 * 4096;
    /// <summary>Explicit control/stream/interop allowance per live producer, not an RSS measurement.</summary>
    public const long ControlAllowancePerProducer = 64 * 1024;
    /// <summary>Reservation held before opening transport and retained through quarantine.</summary>
    public const long TransportReservationPerProducer = RequiredPipeBackingPerProducer + DrainScratchPerProducer + ControlAllowancePerProducer;

    private readonly object _gate = new();
    private readonly IAsyncDisposable _sourceLease;
    private readonly long _managedPeak;
    private readonly long[] _nativeLimits;
    private readonly bool[] _started;
    private readonly bool[] _drained;
    private long _liveManaged;
    private int _borrows;
    private bool _releasing;
    private bool _released;

    /// <summary>Freezes a complete plan before allocation, pipes, probing or native execution.</summary>
    /// <param name="maxTotalBytes">Total ceiling from the profile-owning caller.</param>
    /// <param name="managedPeakBytes">Complete simultaneous managed capacity, including transport allowances.</param>
    /// <param name="nativeLimitBytes">Single-use native memory.max partitions.</param>
    /// <param name="maximumPartitions">Caller profile's maximum number of owned native producers.</param>
    /// <param name="transferredSourceLease">Source ownership transferred only after successful construction.</param>
    public DecodeOperationBudget(long maxTotalBytes, long managedPeakBytes, long[] nativeLimitBytes,
        int maximumPartitions, IAsyncDisposable transferredSourceLease)
    {
        ArgumentNullException.ThrowIfNull(nativeLimitBytes);
        ArgumentNullException.ThrowIfNull(transferredSourceLease);
        if (maxTotalBytes <= 0 || managedPeakBytes <= 0 || maximumPartitions <= 0
            || nativeLimitBytes.Length < 1 || nativeLimitBytes.Length > maximumPartitions || nativeLimitBytes.Any(x => x <= 0))
            throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "decode-plan-invalid");
        long total;
        try { total = nativeLimitBytes.Aggregate(managedPeakBytes, (a, b) => checked(a + b)); }
        catch (OverflowException) { throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "decode-plan-overflow"); }
        if (total > maxTotalBytes || managedPeakBytes < TransportReservationPerProducer)
            throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "decode-plan-exceeds-profile");
        PlannedBytes = total;
        _managedPeak = managedPeakBytes;
        _nativeLimits = (long[])nativeLimitBytes.Clone();
        _started = new bool[_nativeLimits.Length];
        _drained = new bool[_nativeLimits.Length];
        _sourceLease = transferredSourceLease;
    }

    /// <summary>Total frozen native limits plus managed peak.</summary>
    public long PlannedBytes { get; }
    /// <summary>Currently reserved managed capacities, including pipe/control backing.</summary>
    public long LiveManagedBytes { get { lock (_gate) return _liveManaged; } }
    /// <summary>Number of awaited consumers that currently hold borrowed memory.</summary>
    public int ActiveBorrows { get { lock (_gate) return _borrows; } }

    /// <summary>Claims one immutable native partition exactly once; only proven drain releases it.</summary>
    public NativeLease BeginNative(int partition)
    {
        lock (_gate)
        {
            if (_releasing || _released || partition < 0 || partition >= _started.Length || _started[partition])
                throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "native-partition-reuse");
            _started[partition] = true;
            return new(this, partition, _nativeLimits[partition]);
        }
    }

    /// <summary>Reserves actual array capacity before allocation. Array-pool capacity must never be guessed.</summary>
    public ManagedLease ReserveManaged(long actualCapacityBytes)
    {
        lock (_gate)
        {
            if (_releasing || _released || actualCapacityBytes <= 0 || actualCapacityBytes > _managedPeak - _liveManaged)
                throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "managed-live-capacity");
            _liveManaged += actualCapacityBytes;
            return new(this, actualCapacityBytes);
        }
    }

    /// <summary>Keeps source ownership live for the complete awaited single-flight consumer callback.</summary>
    public IDisposable BeginBorrow()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released || _releasing, this);
            _borrows++;
            return new Borrow(this);
        }
    }

    /// <summary>Releases source ownership only after every started producer drained and all managed/borrow leases ended.</summary>
    public async ValueTask ReleaseSourceAsync()
    {
        lock (_gate)
        {
            if (_released) return;
            if (_releasing || _liveManaged != 0 || _borrows != 0 || _started.Where((x, i) => x && !_drained[i]).Any())
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "decode-lease-still-active");
            _releasing = true;
        }
        try
        {
            await _sourceLease.DisposeAsync().ConfigureAwait(false);
            lock (_gate) { _released = true; _releasing = false; }
        }
        catch (Exception cause)
        {
            // Do not claim a failed source release succeeded. The source lease remains retained.
            lock (_gate) _releasing = false;
            throw EnvelopeException.WithRetainedCause(EnvelopeFailure.DrainFailed, "source-lease-release-failed", cause);
        }
    }

    /// <summary>A single-use native hard partition; callers cannot dispose it without proving drain.</summary>
    public sealed class NativeLease
    {
        private readonly DecodeOperationBudget _owner;
        private readonly int _partition;
        private bool _done;
        private int _runClaimed;
        internal NativeLease(DecodeOperationBudget owner, int partition, long bytes)
        { _owner = owner; _partition = partition; Bytes = bytes; }
        /// <summary>Exact native memory.max limit.</summary>
        public long Bytes { get; }
        /// <summary>Owning operation; keeps its source lease reachable while retained.</summary>
        public DecodeOperationBudget Operation => _owner;
        // Claim outside RunAsync's cleanup try. A rejected duplicate invocation
        // must never MarkDrained or dispose resources belonging to the first run.
        internal void ClaimRun()
        {
            if (Interlocked.CompareExchange(ref _runClaimed, 1, 0) != 0)
                throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "native-partition-already-run");
        }
        internal void MarkDrained()
        { lock (_owner._gate) { if (_done) return; _done = true; _owner._drained[_partition] = true; } }
    }

    /// <summary>Actual managed buffer capacity retained until its last consumer is finished.</summary>
    public sealed class ManagedLease : IDisposable
    {
        private readonly DecodeOperationBudget _owner;
        private bool _done;
        internal ManagedLease(DecodeOperationBudget owner, long bytes) { _owner = owner; Bytes = bytes; }
        /// <summary>Reserved capacity.</summary>
        public long Bytes { get; }
        /// <summary>Releases this capacity once.</summary>
        public void Dispose()
        { lock (_owner._gate) { if (_done) return; _done = true; _owner._liveManaged -= Bytes; } }
    }

    private sealed class Borrow(DecodeOperationBudget owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        { lock (owner._gate) { if (_done) return; _done = true; owner._borrows--; } }
    }
}
