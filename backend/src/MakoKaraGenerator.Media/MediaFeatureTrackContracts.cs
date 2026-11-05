using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>The owner ledger category for simultaneously live media backing.</summary>
public enum MediaWorkingSetPurpose
{
    /// <summary>Native decoder envelope backing.</summary>
    DecoderNative,
    /// <summary>Borrowed canonical pixel backing.</summary>
    CanonicalPixels,
    /// <summary>Canonical color conversion backing.</summary>
    Conversion,
    /// <summary>Bounded decoder metadata backing.</summary>
    Metadata,
    /// <summary>Builder and live section-copy backing.</summary>
    Builder,
    /// <summary>Canonical writer or validation backing.</summary>
    CodecValidation,
    /// <summary>Validated reader backing, including its initial validation pass.</summary>
    ReaderCache,
}

/// <summary>An owner-issued charge that remains live until its backing users drain.</summary>
public interface IMediaMemoryReservation : IDisposable
{
    /// <summary>The exact owner-ledger charge held by this reservation.</summary>
    long Bytes { get; }
}

/// <summary>A shared atomic owner ledger; refusal occurs before allocating the charged backing.</summary>
public interface IMediaWorkingSetBudget
{
    /// <summary>The operation ceiling; it cannot exceed the applicable fixed profile ceiling.</summary>
    long LimitBytes { get; }
    /// <summary>Atomically admits simultaneous live backing before its allocation or operation begins.</summary>
    /// <param name="bytes">Positive bytes to reserve.</param>
    /// <param name="purpose">The owning operation category.</param>
    /// <returns>A charge released only after every user of the owned backing drains.</returns>
    /// <exception cref="MediaAbException">The owner refuses admission with CapacityExceeded.</exception>
    IMediaMemoryReservation Reserve(long bytes, MediaWorkingSetPurpose purpose);
}

/// <summary>Forwarding names for the one Core-owned wire layout.</summary>
public static class MediaTrackV1
{
    /// <summary>Fixed complete header bytes.</summary>
    public const int HeaderBytes = MediaTrackLayoutV1.HeaderBytes;
    /// <summary>One explicit canonical interval record.</summary>
    public const int IntervalBytes = MediaTrackLayoutV1.IntervalBytes;
    /// <summary>Source-space grid columns.</summary>
    public const int Columns = MediaTrackLayoutV1.Columns;
    /// <summary>Source-space grid rows.</summary>
    public const int Rows = MediaTrackLayoutV1.Rows;
    /// <summary>Grid cells.</summary>
    public const int Cells = MediaTrackLayoutV1.Cells;
    /// <summary>Raw moments per cell.</summary>
    public const int Moments = MediaTrackLayoutV1.Moments;
    /// <summary>Wire cell bytes, including canonical reserved bytes.</summary>
    public const int CellBytes = MediaTrackLayoutV1.CellBytes;
    /// <summary>One explicit wire grid.</summary>
    public const int FrameGridBytes = MediaTrackLayoutV1.FrameGridBytes;
    /// <summary>Unsigned sixteen-bit quantization maximum.</summary>
    public const int QuantMax = MediaTrackLayoutV1.QuantMax;
    /// <summary>Frames per temporal prefix block.</summary>
    public const int BlockFrames = MediaTrackLayoutV1.BlockFrames;
    /// <summary>One explicit prefix entry.</summary>
    public const int PrefixEntryBytes = MediaTrackLayoutV1.PrefixEntryBytes;
}

/// <summary>An engineering time and bounded trailing window, both expressed in integer microseconds.</summary>
/// <param name="ProjectTimeUs">Signed engineering time.</param>
/// <param name="WindowUs">Trailing window from zero through the fixed two-second ceiling.</param>
/// <param name="Playback">A recognized background playback policy.</param>
public readonly record struct MediaTemporalQuery(long ProjectTimeUs, long WindowUs, BackgroundPlaybackMode Playback);

/// <summary>The three independently bounded canonical section spools.</summary>
public enum MediaTrackSection
{
    /// <summary>Explicit presentation records.</summary>
    Intervals,
    /// <summary>Quantized frame grids.</summary>
    Grids,
    /// <summary>Exact cumulative temporal blocks.</summary>
    Prefixes,
}

/// <summary>C owns this reservation, including simultaneous section spools and the final candidate.</summary>
public interface IMediaTrackScratchReservation : IAsyncDisposable
{
    /// <summary>C-owned admission for simultaneous section spools and the final candidate, at most one GiB.</summary>
    long ReservedByteBudget { get; }
    /// <summary>Opens one empty independent seekable readable writable stream per section, charging before growth.</summary>
    /// <param name="section">The section, opened at most once.</param>
    /// <param name="maximumBytes">The section growth ceiling already covered by the shared reservation.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    /// <returns>A bounded stream owned by B until successful builder transfer, then by the write source.</returns>
    ValueTask<Stream> OpenSectionAsync(MediaTrackSection section, long maximumBytes, CancellationToken cancellationToken);
}

