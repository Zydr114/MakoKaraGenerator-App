using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace MakoKaraGenerator.Media.Tests;

internal static class MediaPresentationFixture
{
    internal static byte[] Cat(params byte[][] values) => values.SelectMany(value => value).ToArray();
    internal static byte[] Be(params uint[] values)
    { byte[] result = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), values[i]); return result; }
    internal static byte[] Box(string type, params byte[][] payload)
    { byte[] body = Cat(payload); return Cat(Be((uint)(8 + body.Length)), Encoding.ASCII.GetBytes(type), body); }
    internal static byte[] Movie(out long payloadPosition, uint duration = 1, bool composition = false, bool missingDuration = false, bool edit = false)
    {
        byte[] payload = [10, 20, 30, 40, 50, 60, 70, 80, 90];
        byte[] Build(uint offset)
        {
            byte[] stbl = Box("stbl", Box("stsd", Be(0, 1), Box("avc1")), missingDuration ? [] : Box("stts", Be(0, 1, 3, duration)),
                composition ? Box("ctts", Be(0x01000000, 3, 1, 0, 1, 1, 1, uint.MaxValue)) : [],
                Box("stsz", Be(0, 3, 3)), Box("stsc", Be(0, 1, 1, 3, 1)), Box("stco", Be(0, 1, offset)));
            byte[] edits = edit ? Box("edts", Box("elst", Be(0, 1, 3 * duration, 0, 0x00010000))) : [];
            return Cat(Box("ftyp", "isom"u8.ToArray()), Box("moov", Box("mvhd", Be(0, 0, 0, 1000)),
                Box("trak", Box("tkhd", Be(0, 0, 0, 7, 0)), edits, Box("mdia", Box("mdhd", Be(0, 0, 0, 1000)), Box("minf", stbl)))), Box("mdat", payload));
        }
        byte[] before = Build(0); payloadPosition = before.Length - payload.Length; return Build((uint)payloadPosition);
    }
    internal static MediaDigest Hash(ReadOnlySpan<byte> value) => new(Convert.ToHexStringLower(SHA256.HashData(value)));
    internal static byte[] Ebml(ulong id, params byte[][] payload)
    {
        byte[] data = Cat(payload); var idBytes = new List<byte>(); do { idBytes.Insert(0, (byte)id); id >>= 8; } while (id != 0);
        return Cat(idBytes.ToArray(), Vint((ulong)data.Length), data);
    }
    internal static byte[] Vint(ulong value)
    {
        int length = 1; while (value >= (1UL << (7 * length)) - 1) length++;
        byte[] result = new byte[length]; for (int i = length - 1; i >= 0; i--) { result[i] = (byte)value; value >>= 8; }
        result[0] |= (byte)(1 << (8 - length)); return result;
    }
    internal static byte[] UInt(ulong value)
    { var bytes = new List<byte>(); do { bytes.Insert(0, (byte)value); value >>= 8; } while (value != 0); return bytes.ToArray(); }
    internal static byte[] Matroska(out long packetPosition, out long payloadPosition, bool defaultDuration = true, bool blockDuration = false, bool lace = false, int payloadBytes = 3, ulong trackNumber = 5)
    {
        byte[] track = Ebml(0xAE, Ebml(0xD7, UInt(trackNumber)), Ebml(0x83, UInt(1)), defaultDuration ? Ebml(0x23E383, UInt(33_333_333)) : [],
            Ebml(0xE0, Ebml(0x2383E3, [0x41, 0xF0, 0, 0]))); // Deprecated FrameRate is never authority.
        byte[] trackVint = Vint(trackNumber);
        byte[] payload = Enumerable.Range(0, payloadBytes).Select(index => (byte)((index % 3 + 1) * 10)).ToArray();
        byte[] blockData = Cat(trackVint, [0xFF, 0xFF, lace ? (byte)0x82 : (byte)0x80], payload);
        byte[] block = Ebml(blockDuration ? 0xA1UL : 0xA3UL, blockData);
        byte[] wrapped = blockDuration ? Ebml(0xA0, block, Ebml(0x9B, UInt(40))) : block;
        byte[] file = Cat(Ebml(0x1A45DFA3), Ebml(0x18538067, Ebml(0x1549A966, Ebml(0x2AD7B1, UInt(1_000_000))),
            Ebml(0x1654AE6B, track), Ebml(0x1F43B675, Ebml(0xE7, UInt(0)), wrapped)));
        int blockAt = file.AsSpan().LastIndexOf(block);
        packetPosition = blockAt + block.Length - blockData.Length; // FFmpeg bin.pos is after ID and encoded size.
        payloadPosition = packetPosition + trackVint.Length + 3; return file;
    }
    internal static byte[] MatroskaFrames(out long[] packetPositions, out long[] payloadPositions,
        long[] ptsMs, bool defaultDuration, ulong? finalBlockDurationMs)
    {
        byte[] track = Ebml(0xAE, Ebml(0xD7, UInt(5)), Ebml(0x83, UInt(1)),
            defaultDuration ? Ebml(0x23E383, UInt(33_333_333)) : []);
        var rawBlocks = new List<byte[]>(); var wrappedBlocks = new List<byte[]>();
        for (int i = 0; i < ptsMs.Length; i++)
        {
            short relative = checked((short)ptsMs[i]); bool finalDuration = i == ptsMs.Length - 1 && finalBlockDurationMs is not null;
            byte[] data = [0x85, (byte)(relative >> 8), (byte)relative, 0x80, (byte)(10 + i), (byte)(20 + i), (byte)(30 + i)];
            byte[] block = Ebml(finalDuration ? 0xA1UL : 0xA3UL, data); rawBlocks.Add(block);
            wrappedBlocks.Add(finalDuration ? Ebml(0xA0, block, Ebml(0x9B, UInt(finalBlockDurationMs!.Value))) : block);
        }
        byte[] file = Cat(Ebml(0x1A45DFA3), Ebml(0x18538067,
            Ebml(0x1549A966, Ebml(0x2AD7B1, UInt(1_000_000))), Ebml(0x1654AE6B, track),
            Ebml(0x1F43B675, Ebml(0xE7, UInt(0)), Cat(wrappedBlocks.ToArray()))));
        packetPositions = new long[rawBlocks.Count]; payloadPositions = new long[rawBlocks.Count];
        for (int i = 0; i < rawBlocks.Count; i++)
        {
            int at = file.AsSpan().IndexOf(rawBlocks[i]); packetPositions[i] = at + rawBlocks[i].Length - 7;
            payloadPositions[i] = packetPositions[i] + 4;
        }
        return file;
    }

    internal static byte[] Png(bool srgb = true, bool icc = false, bool animated = false, bool conflictingGamma = false, int bits = 8)
    {
        byte[] ihdr = Cat(Be(1, 1), [(byte)bits, 6, 0, 0, 0]);
        return Cat([137, 80, 78, 71, 13, 10, 26, 10], PngChunk("IHDR", ihdr), srgb ? PngChunk("sRGB", [0]) : [],
            icc ? PngChunk("iCCP", [0]) : [], animated ? PngChunk("acTL", Be(1, 0)) : [], conflictingGamma ? PngChunk("gAMA", Be(100000)) : [],
            PngChunk("IDAT", [1]), PngChunk("IEND", []));
    }
    internal static byte[] PngChunk(string type, byte[] data)
    {
        byte[] name = Encoding.ASCII.GetBytes(type), content = Cat(name, data); uint crc = 0xFFFFFFFF;
        foreach (byte b in content) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u); }
        return Cat(Be((uint)data.Length), content, Be(crc ^ 0xFFFFFFFF));
    }
    internal static byte[] Jpeg(bool jfif = true, bool progressive = false, int bits = 8, bool icc = false, byte sampling = 0x22, bool cmyk = false, byte chroma = 0x11)
    {
        byte[] Marker(byte id, byte[] data) => Cat([0xFF, id, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)], data);
        byte[] ids = cmyk ? [(byte)bits, 0, 1, 0, 1, 4, 1, sampling, 0, 2, 0x11, 0, 3, 0x11, 0, 4, 0x11, 0]
            : [(byte)bits, 0, 1, 0, 1, 3, 1, sampling, 0, 2, chroma, 0, 3, chroma, 0];
        return Cat([0xFF, 0xD8], jfif ? Marker(0xE0, Cat("JFIF\0"u8.ToArray(), [1, 2, 0, 0, 1, 0, 1, 0, 0])) : [],
            icc ? Marker(0xE2, Cat("ICC_PROFILE\0"u8.ToArray(), [1, 1])) : [], Marker(progressive ? (byte)0xC2 : (byte)0xC0, ids),
            Marker(0xDA, [1, 1, 0, 0, 63, 0]), [0, 0xFF, 0, 0, 0xFF, 0xD9]);
    }

    internal sealed class Budget : IMediaWorkingSetBudget
    {
        internal long Live, Peak;
        public long LimitBytes => MediaAbCapacityV1.MaximumDecodeWorkingBytes;
        public IMediaMemoryReservation Reserve(long bytes, MediaWorkingSetPurpose purpose)
        { if (bytes <= 0 || bytes > LimitBytes - Live) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Fixture capacity exceeded."); Live += bytes; Peak = Math.Max(Peak, Live); return new Charge(this, bytes); }
        private sealed class Charge(Budget owner, long bytes) : IMediaMemoryReservation
        { private bool _done; public long Bytes => bytes; public void Dispose() { if (_done) return; _done = true; owner.Live -= bytes; } }
    }
    internal sealed class NoopLease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
