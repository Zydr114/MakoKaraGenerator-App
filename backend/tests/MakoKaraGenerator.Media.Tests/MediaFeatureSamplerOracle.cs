using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

// Independent per-cell quantization, integer-us temporal enumeration, direct rectangle overlap and composition.
// Does not call the production kernel, source/canvas integrators, placement, temporal reader or composer.
internal static class MediaFeatureSamplerOracle
{
    internal sealed record Values(double[] Raw, LinearPremulRgba Mean, double MeanY, double SecondY, double Variance);

    internal static double[][] Grid(float[][] frames, long timeUs, long windowUs, BackgroundPlaybackMode playback, long frameUs = 2_000)
    {
        long[] weights = new long[frames.Length];
        if (windowUs == 0) weights[Frame(timeUs)]++;
        else for (long at = timeUs - windowUs; at < timeUs; at++) weights[Frame(at)]++;
        double divisor = windowUs == 0 ? 1 : windowUs;
        var grid = new double[576][];
        for (int cell = 0; cell < grid.Length; cell++)
        {
            grid[cell] = new double[7];
            for (int frame = 0; frame < frames.Length; frame++)
            {
                double[] q = Quantize(frames[frame][cell * 4], frames[frame][cell * 4 + 1], frames[frame][cell * 4 + 2], frames[frame][cell * 4 + 3]);
                for (int component = 0; component < 7; component++) grid[cell][component] += q[component] * weights[frame] / divisor;
            }
        }
        return grid;
        int Frame(long at)
        {
            if (at < 0) return 0;
            long duration = frameUs * frames.Length;
            long sourceTime = playback == BackgroundPlaybackMode.Loop ? at % duration : Math.Min(at, duration - 1);
            return (int)(sourceTime / frameUs);
        }
    }

    internal static double[] Quantize(double r, double g, double b, double a)
    {
        double y = .2126d * r + .7152d * g + .0722d * b;
        double[] raw = [r, g, b, a, y * y, a * y, a * a];
        return raw.Select(value => Math.Floor(value * 65535d + .5d) / 65535d).ToArray();
    }

    internal static Values Rect(double[][] grid, MediaFeatureQueryRect query, double fullX, double fullY,
        double fullWidth, double fullHeight, LinearPremulRgba bottom)
    {
        double[] raw = new double[7];
        for (int y = 0; y < 18; y++) for (int x = 0; x < 32; x++)
        {
            double left = fullX + fullWidth * x / 32, right = fullX + fullWidth * (x + 1) / 32;
            double top = fullY + fullHeight * y / 18, end = fullY + fullHeight * (y + 1) / 18;
            double weight = Math.Max(0, Math.Min(query.Right, right) - Math.Max(query.Left, left)) / (query.Right - query.Left)
                * (Math.Max(0, Math.Min(query.Bottom, end) - Math.Max(query.Top, top)) / (query.Bottom - query.Top));
            for (int component = 0; component < 7; component++) raw[component] += grid[y * 32 + x][component] * weight;
        }
        return Compose(raw, bottom);
    }

    internal static Values Compose(double[] raw, LinearPremulRgba bottom)
    {
        double y = .2126d * raw[0] + .7152d * raw[1] + .0722d * raw[2];
        double by = .2126d * bottom.R + .7152d * bottom.G + .0722d * bottom.B;
        var mean = new LinearPremulRgba(raw[0] + bottom.R * (1 - raw[3]), raw[1] + bottom.G * (1 - raw[3]),
            raw[2] + bottom.B * (1 - raw[3]), raw[3] + bottom.A * (1 - raw[3]));
        double meanY = y + by * (1 - raw[3]);
        double second = raw[4] + 2 * by * (y - raw[5]) + by * by * (1 - 2 * raw[3] + raw[6]);
        return new(raw, mean, meanY, second, Math.Max(0, second - meanY * meanY));
    }

    internal static void Source(Values expected, ProjectMediaFeatureSample actual)
    {
        Assert.Null(actual.CanvasValues); ProjectSourceMediaFeatureValues value = Assert.IsType<ProjectSourceMediaFeatureValues>(actual.SourceValues);
        Mean(expected.Mean, value.Mean); Near(expected.MeanY, value.MeanPremulY); Near(expected.SecondY, value.MeanSquaredPremulY);
        Near(expected.Raw[5], value.MeanAlphaTimesPremulY); Near(expected.Raw[6], value.MeanSquaredAlpha); Near(expected.Variance, value.PremulYVariance);
    }

    internal static void Canvas(Values expected, ProjectMediaFeatureSample actual)
    {
        Assert.Null(actual.SourceValues); ProjectCanvasMediaFeatureValues value = Assert.IsType<ProjectCanvasMediaFeatureValues>(actual.CanvasValues);
        Mean(expected.Mean, value.Mean); Near(expected.MeanY, value.MeanPremulY); Near(expected.SecondY, value.MeanSquaredPremulY); Near(expected.Variance, value.PremulYVariance);
        if (value.IsOpaque) Near(1, expected.Mean.A);
    }

    internal static void Mean(LinearPremulRgba expected, LinearPremulRgba actual)
    { Near(expected.R, actual.R); Near(expected.G, actual.G); Near(expected.B, actual.B); Near(expected.A, actual.A); }
    internal static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 3e-12);
}
