namespace MakoKaraGenerator.Core.Media;

/// <summary>Common accepted-boundary validation for passive immutable media values.</summary>
public interface IMediaAbValueValidator
{
    /// <summary>Validates supported color combinations and recomputes the interpretation digest.</summary>
    /// <param name="value">The candidate fixed color interpretation.</param>
    /// <exception cref="MediaAbException">A color field, combination or digest is invalid.</exception>
    void ValidateColor(MediaColorInterpretation value);

    /// <summary>Validates the complete supported profile and recomputes its digest.</summary>
    /// <param name="value">The candidate profile bundle.</param>
    /// <exception cref="MediaAbException">A profile field or digest is invalid.</exception>
    void ValidateProfile(MediaProfileBundle value);

    /// <summary>Validates a file source, every nested digest and all cross-identity bindings.</summary>
    /// <param name="value">The candidate file source.</param>
    /// <exception cref="MediaAbException">A source, profile, color, presentation or capacity requirement fails.</exception>
    void ValidateSource(MediaFeatureFileSource value);

    /// <summary>Validates expected artifact metadata and exact layout against a fully validated file source.</summary>
    /// <param name="source">The candidate source, independently revalidated by this operation.</param>
    /// <param name="value">The external artifact identity metadata.</param>
    /// <exception cref="MediaAbException">Identity binding, layout, time, count or capacity is invalid.</exception>
    /// <remarks>This cannot prove any unavailable file bytes. The codec must separately verify complete bytes, hash and index.</remarks>
    void ValidateArtifactIdentity(MediaFeatureFileSource source, MediaArtifactIdentity value);
}

/// <summary>Strict version-one media validation using the common canonical writer.</summary>
/// <remarks>This proves internal consistency only. The inspector owns source authorization, actual tool closure and controlled color authority.</remarks>
public sealed class MediaAbValueValidator : IMediaAbValueValidator
{
    private readonly MediaAbIdentityWriter _writer = new();

    /// <inheritdoc/>
    public void ValidateColor(MediaColorInterpretation value)
    {
        MediaAbRules.ValidateColorShape(value);
        MediaAbRules.ValidateDigest(value.Digest, MediaAbFailure.ColorUnsupported);
        RequireEqual(value.Digest, _writer.ComputeColorDigest(value), MediaAbFailure.ColorUnsupported,
            "Color interpretation digest does not match its canonical fields.");
    }

    /// <inheritdoc/>
    public void ValidateProfile(MediaProfileBundle value)
    {
        MediaAbRules.ValidateProfileShape(value);
        MediaAbRules.ValidateDigest(value.Digest, MediaAbFailure.ProfileUnsupported);
        RequireEqual(value.Digest, _writer.ComputeProfileDigest(value), MediaAbFailure.ProfileUnsupported,
            "Profile bundle digest does not match its canonical fields.");
    }

    /// <inheritdoc/>
    public void ValidateSource(MediaFeatureFileSource value)
    {
        MediaAbRules.ValidateSourceShape(value);
        ValidateColor(value.Presentation.Color);
        MediaAbRules.ValidateDigest(value.Tool.Digest, MediaAbFailure.ProfileUnsupported);
        RequireEqual(value.Tool.Digest, _writer.ComputeToolDigest(value.Tool), MediaAbFailure.ProfileUnsupported,
            "Tool digest does not match its canonical fields.");
        ValidateProfile(value.Profile);
        RequireEqual(value.Presentation.Digest, _writer.ComputeDescriptorDigest(value.Presentation), MediaAbFailure.PresentationInvalid,
            "Source descriptor digest does not match its canonical fields.");
        RequireEqual(value.Profile.DecoderIdentity, value.Tool.Digest, MediaAbFailure.ProfileUnsupported,
            "Profile decoder binding does not match the source tool.");
        RequireEqual(value.Profile.ColorInterpretation, value.Presentation.Color.Digest, MediaAbFailure.ColorUnsupported,
            "Profile color binding does not match the source descriptor.");
        MediaAbRules.ValidateDigest(value.Key.Digest, MediaAbFailure.PresentationInvalid);
        RequireEqual(value.Key.Digest, _writer.ComputeSourceKey(value).Digest, MediaAbFailure.PresentationInvalid,
            "File source key does not match its canonical fields.");
    }

