using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Media;

/// <summary>Accepted-boundary validation that independently recomputes nested input, mapping, source and profile identities.</summary>
/// <remarks>Internal consistency never grants file, tool, color authority or proof of unavailable artifact bytes.</remarks>
public static class MediaFeatureInputValidator
{
    /// <summary>Validates every mapping field and recomputes the complete mapping key.</summary>
    /// <param name="value">The immutable mapping to revalidate.</param>
    public static void ValidateMapping(MediaMapping value)
    {
        MediaFeatureInputRules.ValidateMappingShape(value);
        MediaAbRules.ValidateDigest(value.Key.Digest, MediaAbFailure.ProfileUnsupported);
        RequireEqual(value.Key.Digest, MediaFeatureInputIdentityWriter.ComputeMappingKey(value).Digest, "Mapping key does not match its canonical fields.");
    }

    /// <summary>Validates the complete solid profile and canonical color, then recomputes its solid-domain key.</summary>
    /// <param name="value">The immutable analytic source.</param>
    public static void ValidateSolidSource(MediaFeatureSolidSource value)
    {
        MediaFeatureInputRules.ValidateSolidShape(value);
        MediaAbRules.ValidateDigest(value.Key.Digest, MediaAbFailure.SourceChanged);
        if (value.Key != MediaFeatureInputIdentityWriter.ComputeSolidSourceKey(value))
            throw new MediaAbException(MediaAbFailure.SourceChanged, "Solid source key does not match its canonical fields.");
    }

    /// <summary>Validates all state invariants and nested bindings and recomputes the fixed input revision.</summary>
    /// <param name="value">The immutable fixed input to accept.</param>
    public static void Validate(MediaFeatureInput value)
    {
        MediaFeatureInputRules.ValidateInputShape(value);
        MediaAbRules.ValidateDigest(value.InputRevision.Digest, MediaAbFailure.ProfileUnsupported);
        RequireEqual(value.InputRevision.Digest, MediaFeatureInputIdentityWriter.ComputeInputRevision(value).Digest,
            "Input revision does not match its canonical fields.");
    }

    private static void RequireEqual(MediaDigest actual, MediaDigest expected, string detail)
    {
        if (actual != expected) throw new MediaAbException(MediaAbFailure.ProfileUnsupported, detail);
    }
}

internal static class MediaFeatureInputRules
{
    internal static void ValidateMappingShape(MediaMapping value)
    {
        ArgumentNullException.ThrowIfNull(value);
        MediaAbRules.ValidateDimensions(value.CanvasWidth, value.CanvasHeight);
        if (!Enum.IsDefined(value.Fit) || !Enum.IsDefined(value.Playback) || !Enum.IsDefined(value.OutputMode))
            throw Invalid("Mapping contains an unknown fit playback or output policy.");
        MediaAbValueValidator.ValidateLinearPremul(value.BackColor);
        if (value.GeometryProfile != MediaProfilesV1.Geometry || value.CompositeProfile != MediaProfilesV1.BackgroundComposite ||
            value.SamplingProfile != MediaProfilesV1.Query)
            throw Invalid("Mapping does not use the complete supported geometry composition and area sampling profiles.");
    }

    internal static void ValidateSolidShape(MediaFeatureSolidSource value)
    {
        ArgumentNullException.ThrowIfNull(value);
        new MediaAbValueValidator().ValidateProfile(value.Profile);
        MediaAbValueValidator.ValidateLinearPremul(value.Color);
    }

    internal static void ValidateContext(MediaInputContext value)
    {
        ArgumentNullException.ThrowIfNull(value);
        new MediaAbValueValidator().ValidateProfile(value.RequestedProfile);
        MediaFeatureInputValidator.ValidateMapping(value.Mapping);
        MediaAbRules.ValidateDigest(value.QueryCatalogDigest, MediaAbFailure.ProfileUnsupported);
        MediaAbRules.ValidateDigest(value.FixedDependencyDigest, MediaAbFailure.ProfileUnsupported);
        if (value.RequestedProfile.Query != value.Mapping.SamplingProfile)
            throw Invalid("Requested query profile does not match its fixed mapping.");
    }

