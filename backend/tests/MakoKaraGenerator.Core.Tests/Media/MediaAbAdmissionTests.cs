using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Core.Tests.Media;

/// <summary>Supported SDR combinations, exact layout and total allocation admission regressions.</summary>
public sealed class MediaAbAdmissionTests
{
    /// <summary>Known independent layout totals include all prefix entries and both spool and candidate storage.</summary>
    [Fact]
    public void CanonicalLayoutHasExactMaximumTotals()
    {
        Assert.Equal(9536, MediaTrackLayoutV1.GetArtifactBytes(MediaSourceKind.Still, 1));
        Assert.Equal(52_887_648, MediaTrackLayoutV1.GetArtifactBytes(MediaSourceKind.Video, 5400));
        Assert.Equal(528_390_096, MediaTrackLayoutV1.GetArtifactBytes(MediaSourceKind.Video, 54000));
        Assert.Equal(1_056_779_936, MediaTrackLayoutV1.GetScratchAndCandidateBytes(MediaSourceKind.Video, 54000));
        Assert.Equal(16_961_888, MediaAbCapacityV1.MaximumScratchAndCandidateBytes -
            MediaTrackLayoutV1.GetScratchAndCandidateBytes(MediaSourceKind.Video, 54000));
        Assert.Equal(845, MediaTrackLayoutV1.GetPrefixCount(MediaSourceKind.Video, 54000));
        Assert.Equal(0, MediaTrackLayoutV1.GetPrefixCount(MediaSourceKind.Still, 1));
        Assert.Throws<MediaAbException>(() => MediaTrackLayoutV1.GetArtifactBytes(MediaSourceKind.Video, 54001));
        Assert.Throws<MediaAbException>(() => MediaTrackLayoutV1.GetArtifactBytes(MediaSourceKind.Still, 2));
    }

