using MakoKaraGenerator.Core.Media;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaContainerTimingAuthorityTests
{
    [Fact]
    public void Mov_ReorderedBFramesHaveIndependentExactTailAndSourcePayloadProof()
    {
        byte[] file = Movie(out long offset, duration: 1, composition: true); using var stream = new MemoryStream(file); var budget = new Budget();
        using (MediaContainerTimingAuthority timing = MediaContainerTimingAuthority.ReadMov(stream, 7, budget))
        {
            Assert.Equal(3, timing.SampleCount);
            RationalTimestamp first = new(0, new(1, 1000)), next = new(1, new(1, 1000)), last = new(2, new(1, 1000));
            _ = timing.VerifyPacket(stream, offset, 3, first, Hash(file.AsSpan((int)offset, 3)));
            _ = timing.VerifyPacket(stream, offset + 6, 3, next, Hash(file.AsSpan((int)offset + 6, 3)));
            RationalTimestamp tail = timing.VerifyPacket(stream, offset + 3, 3, last, Hash(file.AsSpan((int)offset + 3, 3))) ?? throw new InvalidOperationException("MOV stts duration missing.");
            Assert.Equal(3000, new MediaPresentationTimeMath().NormalizeBoundaryUs(MediaContainerTimingAuthority.AddDuration(last, tail), first)); timing.Complete();
        }
        Assert.Equal(0, budget.Live);
    }

    [Fact]
    public void Mov_DroppedDuplicateChangedBytesMissingDurationAndHostileOffsetFail()
    {
        byte[] file = Movie(out long offset); var budget = new Budget(); using var stream = new MemoryStream(file);
        using (MediaContainerTimingAuthority timing = MediaContainerTimingAuthority.ReadMov(stream, 7, budget))
        {
            Assert.Throws<MediaAbException>(() => timing.Complete());
            Assert.Throws<MediaAbException>(() => timing.VerifyPacket(stream, offset, 3, new(0, new(1, 1000)), Hash([99])));
            _ = timing.VerifyPacket(stream, offset, 3, new(0, new(1, 1000)), Hash(file.AsSpan((int)offset, 3)));
            Assert.Throws<MediaAbException>(() => timing.VerifyPacket(stream, offset, 3, new(0, new(1, 1000)), Hash(file.AsSpan((int)offset, 3))));
        }
        using var missing = new MemoryStream(Movie(out _, missingDuration: true));
        Assert.Throws<MediaAbException>(() => MediaContainerTimingAuthority.ReadMov(missing, 7, budget));
        Assert.Equal(0, budget.Live);
    }

    [Theory]
    [InlineData(false, 33_333_333)]
    [InlineData(true, 40_000_000)]
    public void Matroska_UsesRealNanosecondDefaultOrExplicitBlockDuration(bool blockDuration, long expectedDuration)
    {
        byte[] file = Matroska(out long packet, out long payload, defaultDuration: true, blockDuration: blockDuration);
        using var stream = new MemoryStream(file); var budget = new Budget();
        Assert.Equal(5UL, MediaContainerTimingAuthority.ReadMatroskaTrackNumber(stream, packet));
        using (MediaContainerTimingAuthority timing = MediaContainerTimingAuthority.ReadMatroska(stream, 5, budget))
        {
            RationalTimestamp? duration = timing.VerifyPacket(stream, packet, 3, new(-1, new(1, 1000)), Hash(file.AsSpan((int)payload, 3)));
            Assert.NotNull(duration); Assert.Equal(expectedDuration, duration.Value.Ticks); Assert.Equal(new RationalTimeBase(1, 1_000_000_000), duration.Value.TimeBase); timing.Complete();
        }
        Assert.Equal(0, budget.Live);
    }

    [Theory]
    [InlineData(false, 3, 5UL)]
    [InlineData(false, 130, 5UL)]
    [InlineData(false, 3, 300UL)]
    [InlineData(false, 130, 300UL)]
    [InlineData(true, 3, 5UL)]
    [InlineData(true, 130, 5UL)]
    [InlineData(true, 3, 300UL)]
    [InlineData(true, 130, 300UL)]
    public void Matroska_PacketPositionIsTrackNumberAfterCompleteEbmlHeader(bool blockGroup, int payloadBytes, ulong trackNumber)
    {
        byte[] file = Matroska(out long packet, out long payload, blockDuration: blockGroup, payloadBytes: payloadBytes, trackNumber: trackNumber);
        int trackWidth = Vint(trackNumber).Length, sizeWidth = Vint((ulong)(trackWidth + 3 + payloadBytes)).Length;
        Assert.Equal((byte)(blockGroup ? 0xA1 : 0xA3), file[(int)packet - sizeWidth - 1]);
        Assert.Equal(Vint(trackNumber), file.AsSpan((int)packet, trackWidth).ToArray());
        Assert.Equal(packet + trackWidth + 3, payload);
        using var stream = new MemoryStream(file); var budget = new Budget(); stream.Position = 1;
        Assert.Equal(trackNumber, MediaContainerTimingAuthority.ReadMatroskaTrackNumber(stream, packet));
        Assert.Equal(1, stream.Position);
        using (MediaContainerTimingAuthority timing = MediaContainerTimingAuthority.ReadMatroska(stream, trackNumber, budget))
        {
            RationalTimestamp pts = new(-1, new(1, 1000)); MediaDigest hash = Hash(file.AsSpan((int)payload, payloadBytes));
            Assert.Throws<MediaAbException>(() => timing.VerifyPacket(stream, packet - sizeWidth, payloadBytes, pts, hash));
            RationalTimestamp? duration = timing.VerifyPacket(stream, packet, payloadBytes, pts, hash);
            Assert.NotNull(duration); Assert.Equal(blockGroup ? 40_000_000 : 33_333_333, duration.Value.Ticks); timing.Complete();
        }
        Assert.Equal(0, budget.Live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Matroska_LacingRemainsUnsupported(bool defaultDuration)
    {
        byte[] file = Matroska(out _, out _, defaultDuration: defaultDuration, lace: true); using var stream = new MemoryStream(file); var budget = new Budget();
        Assert.Throws<MediaAbException>(() => MediaContainerTimingAuthority.ReadMatroska(stream, 5, budget)); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public void Matroska_DeprecatedFrameRateNeverInventsAbsentDuration()
    {
        byte[] file = Matroska(out long packet, out long payload, defaultDuration: false);
        using var stream = new MemoryStream(file); var budget = new Budget();
        using (MediaContainerTimingAuthority timing = MediaContainerTimingAuthority.ReadMatroska(stream, 5, budget))
        {
            Assert.Null(timing.VerifyPacket(stream, packet, 3, new(-1, new(1, 1000)), Hash(file.AsSpan((int)payload, 3))));
            timing.Complete();
        }
        Assert.Equal(0, budget.Live);
    }

    [Fact]
    public void ContainerCancellationIsNeverWrapped()
    {
        using var source = new MemoryStream(Movie(out _)); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MediaContainerTimingAuthority.ReadMov(source, 7, new Budget(), cancellation.Token));
    }
}
