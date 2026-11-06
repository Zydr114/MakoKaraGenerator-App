using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Fixes the complete media-feature input of one runtime source before it is encoded for a Worker.</summary>
internal static class BackendMediaFeatureInputs
{
    private static readonly MediaProfileBundle AnalyticProfile = CreateAnalyticProfile();

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
        MediaMapping FileMapping(MediaOutputMode mode) => MediaMapping.Create(composition.CanvasWidth, composition.CanvasHeight,
            background.Fit, background.Playback, bottom, mode);
        MediaInputContext Context(MediaProfileBundle profile, MediaMapping mapping)
            => MediaInputContext.Create(profile, mapping, plan.QueryCatalogDigest, plan.FixedDependencyDigest);
        MediaFeatureInput NoMedia(MediaNoMediaReason reason, MediaOutputMode mode)
            => MediaFeatureInput.CreateNoMedia(Context(AnalyticProfile, FileMapping(mode)), reason, Declaration(snapshot));

        if (transparentOutput)
            return (snapshot with { MediaFeatureInput = NoMedia(MediaNoMediaReason.TransparentOutput, MediaOutputMode.Transparent) }, null);
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
    internal static LinearPremulRgba Linear(RgbaColor color)
    {
        double alpha = color.Alpha / 255.0;
        static double Decode(byte value)
        {
            double encoded = value / 255.0;
            return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }
        static double Clean(double value) => value <= 0 ? 0 : value;
        double r = Clean(Math.Min(alpha, Decode(color.Red) * alpha)), g = Clean(Math.Min(alpha, Decode(color.Green) * alpha)),
            b = Clean(Math.Min(alpha, Decode(color.Blue) * alpha));
        return new(r, g, b, Clean(alpha));
    }

    private static MediaDigest Declaration(ProjectSnapshot snapshot)
    {
        BackgroundSource background = snapshot.Background;
        RgbaColor color = snapshot.Composition.BackgroundColor;
        string text = string.Join('\n', "mako.media.background-declaration.v1", background.Kind.ToString(),
            background.Media?.Hash ?? "-", (background.Media?.ByteLength ?? 0).ToString(CultureInfo.InvariantCulture),
            background.Fit.ToString(), background.Playback.ToString(),
            string.Create(CultureInfo.InvariantCulture, $"{color.Red},{color.Green},{color.Blue},{color.Alpha}"));
        return Digest(text);
    }

    private static MediaProfileBundle CreateAnalyticProfile()
    {
        var preliminary = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            Digest("mako.media.analytic-solid-decoder.v1"), Digest("mako.media.analytic-solid-color.v1\n" + MediaProfilesV1.Color), default);
        return new(preliminary.Presentation, preliminary.DecoderPipeline, preliminary.Color, preliminary.Statistics, preliminary.Quantization,
            preliminary.Codec, preliminary.Query, preliminary.Gpu, preliminary.DecoderIdentity, preliminary.ColorInterpretation,
            new MediaAbIdentityWriter().ComputeProfileDigest(preliminary));
    }

    private static MediaDigest Digest(string text) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
