using MakoKaraGenerator.Core.Media;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaPresentationInterpretationTests
{
    [Fact]
    public void PositiveNonSquareSarAndOrthogonalRotationBindIdentityWithoutResampling()
    {
        MediaToolIdentity tool = Tool();
        var matrix = new MediaNativeDisplayMatrix(true, 0, 65536, 0, -65536, 0, 0, 0, 0, 1 << 30);
        MediaFeatureFileSource source = MediaPresentationInterpretation.Create(new(Hash([1]), 1), new(MediaSourceKind.Video, 0, null),
            Frame(720, 576, 16, 15, matrix), null, tool);
        Assert.Equal(576, source.Presentation.CanonicalWidth); Assert.Equal(720, source.Presentation.CanonicalHeight);
        Assert.Equal(16, source.Presentation.SarNumerator); Assert.Equal(15, source.Presentation.SarDenominator);
        Assert.Equal(MediaQuarterTurn.Clockwise90, source.Presentation.Rotation);
        MediaRasterGeometry geometry = MediaRasterGeometry.Create(720, 576, 16, 15, source.Presentation.Rotation, false);
        Assert.Equal(576, geometry.DisplayWidth.ToDouble()); Assert.Equal(768, geometry.DisplayHeight.ToDouble());
        MediaFeatureFileSource square = MediaPresentationInterpretation.Create(source.Content, new(MediaSourceKind.Video, 0, null), Frame(720, 576, 1, 1, matrix), null, tool);
        Assert.NotEqual(square.Key, source.Key);
    }

    [Fact]
    public void ArbitraryAffineCannotMasqueradeAsQuarterTurn()
    {
        var skew = new MediaNativeDisplayMatrix(true, 65536, 1, 0, 0, 65536, 0, 0, 0, 1 << 30);
        Assert.Throws<MediaAbException>(() => MediaPresentationInterpretation.Create(new(Hash([1]), 1), new(MediaSourceKind.Video, 0, null), Frame(1, 1, 1, 1, skew), null, Tool()));
    }

    [Fact]
    public void UntaggedPngRequiresTrustedCompleteAuthorityAndExplicitContradictionCannotBeOverridden()
    {
        using var bytes = new MemoryStream(Png(srgb: false)); MediaStillStructure still = MediaStillStructure.Read(bytes, 3, new Budget(), default);
        var frame = new MediaNativePresentationMetadata(0, 0, 0, (int)bytes.Length, new(0, new(1, 25)), 0, 1, 1,
            MediaNativePixelFormat.Rgba32, 2, 2, 2, 0, 0, 0, 0, 1, default, 1, 4, 4, 0, 0, 0, 0, 0, 0, 3, Hash(bytes.ToArray()));
        Assert.Throws<MediaAbException>(() => MediaPresentationInterpretation.Create(new(Hash(bytes.ToArray()), bytes.Length), new(MediaSourceKind.Still, 0, null), frame, still, Tool()));
        var authority = new TrustedMediaColorInterpretation(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full, MediaAlpha.Straight, MediaChromaLocation.None, Hash([42]));
        MediaFeatureFileSource accepted = MediaPresentationInterpretation.Create(new(Hash(bytes.ToArray()), bytes.Length), new(MediaSourceKind.Still, 0, authority), frame, still, Tool());
        Assert.True(accepted.Presentation.Color.FromTrustedMissingTagOverride); Assert.Equal(authority.AuthorityDigest, accepted.Presentation.Color.AuthorityDigest);
        var contradictory = new TrustedMediaColorInterpretation(MediaTransfer.Bt709, MediaMatrix.Rgb, MediaRange.Full, MediaAlpha.Straight, MediaChromaLocation.None, Hash([42]));
        Assert.Throws<MediaAbException>(() => MediaPresentationInterpretation.Create(accepted.Content, new(MediaSourceKind.Still, 0, contradictory), frame, still, Tool()));
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 4, true)]
    [InlineData(0, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(1, 1, false)]
    public void PngDensityRatioMustMatchPhysicalPixelAspectBeforeRotation(int nativeNumerator, int nativeDenominator, bool valid)
    {
        byte[] png = Png();
        // pHYs says one horizontal and two vertical pixels per unit: pixel width/height is 2:1.
        byte[] bytes = Cat(png[..33], PngChunk("pHYs", Cat(Be(1, 2), [0])), png[33..]);
        using var stream = new MemoryStream(bytes);
        MediaStillStructure still = MediaStillStructure.Read(stream, 3, new Budget(), default);
        Assert.Equal(2, still.SarNumerator); Assert.Equal(1, still.SarDenominator);
        var frame = new MediaNativePresentationMetadata(0, 0, 0, bytes.Length, new(0, new(1, 25)), 0, 1, 1,
            MediaNativePixelFormat.Rgba32, 1, 13, 0, 2, 0, 2, nativeNumerator, nativeDenominator, default,
            1, 4, 4, 0, 0, 0, 0, 0, 0, 3, Hash(bytes));
        MediaFeatureFileSource Create() => MediaPresentationInterpretation.Create(new(Hash(bytes), bytes.Length),
            new(MediaSourceKind.Still, 0, null), frame, still, Tool());
        if (valid)
        {
            MediaFeatureFileSource source = Create();
            Assert.Equal(2, source.Presentation.SarNumerator); Assert.Equal(1, source.Presentation.SarDenominator);
        }
        else Assert.Equal(MediaAbFailure.PresentationMismatch, Assert.Throws<MediaAbException>(Create).Failure);
    }

    [Theory]
    [InlineData(5, true, true)]
    [InlineData(0, true, true)]
    [InlineData(2, true, true)]
    [InlineData(5, false, false)]
    [InlineData(1, true, false)]
    [InlineData(6, true, false)]
    public void StructuralRgbJpegUsesPackedComponentsAndRequiresIndependentColorAuthority(int nativeMatrix, bool hasAuthority, bool valid)
    {
        // Adobe transform 0 plus explicit R/G/B component IDs, without JFIF.
        byte[] bytes = Jpeg(jfif: false, sampling: 0x11);
        bytes[12] = (byte)'R'; bytes[15] = (byte)'G'; bytes[18] = (byte)'B';
        bytes = Cat(bytes[..2], [0xFF, 0xEE, 0, 14], "Adobe"u8.ToArray(), [0, 100, 0, 0, 0, 0, 0], bytes[2..]);
        using var stream = new MemoryStream(bytes);
        MediaStillStructure still = MediaStillStructure.Read(stream, 4, new Budget(), default);
        Assert.Equal(MediaMatrix.Rgb, still.Matrix); Assert.False(still.HasColorAuthority);
        var frame = new MediaNativePresentationMetadata(0, 0, 0, bytes.Length, new(0, new(1, 25)), 0, 1, 1,
            MediaNativePixelFormat.Rgb24, 2, 2, nativeMatrix, 0, 2, 0, 0, 1, default,
            1, 3, 3, 0, 0, 0, 0, 0, 0, 4, Hash(bytes));
        TrustedMediaColorInterpretation? authority = hasAuthority ? new(MediaTransfer.Srgb, MediaMatrix.Rgb,
            MediaRange.Full, MediaAlpha.Opaque, MediaChromaLocation.None, Hash([42])) : null;
        MediaFeatureFileSource Create() => MediaPresentationInterpretation.Create(new(Hash(bytes), bytes.Length),
            new(MediaSourceKind.Still, 0, authority), frame, still, Tool());
        if (valid)
        {
            MediaFeatureFileSource source = Create();
            Assert.Equal(MediaMatrix.Rgb, source.Presentation.Color.Matrix);
            Assert.True(source.Presentation.Color.FromTrustedMissingTagOverride);
            Assert.Equal(authority!.AuthorityDigest, source.Presentation.Color.AuthorityDigest);
        }
        else Assert.Equal(MediaAbFailure.ColorUnsupported, Assert.Throws<MediaAbException>(Create).Failure);
    }

    private static MediaNativePresentationMetadata Frame(int width, int height, int sarN, int sarD, MediaNativeDisplayMatrix matrix) =>
        new(0, 0, 0, 1, new(0, new(1, 1000)), 1, width, height, MediaNativePixelFormat.Rgb24, 1, 13, 0, 2, 0, 0, sarN, sarD, matrix,
            1, width * 3, width * height * 3, 0, 0, 0, 0, 7, 0, 1, Hash([1]));
    private static MediaToolIdentity Tool()
    {
        var writer = new MediaAbIdentityWriter(); var value = new MediaToolIdentity(Hash([1]), Hash([2]), Hash([3]), default);
        return new(value.Executables, value.Dependencies, value.NormalizedPipeline, writer.ComputeToolDigest(value));
    }
}
