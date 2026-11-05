using System.Numerics;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

public sealed partial class MediaContainerTimingAuthority
{
    private readonly record struct MovBox(uint Type, long Data, long End);
    private readonly record struct ByteRange(long Start, long End);
    private readonly record struct ChunkRun(uint FirstChunk, uint Samples, uint Description);
    private sealed class MovTrack
    {
        internal int TimeScale;
        internal uint[]? Durations, Sizes;
        internal int[]? Composition;
        internal ulong[]? Chunks;
        internal ChunkRun[]? Runs;
        internal long MediaStart;
        internal ulong EmptyMovieDuration, MediaMovieDuration;
        internal bool EditsSeen, HeaderSeen, DescriptionSeen;
    }

    private static MediaContainerTimingAuthority ReadMovCore(Stream source, int trackId, IMediaWorkingSetBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireStream(source); ArgumentNullException.ThrowIfNull(budget);
        long saved = source.Position; var memory = new TimingMemory(budget);
        try
        {
            // Existing parser independently denies dref/url/alias external media references.
            try { source.Position = 0; SelfContainedMovInputValidator.Validate(source, token); }
            catch (IOException) { throw Invalid("MOV source is malformed or contains unsupported external media references."); }
            source.Position = 0; int elements = 0, movieScale = 0, mediaCount = 0; MovBox? selected = null; bool movieSeen = false;
            ByteRange[] media = memory.Allocate<ByteRange>(4096);
            while (source.Position < source.Length)
            {
                MovBox box = NextMovBox(source, source.Length, ref elements, token);
                if (box.Type == 0x6D6F6F66) throw Invalid("Fragmented MOV timing is outside the exact sample-table profile.");
                if (box.Type == 0x6D646174)
                { Require(mediaCount < media.Length, "MOV media box count exceeds its finite structural capacity."); media[mediaCount++] = new(box.Data, box.End); }
                if (box.Type == 0x6D6F6F76)
                {
                    Require(!movieSeen, "MOV contains multiple movie headers."); movieSeen = true; source.Position = box.Data; int tracks = 0;
                    while (source.Position < box.End)
                    {
                        MovBox child = NextMovBox(source, box.End, ref elements, token); source.Position = child.Data;
                        if (child.Type == 0x6D766578) throw Invalid("Fragmented MOV defaults are unsupported by this timing profile.");
                        if (child.Type == 0x6D766864)
                        {
                            Require(movieScale == 0, "MOV movie time base is duplicated.");
                            movieScale = ReadMovScale(source, child, false);
                        }
                        else if (child.Type == 0x7472616B)
                        {
                            Require(++tracks <= 128, "MOV track count exceeds its finite structural capacity.");
                            int id = ReadTrackId(source, child.End, ref elements, token);
                            if (id == trackId) { Require(selected is null, "MOV selected track ID is duplicated."); selected = child; }
                        }
                        source.Position = child.End;
                    }
                }
                source.Position = box.End;
            }
            Require(selected is not null && movieScale > 0 && mediaCount > 0, "MOV selected track movie time base or local media data is absent.");
            var track = new MovTrack(); source.Position = selected!.Value.Data;
            ReadMovTrack(source, selected.Value.End, 0, track, memory, ref elements, token);
            Require(track.TimeScale > 0 && track.HeaderSeen && track.DescriptionSeen && track.Durations is not null && track.Sizes is not null && track.Chunks is not null && track.Runs is not null,
                "MOV selected track lacks an exact timing size chunk or description table.");
            uint[] durations = track.Durations!; uint[] sizes = track.Sizes!; ulong[] chunks = track.Chunks!; ChunkRun[] runs = track.Runs!;
            Require(durations.Length == sizes.Length && (track.Composition is null || track.Composition.Length == sizes.Length), "MOV exact sample table counts disagree.");
            long emptyTicks = ScaleMovieTicks(track.EmptyMovieDuration, track.TimeScale, movieScale);
            long editDuration = track.EditsSeen ? ScaleMovieTicks(track.MediaMovieDuration, track.TimeScale, movieScale) : long.MaxValue;
            var timeBase = new RationalTimeBase(1, track.TimeScale);
            MediaContainerSample[] samples = memory.Allocate<MediaContainerSample>(durations.Length);
            long dts = 0; int ordinal = 0, run = 0;
            for (int chunk = 0; chunk < chunks.Length; chunk++)
            {
                token.ThrowIfCancellationRequested(); while (run + 1 < runs.Length && runs[run + 1].FirstChunk <= chunk + 1) run++;
                Require(runs[run].FirstChunk <= chunk + 1 && runs[run].Description == 1, "MOV sample description switching is unsupported.");
                ulong position = chunks[chunk];
                for (uint entry = 0; entry < runs[run].Samples; entry++)
                {
                    token.ThrowIfCancellationRequested(); Require(ordinal < durations.Length && position <= (ulong)source.Length && sizes[ordinal] <= int.MaxValue && sizes[ordinal] > 0 && sizes[ordinal] <= (ulong)source.Length - position, "MOV selected sample byte range exceeds its stable source.");
                    long start = (long)position, end = checked(start + sizes[ordinal]); bool local = false;
                    for (int i = 0; i < mediaCount; i++) if (start >= media[i].Start && end <= media[i].End) { local = true; break; }
                    Require(local, "MOV selected sample bytes are outside a local media-data payload.");
                    long composition = checked(dts + (track.Composition?[ordinal] ?? 0));
                    long pts = checked(composition - track.MediaStart + emptyTicks);
                    if (track.EditsSeen) Require(composition >= track.MediaStart && composition - track.MediaStart <= editDuration - durations[ordinal], "MOV edit trims repeats or excludes a selected presentation sample.");
                    samples[ordinal] = new(start, start, (int)sizes[ordinal], new(pts, timeBase), new(durations[ordinal], timeBase));
                    position = (ulong)end; dts = checked(dts + durations[ordinal]); ordinal++;
                }
            }
            Require(ordinal == durations.Length && runs[^1].FirstChunk <= chunks.Length, "MOV chunk map does not exactly cover every selected sample.");
            return new(memory, samples, ordinal);
        }
        catch (OverflowException) { memory.Dispose(); throw Invalid("MOV exact sample arithmetic exceeds its bounded representation."); }
        catch { memory.Dispose(); throw; }
        finally { source.Position = saved; }
    }

