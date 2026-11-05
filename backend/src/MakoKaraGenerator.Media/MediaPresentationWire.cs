using System.Buffers.Binary;
using System.Text;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>Exact immutable nine-integer display matrix; absent values carry no affine transform.</summary>
public readonly record struct MediaNativeDisplayMatrix
{
    /// <summary>Creates a read-only transport value; accepted boundaries validate all fields.</summary>
    /// <param name="isPresent">Exact IsPresent transport value.</param>
    /// <param name="a">Exact A transport value.</param>
    /// <param name="b">Exact B transport value.</param>
    /// <param name="u">Exact U transport value.</param>
    /// <param name="c">Exact C transport value.</param>
    /// <param name="d">Exact D transport value.</param>
    /// <param name="v">Exact V transport value.</param>
    /// <param name="x">Exact X transport value.</param>
    /// <param name="y">Exact Y transport value.</param>
    /// <param name="w">Exact W transport value.</param>
    public MediaNativeDisplayMatrix(bool isPresent, int a, int b, int u, int c, int d, int v, int x, int y, int w)
    {
        IsPresent = isPresent;
        A = a;
        B = b;
        U = u;
        C = c;
        D = d;
        V = v;
        X = x;
        Y = y;
        W = w;
    }
    /// <summary>Exact IsPresent transport value.</summary>
    public bool IsPresent { get; }
    /// <summary>Exact A transport value.</summary>
    public int A { get; }
    /// <summary>Exact B transport value.</summary>
    public int B { get; }
    /// <summary>Exact U transport value.</summary>
    public int U { get; }
    /// <summary>Exact C transport value.</summary>
    public int C { get; }
    /// <summary>Exact D transport value.</summary>
    public int D { get; }
    /// <summary>Exact V transport value.</summary>
    public int V { get; }
    /// <summary>Exact X transport value.</summary>
    public int X { get; }
    /// <summary>Exact Y transport value.</summary>
    public int Y { get; }
    /// <summary>Exact W transport value.</summary>
    public int W { get; }
}

