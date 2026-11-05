namespace MakoKaraGenerator.Core.Media;

/// <summary>The fixed SDR interpretation of native decoded pixels.</summary>
/// <remarks>An authority digest is an identity binding, not proof that arbitrary caller-supplied tags are trusted.</remarks>
public sealed record MediaColorInterpretation
{
    /// <summary>Creates a read-only MediaColorInterpretation value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="transfer">The encoded RGB transfer function.</param>
    /// <param name="matrix">The native component interpretation matrix.</param>
    /// <param name="range">The declared component code range.</param>
    /// <param name="alpha">The declared source alpha representation.</param>
    /// <param name="chromaLocation">Explicit chroma sample location for subsampled input.</param>
    /// <param name="nativePixelFormat">The controlled decoder native output representation.</param>
    /// <param name="fromTrustedMissingTagOverride">Whether an internal trusted importer supplied missing tags.</param>
    /// <param name="authorityDigest">The controlled tag interpretation or trusted importer authority identity.</param>
    /// <param name="digest">The recomputable interpretation identity; excluded from its own preimage.</param>
    public MediaColorInterpretation(
        MediaTransfer transfer,
        MediaMatrix matrix,
        MediaRange range,
        MediaAlpha alpha,
        MediaChromaLocation chromaLocation,
        MediaNativePixelFormat nativePixelFormat,
        bool fromTrustedMissingTagOverride,
        MediaDigest authorityDigest,
        MediaDigest digest)
    {
        Transfer = transfer;
        Matrix = matrix;
        Range = range;
        Alpha = alpha;
        ChromaLocation = chromaLocation;
        NativePixelFormat = nativePixelFormat;
        FromTrustedMissingTagOverride = fromTrustedMissingTagOverride;
        AuthorityDigest = authorityDigest;
        Digest = digest;
    }

    /// <summary>The encoded RGB transfer function.</summary>
    public MediaTransfer Transfer { get; }

    /// <summary>The native component interpretation matrix.</summary>
    public MediaMatrix Matrix { get; }

    /// <summary>The declared component code range.</summary>
    public MediaRange Range { get; }

    /// <summary>The declared source alpha representation.</summary>
    public MediaAlpha Alpha { get; }

    /// <summary>Explicit chroma sample location for subsampled input.</summary>
    public MediaChromaLocation ChromaLocation { get; }

    /// <summary>The controlled decoder native output representation.</summary>
    public MediaNativePixelFormat NativePixelFormat { get; }

    /// <summary>Whether an internal trusted importer supplied missing tags.</summary>
    public bool FromTrustedMissingTagOverride { get; }

    /// <summary>The controlled tag interpretation or trusted importer authority identity.</summary>
    public MediaDigest AuthorityDigest { get; }

    /// <summary>The recomputable interpretation identity; excluded from its own preimage.</summary>
    public MediaDigest Digest { get; }

}
