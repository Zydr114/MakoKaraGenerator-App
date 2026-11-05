using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

internal sealed class Mft1ReaderBacking
{
    internal byte[] Copy { get; private set; } = new byte[Mft1Wire.CopyBufferBytes];
    internal byte[] Interval { get; private set; } = new byte[MediaTrackLayoutV1.IntervalBytes];
    internal byte[] Grid { get; private set; } = new byte[MediaTrackLayoutV1.FrameGridBytes];
    internal byte[] PrefixA { get; private set; } = new byte[MediaTrackLayoutV1.PrefixEntryBytes];
    internal byte[] PrefixB { get; private set; } = new byte[MediaTrackLayoutV1.PrefixEntryBytes];
    internal ulong[] Sums { get; private set; } = new ulong[Mft1Wire.SumCount];
    internal void Release() { Copy = []; Interval = []; Grid = []; PrefixA = []; PrefixB = []; Sums = []; }
}

internal sealed class MediaFeatureTrackReader : IMediaFeatureTrackReader
{
    internal const long WorkingBytes = Mft1Wire.CopyBufferBytes + MediaTrackLayoutV1.IntervalBytes +
        MediaTrackLayoutV1.FrameGridBytes + 2L * MediaTrackLayoutV1.PrefixEntryBytes +
        Mft1Wire.SumCount * sizeof(ulong) + 256;
    private readonly Stream _input;
    private readonly MediaSourceKind _kind;
    private readonly Mft1Layout _layout;
    private readonly Mft1ReaderBacking _backing;
    private readonly IMediaMemoryReservation _reservation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal MediaFeatureTrackReader(Stream input, MediaSourceKind kind, MediaArtifactIdentity artifact,
        Mft1Layout layout, Mft1ReaderBacking backing, IMediaMemoryReservation reservation)
    {
        _input = input; _kind = kind; Artifact = artifact; _layout = layout; _backing = backing; _reservation = reservation;
        Summary = new(artifact.SourceKey, artifact.ProfileDigest, artifact.SourceDescriptorDigest,
            artifact.PresentationIndexDigest, artifact.FrameCount, artifact.VideoDurationUs);
    }

    public MediaArtifactIdentity Artifact { get; }
    public PresentationIndexSummary Summary { get; }

