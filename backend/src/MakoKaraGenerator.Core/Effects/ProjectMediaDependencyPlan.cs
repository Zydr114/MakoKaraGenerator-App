using System.Collections.Immutable;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>The actual existing project source kind that owns a query permission.</summary>
public enum ProjectMediaDependencyOwnerKind : uint
{
    /// <summary>An enabled persistent effect instance.</summary>
    Instance = 0,
    /// <summary>A real path and region from SourceMaterialCatalog.</summary>
    SourceMaterial = 1,
}

/// <summary>A fixed dependency owner with exact package revision and completely resolved parameters.</summary>
public sealed class ProjectMediaDependencyOwner
{
    internal ProjectMediaDependencyOwner(ProjectMediaDependencyOwnerKind kind, string path, string region, string packageId,
        string revision, string exportId, ImmutableDictionary<string, EffectParameterValue> parameters, MediaDigest parametersDigest)
    {
        Kind = kind; Path = path; Region = region; PackageId = packageId; Revision = revision; ExportId = exportId;
        Parameters = parameters; ParametersDigest = parametersDigest;
    }
    /// <summary>The instance or real source-material kind.</summary>
    public ProjectMediaDependencyOwnerKind Kind { get; }
    /// <summary>The stable instance identity or actual SourceMaterialReference.Path, never a machine path.</summary>
    public string Path { get; }
    /// <summary>The real source material region; empty for an effect instance.</summary>
    public string Region { get; }
    /// <summary>The exact package identity.</summary>
    public string PackageId { get; }
    /// <summary>The exact immutable package revision, never latest.</summary>
    public string Revision { get; }
    /// <summary>The exact export identity.</summary>
    public string ExportId { get; }
    /// <summary>Complete typed default-plus-override values in a successful plan; empty if a failure precedes resolution.</summary>
    public ImmutableDictionary<string, EffectParameterValue> Parameters { get; }
    /// <summary>The canonical semantic parameter digest, independent of JSON override spelling.</summary>
    public MediaDigest ParametersDigest { get; }
}

/// <summary>A resolved export-local query permission for one actual source owner.</summary>
public sealed class ProjectMediaDependency
{
    internal ProjectMediaDependency(ProjectMediaDependencyOwner owner, EffectMediaQueryDefinition query,
        EffectMediaQueryReference reference, long windowUs)
    { Owner = owner; Query = query; Reference = reference; WindowUs = windowUs; }
    /// <summary>The exact actual owner, retained even when values share a temporal window.</summary>
    public ProjectMediaDependencyOwner Owner { get; }
    /// <summary>The strict declaration with its fixed space.</summary>
    public EffectMediaQueryDefinition Query { get; }
    /// <summary>The validated current-export fallback ownership.</summary>
    public EffectMediaQueryReference Reference { get; }
    /// <summary>The exact resolved integer-microsecond duration.</summary>
    public long WindowUs { get; }
}

/// <summary>A pure fixed query closure; no input state, reader, task, rendering object or runtime lease is retained.</summary>
public sealed class ProjectMediaDependencyPlan
{
    internal ProjectMediaDependencyPlan(ImmutableArray<ProjectMediaDependencyOwner> owners, ImmutableArray<ProjectMediaDependency> dependencies,
        ImmutableArray<long> windows, MediaDigest queryCatalogDigest, MediaDigest fixedDependencyDigest)
    { Owners = owners; Dependencies = dependencies; DistinctWindowsUs = windows; QueryCatalogDigest = queryCatalogDigest; FixedDependencyDigest = fixedDependencyDigest; }
    /// <summary>Actual media-dependent owners, in project instance then source catalog order.</summary>
    public ImmutableArray<ProjectMediaDependencyOwner> Owners { get; }
    /// <summary>All explicitly referenced queries; no visibility or frame condition hides a dependency.</summary>
    public ImmutableArray<ProjectMediaDependency> Dependencies { get; }
    /// <summary>The ascending union of windows, shared across spaces and owners.</summary>
    public ImmutableArray<long> DistinctWindowsUs { get; }
    /// <summary>The fixed actual-owner query directory digest, independent of input or mapping.</summary>
    public MediaDigest QueryCatalogDigest { get; }
    /// <summary>The owner, full resolved parameter and fallback closure digest.</summary>
    public MediaDigest FixedDependencyDigest { get; }
}

/// <summary>A dependency failure retaining the actual project owner plus the original stable package code and field.</summary>
public sealed class ProjectMediaDependencyException : Exception
{
    internal ProjectMediaDependencyException(ProjectMediaDependencyOwner owner, string code, string path, Exception inner)
        : base("媒体依赖闭包失败：" + code, inner) { Owner = owner; Code = code; Path = path; }
    /// <summary>The actual instance or source material owner, including exact package revision.</summary>
    public ProjectMediaDependencyOwner Owner { get; }
    /// <summary>The original stable strict-reader failure code.</summary>
    public string Code { get; }
    /// <summary>The original package or parameter field path, never a host file path.</summary>
    public string Path { get; }
}
