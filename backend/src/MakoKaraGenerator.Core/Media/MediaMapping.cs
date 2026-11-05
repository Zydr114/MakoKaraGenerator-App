using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Media;

/// <summary>Immutable canvas, fit, playback, bottom and numerical presentation mapping.</summary>
public sealed class MediaMapping
{
    private MediaMapping(MediaMappingKey key, int width, int height, BackgroundFit fit,
        BackgroundPlaybackMode playback, LinearPremulRgba backColor, MediaOutputMode outputMode,
        string geometryProfile, string compositeProfile, string samplingProfile)
    {
        Key = key; CanvasWidth = width; CanvasHeight = height; Fit = fit; Playback = playback;
        BackColor = backColor; OutputMode = outputMode; GeometryProfile = geometryProfile;
        CompositeProfile = compositeProfile; SamplingProfile = samplingProfile;
    }

    /// <summary>The recomputable domain-separated mapping identity.</summary>
    public MediaMappingKey Key { get; }
    /// <summary>Positive bounded logical canvas width.</summary>
    public int CanvasWidth { get; }
    /// <summary>Positive bounded logical canvas height.</summary>
    public int CanvasHeight { get; }
    /// <summary>The existing centered placement and crop policy.</summary>
    public BackgroundFit Fit { get; }
    /// <summary>The existing normalized playback policy.</summary>
    public BackgroundPlaybackMode Playback { get; }
    /// <summary>The fixed canonical bottom color used after seven-moment averaging.</summary>
    public LinearPremulRgba BackColor { get; }
    /// <summary>The fixed output background policy.</summary>
    public MediaOutputMode OutputMode { get; }
    /// <summary>The exact supported display geometry profile.</summary>
    public string GeometryProfile { get; }
    /// <summary>The exact supported linear source-over composition profile.</summary>
    public string CompositeProfile { get; }
    /// <summary>The exact supported binary64 area sampling profile.</summary>
    public string SamplingProfile { get; }

    /// <summary>Creates a validated mapping using the existing fit and playback enums.</summary>
    /// <param name="canvasWidth">Positive bounded canvas width.</param>
    /// <param name="canvasHeight">Positive bounded canvas height.</param>
    /// <param name="fit">A defined existing fit.</param>
    /// <param name="playback">A defined existing playback policy.</param>
    /// <param name="backColor">Canonical finite positive-zero premultiplied linear SDR bottom.</param>
    /// <param name="outputMode">A defined fixed output policy.</param>
    /// <returns>A fixed mapping with its recomputed identity.</returns>
    public static MediaMapping Create(int canvasWidth, int canvasHeight, BackgroundFit fit,
        BackgroundPlaybackMode playback, LinearPremulRgba backColor, MediaOutputMode outputMode)
    {
        var value = new MediaMapping(default, canvasWidth, canvasHeight, fit, playback, backColor, outputMode,
            MediaProfilesV1.Geometry, MediaProfilesV1.BackgroundComposite, MediaProfilesV1.Query);
        MediaMappingKey key = MediaFeatureInputIdentityWriter.ComputeMappingKey(value);
        return new(key, canvasWidth, canvasHeight, fit, playback, backColor, outputMode,
            value.GeometryProfile, value.CompositeProfile, value.SamplingProfile);
    }

    /// <summary>Creates the canonical full-canvas analytic-solid mapping without a second bottom contribution.</summary>
    /// <param name="canvasWidth">Positive bounded canvas width.</param>
    /// <param name="canvasHeight">Positive bounded canvas height.</param>
    /// <returns>Normal output, contain, loop and transparent bottom; fit and playback are irrelevant to a solid.</returns>
    public static MediaMapping CreateSolid(int canvasWidth, int canvasHeight) =>
        Create(canvasWidth, canvasHeight, BackgroundFit.Contain, BackgroundPlaybackMode.Loop, default, MediaOutputMode.Normal);
}
