using System.Buffers.Binary;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.TestSupport;
using Xunit;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendMediaFeatureWorkerStageRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledImportPermanentlyConsumesTheSameScopeEvenWithValidFilesAndFreshToken(bool cancelAfterValidation)
    {
        using TemporaryDirectory temp = new(); using var cancel = new CancellationTokenSource();
        bool denyDelete = false;
        var controlledFailure = new IOException("controlled failure after complete canonical validation");
        var cleanupFailure = new IOException("controlled cleanup after rejected reimport");
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"), hooks: new()
        { BeforeDeleteFile = _ => { if (denyDelete) throw cleanupFailure; } });
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        long reserved = store.UsedBytes;
        var firstBudget = MediaFeatureStoreFixture.ReaderBudget();
        var firstCodec = new TrackingValidationCodec(token =>
        {
            if (cancelAfterValidation) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            throw controlledFailure;
        });
        Exception primary;
        if (cancelAfterValidation)
        {
            OperationCanceledException cancelled = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await store.PrepareWorkerCandidateAsync(work.Reservation, work.Scope, identity, firstCodec, firstBudget, cancel.Token));
            Assert.Equal(cancel.Token, cancelled.CancellationToken); primary = cancelled;
        }
        else
        {
            primary = await Assert.ThrowsAsync<IOException>(async () =>
                await store.PrepareWorkerCandidateAsync(work.Reservation, work.Scope, identity, firstCodec, firstBudget, cancel.Token));
            Assert.Same(controlledFailure, primary);
        }
        Assert.Equal(1, firstCodec.OpenCalls); Assert.Equal(1, firstCodec.ValidatedOpens); Assert.True(firstCodec.SawReadOnly);
        Assert.Equal(0, firstBudget.LiveBytes); Assert.Equal(reserved, store.UsedBytes); Assert.True(File.Exists(work.Candidate));

        // The bytes still pass the complete production gate with a fresh token. Reimport must fail on consumed state.
        var independentBudget = MediaFeatureStoreFixture.ReaderBudget();
        await using (FileStream file = new(work.Candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (IMediaFeatureTrackReader checkedReader = await MediaFeatureStoreFixture.Codec().OpenValidatedAsync(file,
            work.Reservation.Source, identity, independentBudget, CancellationToken.None))
            Assert.Equal(identity, checkedReader.Artifact);
        Assert.Equal(0, independentBudget.LiveBytes);

        var retryCodec = new TrackingValidationCodec(); var retryBudget = MediaFeatureStoreFixture.ReaderBudget();
        MediaAbException retry = await Assert.ThrowsAsync<MediaAbException>(async () =>
            await store.PrepareWorkerCandidateAsync(work.Reservation, work.Scope, identity, retryCodec, retryBudget, CancellationToken.None));
        Assert.Equal(MediaAbFailure.ArtifactInvalid, retry.Failure); Assert.Equal(0, retryCodec.OpenCalls);
        Assert.Equal(0, retryBudget.LiveBytes); Assert.Equal(reserved, store.UsedBytes); Assert.True(File.Exists(work.Candidate));

        denyDelete = true; await work.DisposeAsync();
        Assert.Same(cleanupFailure, primary.Data["MediaReservationCleanupFailure"]);
        Assert.False(retry.Data.Contains("MediaReservationCleanupFailure"));
        Assert.True(store.UsedBytes > 0);
        denyDelete = false; store.Prune([]); Assert.Equal(0, store.UsedBytes);
    }

    [Theory]
    [InlineData(63, 2)]
    [InlineData(64, 2)]
    [InlineData(65, 3)]
    public async Task RealVideoWithFewerFramesThanReservedKeepsExactPrefixesAcrossThe64FrameBoundary(int actualFrames, int expectedPrefixes)
    {
        using TemporaryDirectory temp = new();
        const int maximumFrames = 129;
        MediaFeatureFileSource source = MediaFeatureStoreFixture.Source(MediaSourceKind.Video);
        long reserved = MediaTrackLayoutV1.GetScratchAndCandidateBytes(MediaSourceKind.Video, maximumFrames);
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"), reserved);
        await using var work = new BackendMediaFeatureWorkerStageFixture(store, source, maximumFrames);
        Assert.Equal(reserved, store.UsedBytes); Assert.Equal((long)maximumFrames, work.Grant.MaximumFrames);
        Assert.Equal(4L * MediaTrackLayoutV1.PrefixEntryBytes, work.Grant.PrefixMaximumBytes);

        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(actualFrames);
        Assert.Equal((long)actualFrames, identity.FrameCount); Assert.Equal(actualFrames * 2000L, identity.VideoDurationUs);
        Assert.True(identity.FrameCount < work.Grant.MaximumFrames);
        Assert.Equal(actualFrames * (long)MediaTrackLayoutV1.IntervalBytes,
            new FileInfo(Path.Combine(work.Grant.StageDirectory, "intervals.tmp")).Length);
        Assert.Equal(actualFrames * (long)MediaTrackLayoutV1.FrameGridBytes,
            new FileInfo(Path.Combine(work.Grant.StageDirectory, "grids.tmp")).Length);
        string prefixes = Path.Combine(work.Grant.StageDirectory, "prefixes.tmp");
        Assert.Equal(expectedPrefixes * (long)MediaTrackLayoutV1.PrefixEntryBytes, new FileInfo(prefixes).Length);
        Assert.Equal((long)expectedPrefixes, MediaTrackLayoutV1.GetPrefixCount(MediaSourceKind.Video, actualFrames));
        Assert.True(new FileInfo(prefixes).Length < work.Grant.PrefixMaximumBytes);
        Assert.Equal(identity.ByteLength, new FileInfo(work.Candidate).Length);
        await AssertPrefixFileAsync(prefixes, actualFrames, expectedPrefixes);
        long physical = Directory.EnumerateFiles(work.Grant.StageDirectory).Sum(path => new FileInfo(path).Length);
        Assert.Equal(2 * identity.ByteLength - MediaTrackLayoutV1.HeaderBytes, physical);
        Assert.Equal(physical, work.Writer.AdmittedHighWaterBytes); Assert.True(physical < reserved);

        work.Scope.AttestExecutorDrained();
        var importCodec = new TrackingValidationCodec(); var importBudget = MediaFeatureStoreFixture.ReaderBudget();
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await store.PrepareWorkerCandidateAsync(work.Reservation,
            work.Scope, identity, importCodec, importBudget, CancellationToken.None);
        Assert.Equal(1, importCodec.ValidatedOpens); Assert.True(importCodec.SawReadOnly);
        Assert.Equal(0, importBudget.LiveBytes); Assert.Equal(reserved, store.UsedBytes);
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        Assert.Equal(identity.ByteLength, store.UsedBytes);

        var readBudget = MediaFeatureStoreFixture.ReaderBudget();
        await using (BackendMediaFeatureArtifactStore.ReadLease lease = await store.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), readBudget, CancellationToken.None))
        {
            var grid = new MediaMomentValues[MediaTrackLayoutV1.Cells];
            lease.Reader.ReadTemporalGrid(new(actualFrames * 2000L, actualFrames * 2000L, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
            double expectedR = ((actualFrames + 1L) / 2 * 32768L + actualFrames / 2L * 16384L) / (actualFrames * 65535d);
            foreach (MediaMomentValues value in grid)
            {
                Assert.InRange(Math.Abs(expectedR - value.Rp), 0, 3e-12);
                Assert.Equal(32768 / 65535d, value.Alpha);
            }
            PresentationInterval.Timed last = Assert.IsType<PresentationInterval.Timed>(await lease.Reader.ReadIntervalAsync(new(actualFrames - 1), CancellationToken.None));
            Assert.Equal(PresentationEndAuthority.StreamEndpoint, last.EndAuthority); Assert.Equal(actualFrames * 2000L, last.EndUs);
            if (actualFrames == 65)
            {
                PresentationInterval.Timed preceding = Assert.IsType<PresentationInterval.Timed>(await lease.Reader.ReadIntervalAsync(new(63), CancellationToken.None));
                Assert.Equal(PresentationEndAuthority.NextPts, preceding.EndAuthority); Assert.Equal(128_000L, preceding.EndUs);
                lease.Reader.ReadTemporalGrid(new(127_999, 0, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
                Assert.Equal(16384 / 65535d, grid[0].Rp);
                lease.Reader.ReadTemporalGrid(new(128_000, 0, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
                Assert.Equal(32768 / 65535d, grid[0].Rp);
            }
        }
        Assert.Equal(0, readBudget.LiveBytes);
        store.Prune([]); Assert.Equal(0, store.UsedBytes);
    }

    private static async Task AssertPrefixFileAsync(string path, int actualFrames, int expectedPrefixes)
    {
        int[] ends = actualFrames <= 64 ? [0, actualFrames] : [0, 64, actualFrames];
        Assert.Equal(expectedPrefixes, ends.Length);
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] record = new byte[MediaTrackLayoutV1.PrefixEntryBytes];
        foreach (int end in ends)
        {
            await file.ReadExactlyAsync(record, CancellationToken.None);
            Assert.Equal((ulong)end, BinaryPrimitives.ReadUInt64LittleEndian(record));
            Assert.Equal((ulong)end * 2000, BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8)));
            // Independent literal u16 seven-moment values for grayscale .5/.5 alpha and .25/.5 alpha.
            ulong[] half = [32768, 32768, 32768, 32768, 16384, 16384, 16384];
            ulong[] quarter = [16384, 16384, 16384, 32768, 4096, 8192, 16384];
            ulong halfCount = (ulong)((end + 1) / 2), quarterCount = (ulong)(end / 2);
            for (int cell = 0; cell < MediaTrackLayoutV1.Cells; cell++)
            {
                int first = 16 + cell * MediaTrackLayoutV1.Moments * sizeof(ulong);
                for (int moment = 0; moment < MediaTrackLayoutV1.Moments; moment++)
                {
                    ulong sum = checked((half[moment] * halfCount + quarter[moment] * quarterCount) * 2000);
                    Assert.Equal(sum, BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(first + moment * sizeof(ulong))));
                }
            }
        }
        Assert.Equal(file.Length, file.Position);
    }

    /// <summary>Every successful opening delegates the complete production gate. Optional injection fails only after that gate.</summary>
    private sealed class TrackingValidationCodec(Action<CancellationToken>? rejectAfterValidation = null) : IMediaFeatureTrackCodec
    {
        private readonly MediaFeatureTrackCodec _inner = MediaFeatureStoreFixture.Codec();
        internal int OpenCalls { get; private set; }
        internal int ValidatedOpens { get; private set; }
        internal bool SawReadOnly { get; private set; }
        public IMediaFeatureTrackBuilder CreateBuilder(MediaFeatureFileSource source, IMediaTrackScratchReservation scratch,
            IMediaWorkingSetBudget budget) => _inner.CreateBuilder(source, scratch, budget);
        public ValueTask<MediaArtifactIdentity> WriteCanonicalAsync(IMediaFeatureTrackWriteSource source, Stream target,
            IMediaWorkingSetBudget budget, CancellationToken token) => _inner.WriteCanonicalAsync(source, target, budget, token);
        public async ValueTask<IMediaFeatureTrackReader> OpenValidatedAsync(Stream input, MediaFeatureFileSource source,
            MediaArtifactIdentity identity, IMediaWorkingSetBudget budget, CancellationToken token)
        {
            OpenCalls++; SawReadOnly = input.CanRead && input.CanSeek && !input.CanWrite;
            IMediaFeatureTrackReader reader = await _inner.OpenValidatedAsync(input, source, identity, budget, token);
            ValidatedOpens++;
            if (rejectAfterValidation is null) return reader;
            try
            {
                rejectAfterValidation(token);
                throw new InvalidOperationException("The test rejection callback must throw.");
            }
            catch (Exception primary)
            {
                try { await reader.DisposeAsync(); }
                catch (Exception cleanup) { primary.Data["TestValidationReaderCleanupFailure"] = cleanup; }
                throw;
            }
        }
    }
}
