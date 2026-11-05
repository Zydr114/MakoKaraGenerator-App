using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Allocation-free exact-area construction and bounded source-grid integration of seven SDR moments.</summary>
/// <remarks>Only callback-local spans are used. The instance never keeps pixels, destinations, grids or frame metadata. Input and output backing are charged by their owner; this class checks its fixed scratch requirement separately.</remarks>
public sealed class MediaMomentKernel : IMediaMomentKernel
{
    /// <summary>The exact numerical scratch backing for seven means, seven complementary means and their compensation values.</summary>
    /// <remarks>This bounds explicitly allocated scratch, not total CLR stack, input pixels, destination backing or an owner reservation ledger.</remarks>
    public const int WorkingBufferBytes = 4 * MediaTrackLayoutV1.Moments * sizeof(double);

    /// <summary>Creates a numerical kernel with an admitted fixed scratch cap.</summary>
    /// <param name="workingByteLimit">The owner-admitted scratch cap, at least <see cref="WorkingBufferBytes"/> and no greater than the fixed Core builder ceiling.</param>
    /// <exception cref="MediaAbException">The cap is unsupported or insufficient before any scratch allocation.</exception>
    public MediaMomentKernel(long workingByteLimit = WorkingBufferBytes)
    {
        if (workingByteLimit < 0 || workingByteLimit > MediaAbCapacityV1.MaximumBuilderWorkingBytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityUnsupported, "Moment scratch cap is outside the fixed builder capacity profile.");
        }

        if (workingByteLimit < WorkingBufferBytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Moment scratch cap is smaller than the fixed numerical working buffer.");
        }