    private static MovBox NextMovBox(Stream source, long end, ref int elements, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Require(++elements <= MaximumElements && end - source.Position >= 8, "MOV structural count or box boundary exceeds its finite profile.");
        long start = source.Position; ulong size = U32(source); uint type = U32(source);
        if (size == 1) { Require(end - source.Position >= 8, "MOV extended box header is truncated."); size = U64(source); }
        else if (size == 0) size = (ulong)(end - start);
        Require(size >= (ulong)(source.Position - start) && size <= (ulong)(end - start), "MOV box length is invalid.");
        return new(type, source.Position, start + (long)size);
    }

    private static int ReadTrackId(Stream source, long end, ref int elements, CancellationToken token)
    {
        int id = 0;
        while (source.Position < end)
        {
            MovBox box = NextMovBox(source, end, ref elements, token);
            if (box.Type == 0x746B6864)
            {
                Require(id == 0 && box.End - box.Data >= 20, "MOV track header is duplicated or truncated.");
                uint flags = U32(source); uint version = flags >> 24; Require(version <= 1, "MOV track header version is unsupported.");
                Require(box.End - source.Position >= (version == 1 ? 20 : 12), "MOV track header time fields are truncated.");
                source.Position += version == 1 ? 16 : 8; uint rawId = U32(source);
                Require(rawId is > 0 and <= int.MaxValue, "MOV track ID exceeds the supported exact selected-stream identity."); id = (int)rawId;
            }
            source.Position = box.End;
        }
        Require(id > 0, "MOV track has no unique header ID."); return id;
    }

