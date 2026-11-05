using System.Buffers.Binary;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

internal readonly record struct Mft1Layout(long FrameCount, long IntervalBytes, long GridOffset,
    long GridBytes, long PrefixOffset, long PrefixBytes, long PrefixCount, long Length)
{
    internal static Mft1Layout Create(MediaSourceKind kind, long count)
    {
        long length = MediaTrackLayoutV1.GetArtifactBytes(kind, count);
        long prefixes = MediaTrackLayoutV1.GetPrefixCount(kind, count);
        long intervals = checked(count * MediaTrackLayoutV1.IntervalBytes);
        long grids = checked(count * MediaTrackLayoutV1.FrameGridBytes);
        long gridOffset = checked(MediaTrackLayoutV1.HeaderBytes + intervals);
        return new(count, intervals, gridOffset, grids, checked(gridOffset + grids),
            checked(prefixes * MediaTrackLayoutV1.PrefixEntryBytes), prefixes, length);
    }
}

internal static class Mft1Wire
{
    internal const int SumCount = MediaTrackLayoutV1.Cells * MediaTrackLayoutV1.Moments;
    internal const int CopyBufferBytes = 64 * 1024;

    internal static MediaAbException Invalid(string detail) => new(MediaAbFailure.ArtifactInvalid, detail);

    internal static void Require(bool accepted, string detail)
    {
        if (!accepted) throw Invalid(detail);
    }

