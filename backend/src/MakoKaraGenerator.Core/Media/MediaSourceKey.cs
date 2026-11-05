namespace MakoKaraGenerator.Core.Media;

/// <summary>A domain-separated immutable identity of a file source.</summary>
public readonly record struct MediaSourceKey
{
    /// <summary>Creates a read-only MediaSourceKey value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="digest">The complete file-source identity digest.</param>
    public MediaSourceKey(
        MediaDigest digest)
    {
        Digest = digest;
    }

    /// <summary>The complete file-source identity digest.</summary>
    public MediaDigest Digest { get; }

}
