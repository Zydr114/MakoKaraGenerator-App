using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Abstractions;

/// <summary>A zero-based contiguous decoder presentation ordinal.</summary>
public readonly record struct PresentationFrameId
{
    /// <summary>Creates the read-only contract value. Accepted boundaries validate all fields.</summary>
    /// <param name="index">The decoder presentation index after frame reordering.</param>
    public PresentationFrameId(
        long index)
    {
        Index = index;
    }

    /// <summary>The decoder presentation index after frame reordering.</summary>
    public long Index { get; }

}

/// <summary>The exact evidence establishing an interval endpoint.</summary>
public enum PresentationEndAuthority : uint
{
    /// <summary>The next actual decoded presentation timestamp, with hold semantics.</summary>
    NextPts = 1,
    /// <summary>A trusted exact duration of the final decoded frame.</summary>
    FrameDuration = 2,
    /// <summary>A trusted exact selected-stream endpoint for the final frame.</summary>
    StreamEndpoint = 3,
    /// <summary>A single still frame without invented time.</summary>
    Timeless = 4,
}

/// <summary>One controlled decoded frame observation.</summary>
public sealed record PresentationFrameObservation
{
    /// <summary>Creates the read-only contract value. Accepted boundaries validate all fields.</summary>
    /// <param name="id">The contiguous decoder presentation ordinal.</param>
    /// <param name="rawPts">The exact video PTS; absent for a timeless still.</param>
    /// <param name="width">The canonical rotated raster width.</param>
    /// <param name="height">The canonical rotated raster height.</param>
    /// <param name="colorInterpretationDigest">The fixed color identity of this frame.</param>
    public PresentationFrameObservation(
        PresentationFrameId id,
        RationalTimestamp? rawPts,
        int width,
        int height,
        MediaDigest colorInterpretationDigest)
    {
        Id = id;
        RawPts = rawPts;
        Width = width;
        Height = height;
        ColorInterpretationDigest = colorInterpretationDigest;
    }

    /// <summary>The contiguous decoder presentation ordinal.</summary>
    public PresentationFrameId Id { get; }

    /// <summary>The exact video PTS; absent for a timeless still.</summary>
    public RationalTimestamp? RawPts { get; }

    /// <summary>The canonical rotated raster width.</summary>
    public int Width { get; }

    /// <summary>The canonical rotated raster height.</summary>
    public int Height { get; }

    /// <summary>The fixed color identity of this frame.</summary>
    public MediaDigest ColorInterpretationDigest { get; }

}

/// <summary>A callback-scoped borrow of top-left packed RGBA32F linear premultiplied pixels.</summary>
/// <remarks>The memory has exactly width times height times four elements. Consumers cannot retain it, and producers cannot mutate it before the consumer returns. This value does not transfer native ownership.</remarks>
public sealed record PresentationFrameBorrow
{
    /// <summary>Creates the read-only contract value. Accepted boundaries validate all fields.</summary>
    /// <param name="sourceKey">The accepted source key.</param>
    /// <param name="profileDigest">The accepted profile digest.</param>
    /// <param name="observation">The exact frame metadata paired with these pixels.</param>
    /// <param name="linearPremulRgba">The packed pixel memory valid only until the awaited consumer returns.</param>
    public PresentationFrameBorrow(
        MediaSourceKey sourceKey,
        MediaDigest profileDigest,
        PresentationFrameObservation observation,
        ReadOnlyMemory<float> linearPremulRgba)
    {
        SourceKey = sourceKey;
        ProfileDigest = profileDigest;
        Observation = observation;
        LinearPremulRgba = linearPremulRgba;
    }

    /// <summary>The accepted source key.</summary>
    public MediaSourceKey SourceKey { get; }

    /// <summary>The accepted profile digest.</summary>
    public MediaDigest ProfileDigest { get; }

    /// <summary>The exact frame metadata paired with these pixels.</summary>
    public PresentationFrameObservation Observation { get; }

    /// <summary>The packed pixel memory valid only until the awaited consumer returns.</summary>
    public ReadOnlyMemory<float> LinearPremulRgba { get; }

}

/// <summary>A display interval retaining absolute raw source evidence.</summary>
public abstract record PresentationInterval
{
    /// <summary>Creates an interval for one presentation ordinal.</summary>
    /// <param name="id">The exact frame ordinal.</param>
    protected PresentationInterval(PresentationFrameId id) => Id = id;

    /// <summary>The exact frame ordinal selected during this interval.</summary>
    public PresentationFrameId Id { get; }

