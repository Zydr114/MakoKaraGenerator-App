namespace MakoKaraGenerator.Core.Media;

/// <summary>Explicit simultaneous decode reservations to admit before any allocation or process start.</summary>
/// <remarks>This value is arithmetic input, not a reservation or a claim about whole-process RSS. The owner must atomically reserve and enforce these bytes.</remarks>
public sealed record MediaDecodeWorkingSet
{
    /// <summary>Creates a read-only MediaDecodeWorkingSet value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="nativeEnvelopeBytes">The enforceable native decoder envelope, including reference and progressive coefficient buffers.</param>
    /// <param name="canonicalPixelBytes">All simultaneously live canonical pixel backing, including any pending next frame.</param>
    /// <param name="conversionBytes">All simultaneously live conversion backing.</param>
    /// <param name="metadataBytes">The bounded metadata queue and parser backing.</param>
    /// <param name="pipeBytes">The simultaneously charged process pipe backing.</param>
    /// <param name="otherSimultaneousBytes">Any other simultaneous decode backing.</param>
    /// <param name="nativeEnvelopeProven">Whether the native envelope is supported and enforceable by the owning implementation.</param>
    public MediaDecodeWorkingSet(
        long nativeEnvelopeBytes,
        long canonicalPixelBytes,
        long conversionBytes,
        long metadataBytes,
        long pipeBytes,
        long otherSimultaneousBytes,
        bool nativeEnvelopeProven)
    {
        NativeEnvelopeBytes = nativeEnvelopeBytes;
        CanonicalPixelBytes = canonicalPixelBytes;
        ConversionBytes = conversionBytes;
        MetadataBytes = metadataBytes;
        PipeBytes = pipeBytes;
        OtherSimultaneousBytes = otherSimultaneousBytes;
        NativeEnvelopeProven = nativeEnvelopeProven;
    }

    /// <summary>The enforceable native decoder envelope, including reference and progressive coefficient buffers.</summary>
    public long NativeEnvelopeBytes { get; }

    /// <summary>All simultaneously live canonical pixel backing, including any pending next frame.</summary>
    public long CanonicalPixelBytes { get; }

    /// <summary>All simultaneously live conversion backing.</summary>
    public long ConversionBytes { get; }

    /// <summary>The bounded metadata queue and parser backing.</summary>
    public long MetadataBytes { get; }

    /// <summary>The simultaneously charged process pipe backing.</summary>
    public long PipeBytes { get; }

    /// <summary>Any other simultaneous decode backing.</summary>
    public long OtherSimultaneousBytes { get; }

    /// <summary>Whether the native envelope is supported and enforceable by the owning implementation.</summary>
    public bool NativeEnvelopeProven { get; }

}
