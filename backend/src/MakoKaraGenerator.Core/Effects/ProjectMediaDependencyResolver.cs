using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>Resolves fixed query dependencies from enabled instances plus the unique complete source-material catalog.</summary>
public static class ProjectMediaDependencyResolver
{
    /// <summary>Resolves the immutable project source without compiling, inspecting media or opening an execution capability.</summary>
    /// <param name="project">The complete fixed project source.</param>
    /// <param name="cancellationToken">Cancels package reading and identity encoding.</param>
    /// <returns>The complete actual-owner query closure.</returns>
    public static ProjectMediaDependencyPlan Resolve(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Resolve(project.Effects, project.LyricStyle, project.Lyrics, project.LyricPresentation, project.PresetApplications, cancellationToken);
    }

    /// <summary>Resolves fixed snapshot source values without a dependency on Rendering or a runtime binding.</summary>
    /// <param name="effects">Exact byte closures and persistent instance order.</param>
    /// <param name="style">The complete source style.</param>
    /// <param name="lyrics">All source lyrics, including currently invisible objects.</param>
    /// <param name="presentation">All source presentation nodes.</param>
    /// <param name="applications">All fixed preset application source snapshots.</param>
    /// <param name="cancellationToken">Cancels reading and identity encoding.</param>
    /// <returns>The immutable query directory and fixed dependency identities.</returns>
    public static ProjectMediaDependencyPlan Resolve(ProjectEffects effects, LyricStyle style, LyricsDocument lyrics,
        LyricPresentationDefinition presentation, PresetApplications applications, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effects); ArgumentNullException.ThrowIfNull(applications);
        cancellationToken.ThrowIfCancellationRequested();
        var sources = SourceMaterialCatalog.Collect(style, lyrics, presentation, applications);
        Dictionary<(string, string), EffectPackageMediaQueryCatalog> catalogs = [];
        var owners = ImmutableArray.CreateBuilder<ProjectMediaDependencyOwner>();
        var dependencies = ImmutableArray.CreateBuilder<ProjectMediaDependency>(); SortedSet<long> windows = [];
        foreach (var instance in effects.Instances.Where(instance => instance.Enabled))
        {
            var candidate = Candidate(ProjectMediaDependencyOwnerKind.Instance, instance.Id, "", instance.PackageId, instance.Revision, instance.ExportId);
            Add(candidate, instance.Parameters, null);
        }
        foreach (var source in sources)
        {
            var material = source.Material;
            var candidate = Candidate(ProjectMediaDependencyOwnerKind.SourceMaterial, source.Path, source.Region, material.PackageId, material.Revision, material.ExportId);
            Add(candidate, material.Parameters, source);
        }
        var fixedOwners = owners.ToImmutable(); var fixedDependencies = dependencies.ToImmutable();
        MediaDigest queryDigest = MediaDependencyEncoding.QueryCatalog(fixedDependencies, cancellationToken);
        MediaDigest dependencyDigest = MediaDependencyEncoding.DependencyPlan(fixedOwners, fixedDependencies, queryDigest, cancellationToken);
        return new(fixedOwners, fixedDependencies, [.. windows], queryDigest, dependencyDigest);

        void Add(ProjectMediaDependencyOwner candidate, EffectParameterOverrides overrides, SourceMaterialReference? source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = (candidate.PackageId, candidate.Revision);
                if (!catalogs.TryGetValue(key, out var catalog))
                {
                    catalog = EffectPackageMediaQueries.Read(effects.GetPackage(key.PackageId, key.Revision), cancellationToken);
                    catalogs.Add(key, catalog);
                }
                var export = catalog.GetExport(candidate.ExportId);
                if (source is not null) SourceMaterialCatalog.ValidateExport(source, export);
                else if (export is not (EffectControllerDefinition or EffectGraphDefinition))
                    throw Error("PackageExportKindMismatch", "exports." + export.Id + ".kind", "效果实例须引用 controller 或 graph 导出。");
                var parameters = catalog.ResolveParameters(export.Id, overrides.Encode(), cancellationToken);
                if (export.MediaQueries.IsEmpty) return;
                MediaDigest parametersDigest = MediaDependencyEncoding.Parameters(parameters, cancellationToken);
                var owner = new ProjectMediaDependencyOwner(candidate.Kind, candidate.Path, candidate.Region, candidate.PackageId,
                    candidate.Revision, candidate.ExportId, parameters, parametersDigest);
                // Validate identity text while failure still has its concrete source owner.
                MediaDependencyEncoding.ValidateOwner(owner);
                candidate = owner; owners.Add(owner);
                foreach (var reference in export.MediaQueries)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var query = catalog.GetQuery(reference.QueryId);
                    long window = query.Window switch
                    {
                        EffectMediaWindow.Literal literal => literal.DurationUs,
                        EffectMediaWindow.Parameter parameter when parameters[parameter.ParameterId] is EffectParameterValue.Duration duration => duration.Value,
                        _ => throw Error("PackageMediaQueryParameterInvalid", "exports." + export.Id + ".mediaQueries", "窗口不是固定 duration 值。"),
                    };
                    EffectPackageMediaQueries.ValidateWindow(window, "exports." + export.Id + ".mediaQueries." + query.Id);
                    windows.Add(window);
                    if (windows.Count > MediaAbCapacityV1.MaximumWindows)
                        throw Error("ProjectMediaWindowCapacityExceeded", "mediaQueries.windows", "固定输入的联合窗口种类超过四种。");
                    dependencies.Add(new(owner, query, reference, window));
                }
            }
            catch (EffectPackageException error) { throw new ProjectMediaDependencyException(candidate, error.Code, error.Path, error); }
            catch (ImageGraphException error) { throw new ProjectMediaDependencyException(candidate, error.Code, error.Field, error); }
            catch (MediaAbException error) { throw new ProjectMediaDependencyException(candidate, error.Failure.ToString(), "mediaQueries", error); }
        }
    }

    private static ProjectMediaDependencyOwner Candidate(ProjectMediaDependencyOwnerKind kind, string path, string region,
        string packageId, string revision, string exportId) => new(kind, path, region, packageId, revision, exportId,
            ImmutableDictionary<string, EffectParameterValue>.Empty, MediaDependencyEncoding.Parameters(ImmutableDictionary<string, EffectParameterValue>.Empty, CancellationToken.None));
}
