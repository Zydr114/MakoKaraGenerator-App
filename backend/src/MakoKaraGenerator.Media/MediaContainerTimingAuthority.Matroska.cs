using System.Numerics;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

public sealed partial class MediaContainerTimingAuthority
{
    private readonly record struct EbmlElement(ulong Id, long Data, long End);
    private readonly record struct MatroskaBlock(long Position, long Payload, int Bytes, long RelativePts);
    private sealed class MatroskaTrack
    {
        internal ulong Number, Type, DefaultDurationNs;
        internal bool NumberSeen, TypeSeen, DefaultSeen, ScaleSeen, DelaySeen, OffsetSeen, InvalidTiming;
    }

    private static MediaContainerTimingAuthority ReadMatroskaCore(Stream source, ulong trackNumber, IMediaWorkingSetBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireStream(source); ArgumentNullException.ThrowIfNull(budget);
        long saved = source.Position; var memory = new TimingMemory(budget);
        try
        {
            source.Position = 0; int elements = 0; EbmlElement? segment = null;
            while (source.Position < source.Length)
            {
                EbmlElement root = NextEbmlElement(source, source.Length, ref elements, true, token);
                if (root.Id == 0x18538067) { Require(segment is null, "Matroska contains multiple segments."); segment = root; }
                source.Position = root.End;
            }
            Require(segment is not null && trackNumber > 0, "Matroska selected video segment or real track number is missing.");
            ulong timestampScale = 1_000_000; bool scaleSeen = false, infoSeen = false, tracksSeen = false; MatroskaTrack? selected = null;
            source.Position = segment!.Value.Data;
            while (source.Position < segment.Value.End)
            {
                EbmlElement child = NextEbmlElement(source, segment.Value.End, ref elements, false, token);
                if (child.Id == 0x1549A966)
                {
                    Require(!infoSeen, "Matroska segment information is duplicated."); infoSeen = true;
                    while (source.Position < child.End)
                    {
                        EbmlElement info = NextEbmlElement(source, child.End, ref elements, false, token);
                        if (info.Id == 0x2AD7B1) { Require(!scaleSeen, "Matroska timestamp scale is duplicated."); scaleSeen = true; timestampScale = EbmlUInt(source, info); }
                        source.Position = info.End;
                    }
                }
                else if (child.Id == 0x1654AE6B)
                {
                    Require(!tracksSeen, "Matroska track table is duplicated."); tracksSeen = true; int tracks = 0;
                    while (source.Position < child.End)
                    {
                        EbmlElement entry = NextEbmlElement(source, child.End, ref elements, false, token);
                        if (entry.Id == 0xAE)
                        {
                            Require(++tracks <= 128, "Matroska track count exceeds fixed metadata capacity.");
                            MatroskaTrack track = ReadMatroskaTrack(source, entry.End, ref elements, token);
                            if (track.Number == trackNumber) { Require(selected is null, "Matroska selected TrackNumber is duplicated."); selected = track; }
                        }
                        source.Position = entry.End;
                    }
                }
                source.Position = child.End;
            }
            Require(selected is not null && selected.Type == 1 && !selected.InvalidTiming && timestampScale is > 0 and <= 1_000_000_000, "Matroska selected video timing uses an unsupported scale delay offset or content encoding.");
            MediaContainerSample[] samples = memory.Allocate<MediaContainerSample>((int)MediaAbCapacityV1.MaximumFrames); int count = 0;
            source.Position = segment.Value.Data;
            while (source.Position < segment.Value.End)
            {
                EbmlElement cluster = NextEbmlElement(source, segment.Value.End, ref elements, false, token);
                if (cluster.Id == 0x1F43B675)
                {
                    ulong? clusterPts = null;
                    while (source.Position < cluster.End)
                    {
                        EbmlElement item = NextEbmlElement(source, cluster.End, ref elements, false, token);
                        if (item.Id == 0xE7) { Require(clusterPts is null, "Matroska cluster timestamp is duplicated."); clusterPts = EbmlUInt(source, item); }
                        source.Position = item.End;
                    }
                    Require(clusterPts is not null, "Matroska cluster has no exact timestamp."); source.Position = cluster.Data;
                    while (source.Position < cluster.End)
                    {
                        EbmlElement item = NextEbmlElement(source, cluster.End, ref elements, false, token); MatroskaBlock? block = null; ulong? duration = null;
                        if (item.Id == 0xA3) block = ReadMatroskaBlock(source, item, trackNumber);
                        else if (item.Id == 0xA0)
                        {
                            EbmlElement? blockElement = null; bool unsupported = false;
                            while (source.Position < item.End)
                            {
                                EbmlElement part = NextEbmlElement(source, item.End, ref elements, false, token);
                                if (part.Id == 0xA1) { Require(blockElement is null, "Matroska BlockGroup contains multiple block elements."); blockElement = part; }
                                else if (part.Id == 0x9B) { Require(duration is null, "Matroska BlockDuration is duplicated."); duration = EbmlUInt(source, part); }
                                else if (part.Id is 0x75A2 or 0x75A1 or 0xA2) unsupported = true;
                                source.Position = part.End;
                            }
                            Require(blockElement is not null, "Matroska BlockGroup has no encoded block."); source.Position = blockElement!.Value.Data;
                            block = ReadMatroskaBlock(source, blockElement.Value, trackNumber);
                            if (block is not null) Require(!unsupported, "Matroska selected video has discard padding additions or virtual-block semantics.");
                        }
                        if (block is { } value)
                        {
                            if (count >= samples.Length) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Matroska selected frame count exceeds fixed capacity.");
                            BigInteger ptsNs = ((BigInteger)clusterPts!.Value + value.RelativePts) * timestampScale;
                            BigInteger? durationNs = duration is { } realDuration ? (BigInteger)realDuration * timestampScale
                                : selected!.DefaultSeen ? selected.DefaultDurationNs : null;
                            Require(ptsNs >= long.MinValue && ptsNs <= long.MaxValue &&
                                (durationNs is null || durationNs.Value > 0 && durationNs.Value <= long.MaxValue), "Matroska explicit duration or PTS exceeds the exact positive timing representation.");
                            RationalTimestamp? exactDuration = durationNs is { } nanos ? new((long)nanos, new(1, 1_000_000_000)) : null;
                            samples[count++] = new(value.Position, value.Payload, value.Bytes, new((long)ptsNs, new(1, 1_000_000_000)), exactDuration);
                        }
                        source.Position = item.End;
                    }
                }
                source.Position = cluster.End;
            }
            return new(memory, samples, count);
        }
        catch (OverflowException) { memory.Dispose(); throw Invalid("Matroska exact sample arithmetic exceeds its bounded representation."); }
        catch { memory.Dispose(); throw; }
        finally { source.Position = saved; }
    }

