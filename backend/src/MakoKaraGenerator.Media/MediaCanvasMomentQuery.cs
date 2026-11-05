using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>Uncomposed seven-moment canvas-area means, including transparent source in valid letterbox area.</summary>
/// <param name="RawMoments">Seven original moments averaged over the entire canvas query, before background composition.</param>
/// <param name="MediaCoverage">The fraction of query area occupied by the placed media.</param>
public readonly record struct MediaCanvasMomentSample(MediaMomentValues RawMoments, double MediaCoverage);

/// <summary>Pure CPU binary64 area integration through the same display placement used by presentation.</summary>
/// <remarks>No pixels, grids, profiles or per-query memory are retained. This helper is neither the SDK query service nor the GPU prefix-texture implementation.</remarks>
public sealed class MediaCanvasMomentQuery
{
    private readonly MediaMomentKernel _kernel;

    /// <summary>Uses the common source-grid numerical kernel without adding an SDK interface.</summary>
    public MediaCanvasMomentQuery(MediaMomentKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _kernel = kernel;
    }

    /// <summary>Area-averages seven raw quantities before any background composition or variance calculation.</summary>
    /// <param name="grid">Exactly the fixed row-major grid, normally already time-averaged as seven raw quantities.</param>
    /// <param name="placement">The actual media display placement for this canvas.</param>
    /// <param name="rect">A finite nonempty canvas rectangle wholly inside the declared canvas; outside edges are rejected.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>Full-query-area raw means and media coverage. Valid letterbox contributes seven zero source moments.</returns>
    /// <exception cref="MediaAbException">The rectangle, grid or binary64 integration area is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    public MediaCanvasMomentSample Integrate(ReadOnlySpan<MediaMomentValues> grid, MediaDisplayPlacement placement,
        MediaCanvasRect rect, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(placement);
        if (!double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) ||
            !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom) ||
            rect.Left < 0 || rect.Top < 0 || rect.Right > placement.CanvasWidth || rect.Bottom > placement.CanvasHeight ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            throw Invalid("Canvas rectangle must be finite nonempty and wholly inside the declared canvas.");
        }

        double width = rect.Right - rect.Left;
        double height = rect.Bottom - rect.Top;
        MediaDisplayRect destination = placement.Destination;
        double left = Math.Max(rect.Left, destination.X);
        double top = Math.Max(rect.Top, destination.Y);
        double right = Math.Min(rect.Right, destination.X + destination.Width);
        double bottom = Math.Min(rect.Bottom, destination.Y + destination.Height);
        if (right <= left || bottom <= top)
        {
            MediaMomentKernel.ValidateGrid(grid, cancellationToken);
            return new(default, 0);
        }

        // Intersecting valid letterbox with the placed media is semantic coverage, not repair of invalid input.
        // Normalize each extent before multiplying: tiny canvas rectangles do not need their area product to survive.
        double coverage = ((right - left) / width) * ((bottom - top) / height);
        if (!double.IsFinite(coverage) || coverage <= 0 || coverage > 1)
        {
            throw Invalid("Canvas media coverage is not a finite positive unit weight.");
        }

        if (!placement.TryCanvasToSourceNormalized(left, top, out var sourceStart) ||
            !placement.TryCanvasToSourceNormalized(right, bottom, out var sourceEnd))
        {
            throw Invalid("Canvas media intersection did not map through its declared display placement.");
        }

        // No binary32 intermediate and no clamping, expansion or additional coordinate-quantization protocol.
        var sourceRect = new MediaNormalizedRect64(sourceStart.X, sourceStart.Y, sourceEnd.X, sourceEnd.Y);
        MediaMomentValues media = _kernel.IntegrateNormalizedRect(grid, sourceRect, cancellationToken);
        var raw = new MediaMomentValues(media.Rp * coverage, media.Gp * coverage, media.Bp * coverage,
            media.Alpha * coverage, media.YpSquared * coverage, media.AlphaYp * coverage, media.AlphaSquared * coverage);
        cancellationToken.ThrowIfCancellationRequested();
        return new(raw, coverage);
    }

    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.StatisticsInvalid, detail);
}
