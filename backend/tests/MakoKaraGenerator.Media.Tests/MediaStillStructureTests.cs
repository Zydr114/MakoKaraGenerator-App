using MakoKaraGenerator.Core.Media;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaStillStructureTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Png_AcceptsTaggedSingleImageAtSupportedPrecision(int bits)
    {
        using var source = new MemoryStream(Png(bits: bits)); var budget = new Budget(); MediaStillStructure value = MediaStillStructure.Read(source, 3, budget, default);
        Assert.Equal(bits, value.Bits); Assert.True(value.HasColorAuthority); Assert.True(value.HasAlpha); Assert.Equal(0, budget.Live);
    }
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Png_RefusesIccAnimationOrContradictoryGamma(bool icc, bool animated, bool gamma)
    {
        using var source = new MemoryStream(Png(icc: icc, animated: animated, conflictingGamma: gamma)); var budget = new Budget();
        Assert.Throws<MediaAbException>(() => MediaStillStructure.Read(source, 3, budget, default)); Assert.Equal(0, budget.Live);
    }
    [Theory]
    [InlineData(false, 0x22, 0x11, 420)]
    [InlineData(true, 0x21, 0x11, 422)]
    [InlineData(true, 0x11, 0x11, 444)]
    [InlineData(false, 0x12, 0x12, 444)]
    [InlineData(false, 0x22, 0x22, 444)]
    [InlineData(false, 0x42, 0x21, 420)]
    public void Jpeg_AcceptsJfifBaselineAndProgressiveNativeSampling(bool progressive, int sampling, int chroma, int expected)
    {
        using var source = new MemoryStream(Jpeg(progressive: progressive, sampling: (byte)sampling, chroma: (byte)chroma)); var budget = new Budget();
        MediaStillStructure value = MediaStillStructure.Read(source, 4, budget, default);
        Assert.True(value.HasColorAuthority); Assert.Equal(expected, value.Subsampling); Assert.Equal(MediaMatrix.Bt601Jpeg, value.Matrix); Assert.False(value.HasAlpha); Assert.Equal(0, budget.Live);
    }
    [Theory]
    [InlineData(0x41, 0x11)]
    [InlineData(0x32, 0x21)]
    [InlineData(0x11, 0x22)]
    [InlineData(0x22, 0x01)]
    public void Jpeg_RefusesUnsupportedSamplingRatios(int sampling, int chroma)
    {
        using var source = new MemoryStream(Jpeg(sampling: (byte)sampling, chroma: (byte)chroma)); var budget = new Budget();
        Assert.Throws<MediaAbException>(() => MediaStillStructure.Read(source, 4, budget, default)); Assert.Equal(0, budget.Live);
    }
    [Theory]
    [InlineData(12, false, false)]
    [InlineData(8, true, false)]
    [InlineData(8, false, true)]
    public void Jpeg_RefusesUnsupportedPrecisionIccAndCmyk(int bits, bool icc, bool cmyk)
    {
        using var source = new MemoryStream(Jpeg(bits: bits, icc: icc, cmyk: cmyk)); var budget = new Budget();
        Assert.Throws<MediaAbException>(() => MediaStillStructure.Read(source, 4, budget, default)); Assert.Equal(0, budget.Live);
    }
    [Fact]
    public void MissingTagsRemainExplicitlyUntrustedAndMultipleImagesFail()
    {
        using var untagged = new MemoryStream(Png(srgb: false)); Assert.False(MediaStillStructure.Read(untagged, 3, new Budget(), default).HasColorAuthority);
        using var multiple = new MemoryStream(Cat(Jpeg(), Jpeg())); Assert.Throws<MediaAbException>(() => MediaStillStructure.Read(multiple, 4, new Budget(), default));
    }
}