/// <summary>Serial frame and interval consumer retaining at most one pending quantized grid.</summary>
public interface IMediaFeatureTrackBuilder : IMediaPresentationSink, IAsyncDisposable
{
    /// <summary>Completes once after decoder drain, checks the exact summary, and transfers only section-stream ownership.</summary>
    /// <param name="presentation">The drained decoder's exact source-bound index summary.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    /// <returns>The immutable repeatable section source; C continues to own the scratch reservation.</returns>
    ValueTask<IMediaFeatureTrackWriteSource> CompleteAsync(PresentationIndexSummary presentation, CancellationToken cancellationToken);
}

/// <summary>Owns completed section spools and serializes repeatable copies without owning any destination.</summary>
public interface IMediaFeatureTrackWriteSource : IAsyncDisposable
{
    /// <summary>The fully validated immutable source identity.</summary>
    MediaFeatureFileSource Source { get; }
    /// <summary>The exact drained presentation summary bound to these sections.</summary>
    PresentationIndexSummary Presentation { get; }
    /// <summary>Rewinds and copies every interval byte. The caller retains target ownership.</summary>
    /// <param name="target">An independent writable destination.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    ValueTask CopyIntervalsToAsync(Stream target, CancellationToken cancellationToken);
    /// <summary>Rewinds and copies every frame-grid byte. The caller retains target ownership.</summary>
    /// <param name="target">An independent writable destination.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    ValueTask CopyGridsToAsync(Stream target, CancellationToken cancellationToken);
    /// <summary>Rewinds and copies every prefix byte, or no bytes for a still.</summary>
    /// <param name="target">An independent writable destination.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    ValueTask CopyPrefixesToAsync(Stream target, CancellationToken cancellationToken);
}

/// <summary>Streaming canonical persistence and the full immutable-reader validation gate.</summary>
public interface IMediaFeatureTrackCodec
{
    /// <summary>Admits bounded builder backing and validates the accepted source before any callback.</summary>
    /// <param name="source">The accepted still or video source.</param>
    /// <param name="scratch">C-owned bounded scratch and candidate reservation.</param>
    /// <param name="builderBudget">An atomic ledger no larger than the fixed builder ceiling.</param>
    /// <returns>A serial callback sink that owns only its opened spools.</returns>
    IMediaFeatureTrackBuilder CreateBuilder(MediaFeatureFileSource source, IMediaTrackScratchReservation scratch,
        IMediaWorkingSetBudget builderBudget);
    /// <summary>Writes the exact header and three bounded sections without whole-track buffering or publication.</summary>
    /// <param name="source">A completed repeatable section source, retained by the caller.</param>
    /// <param name="target">A caller-owned empty writable stream at its origin. Nonseekable emptiness is a caller precondition.</param>
    /// <param name="codecBudget">An atomic ledger no larger than the fixed builder ceiling.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping; caller discards any partial target.</param>
    /// <returns>The external complete-byte identity. Opening still requires the full validation gate.</returns>
    ValueTask<MediaArtifactIdentity> WriteCanonicalAsync(IMediaFeatureTrackWriteSource source, Stream target,
        IMediaWorkingSetBudget codecBudget, CancellationToken cancellationToken);
    /// <summary>Checks exact layout, complete hash, every canonical interval and grid, and all recomputed prefix sums.</summary>
    /// <param name="seekableSource">Stable readable seekable storage with writing disabled; failure preserves caller ownership.</param>
    /// <param name="source">The accepted immutable source identity, revalidated by this operation.</param>
    /// <param name="expected">The external complete artifact identity.</param>
    /// <param name="readerBudget">An atomic ledger no larger than the fixed runtime CPU ceiling.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    /// <returns>A reader that takes input-stream ownership only after every check passes.</returns>
    ValueTask<IMediaFeatureTrackReader> OpenValidatedAsync(Stream seekableSource, MediaFeatureFileSource source,
        MediaArtifactIdentity expected, IMediaWorkingSetBudget readerBudget, CancellationToken cancellationToken);
}

/// <summary>Serialized immutable index and temporal-grid queries over fully validated canonical storage.</summary>
public interface IMediaFeatureTrackReader : IMediaPresentationIndex, IAsyncDisposable
{
    /// <summary>The exact external identity whose complete bytes passed validation.</summary>
    MediaArtifactIdentity Artifact { get; }
    /// <summary>Reads seven raw means per cell using exact weighted integers until the final normalization.</summary>
    /// <param name="query">A recognized policy and bounded engineering-time window.</param>
    /// <param name="destination">Exactly 576 cells; partial results must be discarded on failure or cancellation.</param>
    /// <param name="cancellationToken">Cancellation propagated without wrapping.</param>
    ValueTask ReadTemporalGridAsync(MediaTemporalQuery query, Memory<MediaMomentValues> destination,
        CancellationToken cancellationToken);
}