    /// <summary>A raster ceiling does not grant admission when native or conversion bytes are also live.</summary>
    [Fact]
    public void MaximumRasterPlusOneNativeByteExceedsDecodeLimit()
    {
        long canonicalBytes = MediaDecodeAdmissionV1.GetCanonicalFrameBytes(4096, 4096);
        Assert.Equal(268_435_456, canonicalBytes);
        var value = new MediaDecodeWorkingSet(1, canonicalBytes, 0, 0, 0, 0, true);
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() => MediaDecodeAdmissionV1.Admit(value)).Failure);
        Assert.Equal(MediaAbFailure.CapacityUnsupported, Assert.Throws<MediaAbException>(() =>
            MediaDecodeAdmissionV1.Admit(new(0, 0, 0, 0, 0, 0, false))).Failure);
    }

    /// <summary>Total admission counts every simultaneous component and safely refuses huge or negative reservations.</summary>
    [Fact]
    public void ConservativeWorkingReservationsAreAddedBeforeAdmission()
    {
        long pixels = MediaDecodeAdmissionV1.GetCanonicalFrameBytes(720, 576);
        Assert.Equal(32_000_000 + 2 * pixels + 1_000_000 + 524_288 + 65_536 + 4_096,
            MediaDecodeAdmissionV1.Admit(new(32_000_000, 2 * pixels, 1_000_000, 524_288, 65_536, 4_096, true)));
        Assert.Equal(MediaAbFailure.CapacityUnsupported, Assert.Throws<MediaAbException>(() =>
            MediaDecodeAdmissionV1.Admit(new(-1, 0, 0, 0, 0, 0, true))).Failure);
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaDecodeAdmissionV1.Admit(new(long.MaxValue, long.MaxValue, 0, 0, 0, 0, true))).Failure);
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaDecodeAdmissionV1.GetCanonicalFrameBytes(8192, 8192)).Failure);
    }

    /// <summary>Non-square SAR changes display metrics without expanding or rounding canonical raster dimensions.</summary>
    [Fact]
    public void ReducedNonSquareSarAndQuarterTurnAreAccepted()
    {
        var descriptor = MediaFoundationFixtures.Descriptor(rotation: MediaQuarterTurn.Clockwise90, mirror: true);
        var source = MediaFoundationFixtures.Source(descriptor: descriptor);
        MediaFoundationFixtures.Validator.ValidateSource(source);
        Assert.Equal(576, descriptor.CanonicalWidth);
        Assert.Equal(720, descriptor.CanonicalHeight);
        Assert.Equal(16, descriptor.SarNumerator);
        Assert.Equal(15, descriptor.SarDenominator);
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Descriptor(sarNumerator: 32, sarDenominator: 30));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Descriptor(rotation: MediaQuarterTurn.Clockwise90, canonicalWidth: 720));
    }

    /// <summary>Controlled RGB, YUV709 and explicit JPEG interpretations remain distinct supported families.</summary>
    [Fact]
    public void SupportedColorFamiliesAreAcceptedWithoutZeroAuthoritySentinel()
    {
        MediaFoundationFixtures.Validator.ValidateSource(MediaFoundationFixtures.Source());
        var still16 = MediaFoundationFixtures.Color(MediaNativePixelFormat.Rgba64Le, alpha: MediaAlpha.Straight);
        MediaFoundationFixtures.Validator.ValidateSource(MediaFoundationFixtures.Source(descriptor: MediaFoundationFixtures.Descriptor(color: still16)));
        var yuv = MediaFoundationFixtures.Color(MediaNativePixelFormat.Yuv420P10Le, MediaTransfer.Bt709,
            MediaMatrix.Bt709, MediaRange.Limited, chroma: MediaChromaLocation.Left);
        MediaFoundationFixtures.Validator.ValidateSource(MediaFoundationFixtures.Source(MediaSourceKind.Video,
            MediaFoundationFixtures.Descriptor(MediaSourceKind.Video, yuv)));
        var jpeg = MediaFoundationFixtures.Color(MediaNativePixelFormat.Yuv422P8, matrix: MediaMatrix.Bt601Jpeg,
            chroma: MediaChromaLocation.Center, trustedMissing: true, authority: MediaFoundationFixtures.Hash('0'));
        MediaFoundationFixtures.Validator.ValidateSource(MediaFoundationFixtures.Source(descriptor: MediaFoundationFixtures.Descriptor(color: jpeg)));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Source(MediaSourceKind.Video,
            MediaFoundationFixtures.Descriptor(MediaSourceKind.Video, jpeg)));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Source(MediaSourceKind.Video,
            MediaFoundationFixtures.Descriptor(MediaSourceKind.Video, still16)));
    }

    /// <summary>Contradictory native interpretation is refused rather than clamped or guessed.</summary>
    [Fact]
    public void UnsupportedColorCombinationsAreRefused()
    {
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Color(range: MediaRange.Limited));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Color(alpha: MediaAlpha.Straight));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Color(MediaNativePixelFormat.Yuv420P8,
            MediaTransfer.Bt709, MediaMatrix.Bt709));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Color(MediaNativePixelFormat.Yuv422P8,
            matrix: MediaMatrix.Bt601Jpeg, chroma: MediaChromaLocation.Left));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Color(MediaNativePixelFormat.Yuv444P10Le,
            matrix: MediaMatrix.Bt601Jpeg));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Color(transfer: (MediaTransfer)99));
    }

    /// <summary>Still and video presence rules cannot borrow or invent one another's timing.</summary>
    [Fact]
    public void StreamTimeBasePresenceIsSourceSpecific()
    {
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Source(descriptor:
            MediaFoundationFixtures.Descriptor(explicitTimeBase: new RationalTimeBase(1, 25))));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Source(MediaSourceKind.Video,
            MediaFoundationFixtures.Descriptor(MediaSourceKind.Video, omitVideoTimeBase: true)));
    }

    /// <summary>Artifact metadata checks exact layout, timing presence and every source identity binding.</summary>
    [Fact]
    public void ArtifactMetadataCannotForgeLengthTimingOrBindings()
    {
        MediaFeatureFileSource still = MediaFoundationFixtures.Source();
        MediaFoundationFixtures.Validator.ValidateArtifactIdentity(still, MediaFoundationFixtures.Artifact(still));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateArtifactIdentity(still,
            MediaFoundationFixtures.Artifact(still, duration: 1)));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateArtifactIdentity(still,
            MediaFoundationFixtures.Artifact(still, byteLength: 9537)));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateArtifactIdentity(still,
            MediaFoundationFixtures.Artifact(still, profileDigest: MediaFoundationFixtures.Hash('a'))));
        MediaFeatureFileSource video = MediaFoundationFixtures.Source(MediaSourceKind.Video);
        MediaFoundationFixtures.Validator.ValidateArtifactIdentity(video, MediaFoundationFixtures.Artifact(video, 54000, 3_600_000_000));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateArtifactIdentity(video,
            MediaFoundationFixtures.Artifact(video, duration: 0)));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Validator.ValidateArtifactIdentity(video,
                MediaFoundationFixtures.Artifact(video, duration: 3_600_000_001))).Failure);
    }

    /// <summary>Every strictly positive integer-microsecond interval needs at least one duration microsecond.</summary>
    [Fact]
    public void ArtifactFrameCountCannotExceedDurationMicroseconds()
    {
        MediaFeatureFileSource video = MediaFoundationFixtures.Source(MediaSourceKind.Video);
        var impossible = MediaFoundationFixtures.Artifact(video, 54_000, 1);
        Assert.Equal(MediaAbFailure.ArtifactInvalid, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Validator.ValidateArtifactIdentity(video, impossible)).Failure);
        MediaFoundationFixtures.Validator.ValidateArtifactIdentity(video,
            MediaFoundationFixtures.Artifact(video, 54_000, 54_000));
    }

    /// <summary>Canonical colors never repair NaN, unpremultiplied channels or negative zero.</summary>
    [Fact]
    public void CanonicalColorValidationNeverClamps()
    {
        MediaAbValueValidator.ValidateLinearPremul(new(0.25, 0.1, 0, 0.5));
        Assert.Throws<MediaAbException>(() => MediaAbValueValidator.ValidateLinearPremul(new(0.6, 0, 0, 0.5)));
        Assert.Throws<MediaAbException>(() => MediaAbValueValidator.ValidateLinearPremul(new(double.NaN, 0, 0, 1)));
        Assert.Throws<MediaAbException>(() => MediaAbValueValidator.ValidateLinearPremul(new(-0.0, 0, 0, 0)));
    }

    /// <summary>Named failures accept only defined categories and bounded path-free diagnostics.</summary>
    [Fact]
    public void FailureDetailIsBoundedAndHostIndependent()
    {
        var error = new MediaAbException(MediaAbFailure.DecodeFailed, "Controlled decoder failed.");
        Assert.Equal(error.SafeDetail, error.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaAbException((MediaAbFailure)1000, "Failed."));
        foreach (string invalid in new[] { "", " ", "/home/private/file", "C:\\private", "line\nbreak", new string('x', 1025) })
        {
            Assert.Throws<ArgumentException>(() => new MediaAbException(MediaAbFailure.DecodeFailed, invalid));
        }
    }
}