        WorkingByteLimit = workingByteLimit;
    }

    /// <summary>The accepted cap for this kernel's fixed numerical scratch backing.</summary>
    public long WorkingByteLimit { get; }

    /// <inheritdoc/>
    public void BuildGrid(PresentationFrameBorrow frame, Span<QuantizedMediaMoments> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireGridLength(destination.Length);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Observation);
        int width = frame.Observation.Width;
        int height = frame.Observation.Height;
        long frameBytes = MediaDecodeAdmissionV1.GetCanonicalFrameBytes(width, height);
        int expectedElements = checked((int)(frameBytes / sizeof(float)));
        if (frame.LinearPremulRgba.Length != expectedElements)
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Canonical frame memory must exactly cover the packed raster.");
        }

        _ = GetMaximumPixelContributions(width, height);
        ReadOnlySpan<float> pixels = frame.LinearPremulRgba.Span;
        cancellationToken.ThrowIfCancellationRequested();
        Span<double> scratch = stackalloc double[4 * MediaTrackLayoutV1.Moments];
        Span<double> sums = scratch[..(2 * MediaTrackLayoutV1.Moments)];
        Span<double> compensation = scratch[(2 * MediaTrackLayoutV1.Moments)..];
        long cellWeight = checked((long)width * height);
        for (int cellY = 0; cellY < MediaTrackLayoutV1.Rows; cellY++)
        {
            int top = checked(cellY * height);
            int bottom = checked((cellY + 1) * height);
            int firstY = top / MediaTrackLayoutV1.Rows;
            int endY = checked((bottom + MediaTrackLayoutV1.Rows - 1) / MediaTrackLayoutV1.Rows);
            for (int cellX = 0; cellX < MediaTrackLayoutV1.Columns; cellX++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sums.Clear();
                compensation.Clear();
                int left = checked(cellX * width);
                int right = checked((cellX + 1) * width);
                int firstX = left / MediaTrackLayoutV1.Columns;
                int endX = checked((right + MediaTrackLayoutV1.Columns - 1) / MediaTrackLayoutV1.Columns);
                long contributedWeight = 0;
                for (int y = firstY; y < endY; y++)
                {
                    int overlapY = checked(Math.Min(bottom, (y + 1) * MediaTrackLayoutV1.Rows) - Math.Max(top, y * MediaTrackLayoutV1.Rows));
                    for (int x = firstX; x < endX; x++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int overlapX = checked(Math.Min(right, (x + 1) * MediaTrackLayoutV1.Columns) - Math.Max(left, x * MediaTrackLayoutV1.Columns));
                        long weight = checked((long)overlapX * overlapY);
                        if (weight <= 0)
                        {
                            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Canonical pixel overlap must be strictly positive.");
                        }

                        contributedWeight = checked(contributedWeight + weight);
                        int offset = checked(checked(y * width + x) * 4);
                        double red = pixels[offset];
                        double green = pixels[offset + 1];
                        double blue = pixels[offset + 2];
                        double alpha = pixels[offset + 3];
                        ValidatePixel(red, green, blue, alpha);
                        AddMoments(sums, compensation, ComputeMoments(red, green, blue, alpha), weight);
                    }
                }

                if (contributedWeight != cellWeight)
                {
                    throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Canonical cell overlap does not cover its exact rational area.");
                }

                MediaMomentValues means = Normalize(sums, cellWeight);
                destination[checked(cellY * MediaTrackLayoutV1.Columns + cellX)] = Quantize(means);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc/>
    public void BuildSolidGrid(LinearPremulRgba color, Span<QuantizedMediaMoments> destination) =>
        BuildSolidGrid(color, destination, CancellationToken.None);

    /// <summary>Builds a solid grid with explicit cancellation and the same seven per-color operations as pixel integration.</summary>
    /// <param name="color">The exact finite canonical double-valued solid color.</param>
    /// <param name="destination">Exactly the fixed row-major cell count.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <exception cref="MediaAbException">The color or destination is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    /// <remarks>Solid values are not implicitly recast to binary32. A one-pixel binary32 source agrees when its values equal the supplied solid values. Discard partial output on cancellation.</remarks>
    public void BuildSolidGrid(LinearPremulRgba color, Span<QuantizedMediaMoments> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireGridLength(destination.Length);
        MediaAbValueValidator.ValidateLinearPremul(color);
        QuantizedMediaMoments quantized = Quantize(ComputeMoments(color.R, color.G, color.B, color.A));
        for (int index = 0; index < destination.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination[index] = quantized;
        }
    }

    /// <inheritdoc/>
    public MediaMomentValues IntegrateSourceRect(ReadOnlySpan<MediaMomentValues> grid, MediaSourceRect rect) =>
        IntegrateSourceRect(grid, rect, CancellationToken.None);

    /// <summary>Area-averages original moments with explicit cancellation.</summary>
    /// <param name="grid">Exactly the fixed row-major grid count; all entries are checked even outside the rectangle.</param>
    /// <param name="rect">Finite binary32 source coordinates within the unit square.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>Seven source-rectangle means without quantization, variance or background composition.</returns>
    /// <exception cref="MediaAbException">The grid, moments or rectangle is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    public MediaMomentValues IntegrateSourceRect(ReadOnlySpan<MediaMomentValues> grid, MediaSourceRect rect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireGridLength(grid.Length);
        ValidateRect(rect);
        foreach (MediaMomentValues value in grid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMoments(value);
        }

        // A binary32 coordinate times 32 or 18 is exactly representable in binary64.
        // Working in grid units makes cell boundaries exact integers and avoids an approximate 1/18.
        double left = (double)rect.Left * MediaTrackLayoutV1.Columns;
        double right = (double)rect.Right * MediaTrackLayoutV1.Columns;
        double top = (double)rect.Top * MediaTrackLayoutV1.Rows;
        double bottom = (double)rect.Bottom * MediaTrackLayoutV1.Rows;
        double area = (right - left) * (bottom - top);
        Span<double> scratch = stackalloc double[4 * MediaTrackLayoutV1.Moments];
        scratch.Clear();
        Span<double> sums = scratch[..(2 * MediaTrackLayoutV1.Moments)];
        Span<double> compensation = scratch[(2 * MediaTrackLayoutV1.Moments)..];
        for (int cellY = 0; cellY < MediaTrackLayoutV1.Rows; cellY++)
        {
            double overlapY = Math.Min(bottom, cellY + 1d) - Math.Max(top, cellY);
            if (overlapY <= 0)
            {
                continue;
            }

            for (int cellX = 0; cellX < MediaTrackLayoutV1.Columns; cellX++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double overlapX = Math.Min(right, cellX + 1d) - Math.Max(left, cellX);
                if (overlapX > 0)
                {
                    AddMoments(sums, compensation, grid[checked(cellY * MediaTrackLayoutV1.Columns + cellX)], overlapX * overlapY);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Normalize(sums, area);
    }

    /// <summary>Quantizes one finite unit value by the normative floor of value times 65535 plus one half.</summary>
    /// <param name="value">A finite canonical value in the closed unit interval.</param>
    /// <returns>The unsigned 16-bit half-up value without clamping.</returns>
    /// <exception cref="MediaAbException">The value is invalid.</exception>
    public static ushort QuantizeUnit(double value)
    {
        ValidateUnit(value);
        return checked((ushort)Math.Floor(value * MediaTrackLayoutV1.QuantMax + 0.5d));
    }

    /// <summary>Computes a checked finite upper bound on source-pixel contributions for one admitted raster.</summary>
    /// <param name="width">The bounded positive raster width.</param>
    /// <param name="height">The bounded positive raster height.</param>
    /// <returns>At most (width plus 31) times (height plus 17) contributions; no loop depends on external unbounded counts.</returns>
    /// <exception cref="MediaAbException">The raster is outside Core capacity.</exception>
    public static long GetMaximumPixelContributions(int width, int height)
    {
        _ = MediaDecodeAdmissionV1.GetCanonicalFrameBytes(width, height);
        return checked((width + (long)MediaTrackLayoutV1.Columns - 1) * (height + (long)MediaTrackLayoutV1.Rows - 1));
    }

    private static void RequireGridLength(int length)
    {
        if (length != MediaTrackLayoutV1.Cells)
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Moment destination or source must have exactly the fixed grid cell count.");
        }
    }

    private static void ValidateRect(MediaSourceRect rect)
    {
        if (!float.IsFinite(rect.Left) || !float.IsFinite(rect.Top) || !float.IsFinite(rect.Right) || !float.IsFinite(rect.Bottom) ||
            rect.Left < 0 || rect.Top < 0 || rect.Right > 1 || rect.Bottom > 1 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Source rectangle must be finite nonempty and wholly inside the unit square.");
        }
    }

    private static void ValidatePixel(double red, double green, double blue, double alpha)
    {
        ValidateUnit(red);
        ValidateUnit(green);
        ValidateUnit(blue);
        ValidateUnit(alpha);
        if (red > alpha || green > alpha || blue > alpha)
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Canonical source pixel is not linear premultiplied RGBA.");
        }
    }

    private static void ValidateUnit(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1 || (value == 0 && BitConverter.DoubleToInt64Bits(value) < 0))
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Canonical moment component must be finite positive-zero and within the unit interval.");
        }
    }

    private static MediaMomentValues ComputeMoments(double red, double green, double blue, double alpha)
    {
        double luminance = 0.2126d * red + 0.7152d * green + 0.0722d * blue;
        return new(red, green, blue, alpha, luminance * luminance, alpha * luminance, alpha * alpha);
    }

    private static void ValidateMoments(MediaMomentValues value)
    {
        ValidateUnit(value.Rp);
        ValidateUnit(value.Gp);
        ValidateUnit(value.Bp);
        ValidateUnit(value.Alpha);
        ValidateUnit(value.YpSquared);
        ValidateUnit(value.AlphaYp);
        ValidateUnit(value.AlphaSquared);
    }

    private static QuantizedMediaMoments Quantize(MediaMomentValues value) => new(
        QuantizeUnit(value.Rp), QuantizeUnit(value.Gp), QuantizeUnit(value.Bp), QuantizeUnit(value.Alpha),
        QuantizeUnit(value.YpSquared), QuantizeUnit(value.AlphaYp), QuantizeUnit(value.AlphaSquared));

    private static MediaMomentValues Normalize(ReadOnlySpan<double> sums, double area)
    {
        if (!double.IsFinite(area) || area <= 0)
        {
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Moment integration area must be finite and strictly positive.");
        }

        var value = new MediaMomentValues(Mean(sums, 0, area), Mean(sums, 1, area), Mean(sums, 2, area), Mean(sums, 3, area),
            Mean(sums, 4, area), Mean(sums, 5, area), Mean(sums, 6, area));
        ValidateMoments(value);
        return value;
    }

    private static double Mean(ReadOnlySpan<double> sums, int index, double area)
    {
        // Evaluate the nearer endpoint representation of the same affine average.
        // This is not a clamp. Exact constants zero and one survive rounded rectangle-area products,
        // while direct positive sums and complementary positive sums cannot create endpoint overshoot.
        return sums[index] <= area * 0.5d
            ? sums[index] / area
            : 1d - sums[index + MediaTrackLayoutV1.Moments] / area;
    }

    private static void AddMoments(Span<double> sums, Span<double> compensation, MediaMomentValues value, double weight)
    {
        AddPair(sums, compensation, 0, value.Rp, weight);
        AddPair(sums, compensation, 1, value.Gp, weight);
        AddPair(sums, compensation, 2, value.Bp, weight);
        AddPair(sums, compensation, 3, value.Alpha, weight);
        AddPair(sums, compensation, 4, value.YpSquared, weight);
        AddPair(sums, compensation, 5, value.AlphaYp, weight);
        AddPair(sums, compensation, 6, value.AlphaSquared, weight);
    }

    private static void AddPair(Span<double> sums, Span<double> compensation, int index, double value, double weight)
    {
        Add(sums, compensation, index, value * weight);
        Add(sums, compensation, index + MediaTrackLayoutV1.Moments, (1d - value) * weight);
    }

    private static void Add(Span<double> sums, Span<double> compensation, int index, double value)
    {
        double corrected = value - compensation[index];
        double updated = sums[index] + corrected;
        compensation[index] = (updated - sums[index]) - corrected;
        sums[index] = updated;
    }
}
