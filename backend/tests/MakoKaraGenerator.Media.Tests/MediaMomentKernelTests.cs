using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Independent rational geometry and per-pixel second-moment regressions.</summary>
public sealed class MediaMomentKernelTests
{
    private readonly MediaMomentKernel _kernel = new();

    /// <summary>Different raster sizes cover enlargement, nonintegral boundaries, and many pixels per cell.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(17, 13)]
    [InlineData(37, 23)]
    public void EveryCellMatchesIndependentExactRationalOracle(int width, int height)
    {
        float[] pixels = Palette(width, height);
        QuantizedMediaMoments[] actual = Grid();
        _kernel.BuildGrid(Frame(width, height, pixels), actual, CancellationToken.None);
        Assert.Equal(MediaMomentOracle.Build(pixels, width, height), actual);
    }

    /// <summary>One source pixel contributes its exact area to all 576 larger grid cells.</summary>
    [Fact]
    public void OnePixelEqualsSolidForExactlyRepresentableColor()
    {
        float[] pixel = [0.25f, 0.125f, 0, 0.5f];
        QuantizedMediaMoments[] actual = Grid();
        QuantizedMediaMoments[] solid = Grid();
        _kernel.BuildGrid(Frame(1, 1, pixel), actual, CancellationToken.None);
        _kernel.BuildSolidGrid(new(0.25, 0.125, 0, 0.5), solid);
        Assert.Equal(solid, actual);
        Assert.All(actual, value => Assert.Equal(actual[0], value));
    }

    /// <summary>Equal means do not imply equal second moments or variance.</summary>
    [Fact]
    public void BlackWhiteCheckerAndUniformGrayRetainDifferentSecondMoments()
    {
        const int width = 64;
        const int height = 36;
        float[] pixels = new float[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = 4 * (y * width + x);
                float gray = (x + y) % 2;
                pixels[offset] = gray;
                pixels[offset + 1] = gray;
                pixels[offset + 2] = gray;
                pixels[offset + 3] = 1;
            }
        }

        QuantizedMediaMoments[] checker = Grid();
        QuantizedMediaMoments[] grayGrid = Grid();
        _kernel.BuildGrid(Frame(width, height, pixels), checker, CancellationToken.None);
        _kernel.BuildSolidGrid(new(0.5, 0.5, 0.5, 1), grayGrid);
        Assert.All(checker, value =>
        {
            Assert.Equal((ushort)32768, value.Rp);
            Assert.Equal((ushort)32768, value.YpSquared);
            Assert.Equal((ushort)32768, value.AlphaYp);
            Assert.Equal(ushort.MaxValue, value.AlphaSquared);
        });
        Assert.Equal(checker[0].Rp, grayGrid[0].Rp);
        Assert.Equal(checker[0].Gp, grayGrid[0].Gp);
        Assert.Equal(checker[0].Bp, grayGrid[0].Bp);
        Assert.Equal(checker[0].AlphaYp, grayGrid[0].AlphaYp);
        Assert.Equal((ushort)16384, grayGrid[0].YpSquared);
        Assert.True(checker[0].YpSquared > grayGrid[0].YpSquared);
    }

    /// <summary>Alpha correlation and alpha-square must be accumulated before averaging as well.</summary>
    [Fact]
    public void TransparentOpaqueMixtureDiffersFromUniformHalfAlpha()
    {
        float[] pixels = new float[64 * 18 * 4];
        for (int y = 0; y < 18; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                float value = x % 2;
                int offset = (y * 64 + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    pixels[offset + channel] = value;
                }
            }
        }

        QuantizedMediaMoments[] mixed = Grid();
        QuantizedMediaMoments[] uniform = Grid();
        _kernel.BuildGrid(Frame(64, 18, pixels), mixed, CancellationToken.None);
        _kernel.BuildSolidGrid(new(0.5, 0.5, 0.5, 0.5), uniform);
        Assert.Equal(mixed[0].Alpha, uniform[0].Alpha);
        Assert.Equal((ushort)32768, mixed[0].AlphaYp);
        Assert.Equal((ushort)32768, mixed[0].AlphaSquared);
        Assert.Equal((ushort)16384, uniform[0].AlphaYp);
        Assert.Equal((ushort)16384, uniform[0].AlphaSquared);
    }

    /// <summary>Fully transparent canonical black remains exactly zero in every raw moment.</summary>
    [Fact]
    public void TransparentBlackHasNoHiddenRgbOrSecondMoments()
    {
        QuantizedMediaMoments[] grid = Grid();
        _kernel.BuildSolidGrid(new(0, 0, 0, 0), grid);
        Assert.All(grid, value => Assert.Equal(default, value));
        _kernel.BuildGrid(Frame(1, 1, [0, 0, 0, 0]), grid, CancellationToken.None);
        Assert.All(grid, value => Assert.Equal(default, value));
    }

    /// <summary>The quantizer uses half-up rather than ties-to-even and never clamps illegal values.</summary>
    [Fact]
    public void QuantizerPreservesEndpointsAndHalfUpTies()
    {
        Assert.Equal((ushort)0, MediaMomentKernel.QuantizeUnit(0));
        Assert.Equal(ushort.MaxValue, MediaMomentKernel.QuantizeUnit(1));
        Assert.Equal((ushort)32768, MediaMomentKernel.QuantizeUnit(0.5));
        Assert.Equal((ushort)1, MediaMomentKernel.QuantizeUnit(0.5 / 65535));
        Assert.Equal((ushort)1, MediaMomentKernel.QuantizeUnit(Math.BitDecrement(1.5 / 65535)));
        Assert.Equal((ushort)2, MediaMomentKernel.QuantizeUnit(1.5 / 65535));
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.001, 1.001, -0.0 })
        {
            Assert.Equal(MediaAbFailure.StatisticsInvalid, Assert.Throws<MediaAbException>(() => MediaMomentKernel.QuantizeUnit(invalid)).Failure);
        }
    }

    internal static QuantizedMediaMoments[] Grid() => new QuantizedMediaMoments[MediaTrackLayoutV1.Cells];

    internal static PresentationFrameBorrow Frame(int width, int height, float[] pixels) =>
        FrameMemory(width, height, pixels);

    internal static PresentationFrameBorrow FrameMemory(int width, int height, ReadOnlyMemory<float> pixels) =>
        new(new MediaSourceKey(new MediaDigest(new string('0', 64))), new MediaDigest(new string('1', 64)),
            new(new(0), null, width, height, new(new string('2', 64))), pixels);

    private static float[] Palette(int width, int height)
    {
        float[][] colors = [[0, 0, 0, 1], [1, 1, 1, 1], [0, 0, 0, 0], [0.5f, 0, 0, 0.5f],
            [0.25f, 0.125f, 0.5f, 0.75f], [0, 0.5f, 0.25f, 0.5f], [0.125f, 0.25f, 0.375f, 0.5f]];
        float[] pixels = new float[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                colors[(x * 3 + y * 5) % colors.Length].CopyTo(pixels, 4 * (y * width + x));
            }
        }

        return pixels;
    }
}