    internal static void ValidateFailure(MediaFailure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        MediaAbRules.ValidateDigest(value.Id.Digest, MediaAbFailure.ProfileUnsupported);
        if (string.IsNullOrEmpty(value.Code) || value.Code.Length > 64 || value.Code.Any(static c =>
            c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw Invalid("Failure code must contain one through sixty four restricted ASCII characters.");
        MediaAbRules.ValidateCanonicalText(value.SafeDetail, MediaAbFailure.ProfileUnsupported);
        if (string.IsNullOrWhiteSpace(value.SafeDetail) || value.SafeDetail.Length > 1024 ||
            value.SafeDetail.Any(char.IsControl) || value.SafeDetail.Contains('/') || value.SafeDetail.Contains('\\') || value.SafeDetail.Contains(':'))
            throw Invalid("Failure detail must be bounded safe text without controls or path delimiters.");
    }

    internal static void ValidateInputShape(MediaFeatureInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        MediaInputState expectedState = value switch
        {
            MediaFeatureInput.Pending => MediaInputState.Pending,
            MediaFeatureInput.AvailableFile or MediaFeatureInput.AvailableSolid => MediaInputState.Available,
            MediaFeatureInput.Unavailable => MediaInputState.Unavailable,
            MediaFeatureInput.NoMedia => MediaInputState.NoMedia,
            _ => throw Invalid("Media input branch is not a recognized fixed state."),
        };
        if (value.State != expectedState) throw Invalid("Media input state does not agree with its fixed branch.");
        ValidateContext(value.Context);
        switch (value)
        {
            case MediaFeatureInput.Pending pending:
                RequireNormal(value); ValidateFile(value.Context, pending.Source);
                MediaAbRules.ValidateDigest(pending.Request.Digest, MediaAbFailure.ProfileUnsupported);
                break;
            case MediaFeatureInput.AvailableFile available:
                RequireNormal(value); ValidateFile(value.Context, available.Source);
                new MediaAbValueValidator().ValidateArtifactIdentity(available.Source, available.Artifact);
                break;
            case MediaFeatureInput.AvailableSolid solid:
                RequireNormal(value);
                MediaFeatureInputValidator.ValidateSolidSource(solid.Source);
                RequireProfile(value.Context, solid.Source.Profile);
                MediaMapping map = value.Context.Mapping;
                if (map.Fit != BackgroundFit.Contain || map.Playback != BackgroundPlaybackMode.Loop || map.BackColor != default)
                    throw Invalid("Analytic solid requires canonical full canvas mapping with a transparent bottom.");
                break;
            case MediaFeatureInput.Unavailable unavailable:
                RequireNormal(value); ValidateFile(value.Context, unavailable.Source);
                MediaAbRules.ValidateDigest(unavailable.Request.Digest, MediaAbFailure.ProfileUnsupported);
                ValidateFailure(unavailable.Failure);
                break;
            case MediaFeatureInput.NoMedia absent:
                if (!Enum.IsDefined(absent.Reason) ||
                    (absent.Reason == MediaNoMediaReason.TransparentOutput) != (value.Context.Mapping.OutputMode == MediaOutputMode.Transparent))
                    throw Invalid("No media reason must agree with the captured output background policy.");
                MediaAbRules.ValidateDigest(absent.CapturedBackgroundDeclaration, MediaAbFailure.ProfileUnsupported);
                break;
            default:
                throw Invalid("Media input branch is not a recognized fixed state.");
        }
    }

    private static void ValidateFile(MediaInputContext context, MediaFeatureFileSource source)
    {
        new MediaAbValueValidator().ValidateSource(source);
        RequireProfile(context, source.Profile);
    }

    private static void RequireProfile(MediaInputContext context, MediaProfileBundle source)
    {
        if (context.RequestedProfile.Digest != source.Digest)
            throw Invalid("Requested complete profile does not match the fixed source profile.");
    }

    private static void RequireNormal(MediaFeatureInput input)
    {
        if (input.Context.Mapping.OutputMode != MediaOutputMode.Normal)
            throw Invalid("Transparent output must use explicit no media state before source preparation.");
    }

    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.ProfileUnsupported, detail);
}