/// <summary>Immutable same-AVFrame metadata paired with the following native planes.</summary>
public sealed class MediaNativePresentationMetadata
{
    /// <summary>Creates one bounded native observation; the transport validates framing and the interpretation validates semantics.</summary>
    /// <param name="ordinal">Exact native ordinal field.</param>
    /// <param name="packetId">Exact native packetId field.</param>
    /// <param name="packetPosition">Exact native packetPosition field.</param>
    /// <param name="packetBytes">Exact native packetBytes field.</param>
    /// <param name="pts">Exact native pts field.</param>
    /// <param name="decoderDurationTicks">Exact native decoderDurationTicks field.</param>
    /// <param name="width">Exact native width field.</param>
    /// <param name="height">Exact native height field.</param>
    /// <param name="pixelFormat">Exact native pixelFormat field.</param>
    /// <param name="primaries">Exact native primaries field.</param>
    /// <param name="transfer">Exact native transfer field.</param>
    /// <param name="matrix">Exact native matrix field.</param>
    /// <param name="range">Exact native range field.</param>
    /// <param name="chroma">Exact native chroma field.</param>
    /// <param name="alpha">Exact native alpha field.</param>
    /// <param name="sarNumerator">Exact native sarNumerator field.</param>
    /// <param name="sarDenominator">Exact native sarDenominator field.</param>
    /// <param name="displayMatrix">Exact native displayMatrix field.</param>
    /// <param name="planeCount">Exact native planeCount field.</param>
    /// <param name="firstStride">Exact native firstStride field.</param>
    /// <param name="firstBytes">Exact native firstBytes field.</param>
    /// <param name="cbStride">Exact native cbStride field.</param>
    /// <param name="cbBytes">Exact native cbBytes field.</param>
    /// <param name="crStride">Exact native crStride field.</param>
    /// <param name="crBytes">Exact native crBytes field.</param>
    /// <param name="streamId">Exact native streamId field.</param>
    /// <param name="streamIndex">Exact native streamIndex field.</param>
    /// <param name="family">Exact native family field.</param>
    /// <param name="packetHash">Exact native packetHash field.</param>
    public MediaNativePresentationMetadata(long ordinal, long packetId, long packetPosition, int packetBytes, RationalTimestamp pts, long decoderDurationTicks, int width, int height, MediaNativePixelFormat pixelFormat, int primaries, int transfer, int matrix, int range, int chroma, int alpha, int sarNumerator, int sarDenominator, MediaNativeDisplayMatrix displayMatrix, int planeCount, int firstStride, int firstBytes, int cbStride, int cbBytes, int crStride, int crBytes, int streamId, int streamIndex, int family, MediaDigest packetHash)
    {
        Ordinal = ordinal;
        PacketId = packetId;
        PacketPosition = packetPosition;
        PacketBytes = packetBytes;
        Pts = pts;
        DecoderDurationTicks = decoderDurationTicks;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Primaries = primaries;
        Transfer = transfer;
        Matrix = matrix;
        Range = range;
        Chroma = chroma;
        Alpha = alpha;
        SarNumerator = sarNumerator;
        SarDenominator = sarDenominator;
        DisplayMatrix = displayMatrix;
        PlaneCount = planeCount;
        FirstStride = firstStride;
        FirstBytes = firstBytes;
        CbStride = cbStride;
        CbBytes = cbBytes;
        CrStride = crStride;
        CrBytes = crBytes;
        StreamId = streamId;
        StreamIndex = streamIndex;
        Family = family;
        PacketHash = packetHash;
    }
    /// <summary>Exact native Ordinal value; no repaired or floating-point timestamp is used.</summary>
    public long Ordinal { get; }
    /// <summary>Exact native PacketId value; no repaired or floating-point timestamp is used.</summary>
    public long PacketId { get; }
    /// <summary>Exact native PacketPosition value; no repaired or floating-point timestamp is used.</summary>
    public long PacketPosition { get; }
    /// <summary>Exact native PacketBytes value; no repaired or floating-point timestamp is used.</summary>
    public int PacketBytes { get; }
    /// <summary>Exact native Pts value; no repaired or floating-point timestamp is used.</summary>
    public RationalTimestamp Pts { get; }
    /// <summary>Exact native DecoderDurationTicks value; no repaired or floating-point timestamp is used.</summary>
    public long DecoderDurationTicks { get; }
    /// <summary>Exact native Width value; no repaired or floating-point timestamp is used.</summary>
    public int Width { get; }
    /// <summary>Exact native Height value; no repaired or floating-point timestamp is used.</summary>
    public int Height { get; }
    /// <summary>Exact native PixelFormat value; no repaired or floating-point timestamp is used.</summary>
    public MediaNativePixelFormat PixelFormat { get; }
    /// <summary>Exact native Primaries value; no repaired or floating-point timestamp is used.</summary>
    public int Primaries { get; }
    /// <summary>Exact native Transfer value; no repaired or floating-point timestamp is used.</summary>
    public int Transfer { get; }
    /// <summary>Exact native Matrix value; no repaired or floating-point timestamp is used.</summary>
    public int Matrix { get; }
    /// <summary>Exact native Range value; no repaired or floating-point timestamp is used.</summary>
    public int Range { get; }
    /// <summary>Exact native Chroma value; no repaired or floating-point timestamp is used.</summary>
    public int Chroma { get; }
    /// <summary>Exact native Alpha value; no repaired or floating-point timestamp is used.</summary>
    public int Alpha { get; }
    /// <summary>Exact native SarNumerator value; no repaired or floating-point timestamp is used.</summary>
    public int SarNumerator { get; }
    /// <summary>Exact native SarDenominator value; no repaired or floating-point timestamp is used.</summary>
    public int SarDenominator { get; }
    /// <summary>Exact native DisplayMatrix value; no repaired or floating-point timestamp is used.</summary>
    public MediaNativeDisplayMatrix DisplayMatrix { get; }
    /// <summary>Exact native PlaneCount value; no repaired or floating-point timestamp is used.</summary>
    public int PlaneCount { get; }
    /// <summary>Exact native FirstStride value; no repaired or floating-point timestamp is used.</summary>
    public int FirstStride { get; }
    /// <summary>Exact native FirstBytes value; no repaired or floating-point timestamp is used.</summary>
    public int FirstBytes { get; }
    /// <summary>Exact native CbStride value; no repaired or floating-point timestamp is used.</summary>
    public int CbStride { get; }
    /// <summary>Exact native CbBytes value; no repaired or floating-point timestamp is used.</summary>
    public int CbBytes { get; }
    /// <summary>Exact native CrStride value; no repaired or floating-point timestamp is used.</summary>
    public int CrStride { get; }
    /// <summary>Exact native CrBytes value; no repaired or floating-point timestamp is used.</summary>
    public int CrBytes { get; }
    /// <summary>Exact native StreamId value; no repaired or floating-point timestamp is used.</summary>
    public int StreamId { get; }
    /// <summary>Exact native StreamIndex value; no repaired or floating-point timestamp is used.</summary>
    public int StreamIndex { get; }
    /// <summary>Exact native Family value; no repaired or floating-point timestamp is used.</summary>
    public int Family { get; }
    /// <summary>Exact native PacketHash value; no repaired or floating-point timestamp is used.</summary>
    public MediaDigest PacketHash { get; }
}
/// <summary>Complete native flush evidence; probe completion never masquerades as full decode completion.</summary>
public readonly record struct MediaNativePresentationComplete
{
    /// <summary>Creates a read-only transport value; accepted boundaries validate all fields.</summary>
    /// <param name="frames">Exact Frames transport value.</param>
    /// <param name="packets">Exact Packets transport value.</param>
    /// <param name="fullyFlushed">Exact FullyFlushed transport value.</param>
    public MediaNativePresentationComplete(long frames, long packets, bool fullyFlushed)
    {
        Frames = frames;
        Packets = packets;
        FullyFlushed = fullyFlushed;
    }
    /// <summary>Exact Frames transport value.</summary>
    public long Frames { get; }
    /// <summary>Exact Packets transport value.</summary>
    public long Packets { get; }
    /// <summary>Exact FullyFlushed transport value.</summary>
    public bool FullyFlushed { get; }
}