    public async ValueTask<PresentationInterval> ReadIntervalAsync(PresentationFrameId id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            if (id.Index < 0 || id.Index >= _layout.FrameCount) throw new ArgumentOutOfRangeException(nameof(id));
            PresentationInterval interval = await ReadIntervalCoreAsync(id.Index, synchronous: false, cancellationToken).ConfigureAwait(false);
            Mft1Wire.CheckLength(_input, _layout.Length); return interval;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PresentationFrameId> SelectAsync(long sourceTimeUs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            if (sourceTimeUs < 0 || (_kind == MediaSourceKind.Video && sourceTimeUs >= Summary.VideoDurationUs))
                throw new ArgumentOutOfRangeException(nameof(sourceTimeUs));
            long ordinal = _kind == MediaSourceKind.Still ? 0 : await SelectCoreAsync(sourceTimeUs, synchronous: false, cancellationToken).ConfigureAwait(false);
            Mft1Wire.CheckLength(_input, _layout.Length); return new(ordinal);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ReadTemporalGridAsync(MediaTemporalQuery query, Memory<MediaMomentValues> destination,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query, destination.Length);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReadTemporalGridCoreAsync(query, destination, synchronous: false, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void ReadTemporalGrid(MediaTemporalQuery query, Memory<MediaMomentValues> destination,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query, destination.Length);
        _gate.Wait(cancellationToken);
        try
        {
            // The shared numerical algorithm uses only direct bounded Stream.Read in this mode.
            // Every ValueTask is already complete; this never waits for asynchronous I/O or a continuation.
            ValueTask completed = ReadTemporalGridCoreAsync(query, destination, synchronous: true, cancellationToken);
            if (!completed.IsCompleted) throw new InvalidOperationException("Synchronous track reads cannot defer completion.");
            completed.GetAwaiter().GetResult();
        }
        finally { _gate.Release(); }
    }

    private static void ValidateQuery(MediaTemporalQuery query, int destinationLength)
    {
        if (destinationLength != MediaTrackLayoutV1.Cells) throw new ArgumentException("Temporal destination must contain exactly 576 cells.", nameof(destinationLength));
        if (query.WindowUs < 0 || query.WindowUs > MediaAbCapacityV1.MaximumWindowUs) throw new ArgumentOutOfRangeException(nameof(query));
        if (query.Playback is not (BackgroundPlaybackMode.Loop or BackgroundPlaybackMode.FreezeLastFrame)) throw new ArgumentOutOfRangeException(nameof(query));
    }

    private async ValueTask ReadTemporalGridCoreAsync(MediaTemporalQuery query, Memory<MediaMomentValues> destination,
        bool synchronous, CancellationToken cancellationToken)
    {
        EnsureActive(); cancellationToken.ThrowIfCancellationRequested(); Array.Clear(_backing.Sums);
        if (_kind == MediaSourceKind.Still)
        {
            await AddFrameAsync(0, 1, synchronous, cancellationToken).ConfigureAwait(false);
            Mft1Wire.CheckLength(_input, _layout.Length);
            Mft1Wire.Normalize(_backing.Sums, MediaTrackLayoutV1.QuantMax, destination.Span); return;
        }
        long duration = Summary.VideoDurationUs!.Value;
        if (query.WindowUs == 0)
        {
            long mapped = BackgroundPlayback.ResolveSourceTimeUs(query.ProjectTimeUs, duration, query.Playback);
            long frame = await SelectCoreAsync(mapped, synchronous, cancellationToken).ConfigureAwait(false);
            await AddFrameAsync(frame, 1, synchronous, cancellationToken).ConfigureAwait(false);
            Mft1Wire.CheckLength(_input, _layout.Length);
            Mft1Wire.Normalize(_backing.Sums, MediaTrackLayoutV1.QuantMax, destination.Span); return;
        }

        // Widen before subtracting so long.MinValue remains a valid engineering time.
        Int128 end = query.ProjectTimeUs, start = end - query.WindowUs;
        Int128 negativeEnd = Int128.Min(end, 0);
        if (start < negativeEnd)
            await AddFrameAsync(0, checked((ulong)(negativeEnd - start)), synchronous, cancellationToken).ConfigureAwait(false);
        Int128 positiveStart = Int128.Max(start, 0);
        if (end > positiveStart)
        {
            if (query.Playback == BackgroundPlaybackMode.FreezeLastFrame)
            {
                Int128 insideEnd = Int128.Min(end, duration);
                if (positiveStart < insideEnd)
                    await IntegrateSourceRangeAsync((long)positiveStart, (long)insideEnd, synchronous, cancellationToken).ConfigureAwait(false);
                Int128 tailStart = Int128.Max(positiveStart, duration);
                if (tailStart < end)
                    await AddFrameAsync(_layout.FrameCount - 1, checked((ulong)(end - tailStart)), synchronous, cancellationToken).ConfigureAwait(false);
            }
            else
                await IntegrateLoopRangeAsync(positiveStart, end, duration, synchronous, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested(); Mft1Wire.CheckLength(_input, _layout.Length);
        Mft1Wire.Normalize(_backing.Sums, query.WindowUs * (double)MediaTrackLayoutV1.QuantMax, destination.Span);
    }

    private async ValueTask IntegrateLoopRangeAsync(Int128 start, Int128 end, long duration, bool synchronous, CancellationToken token)
    {
        long remaining = checked((long)(end - start));
        long phase = (long)(start % duration);
        long first = Math.Min(remaining, duration - phase);
        if (first > 0) await IntegrateSourceRangeAsync(phase, phase + first, synchronous, token).ConfigureAwait(false);
        remaining -= first;
        long cycles = remaining / duration;
        if (cycles > 0)
        {
            await ReadPrefixAsync(_layout.PrefixCount - 1, _backing.PrefixA, synchronous, token).ConfigureAwait(false);
            for (int i = 0; i < _backing.Sums.Length; i++)
                _backing.Sums[i] = checked(_backing.Sums[i] + Mft1Wire.R64(_backing.PrefixA, 16 + i * 8) * (ulong)cycles);
            remaining %= duration;
        }
        if (remaining > 0) await IntegrateSourceRangeAsync(0, remaining, synchronous, token).ConfigureAwait(false);
    }

    private async ValueTask IntegrateSourceRangeAsync(long startUs, long endUs, bool synchronous, CancellationToken token)
    {
        if (startUs == endUs) return;
        long first = await SelectCoreAsync(startUs, synchronous, token).ConfigureAwait(false);
        long last = await SelectCoreAsync(endUs - 1, synchronous, token).ConfigureAwait(false);
        long fullStart = checked((first / MediaTrackLayoutV1.BlockFrames + 1) * MediaTrackLayoutV1.BlockFrames);
        long fullEnd = last / MediaTrackLayoutV1.BlockFrames * MediaTrackLayoutV1.BlockFrames;
        if (fullStart > fullEnd)
        {
            await ScanFramesAsync(first, last + 1, startUs, endUs, synchronous, token).ConfigureAwait(false); return;
        }
        // At most 64 boundary frames on either side. Each grid is read once for all seven moments.
        await ScanFramesAsync(first, fullStart, startUs, endUs, synchronous, token).ConfigureAwait(false);
        if (fullStart < fullEnd)
        {
            await ReadPrefixAsync(fullStart / MediaTrackLayoutV1.BlockFrames, _backing.PrefixA, synchronous, token).ConfigureAwait(false);
            await ReadPrefixAsync(fullEnd / MediaTrackLayoutV1.BlockFrames, _backing.PrefixB, synchronous, token).ConfigureAwait(false);
            for (int i = 0; i < _backing.Sums.Length; i++)
            {
                ulong before = Mft1Wire.R64(_backing.PrefixA, 16 + i * 8), after = Mft1Wire.R64(_backing.PrefixB, 16 + i * 8);
                Mft1Wire.Require(after >= before, "Immutable prefix sums changed after validation.");
                _backing.Sums[i] = checked(_backing.Sums[i] + after - before);
            }
        }
        await ScanFramesAsync(fullEnd, last + 1, startUs, endUs, synchronous, token).ConfigureAwait(false);
    }

    private async ValueTask ScanFramesAsync(long first, long exclusiveEnd, long startUs, long endUs, bool synchronous, CancellationToken token)
    {
        for (long frame = first; frame < exclusiveEnd; frame++)
        {
            var interval = (PresentationInterval.Timed)await ReadIntervalCoreAsync(frame, synchronous, token).ConfigureAwait(false);
            long duration = Math.Min(interval.EndUs, endUs) - Math.Max(interval.StartUs, startUs);
            if (duration > 0) await AddFrameAsync(frame, (ulong)duration, synchronous, token).ConfigureAwait(false);
        }
    }

    private async ValueTask AddFrameAsync(long ordinal, ulong duration, bool synchronous, CancellationToken token)
    {
        await ReadAtAsync(_layout.GridOffset + ordinal * MediaTrackLayoutV1.FrameGridBytes, _backing.Grid, synchronous, token).ConfigureAwait(false);
        Mft1Wire.AddGrid(_backing.Grid, _backing.Sums, duration);
    }

    private ValueTask ReadPrefixAsync(long ordinal, byte[] destination, bool synchronous, CancellationToken token) =>
        ReadAtAsync(_layout.PrefixOffset + ordinal * MediaTrackLayoutV1.PrefixEntryBytes, destination, synchronous, token);

    private async ValueTask<long> SelectCoreAsync(long sourceTimeUs, bool synchronous, CancellationToken token)
    {
        long low = 0, high = _layout.FrameCount - 1;
        while (low < high)
        {
            long middle = low + (high - low + 1) / 2;
            var interval = (PresentationInterval.Timed)await ReadIntervalCoreAsync(middle, synchronous, token).ConfigureAwait(false);
            if (interval.StartUs <= sourceTimeUs) low = middle; else high = middle - 1;
        }
        return low;
    }

    private async ValueTask<PresentationInterval> ReadIntervalCoreAsync(long ordinal, bool synchronous, CancellationToken token)
    {
        await ReadAtAsync(MediaTrackLayoutV1.HeaderBytes + ordinal * MediaTrackLayoutV1.IntervalBytes,
            _backing.Interval, synchronous, token).ConfigureAwait(false);
        return Mft1Wire.DecodeInterval(_backing.Interval, _kind, ordinal);
    }

    private ValueTask ReadAtAsync(long offset, Memory<byte> destination, bool synchronous, CancellationToken token)
    {
        if (!synchronous) return Mft1Wire.ReadAtAsync(_input, offset, destination, token);
        token.ThrowIfCancellationRequested();
        _input.Position = offset;
        while (!destination.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            int read = _input.Read(destination.Span);
            if (read == 0) throw Mft1Wire.Invalid("Artifact ended before its complete canonical section.");
            destination = destination[read..];
        }
        token.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); Mft1Wire.CheckLength(_input, _layout.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true;
            try { await _input.DisposeAsync().ConfigureAwait(false); }
            finally { _backing.Release(); _reservation.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}