    /// <summary>A half-open canonical interval with exact absolute raw endpoints.</summary>
    public sealed record Timed : PresentationInterval
    {
        /// <summary>Creates a timed interval. Accepted boundaries must validate exact normalization and adjacency.</summary>
        /// <param name="id">The exact frame ordinal.</param>
        /// <param name="rawStart">The actual decoded source PTS.</param>
        /// <param name="rawEnd">The exact absolute raw endpoint established by the authority.</param>
        /// <param name="startUs">The normalized inclusive boundary.</param>
        /// <param name="endUs">The normalized exclusive boundary.</param>
        /// <param name="endAuthority">NextPts for nonfinal frames; FrameDuration or StreamEndpoint for the final frame.</param>
        public Timed(PresentationFrameId id, RationalTimestamp rawStart, RationalTimestamp rawEnd,
            long startUs, long endUs, PresentationEndAuthority endAuthority) : base(id)
        {
            RawStart = rawStart;
            RawEnd = rawEnd;
            StartUs = startUs;
            EndUs = endUs;
            EndAuthority = endAuthority;
        }

        /// <summary>The actual decoded source PTS.</summary>
        public RationalTimestamp RawStart { get; }
        /// <summary>The exact absolute endpoint established by the authority.</summary>
        public RationalTimestamp RawEnd { get; }
        /// <summary>The normalized inclusive boundary.</summary>
        public long StartUs { get; }
        /// <summary>The normalized exclusive boundary.</summary>
        public long EndUs { get; }
        /// <summary>The exact source of endpoint evidence.</summary>
        public PresentationEndAuthority EndAuthority { get; }
    }

    /// <summary>The sole still frame without invented FPS or duration.</summary>
    public sealed record Timeless : PresentationInterval
    {
        /// <summary>Creates a timeless frame contract.</summary>
        /// <param name="id">Frame zero for a valid still.</param>
        public Timeless(PresentationFrameId id) : base(id) { }
    }
}

/// <summary>The drained presentation index identity shared by decode, codec and exact frame readers.</summary>
public sealed record PresentationIndexSummary
{
    /// <summary>Creates the read-only contract value. Accepted boundaries validate all fields.</summary>
    /// <param name="sourceKey">The accepted source key.</param>
    /// <param name="profileDigest">The accepted profile digest.</param>
    /// <param name="sourceDescriptorDigest">The accepted source descriptor digest.</param>
    /// <param name="indexDigest">SHA-256 of exact canonical interval bytes without a domain prefix.</param>
    /// <param name="frameCount">The complete presentation frame count.</param>
    /// <param name="videoDurationUs">The complete canonical video endpoint; absent for a still.</param>
    public PresentationIndexSummary(
        MediaSourceKey sourceKey,
        MediaDigest profileDigest,
        MediaDigest sourceDescriptorDigest,
        MediaDigest indexDigest,
        long frameCount,
        long? videoDurationUs)
    {
        SourceKey = sourceKey;
        ProfileDigest = profileDigest;
        SourceDescriptorDigest = sourceDescriptorDigest;
        IndexDigest = indexDigest;
        FrameCount = frameCount;
        VideoDurationUs = videoDurationUs;
    }

    /// <summary>The accepted source key.</summary>
    public MediaSourceKey SourceKey { get; }

    /// <summary>The accepted profile digest.</summary>
    public MediaDigest ProfileDigest { get; }

    /// <summary>The accepted source descriptor digest.</summary>
    public MediaDigest SourceDescriptorDigest { get; }

    /// <summary>SHA-256 of exact canonical interval bytes without a domain prefix.</summary>
    public MediaDigest IndexDigest { get; }

    /// <summary>The complete presentation frame count.</summary>
    public long FrameCount { get; }

    /// <summary>The complete canonical video endpoint; absent for a still.</summary>
    public long? VideoDurationUs { get; }

}

/// <summary>A strictly serial sink receiving F0, I0, F1, I1 and so on.</summary>
/// <remarks>No callback overlaps another. A producer publishes the previous interval before the next frame. Both callback counts must match after a successful complete decode.</remarks>
public interface IMediaPresentationSink
{
    /// <summary>Consumes one borrowed canonical frame while its memory remains valid.</summary>
    /// <param name="frame">The exact observed frame and callback-scoped pixels.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged through the consumer.</param>
    /// <returns>Completion of all uses of the borrowed frame memory.</returns>
    ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken cancellationToken);

    /// <summary>Consumes the interval of the immediately preceding frame.</summary>
    /// <param name="interval">The fully validated exact interval.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>Completion of interval consumption.</returns>
    ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken cancellationToken);
}

