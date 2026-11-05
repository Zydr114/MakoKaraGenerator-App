using System.Buffers.Binary;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>Structural format authority established without decoding or filename guesses.</summary>
/// <remarks>Only single-frame PNG 8/16 and JPEG 8-bit baseline/progressive are admitted. ICC and animation are always rejected.</remarks>
internal sealed class MediaStillStructure
{
    private MediaStillStructure(int family, int width, int height, int bits, bool alpha, bool tagged,
        MediaMatrix matrix, MediaChromaLocation chroma, int subsampling, int sarN, int sarD, int orientation)
    { Family = family; Width = width; Height = height; Bits = bits; HasAlpha = alpha; HasColorAuthority = tagged;
      Matrix = matrix; Chroma = chroma; Subsampling = subsampling; SarNumerator = sarN; SarDenominator = sarD; Orientation = orientation; }
    internal int Family { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Bits { get; }
    internal bool HasAlpha { get; }
    internal bool HasColorAuthority { get; }
    internal MediaMatrix Matrix { get; }
    internal MediaChromaLocation Chroma { get; }
    internal int Subsampling { get; }
    internal int SarNumerator { get; }
    internal int SarDenominator { get; }
    internal int Orientation { get; }
    internal const int ScratchBytes = 128 * 1024;

    internal static int DetectFamily(Stream source, MediaSourceKind kind)
    {
        long saved = source.Position; Span<byte> head = stackalloc byte[12];
        try
        {
            source.Position = 0; int got = 0;
            while (got < head.Length) { int count = source.Read(head[got..]); if (count == 0) break; got += count; }
            if (got >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10])) return kind == MediaSourceKind.Still ? 3 : throw Unsupported("PNG cannot be selected as a timed video source.");
            if (got >= 2 && head[0] == 0xFF && head[1] == 0xD8) return kind == MediaSourceKind.Still ? 4 : throw Unsupported("JPEG cannot be selected as a timed video source.");
            if (got >= 4 && head[..4].SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3])) return kind == MediaSourceKind.Video ? 2 : throw Unsupported("Matroska cannot be selected as a timeless still source.");
            if (got >= 8 && (head.Slice(4, 4).SequenceEqual("ftyp"u8) || head.Slice(4, 4).SequenceEqual("moov"u8) || head.Slice(4, 4).SequenceEqual("wide"u8) || head.Slice(4, 4).SequenceEqual("mdat"u8)))
                return kind == MediaSourceKind.Video ? 1 : throw Unsupported("MOV cannot be selected as a timeless still source.");
            throw Unsupported("Source magic is outside the controlled PNG JPEG MOV and Matroska input families.");
        }
        finally { source.Position = saved; }
    }

    internal static MediaStillStructure Read(Stream source, int family, IMediaWorkingSetBudget budget, CancellationToken token)
    {
        long saved = source.Position;
        Need(source.Length <= 64L * 1024 * 1024, "Still compressed source exceeds the controlled native packet capacity.");
        using IMediaMemoryReservation reserved = budget.Reserve(ScratchBytes, MediaWorkingSetPurpose.Metadata);
        byte[] scratch = new byte[64 * 1024];
        try { source.Position = 0; return family == 3 ? Png(source, scratch, token) : family == 4 ? Jpeg(source, scratch, token) : throw Unsupported("Still structure family is unsupported."); }
        catch (OverflowException) { throw Color("Still structural arithmetic exceeds its supported raster representation."); }
        finally { source.Position = saved; }
    }

    private static MediaStillStructure Png(Stream source, byte[] scratch, CancellationToken token)
    {
        Span<byte> signature = stackalloc byte[8]; Exact(source, signature);
        Need(signature.SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]), "PNG signature is invalid.");
        int width = 0, height = 0, bits = 0, type = 0, orientation = 1, sarN = 1, sarD = 1, chunks = 0;
        bool header = false, end = false, data = false, dataEnded = false, srgb = false, gamma = false, chroma = false, phys = false, exif = false, transparency = false, palette = false; int paletteEntries = 0;
        Span<byte> name = stackalloc byte[4];
        while (!end)
        {
            token.ThrowIfCancellationRequested(); Need(++chunks <= 200_000 && source.Length - source.Position >= 12, "PNG chunk count or boundary exceeds its finite profile.");
            uint length = Big32(source); Exact(source, name);
            Need(length <= source.Length - source.Position - 4, "PNG chunk length exceeds its stable source.");
            uint code = BinaryPrimitives.ReadUInt32BigEndian(name); Need(header || code == 0x49484452, "PNG IHDR is not the first chunk.");
            if (code is 0x69434350 or 0x6163544C or 0x6663544C or 0x66644154 or 0x63494350 or 0x6D444356 or 0x634C4C49) throw Color("PNG ICC animation and multiframe chunks are unsupported.");
            if (data && code != 0x49444154) dataEnded = true;
            bool capture = code is 0x49484452 or 0x73524742 or 0x67414D41 or 0x6348524D or 0x70485973 or 0x65584966;
            Need(!capture || length <= scratch.Length, "PNG ancillary metadata exceeds its fixed record capacity.");
            uint crc = Crc(0xFFFFFFFF, name); uint remaining = length; int offset = 0;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested(); int take = (int)Math.Min(remaining, (uint)(capture ? scratch.Length - offset : scratch.Length));
                Span<byte> block = scratch.AsSpan(capture ? offset : 0, take); Exact(source, block); crc = Crc(crc, block); remaining -= (uint)take; if (capture) offset += take;
            }
            Need((crc ^ 0xFFFFFFFF) == Big32(source), "PNG chunk CRC disagrees with its source payload.");
            ReadOnlySpan<byte> value = scratch.AsSpan(0, capture ? (int)length : 0);
            switch (code)
            {
                case 0x49484452:
                    Need(!header && length == 13, "PNG IHDR is duplicated or malformed."); header = true; width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(value)); height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(value[4..])); bits = value[8]; type = value[9];
                    Need(bits is 8 or 16 && type is 0 or 2 or 3 or 4 or 6 && !(type == 3 && bits != 8) && value[10] == 0 && value[11] == 0 && value[12] <= 1, "PNG bit depth component layout or coding method is unsupported."); Dimensions(width, height); break;
                case 0x73524742: Need(!srgb && !data && length == 1 && value[0] <= 3, "PNG sRGB chunk is duplicated late or malformed."); srgb = true; break;
                case 0x67414D41: Need(!gamma && !data && length == 4 && BinaryPrimitives.ReadUInt32BigEndian(value) == 45455, "PNG gamma contradicts the supported sRGB interpretation."); gamma = true; break;
                case 0x6348524D:
                    Need(!chroma && !data && length == 32, "PNG primary chromaticities are duplicated late or malformed."); chroma = true;
                    ReadOnlySpan<uint> expected = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
                    for (int i = 0; i < 8; i++) Need(BinaryPrimitives.ReadUInt32BigEndian(value[(i * 4)..]) == expected[i], "PNG chromaticities contradict sRGB primaries."); break;
                case 0x70485973:
                    Need(!phys && !data && length == 9 && value[8] <= 1, "PNG pixel aspect metadata is duplicated late or malformed."); phys = true;
                    (sarN, sarD) = Aspect(BinaryPrimitives.ReadUInt32BigEndian(value[4..]), BinaryPrimitives.ReadUInt32BigEndian(value)); break;
                case 0x65584966: Need(!exif && !data, "PNG Exif metadata is duplicated or late."); exif = true; orientation = Exif(value); break;
                case 0x504C5445: Need(!palette && !data && length is >= 3 and <= 768 && length % 3 == 0, "PNG palette is duplicated late or malformed."); palette = true; paletteEntries = (int)length / 3; break;
                case 0x74524E53: Need(!transparency && !data && (type == 0 && length == 2 || type == 2 && length == 6 || type == 3 && palette && length > 0 && length <= paletteEntries), "PNG transparency metadata is unsupported."); transparency = true; break;
                case 0x49444154: Need(!dataEnded && (type != 3 || palette), "PNG image data is noncontiguous or lacks its palette."); data = true; break;
                case 0x49454E44: Need(length == 0 && data, "PNG IEND is malformed or image data is absent."); end = true; break;
                default: Need((name[0] & 32) != 0, "PNG contains an unsupported critical chunk."); break;
            }
        }
        Need(source.Position == source.Length, "PNG contains trailing bytes or multiple images.");
        return new(3, width, height, bits, type is 4 or 6 || transparency, srgb, MediaMatrix.Rgb, MediaChromaLocation.None, 0, sarN, sarD, orientation);
    }

    private static MediaStillStructure Jpeg(Stream source, byte[] scratch, CancellationToken token)
    {
        Need(Byte(source) == 0xFF && Byte(source) == 0xD8, "JPEG SOI is invalid.");
        bool jfif = false, adobe = false, exif = false, frame = false, scan = false, entropy = false, ended = false; int transform = -1, orientation = 1, sarN = 1, sarD = 1, width = 0, height = 0, count = 0, sampling = 0, markers = 0;
        byte id0 = 0, id1 = 0, id2 = 0; int pending = -1;
        while (!ended)
        {
            token.ThrowIfCancellationRequested(); Need(++markers <= 200_000, "JPEG marker count exceeds its finite structural capacity.");
            int marker;
            if (pending >= 0) { marker = pending; pending = -1; }
            else { Need(Byte(source) == 0xFF, "JPEG marker prefix is missing."); do { marker = Byte(source); } while (marker == 0xFF); }
            Need(marker != 0 && marker != 0xD8 && marker is not (>= 0xD0 and <= 0xD7), "JPEG has an unexpected standalone marker or second image.");
            if (marker == 0xD9) { Need(frame && scan, "JPEG ended before a complete image scan."); ended = true; continue; }
            int length = (Byte(source) << 8) | Byte(source); Need(length >= 2 && length - 2 <= scratch.Length && source.Length - source.Position >= length - 2, "JPEG marker payload exceeds its stable source or fixed metadata capacity.");
            Exact(source, scratch.AsSpan(0, length - 2)); ReadOnlySpan<byte> value = scratch.AsSpan(0, length - 2);
            if (marker == 0xE0 && value.StartsWith("JFIF\0"u8))
            {
                Need(!jfif && !scan && value.Length >= 14 && value[5] == 1 && value[6] <= 2 && value[7] <= 2 && value.Length == 14 + 3 * value[12] * value[13], "JPEG JFIF metadata is duplicated late or malformed."); jfif = true;
                (sarN, sarD) = Aspect(BinaryPrimitives.ReadUInt16BigEndian(value[10..]), BinaryPrimitives.ReadUInt16BigEndian(value[8..]));
            }
            else if (marker == 0xEE && value.StartsWith("Adobe"u8))
            { Need(!adobe && !scan && value.Length == 12 && value[11] <= 1, "JPEG Adobe metadata is duplicated late or declares CMYK YCCK."); adobe = true; transform = value[11]; }
            else if (marker == 0xE1 && value.StartsWith("Exif\0\0"u8))
            { Need(!exif && !scan, "JPEG Exif metadata is duplicated or late."); exif = true; orientation = Exif(value[6..]); }
            else if (marker == 0xE2 && value.StartsWith("ICC_PROFILE\0"u8)) throw Color("JPEG ICC profiles are unsupported even when named sRGB.");
            else if (marker is 0xC0 or 0xC2)
            {
                Need(!frame && !scan && value.Length >= 6 && value[0] == 8, "JPEG requires exactly one 8-bit baseline or progressive frame."); frame = true;
                height = BinaryPrimitives.ReadUInt16BigEndian(value[1..]); width = BinaryPrimitives.ReadUInt16BigEndian(value[3..]); count = value[5]; Dimensions(width, height);
                Need(count is 1 or 3 && value.Length == 6 + 3 * count, "JPEG CMYK YCCK or malformed frame component layout is unsupported."); id0 = value[6];
                if (count == 1) { Need(value[7] == 0x11, "JPEG grayscale sampling is unsupported."); sampling = 0; }
                else
                {
                    id1 = value[9]; id2 = value[12]; Need(id0 != id1 && id0 != id2 && id1 != id2 && value[10] == 0x11 && value[13] == 0x11, "JPEG chroma sampling factors or component IDs are unsupported.");
                    sampling = value[7] switch { 0x11 => 444, 0x21 => 422, 0x22 => 420, _ => throw Color("JPEG supports only 420 422 and 444 native sampling.") };
                }
            }
            else if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4)) throw Color("JPEG lossless arithmetic hierarchical or non-8-bit frame mode is unsupported.");
            else if (marker == 0xDA)
            {
                Need(frame && value.Length >= 6 && value[0] > 0 && value[0] <= count && value.Length == 1 + 2 * value[0] + 3, "JPEG scan structure is malformed."); scan = true; entropy = true;
                long inspected = 0;
                while (entropy)
                {
                    if ((inspected++ & 8191) == 0) token.ThrowIfCancellationRequested(); int current = Byte(source); if (current != 0xFF) continue;
                    do { current = Byte(source); } while (current == 0xFF);
                    if (current == 0 || current is >= 0xD0 and <= 0xD7) continue; pending = current; entropy = false;
                }
            }
            else if (marker is 0xDC or 0xDE or 0xDF) throw Color("JPEG dynamic-height hierarchical and expansion semantics are unsupported.");
        }
        Need(source.Position == source.Length, "JPEG contains trailing bytes or concatenated images.");
        bool rgbIds = count == 3 && id0 == (byte)'R' && id1 == (byte)'G' && id2 == (byte)'B';
        Need(!(jfif && adobe && transform == 0 && count == 3) && !(jfif && rgbIds) && !(adobe && transform == 1 && rgbIds), "JPEG JFIF Adobe and explicit RGB component metadata contradict one another.");
        bool rgb = count == 1 || rgbIds || (adobe && transform == 0);
        Need(!rgb || count == 1 || sampling == 444, "JPEG explicitly RGB-coded components must use 444 sampling.");
        MediaMatrix matrix = rgb ? MediaMatrix.Rgb : MediaMatrix.Bt601Jpeg;
        return new(4, width, height, 8, false, jfif, matrix, rgb || sampling == 444 ? MediaChromaLocation.None : MediaChromaLocation.Center, sampling, sarN, sarD, orientation);
    }

    private static int Exif(ReadOnlySpan<byte> data)
    {
        Need(data.Length >= 8, "Exif TIFF header is truncated."); bool little = data[..2].SequenceEqual("II"u8); Need(little || data[..2].SequenceEqual("MM"u8), "Exif byte order is unsupported.");
        static ushort R16(ReadOnlySpan<byte> b, int at, bool le) { Need(at >= 0 && at <= b.Length - 2, "Exif short is outside its bounded metadata."); return le ? BinaryPrimitives.ReadUInt16LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt16BigEndian(b[at..]); }
        static uint R32(ReadOnlySpan<byte> b, int at, bool le) { Need(at >= 0 && at <= b.Length - 4, "Exif integer is outside its bounded metadata."); return le ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt32BigEndian(b[at..]); }
        Need(R16(data, 2, little) == 42, "Exif TIFF magic is invalid."); uint rawOffset = R32(data, 4, little); Need(rawOffset <= int.MaxValue, "Exif directory offset is outside its finite record.");
        int at = (int)rawOffset, orientation = 1; bool orientationSeen = false; int directories = 0; uint exifDirectory = 0;
        while (at != 0)
        {
            Need(++directories <= 2, "Exif thumbnails or chained image directories are outside the controlled single-image profile."); int count = R16(data, at, little); at += 2;
            Need(count <= 256 && at <= data.Length - 4 - 12 * count, "Exif directory exceeds its bounded entry capacity.");
            for (int i = 0; i < count; i++)
            {
                int entry = at + 12 * i; ushort tag = R16(data, entry, little), type = R16(data, entry + 2, little); uint elements = R32(data, entry + 4, little);
                if (tag == 0x0112) { Need(!orientationSeen && type == 3 && elements == 1, "Exif orientation is duplicated or malformed."); orientationSeen = true; orientation = R16(data, entry + 8, little); Need(orientation is >= 1 and <= 8, "Exif orientation is outside the eight exact orthogonal transforms."); }
                else if (tag == 0x8769) { Need(exifDirectory == 0 && type == 4 && elements == 1, "Exif color directory is duplicated or malformed."); exifDirectory = R32(data, entry + 8, little); }
                else if (tag == 0xA001) Need(type == 3 && elements == 1 && R16(data, entry + 8, little) == 1, "Exif explicitly declares an unsupported or ambiguous color space.");
                else if (tag == 0x0213) Need(type == 3 && elements == 1 && R16(data, entry + 8, little) == 1, "Exif chroma positioning contradicts the centered JPEG interpretation.");
                else if (tag is 0x8773 or 0xA005 or 0x012D or 0x013E or 0x013F or 0x0156 or 0xA500)
                    throw Color("Exif ICC interoperability gamma or chromaticity overrides are outside the controlled interpretation.");
            }
            Need(R32(data, at + 12 * count, little) == 0, "Exif thumbnail or chained image IFD is unsupported.");
            if (directories == 1 && exifDirectory != 0) { Need(exifDirectory <= int.MaxValue, "Exif color directory offset is too large."); at = (int)exifDirectory; } else at = 0;
        }
        return orientation;
    }

    internal static (int Numerator, int Denominator) Aspect(uint numerator, uint denominator)
    { Need(numerator > 0 && denominator > 0 && numerator <= int.MaxValue && denominator <= int.MaxValue, "Pixel aspect ratio must be positive and fit the supported rational representation."); uint a = numerator, b = denominator; while (b != 0) { uint next = a % b; a = b; b = next; } return ((int)(numerator / a), (int)(denominator / a)); }
    private static void Dimensions(int width, int height) => Need(width > 0 && height > 0 && width <= MediaAbCapacityV1.MaximumSourceSide && height <= MediaAbCapacityV1.MaximumSourceSide && (long)width * height <= MediaAbCapacityV1.MaximumSourcePixels, "Still dimensions exceed the fixed source raster profile.");
    private static uint Crc(uint value, ReadOnlySpan<byte> bytes) { foreach (byte next in bytes) { value ^= next; for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0); } return value; }
    private static int Byte(Stream source) { int value = source.ReadByte(); Need(value >= 0, "Still structure is truncated."); return value; }
    private static uint Big32(Stream source) { Span<byte> value = stackalloc byte[4]; Exact(source, value); return BinaryPrimitives.ReadUInt32BigEndian(value); }
    private static void Exact(Stream source, Span<byte> bytes) { while (!bytes.IsEmpty) { int read = source.Read(bytes); Need(read > 0, "Still structure is truncated."); bytes = bytes[read..]; } }
    private static void Need(bool condition, string detail) { if (!condition) throw Color(detail); }
    private static MediaAbException Color(string detail) => new(MediaAbFailure.ColorUnsupported, detail);
    private static MediaAbException Unsupported(string detail) => new(MediaAbFailure.ProfileUnsupported, detail);
}
