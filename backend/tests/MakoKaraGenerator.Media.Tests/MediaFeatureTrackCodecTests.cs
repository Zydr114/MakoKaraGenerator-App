using System.Buffers.Binary;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Independent byte fixtures and exact microsecond temporal oracles; source only until host acceptance runs them.</summary>
public sealed class MediaFeatureTrackCodecTests
{
    private readonly MediaFeatureTrackCodec _codec = new(new MediaMomentKernel(), new MediaAbValueValidator());

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(130)]
    public async Task IndependentCanonicalVideoOpensAndSelectsHalfOpenIntervals(int count)
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, count);
        var budget = ReaderBudget(); var input = new Mft1ObservedInput(fixture.Bytes);
        await using (IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None))
        {
            Assert.Equal(count, reader.Summary.FrameCount);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(new PresentationFrameId(i), await reader.SelectAsync(i * 1000, CancellationToken.None));
                Assert.Equal(new PresentationFrameId(i), await reader.SelectAsync((i + 1) * 1000 - 1, CancellationToken.None));
            }
            Assert.True(budget.Live > 0); Assert.False(input.Closed);
        }
        Assert.True(input.Closed); Assert.Equal(0, budget.Live);
    }

    [Theory]
    [InlineData(long.MinValue, 0, BackgroundPlaybackMode.Loop)]
    [InlineData(long.MinValue, 2000000, BackgroundPlaybackMode.Loop)]
    [InlineData(-1, 2000, BackgroundPlaybackMode.FreezeLastFrame)]
    [InlineData(500, 2000, BackgroundPlaybackMode.Loop)]
    [InlineData(130000, 128000, BackgroundPlaybackMode.Loop)]
    [InlineData(133000, 9000, BackgroundPlaybackMode.Loop)]
    [InlineData(133000, 9000, BackgroundPlaybackMode.FreezeLastFrame)]
    [InlineData(long.MaxValue, 2000000, BackgroundPlaybackMode.Loop)]
    [InlineData(long.MaxValue, 2000000, BackgroundPlaybackMode.FreezeLastFrame)]
    public async Task TemporalGridMatchesIndependentUnitMicrosecondOracle(long time, long window, BackgroundPlaybackMode playback)
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 130);
        var budget = ReaderBudget();
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(new Mft1ObservedInput(fixture.Bytes), fixture.Source, fixture.Identity, budget, CancellationToken.None);
        var actual = new MediaMomentValues[576];
        await reader.ReadTemporalGridAsync(new(time, window, playback), actual, CancellationToken.None);
        double[] expected = Oracle(fixture, time, window, playback);
        Assert.All(actual, cell => Assert.Equal(expected, Components(cell)));
        var synchronous = new MediaMomentValues[576];
        reader.ReadTemporalGrid(new(time, window, playback), synchronous, CancellationToken.None);
        Assert.Equal(actual, synchronous);
    }

    [Fact]
    public async Task OneMicrosecondLoopUsesCycleMultiplicationAndBoundedIo()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 1, 1);
        var input = new Mft1ObservedInput(fixture.Bytes); var budget = ReaderBudget();
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None);
        input.ResetObservations(); var actual = new MediaMomentValues[576];
        await reader.ReadTemporalGridAsync(new(long.MaxValue, 2_000_000, BackgroundPlaybackMode.Loop), actual, CancellationToken.None);
        Assert.True(input.Reads < 20); Assert.True(input.BytesRead < 100_000);
        Assert.Equal(Mft1IndependentFixture.Q(0, 0) / 65535d, actual[0].Rp);
    }

    [Fact]
    public async Task LongRangeUsesPrefixDifferenceRatherThanHistoryScan()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 512);
        var input = new Mft1ObservedInput(fixture.Bytes); var budget = ReaderBudget();
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None);
        input.ResetObservations(); var actual = new MediaMomentValues[576];
        await reader.ReadTemporalGridAsync(new(450_000, 400_000, BackgroundPlaybackMode.FreezeLastFrame), actual, CancellationToken.None);
        Assert.True(input.BytesRead < 1_400_000);
        Assert.Equal(Oracle(fixture, 450_000, 400_000, BackgroundPlaybackMode.FreezeLastFrame), Components(actual[0]));
    }

    [Fact]
    public async Task StillHasNoInventedDurationAndAllWindowsUseItsOnlyGrid()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Still); var budget = ReaderBudget();
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(new Mft1ObservedInput(fixture.Bytes), fixture.Source, fixture.Identity, budget, CancellationToken.None);
        Assert.Null(reader.Summary.VideoDurationUs);
        Assert.IsType<PresentationInterval.Timeless>(await reader.ReadIntervalAsync(new(0), CancellationToken.None));
        Assert.Equal(new PresentationFrameId(0), await reader.SelectAsync(long.MaxValue, CancellationToken.None));
        var output = new MediaMomentValues[576];
        await reader.ReadTemporalGridAsync(new(long.MinValue, 2_000_000, BackgroundPlaybackMode.Loop), output, CancellationToken.None);
        Assert.Equal(Mft1IndependentFixture.Q(0, 0) / 65535d, output[575].Rp);
        var synchronous = new MediaMomentValues[576];
        reader.ReadTemporalGrid(new(long.MaxValue, 2_000_000, BackgroundPlaybackMode.FreezeLastFrame), synchronous, CancellationToken.None);
        Assert.Equal(output, synchronous);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("flag")]
    [InlineData("header reserved")]
    [InlineData("offset")]
    [InlineData("count")]
    [InlineData("grid reserved")]
    [InlineData("interval flag")]
    [InlineData("interval reserved")]
    [InlineData("unreduced")]
    [InlineData("changed stream base")]
    [InlineData("canonical time")]
    [InlineData("raw adjacency")]
    [InlineData("early tail")]
    [InlineData("missing tail")]
    [InlineData("prefix zero")]
    [InlineData("prefix end")]
    [InlineData("prefix elapsed")]
    [InlineData("prefix sum")]
    public async Task SelfConsistentHashCannotHideMalformedCanonicalStructure(string mutation)
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 65);
        switch (mutation)
        {
            case "magic": fixture.Bytes[0] ^= 1; break;
            case "version": fixture.U32(8, 2); break;
            case "flag": fixture.U32(20, 1); break;
            case "header reserved": fixture.Bytes[240] = 1; break;
            case "offset": fixture.U64(72, fixture.GridOffset + 1); break;
            case "count": fixture.U64(40, long.MaxValue); break;
            case "grid reserved": fixture.Bytes[fixture.GridOffset + 14] = 1; break;
            case "interval flag": fixture.U32(256 + 52, 1); break;
            case "interval reserved": fixture.U64(256 + 56, 1); break;
            case "unreduced": fixture.I32(256 + 8, 2); fixture.I32(256 + 12, 2_000_000); break;
            case "changed stream base":
                // Keep all exact source times and raw adjacency equal while changing frame one's fixed decoder base.
                fixture.I64(256 + 16, -13_998_000); fixture.I32(256 + 28, 2_000_000);
                fixture.I64(256 + 64, -13_998_000); fixture.I32(256 + 64 + 12, 2_000_000); break;
            case "canonical time": fixture.U64(256 + 40, 999); break;
            case "raw adjacency": fixture.I64(256 + 64, -6_998_999); break;
            case "early tail": fixture.U32(256 + 48, 3); break;
            case "missing tail": fixture.U32(256 + 64 * 64 + 48, 1); break;
            case "prefix zero": fixture.U64(fixture.PrefixOffset + 16, 1); break;
            case "prefix end": fixture.U64(fixture.PrefixOffset + 32272, 63); break;
            case "prefix elapsed": fixture.U64(fixture.PrefixOffset + 32272 + 8, 63_999); break;
            case "prefix sum": fixture.Bytes[fixture.PrefixOffset + 32272 + 16] ^= 1; break;
        }
        fixture.RefreshIndexHash(); // Both outer hashes are recomputed so only semantic checking catches the attack.
        var budget = ReaderBudget(); using var input = new Mft1ObservedInput(fixture.Bytes);
        MediaAbException failure = await Assert.ThrowsAsync<MediaAbException>(async () =>
            await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None));
        Assert.Equal(MediaAbFailure.ArtifactInvalid, failure.Failure);
        Assert.False(input.Closed); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public async Task WrongFullHashTruncationAndTrailingDataAreRejectedWithoutTakingInputOwnership()
    {
        foreach (int delta in new[] { -1, 0, 1 })
        {
            var fixture = new Mft1IndependentFixture(MediaSourceKind.Still);
            byte[] bytes = new byte[fixture.Bytes.Length + delta];
            fixture.Bytes.AsSpan(0, Math.Min(bytes.Length, fixture.Bytes.Length)).CopyTo(bytes);
            MediaArtifactIdentity identity = fixture.Identity;
            if (delta == 0) bytes[fixture.GridOffset] ^= 1;
            var budget = ReaderBudget(); using var input = new Mft1ObservedInput(bytes);
            await Assert.ThrowsAsync<MediaAbException>(async () => await _codec.OpenValidatedAsync(input, fixture.Source, identity, budget, CancellationToken.None));
            Assert.False(input.Closed); Assert.Equal(0, budget.Live);
        }
    }

    [Fact]
    public async Task TrustedFinalEndpointMayUseAnotherReducedTimeBase()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 2);
        fixture.I64(256 + 64 + 16, -13_996_000); fixture.I32(256 + 64 + 28, 2_000_000);
        fixture.RefreshIndexHash();
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(new Mft1ObservedInput(fixture.Bytes),
            fixture.Source, fixture.Identity, ReaderBudget(), CancellationToken.None);
        var last = Assert.IsType<PresentationInterval.Timed>(await reader.ReadIntervalAsync(new(1), CancellationToken.None));
        Assert.Equal(new RationalTimeBase(1, 2_000_000), last.RawEnd.TimeBase);
    }

    [Fact]
    public async Task BuilderRejectsObservationThatChangesTheAcceptedStreamTimeBase()
    {
        MediaFeatureFileSource source = Mft1IndependentFixture.MakeSource(MediaSourceKind.Video);
        var budget = BuilderBudget(); await using var scratch = new Mft1TestScratch(200_000);
        await using IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(source, scratch, budget);
        var frame = new PresentationFrameBorrow(source.Key, source.Profile.Digest,
            new(new(0), new RationalTimestamp(0, new RationalTimeBase(1, 2_000_000)), 1, 1, source.Presentation.Color.Digest),
            new float[] { 0, 0, 0, 1 });
        MediaAbException failure = await Assert.ThrowsAsync<MediaAbException>(async () => await builder.OnFrameAsync(frame, CancellationToken.None));
        Assert.Equal(MediaAbFailure.PresentationMismatch, failure.Failure);
        Assert.Equal(0, scratch.OpenCount); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public async Task CancelledValidationReleasesBackingAndLeavesInputCallerOwned()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 130);
        using var cancellation = new CancellationTokenSource();
        using var input = new Mft1ObservedInput(fixture.Bytes); var budget = ReaderBudget();
        input.BeforeRead = () => { if (input.Reads == 2) cancellation.Cancel(); };
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(input.Closed); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public async Task ReaderBudgetRefusesBeforeIoAndQueryRejectsInvalidBounds()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Still);
        using var input = new Mft1ObservedInput(fixture.Bytes); var tiny = new Mft1TestBudget(1);
        MediaAbException failure = await Assert.ThrowsAsync<MediaAbException>(async () =>
            await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, tiny, CancellationToken.None));
        Assert.Equal(MediaAbFailure.CapacityExceeded, failure.Failure); Assert.Equal(0, input.Reads);
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, ReaderBudget(), CancellationToken.None);
        var output = new MediaMomentValues[576];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await reader.ReadTemporalGridAsync(new(0, 2_000_001, BackgroundPlaybackMode.Loop), output, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await reader.ReadTemporalGridAsync(new(0, 0, (BackgroundPlaybackMode)999), output, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await reader.ReadTemporalGridAsync(new(0, 0, BackgroundPlaybackMode.Loop), output.AsMemory(0, 575), CancellationToken.None));
    }

    [Fact]
    public async Task BuilderTransfersSpoolsAndWritesRepeatableSectionsWithoutOwningCallerTarget()
    {
        MediaFeatureFileSource source = Mft1IndependentFixture.MakeSource(MediaSourceKind.Still);
        var budget = BuilderBudget(); await using var scratch = new Mft1TestScratch(20_000);
        IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(source, scratch, budget);
        await builder.OnFrameAsync(Frame(source), CancellationToken.None);
        await builder.OnIntervalAsync(new PresentationInterval.Timeless(new(0)), CancellationToken.None);
        byte[] interval = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(interval.AsSpan(48), 4);
        var summary = new PresentationIndexSummary(source.Key, source.Profile.Digest, source.Presentation.Digest,
            new(Convert.ToHexStringLower(SHA256.HashData(interval))), 1, null);
        await using IMediaFeatureTrackWriteSource writeSource = await builder.CompleteAsync(summary, CancellationToken.None);
        await builder.DisposeAsync(); Assert.Equal(0, scratch.ClosedCount); Assert.False(scratch.Disposed);
        using var targetA = new MemoryStream(); using var targetB = new MemoryStream();
        MediaArtifactIdentity identity = await _codec.WriteCanonicalAsync(writeSource, targetA, BuilderBudget(), CancellationToken.None);
        await _codec.WriteCanonicalAsync(writeSource, targetB, BuilderBudget(), CancellationToken.None);
        Assert.Equal(targetA.ToArray(), targetB.ToArray()); Assert.True(targetA.CanWrite); Assert.Equal(9536, identity.ByteLength);
        await using (IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(new MemoryStream(targetA.ToArray(), false), source, identity, ReaderBudget(), CancellationToken.None))
        {
            var grid = new MediaMomentValues[576];
            await reader.ReadTemporalGridAsync(new(0, 0, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
            Assert.Equal(1d, grid[0].Alpha); Assert.Equal(0d, grid[0].Rp);
        }
        await writeSource.DisposeAsync(); Assert.Equal(3, scratch.ClosedCount); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public async Task BuilderPairingFailureClosesItsSpoolsAndDoesNotDisposeScratchReservation()
    {
        MediaFeatureFileSource source = Mft1IndependentFixture.MakeSource(MediaSourceKind.Still);
        var budget = BuilderBudget(); await using var scratch = new Mft1TestScratch(20_000);
        await using IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(source, scratch, budget);
        await builder.OnFrameAsync(Frame(source), CancellationToken.None);
        MediaAbException failure = await Assert.ThrowsAsync<MediaAbException>(async () => await builder.OnFrameAsync(Frame(source), CancellationToken.None));
        Assert.Equal(MediaAbFailure.PresentationMismatch, failure.Failure);
        Assert.Equal(3, scratch.ClosedCount); Assert.False(scratch.Disposed); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public async Task BuilderCancellationDuringSpoolOpeningClosesAcquiredStreamsAndPreservesToken()
    {
        MediaFeatureFileSource source = Mft1IndependentFixture.MakeSource(MediaSourceKind.Video);
        var budget = BuilderBudget(); await using var scratch = new Mft1TestScratch(200_000);
        using var cancellation = new CancellationTokenSource();
        scratch.BeforeOpen = section => { if (section == MediaTrackSection.Prefixes) cancellation.Cancel(); };
        await using IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(source, scratch, budget);
        var frame = new PresentationFrameBorrow(source.Key, source.Profile.Digest,
            new(new(0), new RationalTimestamp(-7_000_000, new RationalTimeBase(1, 1_000_000)), 1, 1, source.Presentation.Color.Digest),
            new float[] { 0, 0, 0, 1 });
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await builder.OnFrameAsync(frame, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(3, scratch.ClosedCount); Assert.False(scratch.Disposed); Assert.Equal(0, budget.Live);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task WriterRejectsIncorrectSectionLengthsAndRetainsBothCallerObjects(int delta)
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 2);
        await using var source = new Mft1FixtureWriteSource(fixture) { IntervalLengthDelta = delta };
        using var target = new MemoryStream(); var budget = BuilderBudget();
        MediaAbException failure = await Assert.ThrowsAsync<MediaAbException>(async () =>
            await _codec.WriteCanonicalAsync(source, target, budget, CancellationToken.None));
        Assert.Equal(MediaAbFailure.ArtifactInvalid, failure.Failure);
        Assert.True(target.CanWrite); Assert.False(source.Disposed); Assert.Equal(0, budget.Live);
    }

    [Fact]
    public async Task WriterPreservesIndependentFixtureBytesAndRejectsNonemptyTarget()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 65);
        await using var source = new Mft1FixtureWriteSource(fixture);
        using var target = new MemoryStream();
        MediaArtifactIdentity identity = await _codec.WriteCanonicalAsync(source, target, BuilderBudget(), CancellationToken.None);
        Assert.Equal(fixture.Bytes, target.ToArray()); Assert.Equal(fixture.Identity, identity);
        await Assert.ThrowsAsync<MediaAbException>(async () => await _codec.WriteCanonicalAsync(source, target, BuilderBudget(), CancellationToken.None));
        Assert.True(target.CanWrite); Assert.False(source.Disposed);
    }

    [Fact]
    public async Task VideoBuilderWriterReaderMatchesIndependentVariableDurationWireAcrossFullAndPartialBlocks()
    {
        const int count = 65;
        var expected = new Mft1IndependentFixture(MediaSourceKind.Video, count);
        long[] durations = Enumerable.Range(0, count).Select(i => (long)(i % 7 + 1) * 137).ToArray();
        expected.ExplicitDurationUs = durations.Sum(); expected.U64(48, durations.Sum());
        var sums = new ulong[7]; long elapsed = 0; int prefixOrdinal = 1;
        var budget = BuilderBudget(); await using var scratch = new Mft1TestScratch(2_000_000);
        await using IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(expected.Source, scratch, budget);
        for (int frame = 0; frame < count; frame++)
        {
            (float[] pixels, ushort[] q) = KnownPixel(frame % 5);
            long end = elapsed + durations[frame]; int at = 256 + frame * 64;
            expected.I64(at, -7_000_000 + elapsed); expected.I64(at + 16, -7_000_000 + end);
            expected.U64(at + 32, elapsed); expected.U64(at + 40, end);
            for (int cell = 0; cell < 576; cell++)
                for (int moment = 0; moment < 7; moment++) expected.U16(expected.GridOffset + frame * 9216 + cell * 16 + moment * 2, q[moment]);
            for (int moment = 0; moment < 7; moment++) sums[moment] += (ulong)q[moment] * (ulong)durations[frame];
            if ((frame + 1) % 64 == 0 || frame == count - 1)
            {
                int prefix = expected.PrefixOffset + prefixOrdinal++ * 32272;
                expected.U64(prefix, frame + 1); expected.U64(prefix + 8, end);
                for (int cell = 0; cell < 576; cell++)
                    for (int moment = 0; moment < 7; moment++)
                        BinaryPrimitives.WriteUInt64LittleEndian(expected.Bytes.AsSpan(prefix + 16 + (cell * 7 + moment) * 8), sums[moment]);
            }
            var rawStart = new RationalTimestamp(-7_000_000 + elapsed, new(1, 1_000_000));
            var rawEnd = new RationalTimestamp(-7_000_000 + end, new(1, 1_000_000));
            var id = new PresentationFrameId(frame);
            await builder.OnFrameAsync(new(expected.Source.Key, expected.Source.Profile.Digest,
                new(id, rawStart, 1, 1, expected.Source.Presentation.Color.Digest), pixels), CancellationToken.None);
            await builder.OnIntervalAsync(new PresentationInterval.Timed(id, rawStart, rawEnd, elapsed, end,
                frame == count - 1 ? PresentationEndAuthority.StreamEndpoint : PresentationEndAuthority.NextPts), CancellationToken.None);
            elapsed = end;
        }
        expected.RefreshIndexHash();
        var summary = new PresentationIndexSummary(expected.Source.Key, expected.Source.Profile.Digest, expected.Source.Presentation.Digest,
            expected.Identity.PresentationIndexDigest, count, elapsed);
        await using IMediaFeatureTrackWriteSource writeSource = await builder.CompleteAsync(summary, CancellationToken.None);
        using var target = new MemoryStream();
        MediaArtifactIdentity actual = await _codec.WriteCanonicalAsync(writeSource, target, BuilderBudget(), CancellationToken.None);
        Assert.Equal(expected.Bytes, target.ToArray()); Assert.Equal(expected.Identity, actual);
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(new Mft1ObservedInput(target.ToArray()),
            expected.Source, actual, ReaderBudget(), CancellationToken.None);
        var grid = new MediaMomentValues[576];
        await reader.ReadTemporalGridAsync(new(elapsed, elapsed, BackgroundPlaybackMode.FreezeLastFrame), grid, CancellationToken.None);
        Assert.Equal(sums.Select(sum => sum / (elapsed * 65535d)), Components(grid[0]));
        await writeSource.DisposeAsync(); Assert.Equal(0, budget.Live); Assert.Equal(3, scratch.ClosedCount);
    }

    [Fact]
    public async Task CancelledQueryCanRecoverAndDisposeReleasesOwnershipOnce()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 130);
        var input = new Mft1ObservedInput(fixture.Bytes); var budget = ReaderBudget();
        IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None);
        input.ResetObservations(); using var cancellation = new CancellationTokenSource();
        input.BeforeRead = () => { if (input.Reads == 4) cancellation.Cancel(); };
        var output = new MediaMomentValues[576];
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadTemporalGridAsync(new(130_000, 128_000, BackgroundPlaybackMode.Loop), output, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.False(input.Closed); Assert.True(budget.Live > 0);
        input.BeforeRead = null;
        await reader.ReadTemporalGridAsync(new(130_000, 128_000, BackgroundPlaybackMode.Loop), output, CancellationToken.None);
        Assert.Equal(Oracle(fixture, 130_000, 128_000, BackgroundPlaybackMode.Loop), Components(output[0]));
        await reader.DisposeAsync(); await reader.DisposeAsync();
        Assert.True(input.Closed); Assert.Equal(0, budget.Live);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.SelectAsync(0, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuilderMissingIntervalOrWrongSummaryReleasesAllOwnedResources(bool missingInterval)
    {
        MediaFeatureFileSource source = Mft1IndependentFixture.MakeSource(MediaSourceKind.Still);
        var budget = BuilderBudget(); await using var scratch = new Mft1TestScratch(20_000);
        await using IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(source, scratch, budget);
        await builder.OnFrameAsync(Frame(source), CancellationToken.None);
        if (!missingInterval) await builder.OnIntervalAsync(new PresentationInterval.Timeless(new(0)), CancellationToken.None);
        var summary = new PresentationIndexSummary(source.Key, source.Profile.Digest, source.Presentation.Digest,
            new(new string('0', 64)), 1, null);
        await Assert.ThrowsAsync<MediaAbException>(async () => await builder.CompleteAsync(summary, CancellationToken.None));
        Assert.Equal(0, budget.Live); Assert.Equal(3, scratch.ClosedCount); Assert.False(scratch.Disposed);
    }

    private static (float[] Pixels, ushort[] Q) KnownPixel(int palette) => palette switch
    {
        0 => ([0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0]),
        1 => ([0, 0, 0, 1], [0, 0, 0, 65535, 0, 0, 65535]),
        2 => ([1, 1, 1, 1], [65535, 65535, 65535, 65535, 65535, 65535, 65535]),
        3 => ([0.5f, 0.5f, 0.5f, 1], [32768, 32768, 32768, 65535, 16384, 32768, 65535]),
        _ => ([0.5f, 0.5f, 0.5f, 0.5f], [32768, 32768, 32768, 32768, 16384, 16384, 16384]),
    };

    private static PresentationFrameBorrow Frame(MediaFeatureFileSource source) => new(source.Key, source.Profile.Digest,
        new(new(0), null, 1, 1, source.Presentation.Color.Digest), new float[] { 0, 0, 0, 1 });
    private static Mft1TestBudget ReaderBudget() => new(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
    private static Mft1TestBudget BuilderBudget() => new(MediaAbCapacityV1.MaximumBuilderWorkingBytes);
    private static double[] Components(MediaMomentValues cell) => [cell.Rp, cell.Gp, cell.Bp, cell.Alpha, cell.YpSquared, cell.AlphaYp, cell.AlphaSquared];

    private static double[] Oracle(Mft1IndependentFixture fixture, long time, long window, BackgroundPlaybackMode playback)
    {
        var sums = new ulong[7]; long samples = Math.Max(window, 1);
        for (long offset = 0; offset < samples; offset++)
        {
            Int128 project = window == 0 ? time : (Int128)time - window + offset;
            long source = project <= 0 ? 0 : playback == BackgroundPlaybackMode.Loop
                ? (long)(project % fixture.Duration!.Value) : (long)Int128.Min(project, fixture.Duration!.Value - 1);
            long frame = source / fixture.FrameUs;
            for (int moment = 0; moment < 7; moment++) sums[moment] += Mft1IndependentFixture.Q(frame, moment);
        }
        return sums.Select(sum => sum / (samples * 65535d)).ToArray();
    }
}
