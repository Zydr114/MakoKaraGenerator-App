namespace MakoKaraGenerator.Core.Media;

/// <summary>An exact positive reduced display length before the one binary64 placement conversion.</summary>
public readonly record struct MediaDisplayLength
{
    internal MediaDisplayLength(long numerator, int denominator)
    {
        long divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = checked((int)(denominator / divisor));
    }

    /// <summary>The exact positive numerator in canvas units.</summary>
    public long Numerator { get; }

    /// <summary>The exact positive reduced denominator.</summary>
    public int Denominator { get; }

    /// <summary>Converts the validated ratio once, without rounding to an integer raster width.</summary>
    public double ToDouble()
    {
        if (Numerator <= 0 || Denominator <= 0)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Display length must be a positive reduced rational value.");
        }

        return Numerator / (double)Denominator;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }
}

/// <summary>Validated exact raster permutation and SAR display metrics for geometry profile version one.</summary>
/// <remarks>Canonical dimensions are raster counts. SAR never allocates, scales or resamples pixels.</remarks>
public sealed class MediaRasterGeometry
{
    private MediaRasterGeometry(int width, int height, int sarNumerator, int sarDenominator,
        MediaQuarterTurn rotation, bool mirror)
    {
        EncodedWidth = width;
        EncodedHeight = height;
        SarNumerator = sarNumerator;
        SarDenominator = sarDenominator;
        Rotation = rotation;
        MirrorHorizontallyBeforeRotation = mirror;
        bool swapped = rotation is MediaQuarterTurn.Clockwise90 or MediaQuarterTurn.Clockwise270;
        CanonicalWidth = swapped ? height : width;
        CanonicalHeight = swapped ? width : height;
        var encodedDisplayWidth = new MediaDisplayLength(checked((long)width * sarNumerator), sarDenominator);
        var encodedDisplayHeight = new MediaDisplayLength(height, 1);
        DisplayWidth = swapped ? encodedDisplayHeight : encodedDisplayWidth;
        DisplayHeight = swapped ? encodedDisplayWidth : encodedDisplayHeight;
    }

    /// <summary>The unrotated raster width.</summary>
    public int EncodedWidth { get; }

    /// <summary>The unrotated raster height.</summary>
    public int EncodedHeight { get; }

    /// <summary>The rotated raster width without SAR resampling.</summary>
    public int CanonicalWidth { get; }

    /// <summary>The rotated raster height without SAR resampling.</summary>
    public int CanonicalHeight { get; }

    /// <summary>The positive reduced encoded pixel aspect numerator.</summary>
    public int SarNumerator { get; }

    /// <summary>The positive reduced encoded pixel aspect denominator.</summary>
    public int SarDenominator { get; }

    /// <summary>The rotation applied after mirroring.</summary>
    public MediaQuarterTurn Rotation { get; }

    /// <summary>Whether encoded pixels are horizontally mirrored before rotation.</summary>
    public bool MirrorHorizontallyBeforeRotation { get; }

    /// <summary>The exact displayed width after rotation.</summary>
    public MediaDisplayLength DisplayWidth { get; }

    /// <summary>The exact displayed height after rotation.</summary>
    public MediaDisplayLength DisplayHeight { get; }

    /// <summary>Validates bounded dimensions, positive reduced SAR and a supported orthogonal rotation.</summary>
    /// <param name="width">The encoded raster width.</param>
    /// <param name="height">The encoded raster height.</param>
    /// <param name="sarNumerator">The positive reduced SAR numerator.</param>
    /// <param name="sarDenominator">The positive reduced SAR denominator.</param>
    /// <param name="rotation">The clockwise quarter turn.</param>
    /// <param name="mirrorHorizontallyBeforeRotation">Whether to mirror first.</param>
    public static MediaRasterGeometry Create(int width, int height, int sarNumerator, int sarDenominator,
        MediaQuarterTurn rotation, bool mirrorHorizontallyBeforeRotation)
    {
        MediaAbRules.ValidateDimensions(width, height);
        MediaAbRules.ValidateReducedRatio(sarNumerator, sarDenominator, MediaAbFailure.PresentationInvalid);
        if (!Enum.IsDefined(rotation))
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Raster rotation must be a supported quarter turn.");
        }

        return new(width, height, sarNumerator, sarDenominator, rotation, mirrorHorizontallyBeforeRotation);
    }

    /// <summary>Maps one encoded pixel index to exactly one canonical pixel index.</summary>
    /// <param name="x">The encoded horizontal index.</param>
    /// <param name="y">The encoded vertical index.</param>
    public (int X, int Y) EncodedToCanonical(int x, int y)
    {
        RequirePixel(x, y, EncodedWidth, EncodedHeight);
        int mirroredX = MirrorHorizontallyBeforeRotation ? EncodedWidth - 1 - x : x;
        return Rotation switch
        {
            MediaQuarterTurn.None => (mirroredX, y),
            MediaQuarterTurn.Clockwise90 => (EncodedHeight - 1 - y, mirroredX),
            MediaQuarterTurn.Clockwise180 => (EncodedWidth - 1 - mirroredX, EncodedHeight - 1 - y),
            MediaQuarterTurn.Clockwise270 => (y, EncodedWidth - 1 - mirroredX),
            _ => throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Raster rotation is invalid."),
        };
    }

    /// <summary>Maps a canonical pixel index back to its original encoded pixel without interpolation.</summary>
    /// <param name="x">The canonical horizontal index.</param>
    /// <param name="y">The canonical vertical index.</param>
    public (int X, int Y) CanonicalToEncoded(int x, int y)
    {
        RequirePixel(x, y, CanonicalWidth, CanonicalHeight);
        (int mirroredX, int encodedY) = Rotation switch
        {
            MediaQuarterTurn.None => (x, y),
            MediaQuarterTurn.Clockwise90 => (y, EncodedHeight - 1 - x),
            MediaQuarterTurn.Clockwise180 => (EncodedWidth - 1 - x, EncodedHeight - 1 - y),
            MediaQuarterTurn.Clockwise270 => (EncodedWidth - 1 - y, x),
            _ => throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Raster rotation is invalid."),
        };
        return (MirrorHorizontallyBeforeRotation ? EncodedWidth - 1 - mirroredX : mirroredX, encodedY);
    }

    private static void RequirePixel(int x, int y, int width, int height)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Raster pixel index is outside the accepted dimensions.");
        }
    }
}
