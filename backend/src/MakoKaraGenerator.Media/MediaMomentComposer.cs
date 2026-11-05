using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>Composed linear-premultiplied statistics; luminance variance is not unpremultiplied pixel variance.</summary>
/// <param name="MeanLinearPremulRgba">Mean source-over color and alpha in linear premultiplied space.</param>
/// <param name="MeanPremulY">Mean premultiplied luminance.</param>
/// <param name="MeanSquaredPremulY">Mean of per-sample squared premultiplied luminance after composition.</param>
/// <param name="PremulYVariance">Second moment minus squared mean, with only the declared negative quantization tolerance.</param>
public readonly record struct MediaCompositedMoments(LinearPremulRgba MeanLinearPremulRgba,
    double MeanPremulY, double MeanSquaredPremulY, double PremulYVariance)
{
    /// <summary>Only exactly opaque composed means may be used as visible background luminance or contrast.</summary>
    public bool IsOpaque => MeanLinearPremulRgba.A == 1d;
}

/// <summary>Pure final background composition after spatial and temporal averaging of all seven raw moments.</summary>
public static class MediaMomentComposer
{
    /// <summary>The fixed per-component half-step bound of the u16 moment profile.</summary>
    public const double QuantizationDelta = 1d / (2d * MediaTrackLayoutV1.QuantMax);

    /// <summary>The only allowed negative-variance interval; excludes transform, grid and GPU sampling errors.</summary>
    public const double NegativeVarianceTolerance = 13d * QuantizationDelta + 1e-12d;

    /// <summary>Composes one already averaged raw seven-moment value over a fixed canonical bottom color.</summary>
    /// <param name="raw">Spatial and temporal weights must already be nonnegative and normalized; do not average variances.</param>
    /// <param name="bottom">The finite validated linear-premultiplied bottom color.</param>
    /// <returns>Explicit linear-premultiplied means and premultiplied-luminance second moment and variance.</returns>
    /// <exception cref="MediaAbException">Input is invalid or variance is more negative than the fixed profile permits.</exception>
    public static MediaCompositedMoments Compose(MediaMomentValues raw, LinearPremulRgba bottom)
    {
        MediaMomentKernel.ValidateMoments(raw);
        MediaAbValueValidator.ValidateLinearPremul(bottom);
        double remainingAlpha = 1d - raw.Alpha;
        var mean = new LinearPremulRgba(raw.Rp + bottom.R * remainingAlpha,
            raw.Gp + bottom.G * remainingAlpha, raw.Bp + bottom.B * remainingAlpha,
            raw.Alpha + bottom.A * remainingAlpha);
        MediaAbValueValidator.ValidateLinearPremul(mean);
        double sourceY = 0.2126d * raw.Rp + 0.7152d * raw.Gp + 0.0722d * raw.Bp;
        double bottomY = 0.2126d * bottom.R + 0.7152d * bottom.G + 0.0722d * bottom.B;
        double meanY = sourceY + bottomY * remainingAlpha;
        double secondY = raw.YpSquared + 2d * bottomY * (sourceY - raw.AlphaYp)
            + bottomY * bottomY * (1d - 2d * raw.Alpha + raw.AlphaSquared);
        double variance = secondY - meanY * meanY;
        if (!double.IsFinite(meanY) || !double.IsFinite(secondY) || !double.IsFinite(variance) ||
            variance < -NegativeVarianceTolerance)
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Composed premultiplied-luminance statistics exceed the fixed negative-variance tolerance.");
        }

        if (variance < 0) variance = 0;
        return new(mean, meanY, secondY, variance);
    }
}
