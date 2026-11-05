using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Independent canonical-pixel area and source-over checks; no production query or composer in the oracle.</summary>
public sealed class MediaCanvasMomentTests
{
    private readonly MediaMomentKernel _kernel = new();

    [Theory]
    [InlineData(BackgroundFit.Contain, 320, 240, 0, 30, 320, 180, 0, 0, 320, 240)]
    [InlineData(BackgroundFit.Contain, 320, 240, 0, 30, 320, 180, 19.5, 2.25, 207.75, 174.5)]
    [InlineData(BackgroundFit.Cover, 240, 240, -93.33333333333333, 0, 426.6666666666667, 240, 0, 0, 240, 240)]
    [InlineData(BackgroundFit.Cover, 240, 240, -93.33333333333333, 0, 426.6666666666667, 240, 63.125, 8.5, 189.75, 202.25)]
    [InlineData(BackgroundFit.Original, 80, 60, 24, 21, 32, 18, 0, 0, 80, 60)]
    [InlineData(BackgroundFit.Original, 80, 60, 24, 21, 32, 18, 22.5, 19.5, 50.75, 34.25)]
    public void CanvasMeansAndVarianceMatchIndependentCanonicalPixels(BackgroundFit fit, int canvasWidth, int canvasHeight,
        double fullX, double fullY, double fullWidth, double fullHeight, double l, double t, double r, double b)
    {
        LinearPremulRgba[] pixels = Palette();
        MediaMomentValues[] grid = RawGrid(pixels);
        var placement = MediaDisplayPlacement.Compute(MediaRasterGeometry.Create(32, 18, 1, 1, 0, false), canvasWidth, canvasHeight, fit);
        var rect = new MediaCanvasRect(l, t, r, b);
        foreach (LinearPremulRgba bottom in new[] { new LinearPremulRgba(.125, .25, .375, 1), new LinearPremulRgba(.125, .0625, 0, .5) })
        {
            OracleResult expected = PixelOracle(pixels, fullX, fullY, fullWidth, fullHeight, rect, bottom);
            MediaCanvasMomentSample actual = new MediaCanvasMomentQuery(_kernel).Integrate(grid, placement, rect);
            Near(expected.Coverage, actual.MediaCoverage);
            MomentsNear(expected.Raw, actual.RawMoments);
            ComposedNear(expected, MediaMomentComposer.Compose(actual.RawMoments, bottom));
        }
    }

    [Fact]
    public void RotatedNonSquareSarUsesDisplayAreaWithoutResamplingCanonicalPixels()
    {
        // Encoded 18x32 with SAR 2 and a 90-degree turn is canonical 32x18, displayed as 32x36.
        var geometry = MediaRasterGeometry.Create(18, 32, 2, 1, MediaQuarterTurn.Clockwise90, true);
        var placement = MediaDisplayPlacement.Compute(geometry, 320, 240, BackgroundFit.Contain);
        LinearPremulRgba[] pixels = Palette();
        var rect = new MediaCanvasRect(17.25, 22.75, 277.5, 233.5);
        var bottom = new LinearPremulRgba(.25, .125, .0625, 1);
        OracleResult expected = PixelOracle(pixels, 53.33333333333333, 0, 213.33333333333334, 240, rect, bottom);
        MediaCanvasMomentSample actual = new MediaCanvasMomentQuery(_kernel).Integrate(RawGrid(pixels), placement, rect);
        MomentsNear(expected.Raw, actual.RawMoments);
        Near(expected.Coverage, actual.MediaCoverage);
        ComposedNear(expected, MediaMomentComposer.Compose(actual.RawMoments, bottom));
    }

