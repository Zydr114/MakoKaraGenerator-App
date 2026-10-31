using System.Buffers.Binary;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>Bounded PNG container validation. IDAT decompression is deliberately not performed here.</summary>
internal static class PreviewPngValidation
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static bool IsValid(ReadOnlySpan<byte> content, int width, int height)
    {
        if (content.Length < 45 || !content[..8].SequenceEqual(Signature)) return false;
        int offset = 8;
        bool header = false, palette = false, data = false, dataEnded = false;
        byte colorType = 0;
        long dataLength = 0;
        while (offset <= content.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(content[offset..]);
            if (length > (uint)(content.Length - offset - 12)) return false;
            int size = (int)length;
            ReadOnlySpan<byte> type = content.Slice(offset + 4, 4);
            foreach (byte letter in type)
                if (letter is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z')) return false;
            if ((type[2] & 32) != 0) return false;
            ReadOnlySpan<byte> payload = content.Slice(offset + 8, size);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(offset + 8 + size, 4));
            if (Crc(content.Slice(offset + 4, size + 4)) != expectedCrc) return false;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (header || offset != 8 || size != 13) return false;
                if (BinaryPrimitives.ReadUInt32BigEndian(payload) != (uint)width
                    || BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) != (uint)height) return false;
                colorType = payload[9];
                byte bitDepth = payload[8];
                bool validDepth = colorType switch
                {
                    0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => bitDepth is 8 or 16,
                    3 => bitDepth is 1 or 2 or 4 or 8,
                    _ => false,
                };
                if (!validDepth || payload[10] != 0 || payload[11] != 0 || payload[12] > 1) return false;
                header = true;
            }
            else
            {
                if (!header) return false;
                if (type.SequenceEqual("PLTE"u8))
                {
                    if (palette || data || colorType is 0 or 4 || size == 0 || size > 768 || size % 3 != 0) return false;
                    palette = true;
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    if (dataEnded || colorType == 3 && !palette) return false;
                    data = true;
                    dataLength += size;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    return size == 0 && data && dataLength > 0 && offset + 12 == content.Length;
                }
                else
                {
                    if ((type[0] & 32) == 0) return false; // Unknown critical chunks are not safe to ignore.
                    if (data) dataEnded = true;
                }
            }
            offset += size + 12;
        }
        return false;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[index] = value;
        }
        return table;
    }
}
