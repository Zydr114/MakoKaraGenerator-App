using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Core.Tests.Media;

/// <summary>Exact arithmetic regressions with expectations independent of floating-point timestamps.</summary>
public sealed class MediaPresentationTimeMathTests
{
    private readonly MediaPresentationTimeMath _math = new();

    /// <summary>Signed ceiling retains the exact mathematical meaning on both sides of zero.</summary>
    [Theory]
    [InlineData(1, 0, 333334)]
    [InlineData(-1, 0, -333333)]
    [InlineData(-9, -10, 333334)]
    [InlineData(10, 10, 0)]
    [InlineData(0, 1, -333333)]
    public void ThirdSecondBoundariesUseSignedCeiling(long boundary, long first, long expected)
    {
        var timeBase = new RationalTimeBase(1, 3);
        Assert.Equal(expected, _math.NormalizeBoundaryUs(new(boundary, timeBase), new(first, timeBase)));
    }

    /// <summary>Adjacent Int64 ticks remain distinguishable when a binary64 representation collapses them.</summary>
    [Fact]
    public void ExactComparisonPreservesAdjacentHugeTicks()
    {
        long earlier = long.MaxValue - 1;
        var timeBase = new RationalTimeBase(1, int.MaxValue);
        Assert.Equal((double)long.MaxValue, (double)earlier);
        Assert.True(_math.Compare(new(earlier, timeBase), new(long.MaxValue, timeBase)) < 0);
        Assert.True(_math.Compare(new(long.MinValue, timeBase), new(long.MinValue + 1, timeBase)) < 0);
    }

    /// <summary>Different source time bases can describe exactly the same instant.</summary>
    [Fact]
    public void DifferentBasesCompareExactly()
    {
        Assert.Equal(0, _math.Compare(new(-90000, new(1, 90000)), new(-1000, new(1, 1000))));
        Assert.Equal(333334, _math.NormalizeBoundaryUs(new(-2, new(1, 3)), new(-1000, new(1, 1000))));
    }

    /// <summary>Trusted construction reduces while canonical wire construction refuses repair.</summary>
    [Fact]
    public void CanonicalReaderRejectsUnreducedOrDefaultTimeBase()
    {
        Assert.Equal(new RationalTimeBase(1, 3), new RationalTimeBase(2, 6));
        AssertFailure(() => RationalTimeBase.FromCanonical(2, 6));
        AssertFailure(() => _ = new RationalTimeBase(0, 1));
        AssertFailure(() => _ = new RationalTimeBase(1, -1));
        AssertFailure(() => _math.Compare(new(1, default), new(0, new(1, 1))));
    }

    /// <summary>The valid maximal operand domain uses exact wide intermediates and checked final conversion.</summary>
    [Fact]
    public void ExtremeCrossProductsRemainExactAndFinalOverflowIsNamed()
    {
        Assert.True(_math.Compare(new(long.MaxValue, new(int.MaxValue, int.MaxValue - 1)),
            new(long.MinValue, new(int.MaxValue - 1, int.MaxValue))) > 0);
        Assert.Equal(0, _math.NormalizeBoundaryUs(new(long.MinValue, new(int.MaxValue, 1)),
            new(long.MinValue, new(int.MaxValue, 1))));
        AssertFailure(() => _math.NormalizeBoundaryUs(new(long.MaxValue, new(int.MaxValue, 1)),
            new(long.MinValue, new(int.MaxValue, 1))));
    }

    private static void AssertFailure(Action action) =>
        Assert.Equal(MediaAbFailure.PresentationInvalid, Assert.Throws<MediaAbException>(action).Failure);
}