    internal static void CheckBudget(IMediaWorkingSetBudget budget, long ceiling)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.LimitBytes <= 0 || budget.LimitBytes > ceiling)
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Working set budget exceeds the fixed operation ceiling.");
    }

    internal static void ValidateSummary(MediaFeatureFileSource source, PresentationIndexSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.SourceKey != source.Key || summary.ProfileDigest != source.Profile.Digest ||
            summary.SourceDescriptorDigest != source.Presentation.Digest)
            throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Presentation summary does not bind the accepted source.");
        _ = Mft1Layout.Create(source.Kind, summary.FrameCount);
        if ((source.Kind == MediaSourceKind.Still && summary.VideoDurationUs is not null) ||
            (source.Kind == MediaSourceKind.Video && (summary.VideoDurationUs is null or <= 0 ||
                summary.VideoDurationUs < summary.FrameCount || summary.VideoDurationUs > MediaAbCapacityV1.MaximumVideoDurationUs)))
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Presentation summary duration is invalid.");
        ValidateDigest(summary.IndexDigest);
    }

    private static void ValidateDigest(MediaDigest digest)
    {
        string? hex = digest.LowerHex;
        Require(hex is { Length: 64 } && hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Digest encoding is not canonical lowercase SHA256.");
    }

    internal static MediaDigest Digest(ReadOnlySpan<byte> hash) => new(Convert.ToHexStringLower(hash));

    internal static void EncodeHeader(Span<byte> bytes, MediaFeatureFileSource source,
        PresentationIndexSummary summary, Mft1Layout layout)
    {
        bytes.Clear();
        "MKMFTRK1"u8.CopyTo(bytes);
        U32(bytes, 8, 1); U32(bytes, 12, MediaTrackLayoutV1.HeaderBytes);
        U32(bytes, 16, checked((uint)source.Kind));
        U16(bytes, 24, MediaTrackLayoutV1.Columns); U16(bytes, 26, MediaTrackLayoutV1.Rows);
        U16(bytes, 28, MediaTrackLayoutV1.CellBytes); U16(bytes, 30, MediaTrackLayoutV1.Moments);
        U32(bytes, 32, MediaTrackLayoutV1.QuantMax); U32(bytes, 36, MediaTrackLayoutV1.BlockFrames);
        U64(bytes, 40, summary.FrameCount); U64(bytes, 48, summary.VideoDurationUs ?? 0);
        U64(bytes, 56, MediaTrackLayoutV1.HeaderBytes); U64(bytes, 64, layout.IntervalBytes);
        U64(bytes, 72, layout.GridOffset); U64(bytes, 80, layout.GridBytes);
        U64(bytes, 88, layout.PrefixOffset); U64(bytes, 96, layout.PrefixBytes);
        U64(bytes, 104, layout.PrefixCount);
        Convert.FromHexString(source.Key.Digest.LowerHex).CopyTo(bytes[112..144]);
        Convert.FromHexString(source.Profile.Digest.LowerHex).CopyTo(bytes[144..176]);
        Convert.FromHexString(summary.IndexDigest.LowerHex).CopyTo(bytes[176..208]);
        Convert.FromHexString(source.Presentation.Digest.LowerHex).CopyTo(bytes[208..240]);
    }

    internal static void ValidateHeader(ReadOnlySpan<byte> bytes, MediaFeatureFileSource source,
        MediaArtifactIdentity expected, Mft1Layout layout)
    {
        Require(bytes.Length == MediaTrackLayoutV1.HeaderBytes && bytes[..8].SequenceEqual("MKMFTRK1"u8), "Artifact magic is invalid.");
        Require(R32(bytes, 8) == 1 && R32(bytes, 12) == MediaTrackLayoutV1.HeaderBytes &&
            R32(bytes, 16) == (uint)source.Kind && R32(bytes, 20) == 0, "Artifact version or flags are invalid.");
        Require(R16(bytes, 24) == MediaTrackLayoutV1.Columns && R16(bytes, 26) == MediaTrackLayoutV1.Rows &&
            R16(bytes, 28) == MediaTrackLayoutV1.CellBytes && R16(bytes, 30) == MediaTrackLayoutV1.Moments &&
            R32(bytes, 32) == MediaTrackLayoutV1.QuantMax && R32(bytes, 36) == MediaTrackLayoutV1.BlockFrames,
            "Artifact grid layout is invalid.");
        Require(R64(bytes, 40) == (ulong)expected.FrameCount && R64(bytes, 48) == (ulong)(expected.VideoDurationUs ?? 0) &&
            R64(bytes, 56) == MediaTrackLayoutV1.HeaderBytes && R64(bytes, 64) == (ulong)layout.IntervalBytes &&
            R64(bytes, 72) == (ulong)layout.GridOffset && R64(bytes, 80) == (ulong)layout.GridBytes &&
            R64(bytes, 88) == (ulong)layout.PrefixOffset && R64(bytes, 96) == (ulong)layout.PrefixBytes &&
            R64(bytes, 104) == (ulong)layout.PrefixCount, "Artifact sections are not the exact contiguous layout.");
        Require(Digest(bytes[112..144]) == source.Key.Digest && Digest(bytes[144..176]) == source.Profile.Digest &&
            Digest(bytes[176..208]) == expected.PresentationIndexDigest && Digest(bytes[208..240]) == source.Presentation.Digest,
            "Artifact header identities do not match the accepted identity.");
        Require(bytes[240..].IndexOfAnyExcept((byte)0) < 0, "Artifact header reserved bytes are nonzero.");
    }

    internal static void EncodeInterval(Span<byte> bytes, PresentationInterval interval)
    {
        bytes.Clear();
        if (interval is PresentationInterval.Timeless) { U32(bytes, 48, (uint)PresentationEndAuthority.Timeless); return; }
        var timed = (PresentationInterval.Timed)interval;
        WriteTimestamp(bytes, 0, timed.RawStart); WriteTimestamp(bytes, 16, timed.RawEnd);
        U64(bytes, 32, timed.StartUs); U64(bytes, 40, timed.EndUs); U32(bytes, 48, (uint)timed.EndAuthority);
    }

    internal static PresentationInterval DecodeInterval(ReadOnlySpan<byte> bytes, MediaSourceKind kind, long ordinal)
    {
        Require(R32(bytes, 52) == 0 && R64(bytes, 56) == 0, "Interval reserved fields are nonzero.");
        var id = new PresentationFrameId(ordinal);
        if (kind == MediaSourceKind.Still)
        {
            Require(bytes[..48].IndexOfAnyExcept((byte)0) < 0 && R32(bytes, 48) == (uint)PresentationEndAuthority.Timeless,
                "Still interval is not the unique timeless encoding.");
            return new PresentationInterval.Timeless(id);
        }
        try
        {
            Require(R64(bytes, 32) <= long.MaxValue && R64(bytes, 40) <= long.MaxValue, "Interval microseconds overflow signed time.");
            return new PresentationInterval.Timed(id, ReadTimestamp(bytes, 0), ReadTimestamp(bytes, 16),
                (long)R64(bytes, 32), (long)R64(bytes, 40), (PresentationEndAuthority)R32(bytes, 48));
        }
        catch (MediaAbException ex) when (ex.Failure == MediaAbFailure.PresentationInvalid)
        {
            throw Invalid("Interval rational time base is not canonical.");
        }
    }

    internal static void EncodeGrid(Span<byte> bytes, ReadOnlySpan<QuantizedMediaMoments> cells)
    {
        bytes.Clear();
        for (int i = 0; i < cells.Length; i++)
        {
            QuantizedMediaMoments cell = cells[i]; int at = i * MediaTrackLayoutV1.CellBytes;
            U16(bytes, at, cell.Rp); U16(bytes, at + 2, cell.Gp); U16(bytes, at + 4, cell.Bp);
            U16(bytes, at + 6, cell.Alpha); U16(bytes, at + 8, cell.YpSquared);
            U16(bytes, at + 10, cell.AlphaYp); U16(bytes, at + 12, cell.AlphaSquared);
        }
    }

    internal static void ValidateGrid(ReadOnlySpan<byte> grid)
    {
        for (int cell = 0; cell < MediaTrackLayoutV1.Cells; cell++)
            Require(R16(grid, cell * MediaTrackLayoutV1.CellBytes + 14) == 0, "Grid cell reserved bytes are nonzero.");
    }

    internal static void AddGrid(ReadOnlySpan<byte> grid, Span<ulong> sums, ulong duration)
    {
        for (int cell = 0; cell < MediaTrackLayoutV1.Cells; cell++)
            for (int moment = 0; moment < MediaTrackLayoutV1.Moments; moment++)
            {
                int index = cell * MediaTrackLayoutV1.Moments + moment;
                sums[index] = checked(sums[index] + (ulong)R16(grid, cell * MediaTrackLayoutV1.CellBytes + moment * 2) * duration);
            }
    }

    internal static void EncodePrefix(Span<byte> bytes, long endFrame, long elapsedUs, ReadOnlySpan<ulong> sums)
    {
        U64(bytes, 0, endFrame); U64(bytes, 8, elapsedUs);
        for (int i = 0; i < sums.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes[(16 + i * 8)..], sums[i]);
    }

    internal static void ValidatePrefix(ReadOnlySpan<byte> bytes, long endFrame, long elapsedUs, ReadOnlySpan<ulong> sums)
    {
        Require(R64(bytes, 0) == (ulong)endFrame && R64(bytes, 8) == (ulong)elapsedUs, "Prefix frame or elapsed boundary is invalid.");
        for (int i = 0; i < sums.Length; i++)
            Require(R64(bytes, 16 + i * 8) == sums[i], "Prefix sum does not equal the recomputed exact integer sum.");
    }

    internal static void Normalize(ReadOnlySpan<ulong> sums, double denominator, Span<MediaMomentValues> destination)
    {
        for (int cell = 0; cell < destination.Length; cell++)
        {
            int i = cell * MediaTrackLayoutV1.Moments;
            destination[cell] = new(sums[i] / denominator, sums[i + 1] / denominator, sums[i + 2] / denominator,
                sums[i + 3] / denominator, sums[i + 4] / denominator, sums[i + 5] / denominator, sums[i + 6] / denominator);
        }
    }

    internal static async ValueTask ReadAtAsync(Stream source, long offset, Memory<byte> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); source.Position = offset;
        try { await source.ReadExactlyAsync(destination, token).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw Invalid("Artifact ended before its complete canonical section."); }
    }

    internal static void CheckLength(Stream source, long length) => Require(source.Length == length, "Artifact length changed or does not match its identity.");

    internal static async ValueTask DisposeAllAsync(IEnumerable<Stream?> streams)
    {
        Exception? failure = null;
        foreach (Stream? stream in streams)
        {
            if (stream is null) continue;
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async ValueTask DisposeAfterFailureAsync(IEnumerable<Stream?> streams)
    {
        // Cleanup failure must never replace the original cancellation or format failure.
        try { await DisposeAllAsync(streams).ConfigureAwait(false); } catch (Exception) { }
    }

    internal static void DisposeAfterFailure(IDisposable owned)
    {
        try { owned.Dispose(); } catch (Exception) { }
    }

    private static void WriteTimestamp(Span<byte> bytes, int at, RationalTimestamp time)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes[at..], time.Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[(at + 8)..], time.TimeBase.Numerator);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[(at + 12)..], time.TimeBase.Denominator);
    }

    private static RationalTimestamp ReadTimestamp(ReadOnlySpan<byte> bytes, int at) =>
        new(BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]), RationalTimeBase.FromCanonical(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 8)..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 12)..])));

    internal static ushort R16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    internal static uint R32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    internal static ulong R64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
    internal static void U16(Span<byte> bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[at..], value);
    internal static void U32(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
    internal static void U64(Span<byte> bytes, int at, long value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[at..], checked((ulong)value));
}
