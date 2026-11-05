using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Media;

/// <summary>A finite rectangle in canonical normalized or canvas coordinates, as named by its owner.</summary>
public readonly record struct MediaDisplayRect(double X, double Y, double Width, double Height);

/// <summary>Pure binary64 SAR display placement with exact raster geometry kept separately.</summary>
/// <remarks>Delegates all fit and crop math to the shared BackgroundPlacement double entry.
/// No raster is resampled and no project fit is added.</remarks>
public sealed class MediaDisplayPlacement
{
    private readonly double _fullX, _fullY, _fullWidth, _fullHeight;

    private MediaDisplayPlacement(int canvasWidth, int canvasHeight, double displayWidth, double displayHeight,
        BackgroundDisplayPlacement placement)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        SourceNormalized = new(placement.Source.X / displayWidth, placement.Source.Y / displayHeight,
            placement.Source.Width / displayWidth, placement.Source.Height / displayHeight);
        Destination = new(placement.Destination.X, placement.Destination.Y, placement.Destination.Width, placement.Destination.Height);
        _fullWidth = Destination.Width / SourceNormalized.Width;
        _fullHeight = Destination.Height / SourceNormalized.Height;
        _fullX = Destination.X - SourceNormalized.X * _fullWidth;
        _fullY = Destination.Y - SourceNormalized.Y * _fullHeight;
        RequireFinitePositive(_fullWidth, _fullHeight);
        RequireFinitePositive(SourceNormalized.Width, SourceNormalized.Height);
    }

    /// <summary>The bounded canvas width.</summary>
    public int CanvasWidth { get; }

    /// <summary>The bounded canvas height.</summary>
    public int CanvasHeight { get; }

    /// <summary>The visible rectangle within the rotated canonical raster normalized unit square.</summary>
    public MediaDisplayRect SourceNormalized { get; }

    /// <summary>The corresponding visible destination within the canvas.</summary>
    public MediaDisplayRect Destination { get; }

    /// <summary>Computes existing fit semantics from exact display lengths with one conversion per length.</summary>
    /// <param name="geometry">The validated canonical geometry.</param>
    /// <param name="canvasWidth">The bounded positive canvas width.</param>
    /// <param name="canvasHeight">The bounded positive canvas height.</param>
    /// <param name="fit">A defined existing project fit, without unknown-value fallback.</param>
    public static MediaDisplayPlacement Compute(MediaRasterGeometry geometry, int canvasWidth, int canvasHeight, BackgroundFit fit)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        MediaAbRules.ValidateDimensions(canvasWidth, canvasHeight);
        double width = geometry.DisplayWidth.ToDouble(), height = geometry.DisplayHeight.ToDouble();
        if (!Enum.IsDefined(fit))
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Background fit is not supported by display geometry.");
        }

        return new(canvasWidth, canvasHeight, width, height,
            BackgroundPlacement.Compute(width, height, canvasWidth, canvasHeight, fit));
    }

    /// <summary>Maps a canonical unit-square point, including source content cropped outside the canvas.</summary>
    /// <param name="x">The finite closed-unit-square horizontal coordinate.</param>
    /// <param name="y">The finite closed-unit-square vertical coordinate.</param>
    public (double X, double Y) SourceNormalizedToCanvas(double x, double y)
    {
        RequirePoint(x, y, 1, 1);
        return (MapSourceAxis(x, SourceNormalized.X, SourceNormalized.Width, Destination.X, Destination.Width, _fullX, _fullWidth),
            MapSourceAxis(y, SourceNormalized.Y, SourceNormalized.Height, Destination.Y, Destination.Height, _fullY, _fullHeight));
    }

    /// <summary>Returns false for contain or original letterboxing; never clamps outside canvas or invalid coordinates.</summary>
    /// <param name="x">The finite horizontal canvas coordinate.</param>
    /// <param name="y">The finite vertical canvas coordinate.</param>
    /// <param name="source">The canonical normalized point when the canvas point contains media.</param>
    public bool TryCanvasToSourceNormalized(double x, double y, out (double X, double Y) source)
    {
        RequirePoint(x, y, CanvasWidth, CanvasHeight);
        if (x < Destination.X || y < Destination.Y ||
            x > Destination.X + Destination.Width || y > Destination.Y + Destination.Height)
        {
            source = default;
            return false;
        }

        source = (MapCanvasAxis(x, Destination.X, Destination.Width, SourceNormalized.X, SourceNormalized.Width, _fullX, _fullWidth),
            MapCanvasAxis(y, Destination.Y, Destination.Height, SourceNormalized.Y, SourceNormalized.Height, _fullY, _fullHeight));
        return true;
    }

    private static double MapCanvasAxis(double value, double destinationStart, double destinationLength,
        double sourceStart, double sourceLength, double fullStart, double fullLength)
    {
        // Exact declared edges are correspondences, not near-edge values to clamp. Recomputing
        // (start + length - start) / length can otherwise place an actual edge outside [0, 1].
        if (value == destinationStart) return sourceStart;
        if (value == destinationStart + destinationLength) return sourceStart + sourceLength;
        return (value - fullStart) / fullLength;
    }

    private static double MapSourceAxis(double value, double sourceStart, double sourceLength,
        double destinationStart, double destinationLength, double fullStart, double fullLength)
    {
        if (value == sourceStart) return destinationStart;
        if (value == sourceStart + sourceLength) return destinationStart + destinationLength;
        return fullStart + value * fullLength;
    }

    private static void RequirePoint(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > width || y > height)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Mapping coordinate is not finite or outside the declared coordinate space.");
        }
    }

    private static void RequireFinitePositive(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Display placement has an invalid finite extent.");
        }
    }
}
