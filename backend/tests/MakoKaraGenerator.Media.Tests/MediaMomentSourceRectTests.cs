using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Source-rectangle area integrals over piecewise-constant seven-moment cells.</summary>
public sealed class MediaMomentSourceRectTests
{
    private readonly MediaMomentKernel _kernel = new();

    /// <summary>Fractional edges integrate the same seven raw moments as an independent rational box oracle.</summary>
    [Fact]
    public void FractionalRectangleMatchesExactBinary32RationalOracle()
    {
        MediaMomentValues[] grid = PatternGrid();
        MediaSourceRect[] rectangles = [new(0, 0, 1, 1), new(0.1234567f, 0.043219f, 0.7987654f, 0.9123456f),
            new(0.125f, 0.25f, 0.375f, 0.75f), new(0.9f, 0.9f, 1, 1)];
        foreach (MediaSourceRect rect in rectangles)
        {
            AssertClose(MediaMomentOracle.Integrate(grid, rect), _kernel.IntegrateSourceRect(grid, rect));
        }
    }

    /// <summary>A rectangle wholly inside one cell returns that cell even at small finite binary32 widths.</summary>
    [Fact]
    public void InCellAndTinyRectanglesDoNotSampleNeighbours()
    {
        MediaMomentValues[] grid = PatternGrid();
        MediaMomentValues expected = grid[3 * MediaTrackLayoutV1.Columns + 5];
        var rect = new MediaSourceRect(0.16f, 0.17f, 0.17f, 0.2f);
        AssertClose(expected, _kernel.IntegrateSourceRect(grid, rect));
        AssertClose(grid[0], _kernel.IntegrateSourceRect(grid, new(0, 0, float.Epsilon, float.Epsilon)));
    }

    /// <summary>Whole-source average and an exact vertical half source preserve row-major orientation.</summary>
    [Fact]
    public void WholeAndHalfSourceHaveIndependentExpectedMeans()
    {
        MediaMomentValues[] grid = new MediaMomentValues[MediaTrackLayoutV1.Cells];
        for (int y = 0; y < MediaTrackLayoutV1.Rows; y++)
        {
            for (int x = 0; x < MediaTrackLayoutV1.Columns; x++)
            {
                double value = x < MediaTrackLayoutV1.Columns / 2 ? 0.25 : 0.75;
                grid[y * MediaTrackLayoutV1.Columns + x] = All(value);
            }
        }

        AssertClose(All(0.5), _kernel.IntegrateSourceRect(grid, new(0, 0, 1, 1)));
        AssertClose(All(0.25), _kernel.IntegrateSourceRect(grid, new(0, 0, 0.5f, 1)));
        AssertClose(All(0.75), _kernel.IntegrateSourceRect(grid, new(0.5f, 0, 1, 1)));
    }

    /// <summary>Constant endpoint grids stay exact at a rectangle known to cause raw sum divided by area overshoot.</summary>
    [Fact]
    public void EndpointConstantsDoNotNeedClampingAtRoundedRectangleArea()
    {
        var rect = new MediaSourceRect(0.5108954906463623f, 3.1529350508208154e-6f,
            0.5257570147514343f, 0.6185364723205566f);
        foreach (double value in new[] { 0d, 1d })
        {
            MediaMomentValues[] grid = Enumerable.Repeat(All(value), MediaTrackLayoutV1.Cells).ToArray();
            Assert.Equal(All(value), _kernel.IntegrateSourceRect(grid, rect));
        }
    }

    /// <summary>Rectangle validation refuses nonfinite, out-of-range and empty inputs.</summary>
    [Fact]
    public void InvalidRectanglesAreRefused()
    {
        MediaMomentValues[] grid = new MediaMomentValues[MediaTrackLayoutV1.Cells];
        MediaSourceRect[] invalid = [new(float.NaN, 0, 1, 1), new(0, float.PositiveInfinity, 1, 1),
            new(-0.01f, 0, 1, 1), new(0, 0, 1.01f, 1), new(0, 0.5f, 1, 0.5f),
            new(0.7f, 0, 0.2f, 1), new(0, 0.8f, 1, 0.2f)];
        foreach (MediaSourceRect rect in invalid)
        {
            Assert.Equal(MediaAbFailure.StatisticsInvalid, Assert.Throws<MediaAbException>(() => _kernel.IntegrateSourceRect(grid, rect)).Failure);
        }
    }

    /// <summary>Every input component is checked, including cells outside the chosen rectangle.</summary>
    [Fact]
    public void InvalidGridMomentCannotHideOutsideTheRectangle()
    {
        double[] invalid = [double.NaN, double.PositiveInfinity, -0.1, 1.1, -0.0];
        foreach (double value in invalid)
        {
            MediaMomentValues[] grid = new MediaMomentValues[MediaTrackLayoutV1.Cells];
            grid[^1] = new(0, 0, 0, 0, value, 0, 0);
            Assert.Throws<MediaAbException>(() => _kernel.IntegrateSourceRect(grid, new(0, 0, 0.01f, 0.01f)));
        }
    }

    private static MediaMomentValues[] PatternGrid()
    {
        var grid = new MediaMomentValues[MediaTrackLayoutV1.Cells];
        for (int y = 0; y < MediaTrackLayoutV1.Rows; y++)
        {
            for (int x = 0; x < MediaTrackLayoutV1.Columns; x++)
            {
                // Independent raw values; no relationship stronger than each unit component is imposed
                // because temporal and u16 quantization can disturb exact per-pixel equalities.
                double value = ((x * 7 + y * 3) % 31) / 32d;
                grid[y * MediaTrackLayoutV1.Columns + x] = new(value, value / 2, 1 - value, 1,
                    value * value, 0.25 + value / 2, 0.75);
            }
        }

        return grid;
    }

    private static MediaMomentValues All(double value) => new(value, value, value, value, value, value, value);

    private static void AssertClose(MediaMomentValues expected, MediaMomentValues actual)
    {
        double[] expectedValues = MediaMomentOracle.Components(expected);
        double[] actualValues = MediaMomentOracle.Components(actual);
        for (int index = 0; index < expectedValues.Length; index++)
        {
            Assert.InRange(Math.Abs(expectedValues[index] - actualValues[index]), 0, 2e-14);
        }
    }
}
