using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Rendering.Abstractions;

/// <summary>Four binary64 edges in the exact dependency's source or canvas coordinate space.</summary>
public readonly struct MediaFeatureQueryRect
{
    /// <summary>Preserves all four endpoints; the sampling boundary rejects invalid rectangles.</summary>
    public MediaFeatureQueryRect(double left, double top, double right, double bottom)
    { Left = left; Top = top; Right = right; Bottom = bottom; }
    /// <summary>The inclusive left edge.</summary>
    public double Left { get; }
    /// <summary>The inclusive top edge.</summary>
    public double Top { get; }
    /// <summary>The exclusive right edge.</summary>
    public double Right { get; }
    /// <summary>The exclusive bottom edge.</summary>
    public double Bottom { get; }
}

/// <summary>A detached source-space value after raw-moment averaging, without canvas bottom composition.</summary>
public sealed class ProjectSourceMediaFeatureValues
{
    /// <summary>Creates a passive trusted numerical snapshot; no grid or reader is retained.</summary>
    public ProjectSourceMediaFeatureValues(LinearPremulRgba mean, double meanPremulY, double meanSquaredPremulY,
        double meanAlphaTimesPremulY, double meanSquaredAlpha, double premulYVariance)
    {
        Mean = mean; MeanPremulY = meanPremulY; MeanSquaredPremulY = meanSquaredPremulY;
        MeanAlphaTimesPremulY = meanAlphaTimesPremulY; MeanSquaredAlpha = meanSquaredAlpha; PremulYVariance = premulYVariance;
    }
    /// <summary>The mean linear premultiplied source color and alpha.</summary>
    public LinearPremulRgba Mean { get; }
    /// <summary>Mean premultiplied source luminance.</summary>
    public double MeanPremulY { get; }
    /// <summary>Mean squared premultiplied source luminance.</summary>
    public double MeanSquaredPremulY { get; }
    /// <summary>The raw mean of alpha times premultiplied luminance.</summary>
    public double MeanAlphaTimesPremulY { get; }
    /// <summary>The raw mean squared alpha.</summary>
    public double MeanSquaredAlpha { get; }
    /// <summary>Source premultiplied luminance variance after the common quantization tolerance.</summary>
    public double PremulYVariance { get; }
}

/// <summary>A detached canvas-space value after one complete-query-area bottom composition.</summary>
public sealed class ProjectCanvasMediaFeatureValues
{
    /// <summary>Creates a passive trusted composed numerical snapshot.</summary>
    public ProjectCanvasMediaFeatureValues(LinearPremulRgba mean, double meanPremulY, double meanSquaredPremulY, double premulYVariance)
    { Mean = mean; MeanPremulY = meanPremulY; MeanSquaredPremulY = meanSquaredPremulY; PremulYVariance = premulYVariance; }
    /// <summary>The complete-query-area linear premultiplied mean.</summary>
    public LinearPremulRgba Mean { get; }
    /// <summary>The composed mean premultiplied luminance.</summary>
    public double MeanPremulY { get; }
    /// <summary>The composed mean squared premultiplied luminance.</summary>
    public double MeanSquaredPremulY { get; }
    /// <summary>The composed premultiplied luminance variance.</summary>
    public double PremulYVariance { get; }
    /// <summary>Only exactly opaque means may be treated as visible background luminance.</summary>
    public bool IsOpaque => Mean.A == 1d;
}

/// <summary>A detached trusted sample bound to its fixed input and exact declared dependency.</summary>
public sealed class ProjectMediaFeatureSample
{
    /// <summary>Creates a source-only, canvas-only, or fixed nonavailable snapshot.</summary>
    public ProjectMediaFeatureSample(MediaFeatureInput input, ProjectMediaDependency dependency,
        MediaFeatureQueryRect rect, long projectTimeUs, ProjectSourceMediaFeatureValues? sourceValues = null,
        ProjectCanvasMediaFeatureValues? canvasValues = null)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(dependency);
        bool source = input.State == MediaInputState.Available && dependency.Query.Space == EffectMediaCoordinateSpace.SourceNormalized;
        bool canvas = input.State == MediaInputState.Available && dependency.Query.Space == EffectMediaCoordinateSpace.CanvasPixels;
        if ((sourceValues is not null) != source || (canvasValues is not null) != canvas)
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Sample values do not agree with the fixed input state and declared coordinate space.");
        Input = input; Dependency = dependency; Rect = rect; ProjectTimeUs = projectTimeUs;
        SourceValues = sourceValues; CanvasValues = canvasValues;
    }
    /// <summary>The immutable complete input; no state transition is performed by sampling.</summary>
    public MediaFeatureInput Input { get; }
    /// <summary>The exact fixed plan dependency that authorized the query.</summary>
    public ProjectMediaDependency Dependency { get; }
    /// <summary>The original four binary64 endpoints.</summary>
    public MediaFeatureQueryRect Rect { get; }
    /// <summary>The explicitly requested nonnegative engineering time.</summary>
    public long ProjectTimeUs { get; }
    /// <summary>Present only for an available SourceNormalized query.</summary>
    public ProjectSourceMediaFeatureValues? SourceValues { get; }
    /// <summary>Present only for an available CanvasPixels query.</summary>
    public ProjectCanvasMediaFeatureValues? CanvasValues { get; }
}
