namespace MakoKaraGenerator.Core.Media;

/// <summary>External identity and expected canonical layout of one feature artifact.</summary>
/// <remarks>Validating this metadata cannot prove the hash or structure of unavailable artifact bytes. The later codec performs complete byte validation.</remarks>
public sealed record MediaArtifactIdentity
{
    /// <summary>Creates a read-only MediaArtifactIdentity value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="contentHash">SHA-256 of the exact complete file bytes without a domain prefix.</param>
    /// <param name="byteLength">The exact complete artifact length.</param>
    /// <param name="format">The exact canonical codec profile.</param>
    /// <param name="sourceKey">The source key bound by the artifact.</param>
    /// <param name="profileDigest">The profile digest bound by the artifact.</param>
    /// <param name="sourceDescriptorDigest">The source descriptor digest bound by the artifact.</param>
    /// <param name="presentationIndexDigest">SHA-256 of the canonical presentation index bytes.</param>
    /// <param name="frameCount">The exact number of presentation frames.</param>
    /// <param name="videoDurationUs">The normalized video endpoint; absent for a timeless still.</param>
    public MediaArtifactIdentity(
        MediaDigest contentHash,
        long byteLength,
        string format,
        MediaSourceKey sourceKey,
        MediaDigest profileDigest,
        MediaDigest sourceDescriptorDigest,
        MediaDigest presentationIndexDigest,
        long frameCount,
        long? videoDurationUs)
    {
        ContentHash = contentHash;
        ByteLength = byteLength;
        Format = format;
        SourceKey = sourceKey;
        ProfileDigest = profileDigest;
        SourceDescriptorDigest = sourceDescriptorDigest;
        PresentationIndexDigest = presentationIndexDigest;
        FrameCount = frameCount;
        VideoDurationUs = videoDurationUs;
    }

    /// <summary>SHA-256 of the exact complete file bytes without a domain prefix.</summary>
    public MediaDigest ContentHash { get; }

    /// <summary>The exact complete artifact length.</summary>
    public long ByteLength { get; }

    /// <summary>The exact canonical codec profile.</summary>
    public string Format { get; }

    /// <summary>The source key bound by the artifact.</summary>
    public MediaSourceKey SourceKey { get; }

    /// <summary>The profile digest bound by the artifact.</summary>
    public MediaDigest ProfileDigest { get; }

    /// <summary>The source descriptor digest bound by the artifact.</summary>
    public MediaDigest SourceDescriptorDigest { get; }

    /// <summary>SHA-256 of the canonical presentation index bytes.</summary>
    public MediaDigest PresentationIndexDigest { get; }

    /// <summary>The exact number of presentation frames.</summary>
    public long FrameCount { get; }

    /// <summary>The normalized video endpoint; absent for a timeless still.</summary>
    public long? VideoDurationUs { get; }

}
