using System.Buffers.Binary;

namespace MakoKaraGenerator.Media;

/// <summary>Bounded structural reference checks, not a complete QuickTime or codec validator.</summary>
internal static class SelfContainedMovInputValidator
{
    internal const int MaximumDepth = 32;
    internal const int MaximumBoxes = 100_000;
    internal const int MaximumReferenceEntries = 4096;

    internal static void ValidateFile(string path, bool required, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (required || Recognizable(stream)) Validate(stream, cancellationToken);
    }

    private static bool Recognizable(Stream stream)
    {
        if (stream.Length < 8) return false;
        Span<byte> header = stackalloc byte[8]; stream.ReadExactly(header);
        try
        {
            // Preserve the ordinary families' signatures. Other plausible box sequences must be scanned even
            // when their first box is an unknown uuid/junk prefix. This is not a general polyglot validator.
            if (header[..4].SequenceEqual("RIFF"u8) || header[..4].SequenceEqual("RF64"u8)
                || header[..4].SequenceEqual("RIFX"u8) || header[..4].SequenceEqual("BW64"u8)
                || header[..4].SequenceEqual("fLaC"u8) || header[..4].SequenceEqual("OggS"u8)
                || header[..3].SequenceEqual("ID3"u8) || header[0] == 255 && (header[1] & 0xe0) == 0xe0) return false;
            if (Type(header[4..]) is 0x66747970 or 0x6d6f6f76 or 0x6d646174 or 0x66726565 or 0x77696465
                or 0x736b6970 or 0x6d6f6f66 or 0x73747970 or 0x73696478 or 0x726d7261 or 0x72647266) return true;
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header); int headerBytes = 8;
            if (size == 0) return true;
            if (size == 1)
            {
                if (stream.Length < 16) return false;
                stream.ReadExactly(header); size = BinaryPrimitives.ReadUInt64BigEndian(header); headerBytes = 16;
            }
            return size >= (ulong)headerBytes && size <= (ulong)stream.Length;
        }
        finally { stream.Position = 0; }
    }

    internal static void Validate(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw Invalid();
        long previous = stream.Position;
        try
        {
            stream.Position = 0;
            int boxes = 0;
            Visit(stream, stream.Length, 0, ref boxes, cancellationToken);
        }
        catch (EndOfStreamException) { throw Invalid(); }
        finally { stream.Position = previous; }
    }

    private static void Visit(Stream stream, long end, int depth, ref int boxes, CancellationToken token)
    {
        if (depth > MaximumDepth) throw Invalid();
        Span<byte> flags = stackalloc byte[4];
        while (stream.Position < end)
        {
            token.ThrowIfCancellationRequested();
            Box box = ReadBox(stream, end, ref boxes);
            if (box.Type is 0x726d7261 or 0x72647266) throw Invalid(); // rmra, rdrf reference movies
            if (box.Type == 0x64726566) ValidateReferences(stream, box.End, ref boxes, token); // dref
            else if (box.Type == 0x6d657461) // ISO full-box meta; no opaque metadata or mdat text scanning
            {
                Read(stream, flags, box.End);
                if (BinaryPrimitives.ReadUInt32BigEndian(flags) != 0) throw Invalid();
                Visit(stream, box.End, depth + 1, ref boxes, token);
            }
            else if (box.Type is 0x6d6f6f76 or 0x7472616b or 0x6d646961 or 0x6d696e66 or 0x64696e66
                or 0x6d6f6f66 or 0x74726166 or 0x6d766578 or 0x7374626c or 0x65647473 or 0x75647461 or 0x77617665)
                Visit(stream, box.End, depth + 1, ref boxes, token);
            // mdat and all non-container payloads are opaque, including embedded strings resembling box types.
            stream.Position = box.End;
        }
    }

    private static void ValidateReferences(Stream stream, long end, ref int boxes, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8]; Read(stream, header, end);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0 || count is 0 or > MaximumReferenceEntries) throw Invalid();
        Span<byte> flags = stackalloc byte[4];
        for (uint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            Box entry = ReadBox(stream, end, ref boxes);
            Read(stream, flags, entry.End);
            // Flags alone are insufficient for an alias: FFmpeg also parses nonempty alias records irrespective of flags.
            if (entry.Type is not (0x75726c20 or 0x75726e20 or 0x616c6973) // url, urn, alis
                || BinaryPrimitives.ReadUInt32BigEndian(flags) != 1 || stream.Position != entry.End) throw Invalid();
        }
        if (stream.Position != end) throw Invalid();
    }

    private static Box ReadBox(Stream stream, long parentEnd, ref int boxes)
    {
        if (++boxes > MaximumBoxes) throw Invalid();
        long start = stream.Position;
        Span<byte> header = stackalloc byte[8]; Read(stream, header, parentEnd);
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size == 1)
        {
            Span<byte> extended = stackalloc byte[8]; Read(stream, extended, parentEnd);
            size = BinaryPrimitives.ReadUInt64BigEndian(extended);
        }
        else if (size == 0) size = (ulong)(parentEnd - start);
        if (size > (ulong)(parentEnd - start) || size < (ulong)(stream.Position - start)) throw Invalid();
        return new(Type(header[4..]), start + (long)size);
    }

    private static void Read(Stream stream, Span<byte> buffer, long end)
    {
        if (stream.Position > end - buffer.Length) throw Invalid();
        stream.ReadExactly(buffer);
    }

    private static uint Type(ReadOnlySpan<byte> type) => BinaryPrimitives.ReadUInt32BigEndian(type);
    private static IOException Invalid() => new("音频准备拒绝外部MOV引用、非法容器结构或超出结构检查预算。");
    private readonly record struct Box(uint Type, long End);
}
