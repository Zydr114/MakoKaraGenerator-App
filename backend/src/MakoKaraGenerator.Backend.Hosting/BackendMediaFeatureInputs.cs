using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Fixes the complete media-feature input of one runtime source before it is encoded for a Worker.</summary>
internal static class BackendMediaFeatureInputs
{
    private static MediaProfileBundle AnalyticProfile => ProjectRuntimeMediaOutputs.AnalyticProfile;

    /// <summary>Attaches the input required by actual media dependencies; projects without media queries are unchanged.</summary>
    /// <param name="snapshot">The fixed source snapshot.</param>
    /// <param name="backgroundCopy">The private verified background copy, valid for this call.</param>
    /// <param name="service">The Host analysis service when installed.</param>
    /// <param name="transparentOutput">Whether the consumer renders without a background.</param>
    /// <param name="waitForCompletion">Whether to wait for the final analysis instead of returning a pending input.</param>
    /// <param name="token">Cancels this caller's wait only.</param>
    /// <returns>The snapshot with its fixed input and the private artifact path of an available file.</returns>
    internal static async Task<(ProjectSnapshot Snapshot, string? ArtifactPath)> AttachAsync(ProjectSnapshot snapshot,
        string? backgroundCopy, BackendMediaFeatureService? service, bool transparentOutput, bool waitForCompletion, CancellationToken token)
    {
        if (snapshot.MediaFeatureInput is not null) return (snapshot, null);
        ProjectMediaDependencyPlan plan;
        try
        {
            plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
                snapshot.LyricPresentation, snapshot.PresetApplications, token);
        }
        catch (Exception error) when (error is ProjectMediaDependencyException or EffectPackageException)
        { return (snapshot, null); } // The compiler reports the same owner failure with its stable code.
        if (plan.Dependencies.IsEmpty) return (snapshot, null);
        Composition composition = snapshot.Composition;
        BackgroundSource background = snapshot.Background;
        LinearPremulRgba bottom = Linear(composition.BackgroundColor);
        MediaMapping FileMapping(MediaOutputMode mode) => ProjectRuntimeMediaOutputs.FileMapping(snapshot, mode);
        MediaInputContext Context(MediaProfileBundle profile, MediaMapping mapping)
            => MediaInputContext.Create(profile, mapping, plan.QueryCatalogDigest, plan.FixedDependencyDigest);
        MediaFeatureInput NoMedia(MediaNoMediaReason reason, MediaOutputMode mode)
            => MediaFeatureInput.CreateNoMedia(Context(AnalyticProfile, FileMapping(mode)), reason, ProjectRuntimeMediaOutputs.Declaration(snapshot));

        if (transparentOutput)
            return (snapshot with { MediaFeatureInput = ProjectRuntimeMediaOutputs.TransparentNoMedia(snapshot, token) }, null);
        if (background.Kind == BackgroundKind.Color)
        {
            MediaFeatureInput solid = MediaFeatureInput.CreateAvailableSolid(
                Context(AnalyticProfile, MediaMapping.CreateSolid(composition.CanvasWidth, composition.CanvasHeight)),
                MediaFeatureSolidSource.Create(AnalyticProfile, bottom));
            return (snapshot with { MediaFeatureInput = solid }, null);
        }
        MediaFeatureInput none = NoMedia(MediaNoMediaReason.NoBackgroundContribution, MediaOutputMode.Normal);
        if (service is null || backgroundCopy is null || background.Media is not { } media
            || background.Kind is not (BackgroundKind.Image or BackgroundKind.Video))
            return (snapshot with { MediaFeatureInput = none }, null);
        BackendMediaFeatureResolution resolution;
        try
        {
            resolution = await service.ResolveAsync(backgroundCopy, new(new(media.Hash), media.ByteLength),
                background.Kind == BackgroundKind.Image ? MediaSourceKind.Still : MediaSourceKind.Video, waitForCompletion, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MediaAbException or ObjectDisposedException)
        { return (snapshot with { MediaFeatureInput = none }, null); }
        if (resolution.Source is not { } source) return (snapshot with { MediaFeatureInput = none }, null);
        MediaInputContext context = Context(source.Profile, FileMapping(MediaOutputMode.Normal));
        if (resolution.Artifact is { } artifact && resolution.ArtifactPath is { } path)
            return (snapshot with { MediaFeatureInput = MediaFeatureInput.CreateAvailableFile(context, source, artifact.Identity) }, path);
        MediaFeatureInput input = resolution.Failure is { } failure
            ? MediaFeatureInput.CreateUnavailable(context, source, resolution.Request, failure)
            : MediaFeatureInput.CreatePending(context, source, resolution.Request);
        return (snapshot with { MediaFeatureInput = input }, null);
    }

    /// <summary>Exact sRGB-encoded straight color to canonical linear premultiplied color.</summary>
    internal static LinearPremulRgba Linear(RgbaColor color) => ProjectRuntimeMediaOutputs.Linear(color);
}