    [Fact]
    public void TinyCanvasRegionSurvivesWhenANormalizedFloatRectangleWouldCollapse()
    {
        var placement = MediaDisplayPlacement.Compute(MediaRasterGeometry.Create(32, 18, 1, 1, 0, false), 8, 8, BackgroundFit.Original);
        // These canvas endpoints are already exactly representable in the future SDK's binary32 Rect2.
        // Original crops both axes: [0, tiny] maps near normalized (.375, .2778), where an extra float cast collapses it.
        float tiny = MathF.ScaleB(1f, -30);
        var rect = new MediaCanvasRect(0f, 0f, tiny, tiny);
        Assert.True(placement.TryCanvasToSourceNormalized(rect.Left, rect.Top, out var start));
        Assert.True(placement.TryCanvasToSourceNormalized(rect.Right, rect.Bottom, out var end));
        Assert.True(end.X > start.X && end.Y > start.Y);
        Assert.Equal((float)start.X, (float)end.X);
        Assert.Equal((float)start.Y, (float)end.Y);
        LinearPremulRgba[] pixels = Palette();
        MediaCanvasMomentSample sample = new MediaCanvasMomentQuery(_kernel).Integrate(RawGrid(pixels), placement, rect);
        Assert.Equal(1d, sample.MediaCoverage);
        // Entire query lies inside canonical pixel (12,5); use that pixel directly, not an inverse-map oracle.
        MomentsNear(Moments(pixels[5 * 32 + 12]), sample.RawMoments);
    }

    [Fact]
    public void PublicSourceKeepsItsFloatProfileAndExactlyPromotesIntoTheCommonKernel()
    {
        LinearPremulRgba[] pixels = Palette();
        MediaMomentValues[] grid = RawGrid(pixels);
        foreach (MediaSourceRect rect in new[] { new MediaSourceRect(0, 0, 1, 1), new(.125f, .25f, .75f, .875f),
            new(.213f, .171f, .713f, .819f), new(.3f, .4f, MathF.BitIncrement(.3f), MathF.BitIncrement(.4f)) })
        {
            MediaMomentValues source = _kernel.IntegrateSourceRect(grid, rect);
            MediaMomentValues common = _kernel.IntegrateNormalizedRect(grid, new(rect.Left, rect.Top, rect.Right, rect.Bottom));
            Assert.Equal(source, common);
            OracleResult expected = PixelOracle(pixels, 0, 0, 32, 18,
                new((double)rect.Left * 32, (double)rect.Top * 18, (double)rect.Right * 32, (double)rect.Bottom * 18), default);
            MomentsNear(expected.Raw, source);
        }
        foreach (MediaSourceRect rect in new[] { new MediaSourceRect(-.01f, 0, 1, 1), new(0, 0, 1.01f, 1),
            new(0, 0, 0, 1), new(float.NaN, 0, 1, 1) })
            AssertInvalid(() => _kernel.IntegrateSourceRect(grid, rect));
    }

    [Fact]
    public void QuantizedCanonicalGridKeepsItsOwnU16ErrorSeparateFromCanvasAreaSemantics()
    {
        LinearPremulRgba[] pixels = Palette();
        float[] rgba = pixels.SelectMany(p => new[] { (float)p.R, (float)p.G, (float)p.B, (float)p.A }).ToArray();
        var quantized = new QuantizedMediaMoments[MediaTrackLayoutV1.Cells];
        _kernel.BuildGrid(MediaMomentKernelTests.Frame(32, 18, rgba), quantized, CancellationToken.None);
        MediaMomentValues[] grid = quantized.Select(q => new MediaMomentValues(q.Rp / 65535d, q.Gp / 65535d, q.Bp / 65535d,
            q.Alpha / 65535d, q.YpSquared / 65535d, q.AlphaYp / 65535d, q.AlphaSquared / 65535d)).ToArray();
        var placement = MediaDisplayPlacement.Compute(MediaRasterGeometry.Create(32, 18, 1, 1, 0, false), 320, 240, BackgroundFit.Contain);
        var rect = new MediaCanvasRect(19.5, 2.25, 207.75, 174.5);
        var bottom = new LinearPremulRgba(.125, .25, .375, 1);
        OracleResult expected = PixelOracle(pixels, 0, 30, 320, 180, rect, bottom);
        MediaCanvasMomentSample sample = new MediaCanvasMomentQuery(_kernel).Integrate(grid, placement, rect);
        MomentsNear(expected.Raw, sample.RawMoments, MediaMomentComposer.QuantizationDelta + 1e-12);
        MediaCompositedMoments actual = MediaMomentComposer.Compose(sample.RawMoments, bottom);
        Near(expected.Variance, actual.PremulYVariance, MediaMomentComposer.NegativeVarianceTolerance);
    }

