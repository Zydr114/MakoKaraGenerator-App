namespace MakoKaraGenerator.Core.Media;

/// <summary>A SHA-256 display value; accepted values contain exactly 64 lowercase hexadecimal characters.</summary>
/// <remarks>This passive value may be default or malformed until the common validator checks it. It never proves provenance.</remarks>
public readonly record struct MediaDigest
{
    /// <summary>Creates a read-only MediaDigest value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="lowerHex">The exact lowercase display encoding. No prefix or normalization is permitted.</param>
    public MediaDigest(
        string lowerHex)
    {
        LowerHex = lowerHex;
    }

    /// <summary>The exact lowercase display encoding. No prefix or normalization is permitted.</summary>
    public string LowerHex { get; }

}