    private static int ReadMovScale(Stream source, MovBox box, bool media)
    {
        Require(box.End - source.Position >= 4, "MOV time header is truncated."); uint flags = U32(source); uint version = flags >> 24;
        Require(version <= 1 && (flags & 0xFFFFFF) == 0 && box.End - source.Position >= (version == 1 ? 20 : 12), "MOV time header version flags or length are unsupported.");
        source.Position += version == 1 ? 16 : 8; uint scale = U32(source);
        Require(scale is > 0 and <= int.MaxValue, media ? "MOV media time base is invalid." : "MOV movie time base is invalid."); return (int)scale;
    }

    private static void ReadMovTrack(Stream source, long end, int depth, MovTrack track, TimingMemory memory, ref int elements, CancellationToken token)
    {
        Require(depth <= MaximumDepth, "MOV selected sample nesting exceeds its finite depth.");
        while (source.Position < end)
        {
            MovBox box = NextMovBox(source, end, ref elements, token); source.Position = box.Data;
            switch (box.Type)
            {
                case 0x6D646864:
                    Require(!track.HeaderSeen, "MOV selected media time base is duplicated."); track.HeaderSeen = true; track.TimeScale = ReadMovScale(source, box, true); break;
                case 0x73747473:
                    Require(track.Durations is null, "MOV sample timing table is duplicated."); track.Durations = ReadDurationRuns(source, box, memory, token); break;
                case 0x63747473:
                    Require(track.Composition is null, "MOV composition timing table is duplicated."); track.Composition = ReadCompositionRuns(source, box, memory, token); break;
                case 0x7374737A:
                {
                    Require(track.Sizes is null && box.End - source.Position >= 12 && U32(source) == 0, "MOV size table is duplicated truncated or has unsupported flags.");
                    uint uniform = U32(source); int count = Count(U32(source)); Require(box.End - source.Position == (uniform == 0 ? 4L * count : 0), "MOV size table payload length disagrees with its count.");
                    track.Sizes = memory.Allocate<uint>(count); for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); track.Sizes[i] = uniform == 0 ? U32(source) : uniform; } break;
                }
                case 0x7374636F: case 0x636F3634:
                {
                    Require(track.Chunks is null && box.End - source.Position >= 8 && U32(source) == 0, "MOV chunk table is duplicated truncated or has unsupported flags.");
                    int count = Count(U32(source)); Require(box.End - source.Position == (box.Type == 0x7374636F ? 4L : 8L) * count, "MOV chunk table payload length disagrees with its count.");
                    track.Chunks = memory.Allocate<ulong>(count); for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); track.Chunks[i] = box.Type == 0x7374636F ? U32(source) : U64(source); } break;
                }
                case 0x73747363:
                {
                    Require(track.Runs is null && box.End - source.Position >= 8 && U32(source) == 0, "MOV chunk-run table is duplicated truncated or has unsupported flags.");
                    int count = Count(U32(source)); Require(box.End - source.Position == 12L * count, "MOV chunk-run payload length disagrees with its count."); track.Runs = memory.Allocate<ChunkRun>(count);
                    for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); track.Runs[i] = new(U32(source), U32(source), U32(source)); Require(track.Runs[i].Samples > 0 && track.Runs[i].Samples <= MediaAbCapacityV1.MaximumFrames && track.Runs[i].FirstChunk > (i == 0 ? 0 : track.Runs[i - 1].FirstChunk), "MOV chunk runs are not positive strictly ordered entries."); }
                    Require(track.Runs[0].FirstChunk == 1, "MOV chunk map must begin at chunk one."); break;
                }
                case 0x73747364:
                    Require(!track.DescriptionSeen && box.End - source.Position >= 8 && U32(source) == 0 && U32(source) == 1, "MOV requires one stable sample description."); track.DescriptionSeen = true; break;
                case 0x656C7374: ReadMovEdits(source, box, track); break;
                case 0x6D646961: case 0x6D696E66: case 0x7374626C: case 0x65647473:
                    ReadMovTrack(source, box.End, depth + 1, track, memory, ref elements, token); break;
            }
            Require(source.Position <= box.End, "MOV field extends beyond its declared box."); source.Position = box.End;
        }
    }

    private static uint[] ReadDurationRuns(Stream source, MovBox box, TimingMemory memory, CancellationToken token)
    {
        Require(box.End - source.Position >= 8 && U32(source) == 0, "MOV stts flags or length are unsupported."); int runs = Count(U32(source));
        Require(box.End - source.Position == 8L * runs, "MOV stts byte length disagrees with run count."); long start = source.Position; int count = 0;
        for (int i = 0; i < runs; i++) { token.ThrowIfCancellationRequested(); int length = Count(U32(source)); Require(U32(source) > 0 && length <= MediaAbCapacityV1.MaximumFrames - count, "MOV sample duration is missing nonpositive or exceeds frame capacity."); count += length; }
        uint[] values = memory.Allocate<uint>(count); source.Position = start; int at = 0;
        for (int i = 0; i < runs; i++) { int length = Count(U32(source)); uint value = U32(source); token.ThrowIfCancellationRequested(); values.AsSpan(at, length).Fill(value); at += length; } return values;
    }

    private static int[] ReadCompositionRuns(Stream source, MovBox box, TimingMemory memory, CancellationToken token)
    {
        Require(box.End - source.Position >= 8, "MOV ctts is truncated."); uint flags = U32(source); uint version = flags >> 24;
        Require(version <= 1 && (flags & 0xFFFFFF) == 0, "MOV ctts flags are unsupported."); int runs = Count(U32(source));
        Require(box.End - source.Position == 8L * runs, "MOV ctts byte length disagrees with run count."); long start = source.Position; int count = 0;
        for (int i = 0; i < runs; i++) { token.ThrowIfCancellationRequested(); int length = Count(U32(source)); uint value = U32(source); Require((version == 1 || value <= int.MaxValue) && length <= MediaAbCapacityV1.MaximumFrames - count, "MOV composition offset or count exceeds its exact representation."); count += length; }
        int[] values = memory.Allocate<int>(count); source.Position = start; int at = 0;
        for (int i = 0; i < runs; i++) { int length = Count(U32(source)); int value = I32(source); token.ThrowIfCancellationRequested(); values.AsSpan(at, length).Fill(value); at += length; } return values;
    }

    private static void ReadMovEdits(Stream source, MovBox box, MovTrack track)
    {
        Require(!track.EditsSeen && box.End - source.Position >= 8, "MOV edit list is duplicated or truncated."); track.EditsSeen = true;
        uint flags = U32(source); uint version = flags >> 24; uint count = U32(source);
        Require(version <= 1 && (flags & 0xFFFFFF) == 0 && count is 1 or 2 && box.End - source.Position == count * (version == 1 ? 20L : 12L), "MOV edit policy accepts one full rate-one media edit and optional leading empty edit.");
        for (uint i = 0; i < count; i++)
        {
            ulong duration = version == 1 ? U64(source) : U32(source); long media = version == 1 ? I64(source) : I32(source); uint rate = U32(source);
            Require(rate == 0x00010000 && duration > 0, "MOV edit duration or rate is unsupported.");
            if (media == -1) { Require(i == 0 && count == 2, "MOV empty edit must occur once before the media edit."); track.EmptyMovieDuration = duration; }
            else { Require(i == count - 1 && media >= 0, "MOV edit trim or repetition is unsupported."); track.MediaStart = media; track.MediaMovieDuration = duration; }
        }
        Require(track.MediaMovieDuration > 0, "MOV edit list has no positive media interval.");
    }

    private static long ScaleMovieTicks(ulong value, int mediaScale, int movieScale)
    { BigInteger product = (BigInteger)value * mediaScale; Require(product % movieScale == 0 && product / movieScale <= long.MaxValue, "MOV edit duration is not exactly representable in the selected stream time base."); return (long)(product / movieScale); }
    private static int Count(uint value)
    { if (value is 0 || value > MediaAbCapacityV1.MaximumFrames) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Container table count exceeds the fixed selected-frame capacity."); return (int)value; }
}