    /// <inheritdoc/>
    public void ValidateArtifactIdentity(MediaFeatureFileSource source, MediaArtifactIdentity value)
    {
        ValidateSource(source);
        ArgumentNullException.ThrowIfNull(value);
        MediaAbRules.ValidateDigest(value.ContentHash, MediaAbFailure.ArtifactInvalid);
        MediaAbRules.ValidateDigest(value.SourceKey.Digest, MediaAbFailure.ArtifactInvalid);
        MediaAbRules.ValidateDigest(value.ProfileDigest, MediaAbFailure.ArtifactInvalid);
        MediaAbRules.ValidateDigest(value.SourceDescriptorDigest, MediaAbFailure.ArtifactInvalid);
        MediaAbRules.ValidateDigest(value.PresentationIndexDigest, MediaAbFailure.ArtifactInvalid);
        if (value.Format != MediaProfilesV1.Codec)
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Artifact format is not the supported canonical codec.");
        }

        RequireEqual(value.SourceKey.Digest, source.Key.Digest, MediaAbFailure.ArtifactInvalid,
            "Artifact source key does not match the accepted source.");
        RequireEqual(value.ProfileDigest, source.Profile.Digest, MediaAbFailure.ArtifactInvalid,
            "Artifact profile does not match the accepted source.");
        RequireEqual(value.SourceDescriptorDigest, source.Presentation.Digest, MediaAbFailure.ArtifactInvalid,
            "Artifact descriptor does not match the accepted source.");
        if (source.Kind == MediaSourceKind.Still && value.VideoDurationUs is not null)
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "A timeless still must not have an invented video duration.");
        }

        if (source.Kind == MediaSourceKind.Video && (value.VideoDurationUs is null or <= 0))
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "A video artifact requires a positive canonical duration.");
        }

        if (value.VideoDurationUs > MediaAbCapacityV1.MaximumVideoDurationUs)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Canonical video duration exceeds the fixed limit.");
        }

        long expectedBytes = MediaTrackLayoutV1.GetArtifactBytes(source.Kind, value.FrameCount);
        _ = MediaTrackLayoutV1.GetScratchAndCandidateBytes(source.Kind, value.FrameCount);
        if (source.Kind == MediaSourceKind.Video && value.VideoDurationUs is { } durationUs && durationUs < value.FrameCount)
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Positive canonical frame intervals cannot outnumber duration microseconds.");
        }

        if (value.ByteLength != expectedBytes)
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Artifact byte length does not equal its complete canonical layout.");
        }
    }

    /// <summary>Validates one canonical SDR linear premultiplied color without clamping or normalization.</summary>
    /// <param name="value">The candidate color.</param>
    /// <exception cref="MediaAbException">A component is nonfinite, out of range, unpremultiplied or negative zero.</exception>
    public static void ValidateLinearPremul(LinearPremulRgba value)
    {
        if (!double.IsFinite(value.R) || !double.IsFinite(value.G) || !double.IsFinite(value.B) || !double.IsFinite(value.A) ||
            value.A < 0 || value.A > 1 || value.R < 0 || value.G < 0 || value.B < 0 ||
            value.R > value.A || value.G > value.A || value.B > value.A ||
            IsNegativeZero(value.R) || IsNegativeZero(value.G) || IsNegativeZero(value.B) || IsNegativeZero(value.A))
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Canonical color must be finite positive-zero linear premultiplied SDR.");
        }
    }

    private static bool IsNegativeZero(double value) => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

    private static void RequireEqual(MediaDigest actual, MediaDigest expected, MediaAbFailure failure, string detail)
    {
        if (actual != expected)
        {
            throw new MediaAbException(failure, detail);
        }
    }
}