    /// <summary>Reads the actual block TrackNumber at the pinned demuxer's packet position.</summary>
    /// <param name="source">Stable authorized caller-owned source.</param>
    /// <param name="packetPosition">Block data position after its complete EBML ID and size, at TrackNumber.</param>
    /// <returns>Positive block TrackNumber without guessing a stream ordinal.</returns>
    public static ulong ReadMatroskaTrackNumber(Stream source, long packetPosition)
    {
        RequireStream(source); long saved = source.Position;
        try
        {
            Require(packetPosition >= 0 && packetPosition < source.Length, "Matroska packet position is outside its stable source."); source.Position = packetPosition;
            ulong number = Vint(source, false, out _, out bool unknown); Require(!unknown && number > 0, "Matroska block TrackNumber is missing or invalid."); return number;
        }
        finally { source.Position = saved; }
    }

    private static MatroskaTrack ReadMatroskaTrack(Stream source, long end, ref int elements, CancellationToken token)
    {
        var track = new MatroskaTrack();
        while (source.Position < end)
        {
            EbmlElement part = NextEbmlElement(source, end, ref elements, false, token);
            switch (part.Id)
            {
                case 0xD7: Require(!track.NumberSeen, "Matroska TrackNumber is duplicated."); track.NumberSeen = true; track.Number = EbmlUInt(source, part); break;
                case 0x83: Require(!track.TypeSeen, "Matroska TrackType is duplicated."); track.TypeSeen = true; track.Type = EbmlUInt(source, part); break;
                case 0x23E383: Require(!track.DefaultSeen, "Matroska DefaultDuration is duplicated."); track.DefaultSeen = true; track.DefaultDurationNs = EbmlUInt(source, part); break;
                case 0x23314F:
                    Require(!track.ScaleSeen, "Matroska TrackTimestampScale is duplicated."); track.ScaleSeen = true; long width = part.End - part.Data;
                    track.InvalidTiming |= !(width == 4 ? U32(source) == 0x3F800000 : width == 8 && U64(source) == 0x3FF0000000000000); break;
                case 0x56AA: Require(!track.DelaySeen, "Matroska CodecDelay is duplicated."); track.DelaySeen = true; track.InvalidTiming |= EbmlUInt(source, part) != 0; break;
                case 0x537F: Require(!track.OffsetSeen, "Matroska TrackOffset is duplicated."); track.OffsetSeen = true; track.InvalidTiming |= EbmlUInt(source, part) != 0; break;
                case 0x6D80: track.InvalidTiming = true; break;
                // Deprecated Video.FrameRate is deliberately never duration authority.
            }
            source.Position = part.End;
        }
        Require(track.NumberSeen && track.Number > 0 && track.TypeSeen, "Matroska track lacks a unique positive number or type."); return track;
    }

