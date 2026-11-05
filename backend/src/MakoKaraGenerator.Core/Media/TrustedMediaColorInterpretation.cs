namespace MakoKaraGenerator.Core.Media;

/// <summary>An internal authority may fill missing tags without overriding contradictory tags.</summary>
public sealed record TrustedMediaColorInterpretation
{
    /// <summary>Creates a read-only TrustedMediaColorInterpretation value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="transfer">The trusted transfer interpretation.</param>
    /// <param name="matrix">The trusted matrix interpretation.</param>
    /// <param name="range">The trusted component range.</param>
    /// <param name="alpha">The trusted alpha representation.</param>
    /// <param name="chromaLocation">The trusted chroma sample location.</param>
    /// <param name="authorityDigest">The internal import authority identity.</param>
    public TrustedMediaColorInterpretation(
        MediaTransfer transfer,
        MediaMatrix matrix,
        MediaRange range,
        MediaAlpha alpha,
        MediaChromaLocation chromaLocation,
        MediaDigest authorityDigest)
    {
        Transfer = transfer;
        Matrix = matrix;
        Range = range;
        Alpha = alpha;
        ChromaLocation = chromaLocation;
        AuthorityDigest = authorityDigest;
    }

    /// <summary>The trusted transfer interpretation.</summary>
    public MediaTransfer Transfer { get; }

    /// <summary>The trusted matrix interpretation.</summary>
    public MediaMatrix Matrix { get; }

    /// <summary>The trusted component range.</summary>
    public MediaRange Range { get; }

    /// <summary>The trusted alpha representation.</summary>
    public MediaAlpha Alpha { get; }

    /// <summary>The trusted chroma sample location.</summary>
    public MediaChromaLocation ChromaLocation { get; }

    /// <summary>The internal import authority identity.</summary>
    public MediaDigest AuthorityDigest { get; }

}
