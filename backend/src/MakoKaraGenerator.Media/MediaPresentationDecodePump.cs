using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Same-chain native transport consumer and exact sequential presentation publisher.</summary>
/// <remarks>All pixel buffers are operation-owned and single-flight. ACK permits the helper to advance only after the awaited frame callback returns.</remarks>
internal static class MediaPresentationDecodePump
{
    /// <summary>Consumes one fully supervised native decode and returns its exact interval summary.</summary>
    /// <param name="source">Accepted immutable file source.</param>
    /// <param name="file">Stable operation-owned source stream, not transferred.</param>
    /// <param name="still">Internal still structural authority or null for video.</param>
    /// <param name="session">Operation-owned independent input pixel and metadata pipes.</param>
    /// <param name="deployment">Pinned actual loaded-file map.</param>
    /// <param name="sink">Sequential awaited consumer.</param>
    /// <param name="budget">Pre-admitted native and managed operation subledger.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>A complete exact index summary, only after native full-flush completion.</returns>
    internal static async Task<PresentationIndexSummary> RunAsync(MediaFeatureFileSource source, Stream file, MediaStillStructure? still,
        IEnvelopeSession session, MediaPresentationDeployment deployment, IMediaPresentationSink sink, DecodeOperationBudget budget,
        CancellationToken cancellationToken)
    {
        var managed = new MediaPresentationManagedBudget(budget);
        using var wire = new MediaPresentationWire(deployment.Files, managed);
        using IMediaMemoryReservation small = managed.Reserve(128 * 1024, MediaWorkingSetPurpose.Metadata);
        byte[] intervalBytes = new byte[MediaTrackLayoutV1.IntervalBytes]; byte[] hashScratch = new byte[64 * 1024]; byte[] ack = [(byte)'N'];
        using IncrementalHash indexHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        MediaContainerTimingAuthority? timing = null; FrameBuffers? buffers = null;
        MediaNativePresentationMetadata? previous = null; RationalTimestamp? previousDuration = null; RationalTimestamp first = default;
        var math = new MediaPresentationTimeMath(); long count = 0, lastEnd = 0;
        using IMediaMemoryReservation packetIdReservation = managed.Reserve(MediaAbCapacityV1.MaximumFrames + 4096, MediaWorkingSetPurpose.Metadata);
        var packetIds = new bool[(int)MediaAbCapacityV1.MaximumFrames]; long packetIdCount = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); object record = await wire.ReadNextAsync(session.Metadata, cancellationToken).ConfigureAwait(false);
                if (record is MediaNativePresentationComplete complete)
                {
                    if (!complete.FullyFlushed || complete.Frames != count || complete.Packets != count || count == 0 || previous is null || packetIdCount != count)
                        throw Invalid("Native decode did not completely flush an exact one-packet-per-presentation-frame stream.");
                    if (source.Kind == MediaSourceKind.Still)
                    {
                        if (count != 1) throw Invalid("Still decode produced more than one presentation frame.");
                        await PublishIntervalAsync(new PresentationInterval.Timeless(new(0))).ConfigureAwait(false);
                    }
                    else
                    {
                        timing!.Complete();
                        RationalTimestamp tail = previousDuration ?? throw Invalid("Final presentation frame has no real positive container duration.");
                        RationalTimestamp end = MediaContainerTimingAuthority.AddDuration(previous.Pts, tail);
                        lastEnd = await PublishTimedAsync(previous, end, PresentationEndAuthority.FrameDuration).ConfigureAwait(false);
                    }
                    await MediaPresentationWire.RequireEofAsync(session.Output, session.Metadata, cancellationToken).ConfigureAwait(false);
                    await MediaPresentationSourceVerification.VerifyAsync(file, source.Content, hashScratch, cancellationToken).ConfigureAwait(false);
                    return new(source.Key, source.Profile.Digest, source.Presentation.Digest,
                        new(Convert.ToHexStringLower(indexHash.GetHashAndReset())), count, source.Kind == MediaSourceKind.Video ? lastEnd : null);
                }
                if (record is not MediaNativePresentationMetadata frame || frame.Ordinal != count || count >= MediaAbCapacityV1.MaximumFrames ||
                    frame.PacketId < 0 || frame.PacketId >= MediaAbCapacityV1.MaximumFrames || packetIds[(int)frame.PacketId])
                    throw Invalid("Native presentation ordinal packet association or frame capacity is invalid.");
                packetIds[(int)frame.PacketId] = true; packetIdCount++;
                MediaPresentationInterpretation.VerifyFrame(source, frame, still);
                if (previous is not null && (frame.StreamId != previous.StreamId || frame.Family != previous.Family))
                    throw Invalid("Native selected stream identity or input family changed during decode.");
                RationalTimestamp? duration = null;
                if (source.Kind == MediaSourceKind.Video)
                {
                    if (count == 0)
                    {
                        first = frame.Pts;
                        timing = frame.Family switch
                        {
                            1 => MediaContainerTimingAuthority.ReadMov(file, frame.StreamId, managed, cancellationToken),
                            2 => MediaContainerTimingAuthority.ReadMatroska(file, MediaContainerTimingAuthority.ReadMatroskaTrackNumber(file, frame.PacketPosition), managed, cancellationToken),
                            _ => throw Invalid("Timed video is outside the controlled container timing profile."),
                        };
                    }
                    duration = timing!.VerifyPacket(file, frame.PacketPosition, frame.PacketBytes, frame.Pts, frame.PacketHash, cancellationToken);
                    // AVFrame.duration can be a lossy container time-base conversion. It is deliberately NOT an independent authority.
                    if (frame.DecoderDurationTicks < 0) throw Invalid("Decoder reported a negative unsupported duration.");
                    if (previous is not null)
                    {
                        if (math.Compare(previous.Pts, frame.Pts) >= 0)
                            throw Invalid("Decoded presentation PTS is not strictly increasing.");
                        // A container default may be rounded in the packet time
                        // base. Every nonfinal interval ends at next actual PTS.
                        await PublishTimedAsync(previous, frame.Pts, PresentationEndAuthority.NextPts).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (count != 0 || frame.PacketPosition != 0 || frame.PacketBytes != source.Content.ByteLength || frame.PacketHash != source.Content.Sha256)
                        throw Invalid("Still packet does not bind exactly one complete structurally validated source image.");
                }
                buffers ??= FrameBuffers.Create(frame, source, managed);
                buffers.Verify(frame);
                await buffers.ReadAsync(session.Output, cancellationToken).ConfigureAwait(false);
                long ownPixels = buffers.ByteCapacity;
                MediaSdrFrameConverter.Convert(source, buffers.Native, buffers.Canonical,
                    checked(budget.PlannedBytes - ownPixels), cancellationToken);
                var observation = new PresentationFrameObservation(new(count), source.Kind == MediaSourceKind.Video ? frame.Pts : null,
                    source.Presentation.CanonicalWidth, source.Presentation.CanonicalHeight, source.Presentation.Color.Digest);
                using (budget.BeginBorrow())
                    await sink.OnFrameAsync(new(source.Key, source.Profile.Digest, observation, buffers.Canonical), cancellationToken).ConfigureAwait(false);
                await session.Input.WriteAsync(ack, cancellationToken).ConfigureAwait(false);
                await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                previous = frame; previousDuration = duration; count++;
            }
        }
        finally { buffers?.Dispose(); timing?.Dispose(); }

        async Task PublishIntervalAsync(PresentationInterval interval)
        {
            Mft1Wire.EncodeInterval(intervalBytes, interval); indexHash.AppendData(intervalBytes);
            await sink.OnIntervalAsync(interval, cancellationToken).ConfigureAwait(false);
        }
        async Task<long> PublishTimedAsync(MediaNativePresentationMetadata frame, RationalTimestamp end, PresentationEndAuthority authority)
        {
            long startUs = math.NormalizeBoundaryUs(frame.Pts, first), endUs = math.NormalizeBoundaryUs(end, first);
            if (startUs < 0 || endUs <= startUs || endUs > MediaAbCapacityV1.MaximumVideoDurationUs || frame.Ordinal == 0 && startUs != 0 || startUs != lastEnd)
                throw Invalid("Exact presentation boundaries are not positive adjacent normalized microseconds within capacity.");
            await PublishIntervalAsync(new PresentationInterval.Timed(new(frame.Ordinal), frame.Pts, end, startUs, endUs, authority)).ConfigureAwait(false);
            lastEnd = endUs; return endUs;
        }
    }

    internal sealed class FrameBuffers : IDisposable
    {
        private readonly IMediaMemoryReservation _reservation;
        private readonly byte[] _first, _cb, _cr;
        private readonly float[] _canonical;
        private readonly int _firstStride, _cbStride, _crStride;
        private FrameBuffers(MediaNativePresentationMetadata frame, MediaFeatureFileSource source, IMediaWorkingSetBudget budget,
            Func<int, byte[]>? allocateBytes, Func<int, float[]>? allocateFloats)
        {
            int floats = MediaSdrFrameConverter.GetRequiredCanonicalFloatCount(source.Presentation.CanonicalWidth, source.Presentation.CanonicalHeight);
            long capacity = checked((long)frame.FirstBytes + frame.CbBytes + frame.CrBytes + 4L * floats);
            _reservation = budget.Reserve(checked(capacity + 1024), MediaWorkingSetPurpose.CanonicalPixels); ByteCapacity = capacity;
            try
            {
                _first = allocateBytes is null ? new byte[frame.FirstBytes] : allocateBytes(frame.FirstBytes);
                _cb = allocateBytes is null ? new byte[frame.CbBytes] : allocateBytes(frame.CbBytes);
                _cr = allocateBytes is null ? new byte[frame.CrBytes] : allocateBytes(frame.CrBytes);
                _canonical = allocateFloats is null ? new float[floats] : allocateFloats(floats);
            }
            catch { _reservation.Dispose(); throw; }
            _firstStride = frame.FirstStride; _cbStride = frame.CbStride; _crStride = frame.CrStride;
        }
        internal long ByteCapacity { get; }
        internal Memory<float> CanonicalMemory => _canonical;
        internal float[] Canonical => _canonical;
        internal MediaNativeFrameBorrow Native => new(new(_first, _firstStride), new(_cb, _cbStride), new(_cr, _crStride));
        internal static FrameBuffers Create(MediaNativePresentationMetadata f, MediaFeatureFileSource source, IMediaWorkingSetBudget budget,
            Func<int, byte[]>? allocateBytes = null, Func<int, float[]>? allocateFloats = null)
        {
            ValidatePlaneLayout(f); return new(f, source, budget, allocateBytes, allocateFloats);
        }
        internal void Verify(MediaNativePresentationMetadata f)
        { ValidatePlaneLayout(f); if (f.FirstBytes != _first.Length || f.CbBytes != _cb.Length || f.CrBytes != _cr.Length || f.FirstStride != _firstStride || f.CbStride != _cbStride || f.CrStride != _crStride) throw Invalid("Native plane capacities or strides changed during decode."); }
        internal async Task ReadAsync(Stream output, CancellationToken token)
        { await MediaPresentationWire.ReadExactlyAsync(output, _first, token).ConfigureAwait(false); await MediaPresentationWire.ReadExactlyAsync(output, _cb, token).ConfigureAwait(false); await MediaPresentationWire.ReadExactlyAsync(output, _cr, token).ConfigureAwait(false); }
        public void Dispose() => _reservation.Dispose();
    }

    internal static void ValidatePlaneLayout(MediaNativePresentationMetadata f)
    {
        bool planar = (uint)f.PixelFormat >= 5, ten = (uint)f.PixelFormat >= 8; int bytes = ten || f.PixelFormat is MediaNativePixelFormat.Rgb48Le or MediaNativePixelFormat.Rgba64Le ? 2 : 1;
        int components = f.PixelFormat is MediaNativePixelFormat.Rgba32 or MediaNativePixelFormat.Rgba64Le ? 4 : 3;
        int sx = f.PixelFormat is MediaNativePixelFormat.Yuv420P8 or MediaNativePixelFormat.Yuv422P8 or MediaNativePixelFormat.Yuv420P10Le or MediaNativePixelFormat.Yuv422P10Le ? 2 : 1;
        int sy = f.PixelFormat is MediaNativePixelFormat.Yuv420P8 or MediaNativePixelFormat.Yuv420P10Le ? 2 : 1;
        long stride = checked((long)f.Width * bytes * (planar ? 1 : components)), plane = checked(stride * f.Height);
        long chromaStride = planar ? checked(((long)f.Width + sx - 1) / sx * bytes) : 0, chroma = checked(chromaStride * ((f.Height + sy - 1L) / sy));
        if (!Enum.IsDefined(f.PixelFormat) || f.Width <= 0 || f.Height <= 0 || f.Width > MediaAbCapacityV1.MaximumSourceSide || f.Height > MediaAbCapacityV1.MaximumSourceSide || (long)f.Width * f.Height > MediaAbCapacityV1.MaximumSourcePixels ||
            f.PlaneCount != (planar ? 3 : 1) || f.FirstStride != stride || f.FirstBytes != plane || f.CbStride != chromaStride || f.CrStride != chromaStride || f.CbBytes != chroma || f.CrBytes != chroma)
            throw Invalid("Native helper planes are not exact bounded positive-stride tight top-left representations.");
    }
    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.PresentationInvalid, detail);
}

internal sealed class MediaPresentationManagedBudget(DecodeOperationBudget owner) : IMediaWorkingSetBudget
{
    public long LimitBytes => MediaAbCapacityV1.MaximumDecodeWorkingBytes;
    public IMediaMemoryReservation Reserve(long bytes, MediaWorkingSetPurpose purpose) => new Charge(owner.ReserveManaged(bytes));
    private sealed class Charge(DecodeOperationBudget.ManagedLease lease) : IMediaMemoryReservation
    { public long Bytes => lease.Bytes; public void Dispose() => lease.Dispose(); }
}