/// <summary>A read-only borrowed validated presentation index.</summary>
/// <remarks>The caller keeps its owner alive through every frame-source and query operation.</remarks>
public interface IMediaPresentationIndex
{
    /// <summary>The complete validated index summary.</summary>
    PresentationIndexSummary Summary { get; }

    /// <summary>Reads the exact interval for the requested ordinal.</summary>
    /// <param name="id">An ordinal in the validated index.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>The exact validated interval.</returns>
    ValueTask<PresentationInterval> ReadIntervalAsync(PresentationFrameId id, CancellationToken cancellationToken);

    /// <summary>Selects the unique frame whose half-open interval contains a source time.</summary>
    /// <param name="sourceTimeUs">For video, zero through duration minus one; for stills, any nonnegative value.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>The exact selected ordinal; a still always returns zero.</returns>
    ValueTask<PresentationFrameId> SelectAsync(long sourceTimeUs, CancellationToken cancellationToken);
}

/// <summary>A single-flight exact canonical frame reader borrowing its source and validated index.</summary>
/// <remarks>Dispose this reader before releasing either borrowed lease. Returning a nearby seek frame is forbidden.</remarks>
public interface IMediaPresentationFrameSource : IAsyncDisposable
{
    /// <summary>The validated source and index identity to which all reads are bound.</summary>
    PresentationIndexSummary Index { get; }

    /// <summary>Reads and verifies the exact ordinal, raw PTS, dimensions and color before invoking the consumer.</summary>
    /// <param name="id">The exact validated presentation ordinal.</param>
    /// <param name="consume">The awaited callback; it cannot retain the borrowed memory.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged through decode and consumption.</param>
    /// <returns>Completion of the verified frame callback and all borrowed pixel uses.</returns>
    ValueTask ReadFrameAsync(PresentationFrameId id,
        Func<PresentationFrameBorrow, CancellationToken, ValueTask> consume, CancellationToken cancellationToken);
}

/// <summary>Explicit input to the canonical background composition path.</summary>
public sealed record CanonicalBackgroundFrameRequest
{
    /// <summary>Creates the read-only contract value. Accepted boundaries validate all fields.</summary>
    /// <param name="sourceKey">The exact accepted source key.</param>
    /// <param name="profileDigest">The exact accepted feature profile digest.</param>
    /// <param name="frame">The exact validated frame ordinal.</param>
    /// <param name="compositeProfile">The explicit versioned linear source-over profile.</param>
    /// <param name="canvasWidth">The positive output canvas width.</param>
    /// <param name="canvasHeight">The positive output canvas height.</param>
    /// <param name="fit">The existing display geometry fit policy.</param>
    /// <param name="backColor">The canonical linear premultiplied canvas background color.</param>
    public CanonicalBackgroundFrameRequest(
        MediaSourceKey sourceKey,
        MediaDigest profileDigest,
        PresentationFrameId frame,
        string compositeProfile,
        int canvasWidth,
        int canvasHeight,
        BackgroundFit fit,
        LinearPremulRgba backColor)
    {
        SourceKey = sourceKey;
        ProfileDigest = profileDigest;
        Frame = frame;
        CompositeProfile = compositeProfile;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Fit = fit;
        BackColor = backColor;
    }

    /// <summary>The exact accepted source key.</summary>
    public MediaSourceKey SourceKey { get; }

    /// <summary>The exact accepted feature profile digest.</summary>
    public MediaDigest ProfileDigest { get; }

    /// <summary>The exact validated frame ordinal.</summary>
    public PresentationFrameId Frame { get; }

    /// <summary>The explicit versioned linear source-over profile.</summary>
    public string CompositeProfile { get; }

    /// <summary>The positive output canvas width.</summary>
    public int CanvasWidth { get; }

    /// <summary>The positive output canvas height.</summary>
    public int CanvasHeight { get; }

    /// <summary>The existing display geometry fit policy.</summary>
    public BackgroundFit Fit { get; }

    /// <summary>The canonical linear premultiplied canvas background color.</summary>
    public LinearPremulRgba BackColor { get; }

}

/// <summary>Consumes canonical pixels while borrowed and composites in linear light before target encoding.</summary>
public interface ICanonicalBackgroundFrameConsumer
{
    /// <summary>Draws the exact canonical background with the accepted source display geometry.</summary>
    /// <param name="request">The explicit accepted draw request.</param>
    /// <param name="frame">The exact paired callback-scoped canonical pixels.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>Completion of drawing and all borrowed pixel uses.</returns>
    ValueTask DrawAsync(CanonicalBackgroundFrameRequest request, PresentationFrameBorrow frame,
        CancellationToken cancellationToken);
}
