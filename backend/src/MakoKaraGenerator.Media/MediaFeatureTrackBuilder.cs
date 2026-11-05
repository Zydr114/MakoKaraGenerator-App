using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

internal sealed class MediaFeatureTrackBuilder : IMediaFeatureTrackBuilder
{
    // One quantized pending grid, one prefix or wire staging buffer, one exact cumulative vector.
    internal static readonly long WorkingBytes = MediaTrackLayoutV1.Cells * (long)Unsafe.SizeOf<QuantizedMediaMoments>() +
        MediaTrackLayoutV1.PrefixEntryBytes + Mft1Wire.SumCount * sizeof(ulong) + MediaMomentKernel.WorkingBufferBytes;
    private readonly MediaFeatureFileSource _source;
    private readonly IMediaTrackScratchReservation _scratch;
    private readonly IMediaWorkingSetBudget _budget;
    private readonly IMediaMomentKernel _kernel;
    private readonly Mft1IntervalValidator _intervals;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IncrementalHash _indexHash;
    private IMediaMemoryReservation? _reservation;
    private QuantizedMediaMoments[] _pending = [];
    private byte[] _buffer = [];
    private ulong[] _sums = [];
    private Stream? _intervalSpool, _gridSpool, _prefixSpool;
    private readonly long _maximumFrames;
    private PresentationFrameObservation? _observation;
    private long _lastPrefixFrame;
    private bool _terminal, _disposed;

    internal MediaFeatureTrackBuilder(MediaFeatureFileSource source, IMediaTrackScratchReservation scratch,
        IMediaWorkingSetBudget budget, IMediaMomentKernel kernel)
    {
        _source = source; _scratch = scratch; _budget = budget; _kernel = kernel;
        _intervals = new(source, MediaAbFailure.PresentationInvalid);
        Mft1Wire.CheckBudget(budget, MediaAbCapacityV1.MaximumBuilderWorkingBytes);
        if (scratch.ReservedByteBudget <= 0 || scratch.ReservedByteBudget > MediaAbCapacityV1.MaximumScratchAndCandidateBytes)
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Scratch reservation exceeds the fixed combined ceiling.");
        long low = 0, high = source.Kind == MediaSourceKind.Still ? 1 : MediaAbCapacityV1.MaximumFrames;
        while (low < high)
        {
            long middle = (low + high + 1) / 2;
            if (MediaTrackLayoutV1.GetScratchAndCandidateBytes(source.Kind, middle) <= scratch.ReservedByteBudget) low = middle;
            else high = middle - 1;
        }
        if (low == 0) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Scratch reservation cannot hold one complete candidate and its spools.");
        _maximumFrames = low;
        _reservation = budget.Reserve(WorkingBytes, MediaWorkingSetPurpose.Builder);
        try
        {
            _indexHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _pending = new QuantizedMediaMoments[MediaTrackLayoutV1.Cells];
            _buffer = new byte[MediaTrackLayoutV1.PrefixEntryBytes];
            _sums = new ulong[Mft1Wire.SumCount];
        }
        catch { ReleaseBacking(); _indexHash?.Dispose(); throw; }
    }

