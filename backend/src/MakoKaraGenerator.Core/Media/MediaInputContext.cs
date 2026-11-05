namespace MakoKaraGenerator.Core.Media;

/// <summary>The complete immutable request profile and fixed dependency identity shared by every input state.</summary>
/// <remarks>Query and package closure resolution belongs to a later slice; this boundary accepts only their already fixed digests.</remarks>
public sealed class MediaInputContext
{
    private MediaInputContext(MediaProfileBundle requestedProfile, MediaMapping mapping,
        MediaDigest queryCatalogDigest, MediaDigest fixedDependencyDigest)
    {
        RequestedProfile = requestedProfile; Mapping = mapping;
        QueryCatalogDigest = queryCatalogDigest; FixedDependencyDigest = fixedDependencyDigest;
    }

    /// <summary>The complete supported request profile, including in NoMedia state.</summary>
    public MediaProfileBundle RequestedProfile { get; }
    /// <summary>The immutable spatial, temporal and output mapping.</summary>
    public MediaMapping Mapping { get; }
    /// <summary>The fixed resolved-query directory digest; no dynamic query directory is retained.</summary>
    public MediaDigest QueryCatalogDigest { get; }
    /// <summary>The fixed package, parameters and dependency-closure digest.</summary>
    public MediaDigest FixedDependencyDigest { get; }

    /// <summary>Creates a fully validated request context without tasks, readers, leases or caches.</summary>
    /// <param name="requestedProfile">The complete fixed requested profile.</param>
    /// <param name="mapping">The fixed mapping whose identity is independently recomputed.</param>
    /// <param name="queryCatalogDigest">A valid already-resolved directory digest.</param>
    /// <param name="fixedDependencyDigest">A valid already-resolved fixed dependency digest.</param>
    /// <returns>The immutable request context.</returns>
    public static MediaInputContext Create(MediaProfileBundle requestedProfile, MediaMapping mapping,
        MediaDigest queryCatalogDigest, MediaDigest fixedDependencyDigest)
    {
        var value = new MediaInputContext(requestedProfile, mapping, queryCatalogDigest, fixedDependencyDigest);
        MediaFeatureInputRules.ValidateContext(value);
        return value;
    }
}