    [Fact]
    public void PureLetterboxRetainsAllBottomAreaAndStillChecksTheWholeGrid()
    {
        var placement = MediaDisplayPlacement.Compute(MediaRasterGeometry.Create(32, 18, 1, 1, 0, false), 320, 240, BackgroundFit.Contain);
        MediaMomentValues[] grid = RawGrid(Palette());
        var rect = new MediaCanvasRect(0, 0, 320, 20);
        MediaCanvasMomentSample sample = new MediaCanvasMomentQuery(_kernel).Integrate(grid, placement, rect);
        Assert.Equal(default, sample.RawMoments);
        Assert.Equal(0d, sample.MediaCoverage);
        var bottom = new LinearPremulRgba(.25, .125, 0, .5);
        MediaCompositedMoments composed = MediaMomentComposer.Compose(sample.RawMoments, bottom);
        Assert.Equal(bottom, composed.MeanLinearPremulRgba);
        Assert.Equal(0, composed.PremulYVariance);
        Assert.False(composed.IsOpaque);
        grid[^1] = new(double.NaN, 0, 0, 0, 0, 0, 0);
        AssertInvalid(() => new MediaCanvasMomentQuery(_kernel).Integrate(grid, placement, rect));
    }

    [Fact]
    public void IllegalCanvasRectanglesAndCancellationAreRejectedWithoutClipping()
    {
        var placement = MediaDisplayPlacement.Compute(MediaRasterGeometry.Create(32, 18, 1, 1, 0, false), 320, 240, BackgroundFit.Contain);
        var query = new MediaCanvasMomentQuery(_kernel);
        MediaMomentValues[] grid = RawGrid(Palette());
        foreach (MediaCanvasRect rect in new[] { new MediaCanvasRect(-.001, 0, 10, 10), new(0, 0, 320.001, 240),
            new(1, 1, 1, 2), new(1, 2, 2, 1), new(double.NaN, 0, 10, 10), new(0, 0, double.PositiveInfinity, 10) })
            AssertInvalid(() => query.Integrate(grid, placement, rect));
        AssertInvalid(() => query.Integrate(grid.AsSpan(0, grid.Length - 1), placement, new(0, 0, 10, 10)));
        var token = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => query.Integrate(grid, placement, new(0, 0, 10, 10), token));
    }

    [Fact]
    public void SpatialOrTemporalSevenMomentAverageMustPrecedeVariance()
    {
        // Equally weighted opaque black/white frames have zero individual variance and nonzero pooled variance.
        var black = new LinearPremulRgba(0, 0, 0, 1);
        var white = new LinearPremulRgba(1, 1, 1, 1);
        var bottom = new LinearPremulRgba(0, 0, 0, 1);
        Assert.Equal(0, MediaMomentComposer.Compose(Moments(black), bottom).PremulYVariance);
        Assert.Equal(0, MediaMomentComposer.Compose(Moments(white), bottom).PremulYVariance);
        var pooled = new MediaMomentValues(.5, .5, .5, 1, .5, .5, 1);
        MediaCompositedMoments mixed = MediaMomentComposer.Compose(pooled, bottom);
        MediaCompositedMoments gray = MediaMomentComposer.Compose(Moments(new(.5, .5, .5, 1)), bottom);
        Assert.Equal(mixed.MeanLinearPremulRgba, gray.MeanLinearPremulRgba);
        Near(.25, mixed.PremulYVariance);
        Assert.Equal(0, gray.PremulYVariance);
    }

    [Fact]
    public void AlphaCorrelationTermsMatchDirectPerPixelComposition()
    {
        var bottom = new LinearPremulRgba(.125, .25, .375, 1);
        // Transparent black and opaque white: E(aY)=E(a²)=.5, not .25.
        var mixed = new MediaMomentValues(.5, .5, .5, .5, .5, .5, .5);
        MediaCompositedMoments actual = MediaMomentComposer.Compose(mixed, bottom);
        double bottomY = .2126 * bottom.R + .7152 * bottom.G + .0722 * bottom.B;
        double expectedMean = (bottomY + 1) / 2;
        Near(expectedMean, actual.MeanPremulY);
        Near((bottomY * bottomY + 1) / 2, actual.MeanSquaredPremulY);
        Near((1 - bottomY) * (1 - bottomY) / 4, actual.PremulYVariance);
        Assert.True(actual.IsOpaque);
    }

    [Fact]
    public void OnlyTheDeclaredNegativeVarianceIntervalIsNormalizedToZero()
    {
        var transparentBottom = new LinearPremulRgba(0, 0, 0, 0);
        double tolerance = MediaMomentComposer.NegativeVarianceTolerance;
        var within = new MediaMomentValues(.5, .5, .5, 1, .25 - tolerance * .999, .5, 1);
        Assert.Equal(0, MediaMomentComposer.Compose(within, transparentBottom).PremulYVariance);
        var beyond = new MediaMomentValues(.5, .5, .5, 1, .25 - tolerance * 1.001, .5, 1);
        AssertInvalid(() => MediaMomentComposer.Compose(beyond, transparentBottom));
        AssertInvalid(() => MediaMomentComposer.Compose(new(-0d, 0, 0, 0, 0, 0, 0), transparentBottom));
        AssertInvalid(() => MediaMomentComposer.Compose(default, new(1, 0, 0, .5)));
    }

    [Fact]
    public void WhiteAndAlphaMixturesRemainValidWithoutAnyAdditionalClamp()
    {
        var white = new LinearPremulRgba(1, 1, 1, 1);
        foreach (double alpha in new[] { 0d, MediaMomentComposer.QuantizationDelta, 1d / 65535, .1, .5, .9, 1 })
        {
            MediaMomentValues raw = Moments(new(alpha, alpha, alpha, alpha));
            foreach (MediaMomentValues input in new[] { raw, QuantizeAndRestore(raw) })
            {
                MediaCompositedMoments actual = MediaMomentComposer.Compose(input, white);
                Assert.Equal(white, actual.MeanLinearPremulRgba);
                Assert.True(actual.IsOpaque);
                Near(1, actual.MeanPremulY);
                Assert.InRange(actual.PremulYVariance, 0, MediaMomentComposer.NegativeVarianceTolerance);
            }
        }
        // Raw moment mixture of transparent black and opaque white still becomes uniform white over white.
        var mixed = new MediaMomentValues(.5, .5, .5, .5, .5, .5, .5);
        Assert.Equal(white, MediaMomentComposer.Compose(mixed, white).MeanLinearPremulRgba);
        Assert.Equal(0, MediaMomentComposer.Compose(mixed, white).PremulYVariance);

        static MediaMomentValues QuantizeAndRestore(MediaMomentValues p)
        {
            static double Q(double value) => MediaMomentKernel.QuantizeUnit(value) / 65535d;
            return new(Q(p.Rp), Q(p.Gp), Q(p.Bp), Q(p.Alpha), Q(p.YpSquared), Q(p.AlphaYp), Q(p.AlphaSquared));
        }
    }

    private static LinearPremulRgba[] Palette()
    {
        LinearPremulRgba[] colors = [new(0, 0, 0, 0), new(1, 1, 1, 1), new(.5, 0, 0, .5),
            new(.25, .125, .5, .75), new(0, .5, .25, .5), new(.125, .25, .375, .5)];
        return Enumerable.Range(0, 32 * 18).Select(i => colors[((i % 32) * 3 + (i / 32) * 5) % colors.Length]).ToArray();
    }

    private static MediaMomentValues[] RawGrid(LinearPremulRgba[] pixels) => pixels.Select(Moments).ToArray();

    private static MediaMomentValues Moments(LinearPremulRgba p)
    {
        double y = .2126 * p.R + .7152 * p.G + .0722 * p.B;
        return new(p.R, p.G, p.B, p.A, y * y, p.A * y, p.A * p.A);
    }

    private static OracleResult PixelOracle(LinearPremulRgba[] pixels, double fullX, double fullY, double fullWidth, double fullHeight,
        MediaCanvasRect query, LinearPremulRgba bottom)
    {
        double[] raw = new double[7], mean = new double[4];
        double coverage = 0, meanY = 0, secondY = 0;
        for (int y = 0; y < 18; y++) for (int x = 0; x < 32; x++)
        {
            // Direct canonical-pixel rectangles in canvas; no production inverse mapping or grid integration.
            double left = fullX + fullWidth * x / 32, right = fullX + fullWidth * (x + 1) / 32;
            double top = fullY + fullHeight * y / 18, bottomEdge = fullY + fullHeight * (y + 1) / 18;
            double w = Math.Max(0, Math.Min(query.Right, right) - Math.Max(query.Left, left));
            double h = Math.Max(0, Math.Min(query.Bottom, bottomEdge) - Math.Max(query.Top, top));
            double weight = w / (query.Right - query.Left) * (h / (query.Bottom - query.Top));
            if (weight == 0) continue;
            coverage += weight;
            LinearPremulRgba p = pixels[y * 32 + x];
            MediaMomentValues m = Moments(p);
            double[] values = [m.Rp, m.Gp, m.Bp, m.Alpha, m.YpSquared, m.AlphaYp, m.AlphaSquared];
            for (int i = 0; i < 7; i++) raw[i] += weight * values[i];
            AddComposed(p, weight);
        }
        AddComposed(default, 1 - coverage);
        return new(new(raw[0], raw[1], raw[2], raw[3], raw[4], raw[5], raw[6]), coverage,
            new(mean[0], mean[1], mean[2], mean[3]), meanY, secondY, secondY - meanY * meanY);

        void AddComposed(LinearPremulRgba p, double weight)
        {
            double r = p.R + bottom.R * (1 - p.A), g = p.G + bottom.G * (1 - p.A), b = p.B + bottom.B * (1 - p.A);
            double a = p.A + bottom.A * (1 - p.A);
            mean[0] += weight * r; mean[1] += weight * g; mean[2] += weight * b; mean[3] += weight * a;
            double luminance = .2126 * r + .7152 * g + .0722 * b;
            meanY += weight * luminance;
            secondY += weight * luminance * luminance;
        }
    }

    private static void MomentsNear(MediaMomentValues expected, MediaMomentValues actual, double tolerance = 1e-12)
    {
        double[] a = [expected.Rp, expected.Gp, expected.Bp, expected.Alpha, expected.YpSquared, expected.AlphaYp, expected.AlphaSquared];
        double[] b = [actual.Rp, actual.Gp, actual.Bp, actual.Alpha, actual.YpSquared, actual.AlphaYp, actual.AlphaSquared];
        for (int i = 0; i < 7; i++) Near(a[i], b[i], tolerance);
    }

    private static void ComposedNear(OracleResult expected, MediaCompositedMoments actual)
    {
        Near(expected.Mean.R, actual.MeanLinearPremulRgba.R); Near(expected.Mean.G, actual.MeanLinearPremulRgba.G);
        Near(expected.Mean.B, actual.MeanLinearPremulRgba.B); Near(expected.Mean.A, actual.MeanLinearPremulRgba.A);
        Near(expected.MeanY, actual.MeanPremulY); Near(expected.SecondY, actual.MeanSquaredPremulY);
        Near(expected.Variance, actual.PremulYVariance);
        if (actual.IsOpaque) Near(1, expected.Mean.A);
    }

    private static void Near(double expected, double actual, double tolerance = 1e-12) => Assert.InRange(Math.Abs(expected - actual), 0, tolerance);
    private static void AssertInvalid(Action action) => Assert.Equal(MediaAbFailure.StatisticsInvalid, Assert.Throws<MediaAbException>(action).Failure);
    private sealed record OracleResult(MediaMomentValues Raw, double Coverage, LinearPremulRgba Mean, double MeanY, double SecondY, double Variance);
}
