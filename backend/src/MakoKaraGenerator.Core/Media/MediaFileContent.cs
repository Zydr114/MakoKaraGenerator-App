namespace MakoKaraGenerator.Core.Media;

/// <summary>The raw-byte identity and length of authorized source content.</summary>
public readonly record struct MediaFileContent
{
    /// <summary>Creates a read-only MediaFileContent value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="sha256">SHA-256 of the exact source bytes without a domain prefix.</param>
    /// <param name="byteLength">The exact source length in bytes.</param>
    public MediaFileContent(
        MediaDigest sha256,
        long byteLength)
    {
        Sha256 = sha256;
        ByteLength = byteLength;
    }

    /// <summary>SHA-256 of the exact source bytes without a domain prefix.</summary>
    public MediaDigest Sha256 { get; }

    /// <summary>The exact source length in bytes.</summary>
    public long ByteLength { get; }

}
