using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>An exact source sample and its byte-for-byte demuxed payload location.</summary>
public readonly record struct MediaContainerSample
{
    /// <summary>Creates a read-only exact container sample value.</summary>
    /// <param name="packetPosition">Exact PacketPosition evidence.</param>
    /// <param name="payloadPosition">Exact PayloadPosition evidence.</param>
    /// <param name="packetBytes">Exact PacketBytes evidence.</param>
    /// <param name="pts">Exact Pts evidence.</param>
    /// <param name="duration">Exact positive duration when explicitly supplied; null when absent.</param>
    public MediaContainerSample(long packetPosition, long payloadPosition, int packetBytes, RationalTimestamp pts, RationalTimestamp? duration)
    {
        PacketPosition = packetPosition;
        PayloadPosition = payloadPosition;
        PacketBytes = packetBytes;
        Pts = pts;
        Duration = duration;
    }
    /// <summary>Exact PacketPosition evidence.</summary>
    public long PacketPosition { get; }
    /// <summary>Exact PayloadPosition evidence.</summary>
    public long PayloadPosition { get; }
    /// <summary>Exact PacketBytes evidence.</summary>
    public int PacketBytes { get; }
    /// <summary>Exact Pts evidence.</summary>
    public RationalTimestamp Pts { get; }
    /// <summary>Exact positive duration when supplied; absent durations are never inferred from frame rate.</summary>
    public RationalTimestamp? Duration { get; }
}

/// <summary>Bounded independent container proof for the selected one-packet-per-frame video family.</summary>
/// <remarks>Caller owns the stable stream. Every actual backing allocation is charged before allocation and held until disposal. No duration estimate, decoder timestamp repair or external reference is accepted.</remarks>
public sealed partial class MediaContainerTimingAuthority : IDisposable
{
    /// <summary>Maximum structural elements visited, including repeated metadata passes.</summary>
    public const int MaximumElements = 200_000;
    /// <summary>Maximum recursively supported container depth.</summary>
    public const int MaximumDepth = 16;
    /// <summary>Admission ceiling for exact timing tables, byte-hash scratch and structural state.</summary>
    public const long MaximumWorkingBytes = 12L * 1024 * 1024;
    private readonly TimingMemory _memory;
    private readonly MediaContainerSample[] _samples;
    private readonly bool[] _seen;
    private readonly byte[] _hashScratch;
    private readonly int _count;
    private readonly MediaPresentationTimeMath _math = new();
    private int _verified;
    private bool _disposed;

    private MediaContainerTimingAuthority(TimingMemory memory, MediaContainerSample[] samples, int count)
    {
        Require(count > 0, "Selected container stream has no exact samples.");
        _memory = memory; _samples = samples; _count = count;
        Array.Sort(_samples, 0, count, Comparer<MediaContainerSample>.Create((left, right) => left.PacketPosition.CompareTo(right.PacketPosition)));
        for (int i = 0; i < count; i++)
        {
            Require(samples[i].PacketPosition >= 0 && samples[i].PayloadPosition >= 0 && samples[i].PacketBytes > 0 &&
                (i == 0 || samples[i - 1].PacketPosition != samples[i].PacketPosition), "Container sample byte positions are empty or duplicated.");
            if (i > 0) Require(samples[i - 1].PayloadPosition <= samples[i].PayloadPosition - samples[i - 1].PacketBytes,
                "Selected container sample payload byte ranges overlap.");
        }
        _seen = memory.Allocate<bool>(count); _hashScratch = memory.Allocate<byte>(8192);
    }

    /// <summary>Exact selected container sample count.</summary>
    public long SampleCount => _count;

    /// <summary>Reads nonfragmented MOV/MP4 sample tables for the exact selected track ID.</summary>
    /// <param name="source">Stable authorized caller-owned readable seekable stream.</param>
    /// <param name="trackId">Actual selected AVStream.id, matched to tkhd.</param>
    /// <param name="budget">The operation's shared, pre-admitted managed subledger.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>A disposable exact timing and packet-byte proof.</returns>
    public static MediaContainerTimingAuthority ReadMov(Stream source, int trackId, IMediaWorkingSetBudget budget,
        CancellationToken cancellationToken = default) => ReadMovCore(source, trackId, budget, cancellationToken);

    /// <summary>Reads Matroska's real DefaultDuration or BlockDuration for one exact TrackNumber.</summary>
    /// <param name="source">Stable authorized caller-owned readable seekable stream.</param>
    /// <param name="trackNumber">Real block TrackNumber, never the stream ordinal plus one.</param>
    /// <param name="budget">The operation's shared, pre-admitted managed subledger.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>A disposable exact timing and packet-byte proof.</returns>
    public static MediaContainerTimingAuthority ReadMatroska(Stream source, ulong trackNumber, IMediaWorkingSetBudget budget,
        CancellationToken cancellationToken = default) => ReadMatroskaCore(source, trackNumber, budget, cancellationToken);

