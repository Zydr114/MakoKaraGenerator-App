using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>The fixed media mapping fields; the receiver recomputes its canonical key.</summary>
public sealed record ProjectRuntimeMediaMapping(int CanvasWidth, int CanvasHeight, BackgroundFit Fit,
    BackgroundPlaybackMode Playback, LinearPremulRgba BackColor, MediaOutputMode OutputMode);

/// <summary>
/// Host-private transport of one fixed <see cref="MediaFeatureInput"/> plus its private artifact file.
/// Every identity is recomputed by the Core factories on receipt and must equal <see cref="InputRevision"/>.
/// </summary>
public sealed record ProjectRuntimeMediaGrant(
    string State,
    MediaProfileBundle RequestedProfile,
    ProjectRuntimeMediaMapping Mapping,
    MediaDigest QueryCatalogDigest,
    MediaDigest FixedDependencyDigest,
    MediaDigest InputRevision,
    MediaFeatureFileSource? Source = null,
    MediaArtifactIdentity? Artifact = null,
    string? ArtifactPath = null,
    MediaDigest? Request = null,
    MediaDigest? FailureId = null,
    string? FailureCode = null,
    string? FailureDetail = null,
    LinearPremulRgba? SolidColor = null,
    MediaNoMediaReason? NoMediaReason = null,
    MediaDigest? CapturedBackgroundDeclaration = null)
{
    /// <summary>Pending file analysis.</summary>
    public const string PendingState = "pending";
    /// <summary>Available file artifact.</summary>
    public const string AvailableFileState = "availableFile";
    /// <summary>Available analytic solid.</summary>
    public const string AvailableSolidState = "availableSolid";
    /// <summary>Failed file analysis.</summary>
    public const string UnavailableState = "unavailable";
    /// <summary>No background contribution.</summary>
    public const string NoMediaState = "noMedia";

    /// <summary>Captures a fixed input; only an available file carries a private artifact path.</summary>
    public static ProjectRuntimeMediaGrant From(MediaFeatureInput input, string? artifactPath)
    {
        ArgumentNullException.ThrowIfNull(input);
        MediaFeatureInputValidator.Validate(input);
        MediaMapping map = input.Context.Mapping;
        var basis = new ProjectRuntimeMediaGrant(PendingState, input.Context.RequestedProfile,
            new(map.CanvasWidth, map.CanvasHeight, map.Fit, map.Playback, map.BackColor, map.OutputMode),
            input.Context.QueryCatalogDigest, input.Context.FixedDependencyDigest, input.InputRevision.Digest);
        if (input is not MediaFeatureInput.AvailableFile && artifactPath is not null)
            throw new ArgumentException("Only an available file input carries an artifact path.", nameof(artifactPath));
        return input switch
        {
            MediaFeatureInput.Pending value => basis with { Source = value.Source, Request = value.Request.Digest },
            MediaFeatureInput.AvailableFile value => basis with { State = AvailableFileState, Source = value.Source, Artifact = value.Artifact,
                ArtifactPath = RequirePath(artifactPath) },
            MediaFeatureInput.AvailableSolid value => basis with { State = AvailableSolidState, SolidColor = value.Source.Color },
            MediaFeatureInput.Unavailable value => basis with { State = UnavailableState, Source = value.Source, Request = value.Request.Digest,
                FailureId = value.Failure.Id.Digest, FailureCode = value.Failure.Code, FailureDetail = value.Failure.SafeDetail },
            MediaFeatureInput.NoMedia value => basis with { State = NoMediaState, NoMediaReason = value.Reason,
                CapturedBackgroundDeclaration = value.CapturedBackgroundDeclaration },
            _ => throw new ArgumentException("Unknown media input state.", nameof(input)),
        };
    }

    /// <summary>Recreates the fixed input through the Core factories and checks the complete revision.</summary>
    public MediaFeatureInput ToInput()
    {
        MediaMapping mapping = MediaMapping.Create(Mapping.CanvasWidth, Mapping.CanvasHeight, Mapping.Fit, Mapping.Playback,
            Mapping.BackColor, Mapping.OutputMode);
        MediaInputContext context = MediaInputContext.Create(RequestedProfile, mapping, QueryCatalogDigest, FixedDependencyDigest);
        bool file = State is PendingState or AvailableFileState or UnavailableState;
        if (file != Source is not null || (State == AvailableFileState) != (Artifact is not null || ArtifactPath is not null)
            || (State is PendingState or UnavailableState) != Request is not null
            || (State == UnavailableState) != (FailureId is not null || FailureCode is not null || FailureDetail is not null)
            || (State == AvailableSolidState) != SolidColor is not null
            || (State == NoMediaState) != (NoMediaReason is not null || CapturedBackgroundDeclaration is not null))
            throw Invalid();
        MediaFeatureInput input = State switch
        {
            PendingState => MediaFeatureInput.CreatePending(context, Source!, new(Request!.Value)),
            AvailableFileState => MediaFeatureInput.CreateAvailableFile(context, Source!, Artifact ?? throw Invalid()),
            AvailableSolidState => MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(RequestedProfile, SolidColor!.Value)),
            UnavailableState => MediaFeatureInput.CreateUnavailable(context, Source!, new(Request!.Value),
                MediaFailure.Create(new(FailureId ?? throw Invalid()), FailureCode ?? throw Invalid(), FailureDetail ?? throw Invalid())),
            NoMediaState => MediaFeatureInput.CreateNoMedia(context, NoMediaReason ?? throw Invalid(), CapturedBackgroundDeclaration ?? throw Invalid()),
            _ => throw Invalid(),
        };
        MediaFeatureInputValidator.Validate(input);
        if (input.InputRevision.Digest != InputRevision) throw Invalid();
        if (ArtifactPath is not null) RequirePath(ArtifactPath);
        return input;
    }

    private static string RequirePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw Invalid();
        return path;
    }

    private static MediaAbException Invalid() => new(MediaAbFailure.ProfileUnsupported, "Runtime media grant is not one complete fixed input.");
}
