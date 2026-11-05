using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>One bounded purpose's atomic-ledger diagnostic values at a snapshot instant.</summary>
/// <param name="Purpose">The fixed recognized owner category.</param>
/// <param name="LiveBytes">Currently held bytes.</param>
/// <param name="PeakBytes">Maximum simultaneously held bytes since ledger construction.</param>
/// <param name="LiveReservationCount">Currently held reservation count.</param>
/// <param name="PeakReservationCount">Maximum simultaneous reservation count since ledger construction.</param>
public readonly record struct MediaWorkingSetPurposeSnapshot(MediaWorkingSetPurpose Purpose, long LiveBytes,
    long PeakBytes, long LiveReservationCount, long PeakReservationCount);

/// <summary>An immutable consistent ledger snapshot, not a process RSS or managed/native allocation measurement.</summary>
public sealed class MediaWorkingSetSnapshot
{
    internal MediaWorkingSetSnapshot(long liveBytes, long peakBytes, long liveCount, long peakCount,
        MediaWorkingSetPurposeSnapshot[] purposes)
    {
        LiveBytes = liveBytes;
        PeakBytes = peakBytes;
        LiveReservationCount = liveCount;
        PeakReservationCount = peakCount;
        Purposes = Array.AsReadOnly(purposes);
    }

    /// <summary>Currently held bytes across all purposes.</summary>
    public long LiveBytes { get; }
    /// <summary>Maximum simultaneously held bytes across all purposes.</summary>
    public long PeakBytes { get; }
    /// <summary>Currently held reservations across all purposes.</summary>
    public long LiveReservationCount { get; }
    /// <summary>Maximum simultaneously held reservations across all purposes.</summary>
    public long PeakReservationCount { get; }
    /// <summary>Exactly the seven recognized purpose diagnostics, in enum declaration order.</summary>
    public IReadOnlyList<MediaWorkingSetPurposeSnapshot> Purposes { get; }
}

/// <summary>Production owner ledger that atomically admits simultaneously live media backing before allocation.</summary>
/// <remarks>Reservations represent admitted backing only. The resource owner must drain all users before Dispose;
/// neither garbage collection nor loss of a reservation reference releases a charge. Consumers still enforce their
/// own smaller operation ceilings. No source, stream, decoder, task or artifact ownership is assumed here.</remarks>
public sealed class MediaWorkingSetBudget : IMediaWorkingSetBudget
{
    private const int PurposeCount = 7;
    private readonly object _gate = new();
    private readonly long[] _liveBytes = new long[PurposeCount];
    private readonly long[] _peakBytes = new long[PurposeCount];
    private readonly long[] _liveCounts = new long[PurposeCount];
    private readonly long[] _peakCounts = new long[PurposeCount];
    private long _live, _peak, _liveCount, _peakCount;

    /// <summary>Creates one fixed shared ceiling; applicable consumers must separately enforce smaller profile limits.</summary>
    /// <param name="limitBytes">Positive bytes no greater than Core's fixed decode-working ceiling.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested ceiling is outside the fixed positive capacity range.</exception>
    public MediaWorkingSetBudget(long limitBytes)
    {
        if (limitBytes <= 0 || limitBytes > MediaAbCapacityV1.MaximumDecodeWorkingBytes)
            throw new ArgumentOutOfRangeException(nameof(limitBytes), "Working-set limit must be positive and no greater than the fixed decode-working ceiling.");
        LimitBytes = limitBytes;
    }

    /// <inheritdoc/>
    public long LimitBytes { get; }

    /// <summary>Reads currently held bytes; use GetSnapshot for mutually consistent multi-counter diagnostics.</summary>
    public long LiveBytes { get { lock (_gate) return _live; } }
    /// <summary>Reads peak simultaneous held bytes since construction.</summary>
    public long PeakBytes { get { lock (_gate) return _peak; } }

    /// <summary>Atomically reserves positive bytes under the shared ceiling without allocating the charged backing.</summary>
    /// <param name="bytes">Positive owner charge, reserved before any corresponding backing allocation.</param>
    /// <param name="purpose">One fixed recognized category.</param>
    /// <returns>An idempotently disposable reservation; its owner releases it after all backing users drain.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The byte count is nonpositive or category is not recognized.</exception>
    /// <exception cref="MediaAbException">Insufficient remaining capacity is CapacityExceeded.</exception>
    public IMediaMemoryReservation Reserve(long bytes, MediaWorkingSetPurpose purpose)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes), "Working-set reservation bytes must be strictly positive.");
        int index = PurposeIndex(purpose);
        // Allocate the handle before committing counters: allocation failure cannot leave an unreachable charge.
        var reservation = new Reservation(this, bytes, index);
        lock (_gate)
        {
            // Subtraction is safe by invariant 0 <= live <= limit. No long addition occurs before admission.
            if (bytes > LimitBytes - _live)
                throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Simultaneously live media backing exceeds the shared working-set ceiling.");
            _live += bytes;
            _liveBytes[index] += bytes;
            _liveCount++;
            _liveCounts[index]++;
            _peak = Math.Max(_peak, _live);
            _peakBytes[index] = Math.Max(_peakBytes[index], _liveBytes[index]);
            _peakCount = Math.Max(_peakCount, _liveCount);
            _peakCounts[index] = Math.Max(_peakCounts[index], _liveCounts[index]);
        }
        return reservation;
    }

    /// <summary>Returns a coherent immutable snapshot with fixed bounded per-purpose cardinality.</summary>
    /// <returns>Totals and exactly seven purpose values from the same atomic ledger instant.</returns>
    public MediaWorkingSetSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var purposes = new MediaWorkingSetPurposeSnapshot[PurposeCount];
            for (int index = 0; index < PurposeCount; index++)
                purposes[index] = new((MediaWorkingSetPurpose)index, _liveBytes[index], _peakBytes[index], _liveCounts[index], _peakCounts[index]);
            return new(_live, _peak, _liveCount, _peakCount, purposes);
        }
    }

    private void Release(long bytes, int index)
    {
        lock (_gate)
        {
            _live -= bytes;
            _liveBytes[index] -= bytes;
            _liveCount--;
            _liveCounts[index]--;
        }
    }

    private static int PurposeIndex(MediaWorkingSetPurpose purpose) => purpose switch
    {
        MediaWorkingSetPurpose.DecoderNative => 0,
        MediaWorkingSetPurpose.CanonicalPixels => 1,
        MediaWorkingSetPurpose.Conversion => 2,
        MediaWorkingSetPurpose.Metadata => 3,
        MediaWorkingSetPurpose.Builder => 4,
        MediaWorkingSetPurpose.CodecValidation => 5,
        MediaWorkingSetPurpose.ReaderCache => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), "Working-set reservation purpose is outside the fixed recognized profile."),
    };

    private sealed class Reservation(MediaWorkingSetBudget owner, long bytes, int purposeIndex) : IMediaMemoryReservation
    {
        private MediaWorkingSetBudget? _owner = owner;
        public long Bytes { get; } = bytes;

        public void Dispose()
        {
            // Exactly one disposer owns release, including concurrent or repeated calls.
            Interlocked.Exchange(ref _owner, null)?.Release(Bytes, purposeIndex);
        }
    }
}
