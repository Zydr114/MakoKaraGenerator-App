using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Pure source-space seven-moment operations using the single canonical Core layout.</summary>
public interface IMediaMomentKernel
{
    /// <summary>Builds the same fixed solid grid with cancellation propagated unchanged.</summary>
    /// <param name="color">Finite canonical linear-premultiplied color.</param>
    /// <param name="destination">Exactly the fixed grid cell count.</param>
    /// <param name="cancellationToken">Cancellation for this bounded operation.</param>
    void BuildSolidGrid(LinearPremulRgba color, Span<QuantizedMediaMoments> destination, CancellationToken cancellationToken);

    /// <summary>Integrates the same source rectangle with cancellation propagated unchanged.</summary>
    /// <param name="grid">Exactly the fixed grid cell count.</param>
    /// <param name="rect">A finite nonempty rectangle in the source unit square.</param>
    /// <param name="cancellationToken">Cancellation for this bounded operation.</param>
    /// <returns>Seven area-weighted original moment means.</returns>
    MediaMomentValues IntegrateSourceRect(ReadOnlySpan<MediaMomentValues> grid, MediaSourceRect rect, CancellationToken cancellationToken);

    /// <summary>Builds the exact-area source grid from borrowed canonical pixels.</summary>
    /// <param name="frame">Callback-scoped top-left packed linear premultiplied RGBA32F pixels.</param>
    /// <param name="destination">Exactly <see cref="MediaTrackLayoutV1.Cells"/> cells in row-major order.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged without retaining the borrow.</param>
    /// <exception cref="MediaAbException">Pixel, raster, destination or capacity requirements fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    /// <remarks>Destination may be partially written on failure or cancellation and must then be discarded. Source authority and exact PTS pairing remain caller responsibilities.</remarks>
    void BuildGrid(PresentationFrameBorrow frame, Span<QuantizedMediaMoments> destination, CancellationToken cancellationToken);

    /// <summary>Computes the same seven moments for a canonical solid color and copies them to every cell.</summary>
    /// <param name="color">Finite linear premultiplied SDR color without clamping.</param>
    /// <param name="destination">Exactly <see cref="MediaTrackLayoutV1.Cells"/> row-major cells.</param>
    /// <exception cref="MediaAbException">Color or destination is invalid.</exception>
    void BuildSolidGrid(LinearPremulRgba color, Span<QuantizedMediaMoments> destination);

    /// <summary>Area-averages the piecewise-constant source grid over a binary32 source rectangle.</summary>
    /// <param name="grid">Exactly <see cref="MediaTrackLayoutV1.Cells"/> seven-moment cells.</param>
    /// <param name="rect">A finite nonempty rectangle wholly within the normalized source.</param>
    /// <returns>The seven original means, without averaging variance.</returns>
    /// <exception cref="MediaAbException">Rectangle, grid size or a grid component is invalid.</exception>
    MediaMomentValues IntegrateSourceRect(ReadOnlySpan<MediaMomentValues> grid, MediaSourceRect rect);
}