    private static MatroskaBlock? ReadMatroskaBlock(Stream source, EbmlElement element, ulong selectedTrack)
    {
        ulong track = Vint(source, false, out _, out bool unknown); Require(!unknown && track > 0 && source.Position <= element.End, "Matroska block track header is invalid.");
        if (track != selectedTrack) return null;
        Require(element.End - source.Position >= 3, "Matroska selected block header is truncated."); int high = source.ReadByte(), low = source.ReadByte(), flags = source.ReadByte();
        Require((flags & 0x0E) == 0, "Matroska selected video lacing or invisible-frame semantics are unsupported.");
        long bytes = element.End - source.Position; Require(bytes is > 0 and <= int.MaxValue, "Matroska selected block payload exceeds its exact packet representation.");
        return new(element.Data, source.Position, (int)bytes, unchecked((short)((high << 8) | low)));
    }

    private static EbmlElement NextEbmlElement(Stream source, long end, ref int elements, bool allowSegment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Require(++elements <= MaximumElements && source.Position < end, "Matroska structural element count exceeds its finite capacity.");
        ulong id = Vint(source, true, out _, out _); Require(source.Position < end, "Matroska element size is truncated.");
        ulong size = Vint(source, false, out _, out bool unknown); long data = source.Position;
        Require(data <= end && (!unknown || (allowSegment && id == 0x18538067)), "Only the outer Matroska segment may have unknown length in this finite profile.");
        Require(unknown || size <= (ulong)(end - data), "Matroska element extends beyond its parent boundary."); return new(id, data, unknown ? end : data + (long)size);
    }
    private static ulong EbmlUInt(Stream source, EbmlElement element)
    { long size = element.End - element.Data; Require(size is >= 1 and <= 8, "Matroska integer width is unsupported."); ulong value = 0; for (long i = 0; i < size; i++) { int next = source.ReadByte(); Require(next >= 0, "Matroska integer is truncated."); value = (value << 8) | (uint)next; } return value; }
    private static ulong Vint(Stream source, bool preserveMarker, out int length, out bool unknown)
    {
        int first = source.ReadByte(); Require(first > 0, "Matroska variable integer is missing or zero."); int marker = 0x80; length = 1;
        while ((first & marker) == 0 && length <= 8) { marker >>= 1; length++; }
        Require(length <= (preserveMarker ? 4 : 8), "Matroska variable integer width is unsupported."); ulong value = (uint)(preserveMarker ? first : first & (marker - 1));
        for (int i = 1; i < length; i++) { int next = source.ReadByte(); Require(next >= 0, "Matroska variable integer is truncated."); value = (value << 8) | (uint)next; }
        unknown = !preserveMarker && value == ((1UL << (7 * length)) - 1); return value;
    }
}
