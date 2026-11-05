namespace MakoKaraGenerator.Core.Media;

/// <summary>A fixed media-input algebra with no mutable cache, task, reader, lease or host-path reference.</summary>
/// <remarks>Pending and failed attempts never change in place. Available identities depend on content, not the preparation attempt.</remarks>
public abstract class MediaFeatureInput
{
    private MediaFeatureInput(MediaInputContext context, MediaInputState state, MediaInputRevision revision)
    { Context = context; State = state; InputRevision = revision; }

    /// <summary>The complete immutable request context for every state.</summary>
    public MediaInputContext Context { get; }
    /// <summary>The explicit fixed state; file and solid branches both report Available.</summary>
    public MediaInputState State { get; }
    /// <summary>The recomputable domain-separated identity of all fixed state and request fields.</summary>
    public MediaInputRevision InputRevision { get; }

    /// <summary>A fixed file preparation attempt; it cannot become available in place.</summary>
    public sealed class Pending : MediaFeatureInput
    {
        internal Pending(MediaInputContext context, MediaFeatureFileSource source, MediaPreparationRequestId request, MediaInputRevision revision)
            : base(context, MediaInputState.Pending, revision) { Source = source; Request = request; }
        /// <summary>The fully described accepted file source.</summary>
        public MediaFeatureFileSource Source { get; }
        /// <summary>The immutable owner-issued preparation attempt.</summary>
        public MediaPreparationRequestId Request { get; }
    }

    /// <summary>A fixed file artifact identity; complete bytes must separately pass the codec gate.</summary>
    public sealed class AvailableFile : MediaFeatureInput
    {
        internal AvailableFile(MediaInputContext context, MediaFeatureFileSource source, MediaArtifactIdentity artifact, MediaInputRevision revision)
            : base(context, MediaInputState.Available, revision) { Source = source; Artifact = artifact; }
        /// <summary>The fully described accepted file source.</summary>
        public MediaFeatureFileSource Source { get; }
        /// <summary>The exact canonical artifact metadata; this object does not itself verify storage bytes.</summary>
        public MediaArtifactIdentity Artifact { get; }
    }

    /// <summary>A timeless analytic solid, with no artifact or preparation attempt.</summary>
    public sealed class AvailableSolid : MediaFeatureInput
    {
        internal AvailableSolid(MediaInputContext context, MediaFeatureSolidSource source, MediaInputRevision revision)
            : base(context, MediaInputState.Available, revision) { Source = source; }
        /// <summary>The canonical analytic solid source.</summary>
        public MediaFeatureSolidSource Source { get; }
    }

    /// <summary>A fixed failed file attempt with stable failure identity and code.</summary>
    public sealed class Unavailable : MediaFeatureInput
    {
        internal Unavailable(MediaInputContext context, MediaFeatureFileSource source, MediaPreparationRequestId request,
            MediaFailure failure, MediaInputRevision revision)
            : base(context, MediaInputState.Unavailable, revision) { Source = source; Request = request; Failure = failure; }
        /// <summary>The fully described accepted file source.</summary>
        public MediaFeatureFileSource Source { get; }
        /// <summary>The immutable attempt that failed; retry requires a new request identity.</summary>
        public MediaPreparationRequestId Request { get; }
        /// <summary>The fixed failure identity and safe bounded diagnostic.</summary>
        public MediaFailure Failure { get; }
    }

    /// <summary>An explicitly absent background contribution without probing or a file SourceKey.</summary>
    public sealed class NoMedia : MediaFeatureInput
    {
        internal NoMedia(MediaInputContext context, MediaNoMediaReason reason, MediaDigest capturedBackgroundDeclaration, MediaInputRevision revision)
            : base(context, MediaInputState.NoMedia, revision) { Reason = reason; CapturedBackgroundDeclaration = capturedBackgroundDeclaration; }
        /// <summary>The fixed reason for no contribution.</summary>
        public MediaNoMediaReason Reason { get; }
        /// <summary>A captured declaration digest only; no path, arbitrary JSON, source probe or task is retained.</summary>
        public MediaDigest CapturedBackgroundDeclaration { get; }
    }

    /// <summary>Creates a fixed pending file attempt and validates all nested source, profile and mapping bindings.</summary>
    /// <param name="context">The complete fixed request context.</param>
    /// <param name="source">The fully validated file source.</param>
    /// <param name="request">The nondefault owner-issued attempt identity.</param>
    /// <returns>A fixed pending input with its canonical revision.</returns>
    public static Pending CreatePending(MediaInputContext context, MediaFeatureFileSource source, MediaPreparationRequestId request)
    {
        var value = new Pending(context, source, request, default);
        return new(context, source, request, MediaFeatureInputIdentityWriter.ComputeInputRevision(value));
    }

    /// <summary>Creates a content-addressed available file input without retaining an attempt identity.</summary>
    /// <param name="context">The complete fixed request context.</param>
    /// <param name="source">The fully validated file source.</param>
    /// <param name="artifact">The matching canonical artifact metadata.</param>
    /// <returns>An available file input; the owner must separately validate artifact bytes and hold its lease.</returns>
    public static AvailableFile CreateAvailableFile(MediaInputContext context, MediaFeatureFileSource source, MediaArtifactIdentity artifact)
    {
        var value = new AvailableFile(context, source, artifact, default);
        return new(context, source, artifact, MediaFeatureInputIdentityWriter.ComputeInputRevision(value));
    }

    /// <summary>Creates an analytic available solid using the canonical full-canvas solid mapping.</summary>
    /// <param name="context">The complete request context, with normal output, contain, loop and transparent bottom.</param>
    /// <param name="source">The canonical analytic solid with the same complete profile.</param>
    /// <returns>An available solid input without a file artifact, decoder or attempt.</returns>
    public static AvailableSolid CreateAvailableSolid(MediaInputContext context, MediaFeatureSolidSource source)
    {
        var value = new AvailableSolid(context, source, default);
        return new(context, source, MediaFeatureInputIdentityWriter.ComputeInputRevision(value));
    }

    /// <summary>Creates an immutable failed attempt; failure detail does not enter canonical identity.</summary>
    /// <param name="context">The complete fixed request context.</param>
    /// <param name="source">The fully validated file source.</param>
    /// <param name="request">The fixed failed preparation attempt.</param>
    /// <param name="failure">The fixed failure identity, code and bounded safe detail.</param>
    /// <returns>A fixed unavailable input; a retry must create another request.</returns>
    public static Unavailable CreateUnavailable(MediaInputContext context, MediaFeatureFileSource source,
        MediaPreparationRequestId request, MediaFailure failure)
    {
        var value = new Unavailable(context, source, request, failure, default);
        return new(context, source, request, failure, MediaFeatureInputIdentityWriter.ComputeInputRevision(value));
    }

    /// <summary>Creates explicit no-contribution state while retaining the complete requested profile.</summary>
    /// <param name="context">The fixed request context; transparent output must use TransparentOutput reason.</param>
    /// <param name="reason">A defined reason consistent with the fixed output policy.</param>
    /// <param name="capturedBackgroundDeclaration">A preexisting declaration digest; no source inspection is required.</param>
    /// <returns>An immutable no-media input without a file SourceKey or artifact.</returns>
    public static NoMedia CreateNoMedia(MediaInputContext context, MediaNoMediaReason reason, MediaDigest capturedBackgroundDeclaration)
    {
        var value = new NoMedia(context, reason, capturedBackgroundDeclaration, default);
        return new(context, reason, capturedBackgroundDeclaration, MediaFeatureInputIdentityWriter.ComputeInputRevision(value));
    }
}
