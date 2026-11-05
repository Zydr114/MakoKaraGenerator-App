namespace MakoKaraGenerator.Core.Media;

/// <summary>One relocatable logical entry in the controlled tool manifest.</summary>
public sealed record MediaToolManifestEntry
{
    /// <summary>Creates a read-only MediaToolManifestEntry value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="logicalId">A unique logical identifier without filesystem separators, drive delimiters or control characters.</param>
    /// <param name="byteLength">The exact entry content length in bytes.</param>
    /// <param name="contentSha256">SHA-256 of the actual entry bytes.</param>
    public MediaToolManifestEntry(
        string logicalId,
        long byteLength,
        MediaDigest contentSha256)
    {
        LogicalId = logicalId;
        ByteLength = byteLength;
        ContentSha256 = contentSha256;
    }

    /// <summary>A unique logical identifier without filesystem separators, drive delimiters or control characters.</summary>
    public string LogicalId { get; }

    /// <summary>The exact entry content length in bytes.</summary>
    public long ByteLength { get; }

    /// <summary>SHA-256 of the actual entry bytes.</summary>
    public MediaDigest ContentSha256 { get; }

}