/// <summary>Internal pinned deployment entry; host paths never enter composite identity or public API.</summary>
public sealed record MediaLoadedToolFile
{
    /// <summary>Creates a read-only transport value; accepted boundaries validate all fields.</summary>
    /// <param name="trustedPath">Exact TrustedPath transport value.</param>
    /// <param name="manifestEntry">Exact ManifestEntry transport value.</param>
    public MediaLoadedToolFile(string trustedPath, MediaToolManifestEntry manifestEntry)
    {
        TrustedPath = trustedPath;
        ManifestEntry = manifestEntry;
    }
    /// <summary>Exact TrustedPath transport value.</summary>
    public string TrustedPath { get; }
    /// <summary>Exact ManifestEntry transport value.</summary>
    public MediaToolManifestEntry ManifestEntry { get; }
}

/// <summary>Strict fd3 framing and package-loaded-file verification for the native presentation helper.</summary>
/// <remarks>Instances are single-flight and owned by one operation. One fixed metadata array is reused; no frame table or native plane backing is retained.</remarks>
public sealed class MediaPresentationWire : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, MediaToolManifestEntry> _allowed;
    private readonly byte[] _buffer;
    private readonly IMediaMemoryReservation _reservation;
    private readonly HashSet<string> _epoch = new(StringComparer.Ordinal);
    private bool _closureVerified;

    /// <summary>Creates a transport reader from the controlled package map.</summary>
    /// <param name="allowed">The complete pinned tool closure; only internal configuration supplies host paths.</param>
    /// <param name="budget">Operation managed subledger, charged before transport allocations.</param>
    /// <exception cref="MediaAbException">A tool entry or map is invalid.</exception>
    public MediaPresentationWire(IReadOnlyList<MediaLoadedToolFile> allowed, IMediaWorkingSetBudget budget)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Count is <= 0 or > MediaAbCapacityV1.MaximumToolManifestEntries)
            throw Failure("Controlled loaded tool map is empty or exceeds fixed capacity.");
        long textBytes = 0;
        foreach (MediaLoadedToolFile file in allowed)
        {
            if (file.TrustedPath.Length > 4096) throw Failure("Controlled loaded tool location exceeds its fixed private capacity.");
            textBytes = checked(textBytes + file.TrustedPath.Length * 2L + 128);
        }
        _reservation = budget.Reserve(checked(MediaAbCapacityV1.MaximumMetadataRecordBytes + textBytes * 2 + allowed.Count * 512L + 4096), MediaWorkingSetPurpose.Metadata);
        try
        {
            _buffer = new byte[MediaAbCapacityV1.MaximumMetadataRecordBytes];
            _allowed = new(StringComparer.Ordinal);
            foreach (MediaLoadedToolFile file in allowed)
            {
                if (!Path.IsPathFullyQualified(file.TrustedPath) || !_allowed.TryAdd(file.TrustedPath, file.ManifestEntry))
                    throw Failure("Controlled loaded tool map contains an invalid or duplicate host location.");
            }
            _ = new MediaAbIdentityWriter().ComputeDependencyManifestDigest(allowed.Select(value => value.ManifestEntry).ToArray());
        }
        catch { _reservation.Dispose(); throw; }
    }

    /// <summary>Reads the next exact frame or completion after validating its entire current loaded-library epoch.</summary>
    /// <param name="metadata">The operation-owned fd3 stream.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>A frame metadata or native completion value.</returns>
    /// <exception cref="MediaAbException">Framing, loaded closure or metadata is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    public async ValueTask<object> ReadNextAsync(Stream metadata, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadExactlyAsync(metadata, _buffer.AsMemory(0, 16), cancellationToken).ConfigureAwait(false);
            if (!_buffer.AsSpan(0, 8).SequenceEqual("MKGPRE1\0"u8)) throw Failure("Native presentation metadata magic is invalid.");
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(8));
            uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(12));
            if (bytes > _buffer.Length) throw Failure("Native presentation metadata record exceeds fixed capacity.");
            await ReadExactlyAsync(metadata, _buffer.AsMemory(0, (int)bytes), cancellationToken).ConfigureAwait(false);
            switch (type)
            {
                case 4:
                    if (_closureVerified) { _epoch.Clear(); _closureVerified = false; }
                    VerifyLoadedFile((int)bytes); break;
                case 5:
                    if (bytes != 0 || _epoch.Count == 0) throw Failure("Native loaded closure epoch is empty or malformed.");
                    _closureVerified = true; break;
                case 2:
                    if (!_closureVerified || bytes != 224) throw Failure("Native frame precedes verified loaded closure or has invalid size.");
                    _closureVerified = false; _epoch.Clear(); return ReadFrame();
                case 3:
                    if (!_closureVerified || bytes != 24 || U32(20) != 0 || U32(16) > 1) throw Failure("Native completion record is malformed or lacks final closure verification.");
                    return new MediaNativePresentationComplete(I64(0), I64(8), U32(16) != 0);
                default: throw Failure("Native presentation metadata type is unsupported.");
            }
        }
    }

    /// <summary>Reads exactly one bounded native plane without accepting an early pipe EOF.</summary>
    /// <param name="stream">The operation-owned stdout stream.</param>
    /// <param name="destination">Previously reserved exact plane backing.</param>
    /// <param name="token">Cancellation propagated unchanged.</param>
    /// <returns>Completion of the exact plane read.</returns>
    public static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken token)
    {
        while (!destination.IsEmpty)
        {
            token.ThrowIfCancellationRequested(); int count = await stream.ReadAsync(destination, token).ConfigureAwait(false);
            if (count == 0) throw Failure("Native presentation pipe ended inside a required record or plane.");
            destination = destination[count..];
        }
    }

    private void VerifyLoadedFile(int bytes)
    {
        if (bytes < 44) throw Failure("Native loaded file record is truncated.");
        long length = I64(0); uint textBytes = U32(40);
        if (textBytes != bytes - 44 || textBytes == 0 || textBytes > 4096 || length < 0 || _epoch.Count >= _allowed.Count)
            throw Failure("Native loaded file record fields exceed their controlled bounds.");
        string path;
        try { path = StrictUtf8.GetString(_buffer, 44, (int)textBytes); }
        catch (DecoderFallbackException) { throw Failure("Native loaded file location is invalid UTF eight."); }
        if (!_epoch.Add(path) || !_allowed.TryGetValue(path, out MediaToolManifestEntry? expected) ||
            expected.ByteLength != length || !Convert.FromHexString(expected.ContentSha256.LowerHex).AsSpan().SequenceEqual(_buffer.AsSpan(8, 32)))
            throw Failure("Native loaded file does not match the complete pinned deployment manifest.");
    }

    private MediaNativePresentationMetadata ReadFrame()
    {
        for (int i = 208; i < 224; i++) if (_buffer[i] != 0) throw Failure("Native frame has unknown reserved metadata.");
        MediaNativeDisplayMatrix matrix = U32(132) switch { 0 => default, 1 => new(true, I32(96), I32(100), I32(104), I32(108), I32(112), I32(116), I32(120), I32(124), I32(128)), _ => throw Failure("Native display matrix presence is invalid.") };
        return new(I64(0), I64(8), I64(16), I32(24), new(I64(28), RationalTimeBase.FromCanonical(I32(36), I32(40))), I64(44),
            I32(52), I32(56), (MediaNativePixelFormat)U32(60), I32(64), I32(68), I32(72), I32(76), I32(80), I32(84), I32(88), I32(92), matrix,
            I32(136), I32(140), I32(144), I32(148), I32(152), I32(156), I32(160), I32(164), I32(168), I32(172), new(Convert.ToHexStringLower(_buffer.AsSpan(176, 32))));
    }
    /// <summary>Rejects every trailing stdout or metadata byte and observes both normal pipe EOFs.</summary>
    /// <param name="output">Native pixel pipe after exact required planes.</param>
    /// <param name="metadata">Native metadata pipe after its completion record.</param>
    /// <param name="token">Cancellation propagated unchanged.</param>
    /// <returns>Completion only after both pipes end without unknown trailing data.</returns>
    internal static async Task RequireEofAsync(Stream output, Stream metadata, CancellationToken token)
    {
        byte[] probe = new byte[1];
        if (await output.ReadAsync(probe, token).ConfigureAwait(false) != 0 || await metadata.ReadAsync(probe, token).ConfigureAwait(false) != 0)
            throw Failure("Native presentation emitted trailing pixels or metadata after completion.");
    }
    /// <summary>Releases fixed transport backing after its final consumer returns.</summary>
    public void Dispose() => _reservation.Dispose();
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(offset));
    private int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(offset));
    private long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(offset));
    private static MediaAbException Failure(string detail) => new(MediaAbFailure.ProfileUnsupported, detail);
}
