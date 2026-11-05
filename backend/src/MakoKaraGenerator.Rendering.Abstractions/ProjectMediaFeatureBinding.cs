using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Rendering.Abstractions;

/// <summary>A trusted synchronous sampler over one fixed input and one exact dependency plan.</summary>
/// <remarks>The outer owner retains the provider and any file read lease through all sessions and queries.</remarks>
public interface IProjectMediaFeatureSampler
{
    /// <summary>The immutable accepted input.</summary>
    MediaFeatureInput Input { get; }
    /// <summary>The exact fixed plan that grants dependency membership.</summary>
    ProjectMediaDependencyPlan Plan { get; }
    /// <summary>Returns a detached result without preparing media or invoking author code.</summary>
    ProjectMediaFeatureSample Sample(ProjectMediaDependency dependency, MediaFeatureQueryRect rect,
        long projectTimeUs, CancellationToken cancellationToken);
}

/// <summary>One trusted input, plan and borrowed sampler binding, without owning a reader or session lease.</summary>
public sealed class ProjectMediaFeatureBinding
{
    /// <summary>Fixes and validates one sampler binding; this does not admit a package execution capability.</summary>
    public ProjectMediaFeatureBinding(IProjectMediaFeatureSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        Input = sampler.Input; Plan = sampler.Plan; Sampler = sampler;
        ValidateInputPlan(Input, Plan);
        if (!ReferenceEquals(sampler.Input, Input) || !ReferenceEquals(sampler.Plan, Plan))
            throw Invalid("Trusted sampler must expose one fixed input and plan.");
    }
    /// <summary>The fixed complete input.</summary>
    public MediaFeatureInput Input { get; }
    /// <summary>The fixed complete dependency plan.</summary>
    public ProjectMediaDependencyPlan Plan { get; }
    /// <summary>The borrowed trusted sampler; its owner controls drain and disposal.</summary>
    public IProjectMediaFeatureSampler Sampler { get; }

    /// <summary>Revalidates the fixed input identities and their exact plan digests.</summary>
    public static void ValidateInputPlan(MediaFeatureInput input, ProjectMediaDependencyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(plan);
        MediaFeatureInputValidator.Validate(input);
        if (input.Context.QueryCatalogDigest != plan.QueryCatalogDigest || input.Context.FixedDependencyDigest != plan.FixedDependencyDigest)
            throw Invalid("Fixed media input does not match its complete query and dependency plan.");
        if (plan.DistinctWindowsUs.Length > MediaAbCapacityV1.MaximumWindows)
            throw Invalid("Fixed dependency plan exceeds the supported window count.");
    }

    /// <summary>Checks an exact existing dependency object, including its already fixed owner, parameters and fallback.</summary>
    public static void RequireDependency(ProjectMediaDependencyPlan plan, ProjectMediaDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(dependency);
        if (!plan.Dependencies.Any(candidate => ReferenceEquals(candidate, dependency)))
            throw Invalid("Media dependency is not an exact member of this fixed plan.");
    }

    /// <summary>Rechecks the actual snapshot source closure and explicit output policy before a session borrows this binding.</summary>
    /// <remarks>This method does not add media state or a new fingerprint to the snapshot.</remarks>
    public void ValidateSnapshot(MakoKaraGenerator.Rendering.ProjectSnapshot snapshot, MediaOutputMode outputMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        ValidateInputPlan(Input, Plan);
        if (!ReferenceEquals(Sampler.Input, Input) || !ReferenceEquals(Sampler.Plan, Plan))
            throw Invalid("Trusted sampler no longer exposes the fixed binding objects.");
        var actual = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications, cancellationToken);
        if (actual.QueryCatalogDigest != Plan.QueryCatalogDigest || actual.FixedDependencyDigest != Plan.FixedDependencyDigest)
            throw Invalid("Snapshot source closure does not match the fixed media dependency plan.");
        MediaMapping mapping = Input.Context.Mapping;
        if (!Enum.IsDefined(outputMode) || mapping.CanvasWidth != snapshot.Composition.CanvasWidth
            || mapping.CanvasHeight != snapshot.Composition.CanvasHeight || mapping.OutputMode != outputMode)
            throw Invalid("Snapshot canvas or explicit output policy does not match the fixed media mapping.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.ProfileUnsupported, detail);
}