    public async ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken cancellationToken)
    {
        await EnterCallbackAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(); cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(frame.Observation);
            if (_observation is not null || frame.Observation.Id.Index != _intervals.Count)
                throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Frame callback violates the serial frame and interval order.");
            if (_intervals.Count >= _maximumFrames)
                throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Presentation exceeds the admitted frame or scratch capacity.");
            PresentationFrameObservation observation = frame.Observation;
            if (frame.SourceKey != _source.Key || frame.ProfileDigest != _source.Profile.Digest ||
                observation.ColorInterpretationDigest != _source.Presentation.Color.Digest ||
                observation.Width != _source.Presentation.CanonicalWidth || observation.Height != _source.Presentation.CanonicalHeight ||
                (_source.Kind == MediaSourceKind.Still ? observation.RawPts is not null :
                    observation.RawPts is null || observation.RawPts.Value.TimeBase != _source.Presentation.StreamTimeBase))
                throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Frame observation does not match the accepted source.");
            await OpenSpoolsAsync(cancellationToken).ConfigureAwait(false);
            _kernel.BuildGrid(frame, _pending, cancellationToken);
            _observation = observation; // Pixel borrow is never retained.
        }
        catch { await FailAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken cancellationToken)
    {
        await EnterCallbackAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(); cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(interval);
            if (_observation is null || interval.Id != _observation.Id ||
                (interval is PresentationInterval.Timed timed && timed.RawStart != _observation.RawPts))
                throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Interval is not paired with the pending exact frame observation.");
            _intervals.Accept(interval);
            Mft1Wire.EncodeInterval(_buffer.AsSpan(0, MediaTrackLayoutV1.IntervalBytes), interval);
            _indexHash.AppendData(_buffer, 0, MediaTrackLayoutV1.IntervalBytes);
            await _intervalSpool!.WriteAsync(_buffer.AsMemory(0, MediaTrackLayoutV1.IntervalBytes), cancellationToken).ConfigureAwait(false);
            Mft1Wire.EncodeGrid(_buffer.AsSpan(0, MediaTrackLayoutV1.FrameGridBytes), _pending);
            await _gridSpool!.WriteAsync(_buffer.AsMemory(0, MediaTrackLayoutV1.FrameGridBytes), cancellationToken).ConfigureAwait(false);
            if (interval is PresentationInterval.Timed duration)
            {
                Mft1Wire.AddGrid(_buffer.AsSpan(0, MediaTrackLayoutV1.FrameGridBytes), _sums, checked((ulong)(duration.EndUs - duration.StartUs)));
                if (_intervals.Count % MediaTrackLayoutV1.BlockFrames == 0)
                    await WritePrefixAsync(cancellationToken).ConfigureAwait(false);
            }
            _observation = null;
        }
        catch { await FailAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask<IMediaFeatureTrackWriteSource> CompleteAsync(PresentationIndexSummary presentation, CancellationToken cancellationToken)
    {
        await EnterCallbackAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(); cancellationToken.ThrowIfCancellationRequested();
            if (_observation is not null) throw new MediaAbException(MediaAbFailure.PresentationInvalid, "A pending frame has no final presentation interval.");
            Mft1Wire.ValidateSummary(_source, presentation);
            _intervals.Complete(presentation.FrameCount, presentation.VideoDurationUs);
            if (Mft1Wire.Digest(_indexHash.GetHashAndReset()) != presentation.IndexDigest)
                throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Decoder summary index hash differs from the exact emitted intervals.");
            if (_source.Kind == MediaSourceKind.Video && _lastPrefixFrame != _intervals.Count)
                await WritePrefixAsync(cancellationToken).ConfigureAwait(false);
            Mft1Layout layout = Mft1Layout.Create(_source.Kind, presentation.FrameCount);
            Mft1Wire.Require(_intervalSpool is not null && _gridSpool is not null && _prefixSpool is not null,
                "Completed builder has no section spools.");
            Mft1Wire.CheckLength(_intervalSpool!, layout.IntervalBytes);
            Mft1Wire.CheckLength(_gridSpool!, layout.GridBytes);
            Mft1Wire.CheckLength(_prefixSpool!, layout.PrefixBytes);
            // Write-source reservation is admitted while builder backing is still live.
            var result = new MediaFeatureTrackWriteSource(_source, presentation, _intervalSpool!, _gridSpool!, _prefixSpool!, _budget);
            _intervalSpool = _gridSpool = _prefixSpool = null;
            _terminal = true; _indexHash.Dispose(); ReleaseBacking();
            return result;
        }
        catch { await FailAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    private async ValueTask OpenSpoolsAsync(CancellationToken token)
    {
        if (_intervalSpool is not null) return;
        Mft1Layout maximum = Mft1Layout.Create(_source.Kind, _maximumFrames);
        _intervalSpool = await _scratch.OpenSectionAsync(MediaTrackSection.Intervals, maximum.IntervalBytes, token).ConfigureAwait(false);
        ValidateEmptySpool(_intervalSpool);
        _gridSpool = await _scratch.OpenSectionAsync(MediaTrackSection.Grids, maximum.GridBytes, token).ConfigureAwait(false);
        ValidateEmptySpool(_gridSpool);
        Mft1Wire.Require(!ReferenceEquals(_intervalSpool, _gridSpool), "Scratch sections must have independent stream cursors.");
        _prefixSpool = await _scratch.OpenSectionAsync(MediaTrackSection.Prefixes, maximum.PrefixBytes, token).ConfigureAwait(false);
        ValidateEmptySpool(_prefixSpool);
        Mft1Wire.Require(!ReferenceEquals(_intervalSpool, _prefixSpool) && !ReferenceEquals(_gridSpool, _prefixSpool),
            "Scratch sections must have independent stream cursors.");
        if (_source.Kind == MediaSourceKind.Video) await WritePrefixAsync(token).ConfigureAwait(false);
    }

    private static void ValidateEmptySpool(Stream stream) => Mft1Wire.Require(stream.CanSeek && stream.CanRead && stream.CanWrite &&
        stream.Length == 0 && stream.Position == 0, "Scratch section must be an empty seekable readable writable stream.");

    private async ValueTask WritePrefixAsync(CancellationToken token)
    {
        Mft1Wire.EncodePrefix(_buffer, _intervals.Count, _intervals.EndUs, _sums);
        await _prefixSpool!.WriteAsync(_buffer, token).ConfigureAwait(false);
        _lastPrefixFrame = _intervals.Count;
    }

    private async ValueTask EnterCallbackAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Concurrent builder callbacks are not permitted.");
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terminal) throw new InvalidOperationException("Feature builder is terminal.");
    }

    private void ReleaseBacking()
    {
        _pending = []; _buffer = []; _sums = []; _observation = null;
        _reservation?.Dispose(); _reservation = null;
    }

    private async ValueTask FailAsync()
    {
        _terminal = true; Mft1Wire.DisposeAfterFailure(_indexHash);
        await Mft1Wire.DisposeAfterFailureAsync([_intervalSpool, _gridSpool, _prefixSpool]).ConfigureAwait(false);
        _intervalSpool = _gridSpool = _prefixSpool = null;
        try { ReleaseBacking(); } catch (Exception) { /* Preserve the original callback failure or cancellation. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true; _terminal = true; _indexHash.Dispose();
            try { await Mft1Wire.DisposeAllAsync([_intervalSpool, _gridSpool, _prefixSpool]).ConfigureAwait(false); }
            finally { _intervalSpool = _gridSpool = _prefixSpool = null; ReleaseBacking(); }
        }
        finally { _gate.Release(); }
    }
}
