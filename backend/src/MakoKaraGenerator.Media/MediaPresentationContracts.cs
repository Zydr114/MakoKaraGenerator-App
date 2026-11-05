using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Authorization and stable source lifetime belong to the importing owner.</summary>
public interface IMediaFileLease : IAsyncDisposable
{
    /// <summary>Exact source SHA-256 and byte length, verified before and after native operations.</summary>
    MediaFileContent Content { get; }
    /// <summary>Private no-link local path protected against writes; never serialized to authors or diagnostics.</summary>
    string TrustedLocalPath { get; }
}

/// <summary>A borrowed accepted source lease whose identity is validated on every operation.</summary>
public interface IMediaSourceLease : IMediaFileLease
{
    /// <summary>Accepted immutable source description.</summary>
    MediaFeatureFileSource Source { get; }
}

/// <summary>Internal selected-stream request; trusted missing-tag authority is supplied only by import infrastructure.</summary>
public sealed class MediaPresentationInspectionRequest
{
    /// <summary>Creates an inspection request.</summary>
    /// <param name="kind">Still or video.</param>
    /// <param name="streamIndex">Exact nonnegative selected stream index.</param>
    /// <param name="missingTagOverride">Internal complete missing-tag interpretation; contradictions cannot be overridden.</param>
    public MediaPresentationInspectionRequest(MediaSourceKind kind, int streamIndex, TrustedMediaColorInterpretation? missingTagOverride)
    { Kind = kind; StreamIndex = streamIndex; MissingTagOverride = missingTagOverride; }
    /// <summary>Still or video input semantics.</summary>
    public MediaSourceKind Kind { get; }
    /// <summary>The exact selected video stream.</summary>
    public int StreamIndex { get; }
    /// <summary>Internal authority filling genuinely missing color fields.</summary>
    public TrustedMediaColorInterpretation? MissingTagOverride { get; }
}

/// <summary>Establishes bounded source format color geometry and controlled-tool identities.</summary>
public interface IMediaPresentationInspector
{
    /// <summary>Inspects the real first decoded frame, then proves native completion and owned drain.</summary>
    /// <param name="source">Borrowed retainable file lease.</param>
    /// <param name="request">Internal selected-stream and optional trusted missing-tag authority.</param>
    /// <param name="decodeBudget">Atomic owner ledger covering the entire native and managed operation.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>A validated immutable source, without an invented frame index.</returns>
    ValueTask<MediaFeatureFileSource> InspectAsync(IMediaFileLease source, MediaPresentationInspectionRequest request,
        IMediaWorkingSetBudget decodeBudget, CancellationToken cancellationToken);
}

/// <summary>Same-chain exact-PTS decode, native resource supervision, and exact indexed frame access.</summary>
public interface IMediaPresentationDecoder
{
    /// <summary>Decodes every presentation frame and exact interval; returns only after full flush and owned drain.</summary>
    /// <param name="source">Borrowed retainable accepted source lease.</param>
    /// <param name="sink">Sequential awaited frame and interval consumer.</param>
    /// <param name="decodeBudget">Complete atomic owner ledger.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>The complete exact interval content hash and source-bound summary.</returns>
    ValueTask<PresentationIndexSummary> DecodeAsync(IMediaSourceLease source, IMediaPresentationSink sink,
        IMediaWorkingSetBudget decodeBudget, CancellationToken cancellationToken);
    /// <summary>Opens a single-flight exact reader that sequentially verifies the complete source on every read.</summary>
    /// <param name="source">Borrowed accepted source, held until returned reader disposal.</param>
    /// <param name="validatedIndex">Borrowed fully validated index, kept live by its owner.</param>
    /// <param name="decodeBudget">Complete atomic owner ledger.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>A reader that never substitutes a nearby frame.</returns>
    ValueTask<IMediaPresentationFrameSource> OpenIndexedAsync(IMediaSourceLease source, IMediaPresentationIndex validatedIndex,
        IMediaWorkingSetBudget decodeBudget, CancellationToken cancellationToken);
}