    /// <summary>Verifies unique exact frame PTS, packet mapping and SHA-256 of the actual source payload.</summary>
    /// <param name="source">Same stable authorized caller-owned source.</param>
    /// <param name="position">Actual packet position from the native demuxer.</param>
    /// <param name="bytes">Actual packet size.</param>
    /// <param name="pts">Actual AVFrame.pts in its reduced packet time base.</param>
    /// <param name="packetHash">Hash of the same AVPacket copied through opaque_ref to that frame.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>Positive exact duration when present; null leaves a nonfinal interval to the next actual PTS.</returns>
    public RationalTimestamp? VerifyPacket(Stream source, long position, int bytes, RationalTimestamp pts,
        MediaDigest packetHash, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
        int lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2; MediaContainerSample sample = _samples[mid];
            if (sample.PacketPosition < position) { lo = mid + 1; continue; }
            if (sample.PacketPosition > position) { hi = mid - 1; continue; }
            if (_seen[mid] || sample.PacketBytes != bytes || _math.Compare(sample.Pts, pts) != 0)
                throw Mismatch("Decoded packet does not uniquely match its exact container time and byte mapping.");
            long saved = source.Position;
            try
            {
                source.Position = sample.PayloadPosition;
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int remaining = bytes;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested(); int take = Math.Min(remaining, _hashScratch.Length);
                    ReadExactly(source, _hashScratch.AsSpan(0, take)); hash.AppendData(_hashScratch, 0, take); remaining -= take;
                }
                string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, packetHash.LowerHex, StringComparison.Ordinal)) throw Mismatch("Decoded packet payload hash disagrees with its container source bytes.");
            }
            finally { source.Position = saved; }
            _seen[mid] = true; _verified++; return sample.Duration;
        }
        throw Mismatch("Decoded packet is absent from the selected exact container sample proof.");
    }

    /// <summary>Requires one verified decoded frame for every selected container packet.</summary>
    public void Complete()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (_verified != _count) throw Mismatch("Full decoder flush did not present every selected exact container sample."); }

    /// <summary>Adds an exact positive duration with a check after every bounded BigInteger operation.</summary>
    /// <param name="start">Original absolute presentation timestamp.</param>
    /// <param name="duration">Exact positive container duration.</param>
    /// <returns>The reduced exact endpoint without floating point.</returns>
    public static RationalTimestamp AddDuration(RationalTimestamp start, RationalTimestamp duration)
    {
        var math = new MediaPresentationTimeMath();
        Require(math.Compare(duration, new(0, new(1, 1))) > 0, "Container sample duration must be positive.");
        static BigInteger Bound(BigInteger value)
        { if (BigInteger.Abs(value).GetBitLength() > MediaPresentationTimeMath.MaximumIntermediateBits) throw Invalid("Exact endpoint exceeds bounded arithmetic."); return value; }
        BigInteger left = Bound((BigInteger)start.Ticks * start.TimeBase.Numerator);
        left = Bound(left * duration.TimeBase.Denominator);
        BigInteger right = Bound((BigInteger)duration.Ticks * duration.TimeBase.Numerator);
        right = Bound(right * start.TimeBase.Denominator);
        BigInteger numerator = Bound(left + right);
        BigInteger denominator = Bound((BigInteger)start.TimeBase.Denominator * duration.TimeBase.Denominator);
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        numerator /= divisor; denominator /= divisor;
        Require(numerator >= long.MinValue && numerator <= long.MaxValue && denominator > 0 && denominator <= int.MaxValue,
            "Exact endpoint cannot be represented by the supported reduced timestamp.");
        return new((long)numerator, RationalTimeBase.FromCanonical(1, (int)denominator));
    }

    /// <summary>Releases timing-table reservations after all source and packet users finish.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _memory.Dispose(); }

    private sealed class TimingMemory : IDisposable
    {
        private readonly IMediaWorkingSetBudget _budget;
        private readonly IMediaMemoryReservation?[] _leases;
        private int _count; private long _bytes;
        internal TimingMemory(IMediaWorkingSetBudget budget)
        { _budget = budget; IMediaMemoryReservation initial = budget.Reserve(4096, MediaWorkingSetPurpose.Metadata); _leases = new IMediaMemoryReservation?[24]; _leases[_count++] = initial; _bytes = 4096; }
        internal T[] Allocate<T>(int count) where T : unmanaged
        {
            Require(count >= 0 && _count < _leases.Length, "Timing allocation count exceeds its fixed structural profile.");
            long bytes = checked((long)Math.Max(count, 1) * Unsafe.SizeOf<T>() + 64);
            if (bytes > MaximumWorkingBytes - _bytes) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Exact timing tables exceed their admitted backing capacity.");
            IMediaMemoryReservation lease = _budget.Reserve(bytes, MediaWorkingSetPurpose.Metadata);
            _leases[_count++] = lease; _bytes += bytes;
            return new T[count];
        }
        public void Dispose() { for (int i = _count - 1; i >= 0; i--) { _leases[i]?.Dispose(); _leases[i] = null; } _count = 0; }
    }

    private static void RequireStream(Stream source)
    { ArgumentNullException.ThrowIfNull(source); Require(source.CanRead && source.CanSeek && source.Length > 0 && source.Length <= MediaAbCapacityV1.MaximumSourceBytes, "Container proof requires a bounded stable readable seekable source."); }
    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.PresentationInvalid, detail);
    private static MediaAbException Mismatch(string detail) => new(MediaAbFailure.PresentationMismatch, detail);
    private static void ReadExactly(Stream source, Span<byte> destination)
    { while (!destination.IsEmpty) { int count = source.Read(destination); if (count == 0) throw Invalid("Container metadata is truncated."); destination = destination[count..]; } }
    private static uint U32(Stream source) { Span<byte> b = stackalloc byte[4]; ReadExactly(source, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    private static ulong U64(Stream source) { Span<byte> b = stackalloc byte[8]; ReadExactly(source, b); return BinaryPrimitives.ReadUInt64BigEndian(b); }
    private static int I32(Stream source) => unchecked((int)U32(source));
    private static long I64(Stream source) => unchecked((long)U64(source));
    private static void Require(bool condition, string detail) { if (!condition) throw Invalid(detail); }
}
