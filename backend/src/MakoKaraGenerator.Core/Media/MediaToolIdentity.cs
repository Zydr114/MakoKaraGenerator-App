namespace MakoKaraGenerator.Core.Media;

/// <summary>The controlled decoder tool closure and normalized pipeline identity.</summary>
public sealed record MediaToolIdentity
{
    /// <summary>Creates a read-only MediaToolIdentity value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="executables">The canonical executable manifest digest.</param>
    /// <param name="dependencies">The canonical runtime dependency and LUT manifest digest.</param>
    /// <param name="normalizedPipeline">SHA-256 of fixed LF UTF-8 pipeline text without a domain prefix.</param>
    /// <param name="digest">The recomputable composite tool digest; excluded from its preimage.</param>
    public MediaToolIdentity(
        MediaDigest executables,
        MediaDigest dependencies,
        MediaDigest normalizedPipeline,
        MediaDigest digest)
    {
        Executables = executables;
        Dependencies = dependencies;
        NormalizedPipeline = normalizedPipeline;
        Digest = digest;
    }

    /// <summary>The canonical executable manifest digest.</summary>
    public MediaDigest Executables { get; }

    /// <summary>The canonical runtime dependency and LUT manifest digest.</summary>
    public MediaDigest Dependencies { get; }

    /// <summary>SHA-256 of fixed LF UTF-8 pipeline text without a domain prefix.</summary>
    public MediaDigest NormalizedPipeline { get; }

    /// <summary>The recomputable composite tool digest; excluded from its preimage.</summary>
    public MediaDigest Digest { get; }

}
