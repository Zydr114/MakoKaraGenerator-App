using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;
using Xunit;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendMediaFeatureArtifactStoreTests
{
    [Theory]
    [InlineData(MediaSourceKind.Still)]
    [InlineData(MediaSourceKind.Video)]
    public async Task RealCodecCandidateIsFlushedValidatedPublishedAndLeasedWithExactQuota(MediaSourceKind kind)
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state");
        var source = MediaFeatureStoreFixture.Source(kind);
        long reserved = MediaTrackLayoutV1.GetScratchAndCandidateBytes(kind, 1);
        var store = new BackendMediaFeatureArtifactStore(state, reserved);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store, source);
        Assert.Equal(reserved, store.UsedBytes);
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        Assert.Equal(reference.Identity.ByteLength, store.UsedBytes);
        string track = Track(state, reference);
        await using (FileStream input = File.OpenRead(track))
            Assert.Equal(reference.Identity.ContentHash.LowerHex, Convert.ToHexStringLower(await SHA256.HashDataAsync(input)));
        var budget = MediaFeatureStoreFixture.ReaderBudget();
        await using (BackendMediaFeatureArtifactStore.ReadLease lease = await store.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), budget, CancellationToken.None))
        {
            var grid = new MediaMomentValues[576];
            await lease.Reader.ReadTemporalGridAsync(new(10, 10, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
            Assert.Equal(32768 / 65535d, grid[0].Alpha);
            Assert.Equal(reference.Identity, lease.Reader.Artifact);
        }
        Assert.Equal(0, budget.LiveBytes);
        Assert.Throws<MediaAbException>(() => store.Publish(prepared));
        store.Prune([]); Assert.Equal(0, store.UsedBytes); Assert.False(File.Exists(track));
    }

    [Fact]
    public async Task EveryScratchSlotIsBoundedAndCannotOpenTwiceOrOutliveItsReservation()
    {
        using TemporaryDirectory temporary = new(); var source = MediaFeatureStoreFixture.Source();
        var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"));
        BackendMediaFeatureArtifactStore.Reservation reservation = store.TryReserve(MediaFeatureStoreFixture.Owner,
            MediaFeatureStoreFixture.Job(), source, 1)!;
        using (reservation.AcquireProducer())
        {
            await using Stream intervals = await reservation.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None);
            await intervals.WriteAsync(new byte[64]);
            MediaAbException exceeded = await Assert.ThrowsAsync<MediaAbException>(async () => await intervals.WriteAsync(new byte[1]));
            Assert.Equal(MediaAbFailure.CapacityExceeded, exceeded.Failure);
            Assert.Throws<MediaAbException>(() => intervals.SetLength(65));
            Assert.Throws<MediaAbException>(() => intervals.Seek(-1, SeekOrigin.Begin));
            await Assert.ThrowsAsync<MediaAbException>(async () => await reservation.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None));
            await Assert.ThrowsAsync<MediaAbException>(async () => await reservation.OpenSectionAsync(MediaTrackSection.Grids, 9217, CancellationToken.None));
            await using Stream grids = await reservation.OpenSectionAsync(MediaTrackSection.Grids, 9216, CancellationToken.None);
            await using Stream prefixes = await reservation.OpenSectionAsync(MediaTrackSection.Prefixes, 0, CancellationToken.None);
            await grids.WriteAsync(new byte[9216]); await prefixes.WriteAsync(ReadOnlyMemory<byte>.Empty);
        }
        await reservation.DisposeAsync(); await reservation.DisposeAsync(); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task ReservationDisposeWaitsForProducerAndInFlightWriteInsteadOfClosingItsHandle()
    {
        using TemporaryDirectory temporary = new();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new BackendMediaFeatureStoreHooks { BeforeStreamWriteAsync = async token => { started.SetResult(); await release.Task.WaitAsync(token); } };
        var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"), hooks: hooks);
        BackendMediaFeatureArtifactStore.Reservation reservation = store.TryReserve(MediaFeatureStoreFixture.Owner,
            MediaFeatureStoreFixture.Job(), MediaFeatureStoreFixture.Source(), 1)!;
        IDisposable producer = reservation.AcquireProducer();
        Stream stream = await reservation.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None);
        Task writing = stream.WriteAsync(new byte[64]).AsTask(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposing = reservation.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted); Assert.True(stream.CanWrite); Assert.True(store.UsedBytes > 0);
        release.SetResult(); await writing; Assert.False(disposing.IsCompleted);
        await stream.DisposeAsync(); producer.Dispose(); await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stream.CanWrite); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task StreamDisposeItselfWaitsForItsInFlightWriteGate()
    {
        using TemporaryDirectory temporary = new(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new BackendMediaFeatureStoreHooks { BeforeStreamWriteAsync = async token => { started.SetResult(); await release.Task.WaitAsync(token); } };
        var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"), hooks: hooks);
        await using BackendMediaFeatureArtifactStore.Reservation reservation = store.TryReserve(MediaFeatureStoreFixture.Owner,
            MediaFeatureStoreFixture.Job(), MediaFeatureStoreFixture.Source(), 1)!;
        using IDisposable producer = reservation.AcquireProducer();
        Stream stream = await reservation.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None);
        Task writing = stream.WriteAsync(new byte[64]).AsTask(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task closing = stream.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        release.SetResult(); await writing; await closing; Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task PublishRequiresProducerDrainAndCanRetryAfterPrematureRejection()
    {
        using TemporaryDirectory temporary = new(); var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"));
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store,
            beforeProducerDrain: candidate => Assert.Throws<MediaAbException>(() => store.Publish(candidate)));
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        Assert.Equal(reference.Identity.ByteLength, store.UsedBytes);
        store.Prune([]); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task ExpiredTrackStaysChargedAndReadableUntilItsFinalLeaseDrains()
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state");
        var store = new BackendMediaFeatureArtifactStore(state);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store);
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        BackendMediaFeatureArtifactStore.ReadLease lease = await store.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None);
        store.Prune([]); Assert.Equal(reference.Identity.ByteLength, store.UsedBytes); Assert.True(File.Exists(Track(state, reference)));
        await Assert.ThrowsAsync<MediaAbException>(async () => await store.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        var grid = new MediaMomentValues[576]; lease.Reader.ReadTemporalGrid(new(0, 0, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
        await lease.DisposeAsync(); await lease.DisposeAsync(); Assert.Equal(0, store.UsedBytes); Assert.False(File.Exists(Track(state, reference)));
    }

    [Fact]
    public async Task ContentHitNeverAuthorizesAnotherOwnerOrReplacesAnExistingArtifactIdentity()
    {
        using TemporaryDirectory temporary = new(); var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"));
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store);
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        await Assert.ThrowsAsync<MediaAbException>(async () => await store.AcquireReadAsync("other-client", reference,
            MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        await Assert.ThrowsAsync<MediaAbException>(async () => await store.AcquireReadAsync("other-client", reference with { OwnerClientId = "other-client" },
            MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        MediaArtifactIdentity identity = reference.Identity;
        var replacement = new MediaArtifactIdentity(new(new string('f', 64)), identity.ByteLength, identity.Format,
            identity.SourceKey, identity.ProfileDigest, identity.SourceDescriptorDigest, identity.PresentationIndexDigest, identity.FrameCount, identity.VideoDurationUs);
        await Assert.ThrowsAsync<MediaAbException>(async () => await store.RecoverAsync(reference with { Identity = replacement },
            MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        Assert.Throws<MediaAbException>(() => store.Prune([reference with { Identity = replacement }]));
        Assert.Equal(identity.ByteLength, store.UsedBytes);
    }

    [Fact]
    public async Task RestartRequiresTrustedLedgerReferenceAndFullCodecValidationBeforeAnyRead()
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state");
        var original = new BackendMediaFeatureArtifactStore(state);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(original);
        BackendMediaFeatureArtifactReference reference = original.Publish(prepared);
        var restarted = new BackendMediaFeatureArtifactStore(state);
        Assert.Equal(reference.Identity.ByteLength, restarted.UsedBytes);
        await Assert.ThrowsAsync<MediaAbException>(async () => await restarted.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        Assert.True(await restarted.RecoverAsync(reference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        await using (BackendMediaFeatureArtifactStore.ReadLease lease = await restarted.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None))
            Assert.Equal(reference, lease.Reference);
        byte[] bytes = File.ReadAllBytes(Track(state, reference)); bytes[^2] ^= 1; File.WriteAllBytes(Track(state, reference), bytes);
        var corruptedRestart = new BackendMediaFeatureArtifactStore(state);
        await Assert.ThrowsAsync<MediaAbException>(async () => await corruptedRestart.RecoverAsync(reference,
            MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        Assert.Equal(reference.Identity.ByteLength, corruptedRestart.UsedBytes);
    }

    [Fact]
    public async Task RestartAfterTrackDeletionTreatsEmptyBundleAsMissingWithoutBlockingHealthyEntries()
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state");
        var original = new BackendMediaFeatureArtifactStore(state);
        await using BackendMediaFeatureArtifactStore.Prepared lost = await MediaFeatureStoreFixture.PrepareAsync(original);
        await using BackendMediaFeatureArtifactStore.Prepared healthy = await MediaFeatureStoreFixture.PrepareAsync(original);
        BackendMediaFeatureArtifactReference lostReference = original.Publish(lost);
        BackendMediaFeatureArtifactReference healthyReference = original.Publish(healthy);
        // Crash window: track unlink completed, bundle directory removal did not.
        File.Delete(Track(state, lostReference));
        var restarted = new BackendMediaFeatureArtifactStore(state);
        Assert.Equal(healthyReference.Identity.ByteLength, restarted.UsedBytes);
        Assert.False(await restarted.RecoverAsync(lostReference, MediaFeatureStoreFixture.Codec(),
            MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        Assert.True(await restarted.RecoverAsync(healthyReference, MediaFeatureStoreFixture.Codec(),
            MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        await Assert.ThrowsAsync<MediaAbException>(async () => await restarted.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            lostReference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        restarted.Prune([healthyReference]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Track(state, lostReference))));
        await using BackendMediaFeatureArtifactStore.ReadLease lease = await restarted.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            healthyReference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None);
        Assert.Equal(healthyReference.Identity, lease.Reader.Artifact);
    }

    [Fact]
    public async Task StartupCountsOrphanCandidateAndRejectsQuotaBeforeNewStorageAdmission()
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state");
        var original = new BackendMediaFeatureArtifactStore(state);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(original);
        long physical = Directory.EnumerateFiles(Path.Combine(state, "media-feature-artifacts", ".staging"), "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var restarted = new BackendMediaFeatureArtifactStore(state, physical);
        Assert.Equal(physical, restarted.UsedBytes);
        Assert.Null(restarted.TryReserve(MediaFeatureStoreFixture.Owner, MediaFeatureStoreFixture.Job(), MediaFeatureStoreFixture.Source(), 1));
        restarted.Prune([]); Assert.Equal(0, restarted.UsedBytes);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(state, "media-feature-artifacts", ".staging")));
    }

    [Theory]
    [InlineData("flush")]
    [InlineData("validation")]
    [InlineData("publish")]
    public async Task FailingBoundaryNeverPublishesAndPreservesPrimaryWhileResidualBytesStayCharged(string stage)
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state"); bool refuseDelete = true;
        var primary = new IOException("controlled boundary failure");
        var hooks = new BackendMediaFeatureStoreHooks
        {
            BeforeCandidateFlush = stage == "flush" ? () => throw primary : null,
            BeforeCandidateValidation = stage == "validation" ? () => throw primary : null,
            BeforePublish = stage == "publish" ? () => throw primary : null,
            BeforeDeleteFile = _ => { if (refuseDelete && stage != "publish") throw new IOException("controlled cleanup failure"); },
        };
        var store = new BackendMediaFeatureArtifactStore(state, hooks: hooks);
        if (stage == "publish")
        {
            BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store);
            Assert.Same(primary, Assert.Throws<IOException>(() => store.Publish(prepared)));
            refuseDelete = false; await prepared.DisposeAsync();
        }
        else
        {
            IOException actual = await Assert.ThrowsAsync<IOException>(async () => await MediaFeatureStoreFixture.PrepareAsync(store));
            Assert.Same(primary, actual); Assert.NotNull(primary.Data["MediaReservationCleanupFailure"]);
            Assert.True(store.UsedBytes > 0); store.Prune([]); Assert.True(store.UsedBytes > 0);
            refuseDelete = false; store.Prune([]);
        }
        Assert.Equal(0, store.UsedBytes);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(state, "media-feature-artifacts"), "media-*"));
    }

    [Fact]
    public void StartupRejectsExtraFilesLinksAndUnboundedIdentitySets()
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("extra");
        _ = new BackendMediaFeatureArtifactStore(state);
        File.WriteAllText(Path.Combine(state, "media-feature-artifacts", "unexpected"), "x");
        Assert.Throws<MediaAbException>(() => new BackendMediaFeatureArtifactStore(state));
        string manyState = temporary.FilePath("many"); _ = new BackendMediaFeatureArtifactStore(manyState);
        string staging = Path.Combine(manyState, "media-feature-artifacts", ".staging");
        for (int i = 0; i <= BackendMediaFeatureArtifactStore.MaximumEntries; i++) Directory.CreateDirectory(Path.Combine(staging, Guid.NewGuid().ToString("N")));
        Assert.Throws<MediaAbException>(() => new BackendMediaFeatureArtifactStore(manyState));
        if (!OperatingSystem.IsWindows())
        {
            string target = temporary.FilePath("target"); Directory.CreateDirectory(target);
            string linked = temporary.FilePath("linked"); Directory.CreateSymbolicLink(linked, target);
            Assert.Throws<MediaAbException>(() => new BackendMediaFeatureArtifactStore(linked));
            Directory.Delete(linked);
        }
    }

    [Fact]
    public async Task CancelledPreparationPreservesTheOriginalTokenAndCleansItsOwnFiles()
    {
        using TemporaryDirectory temporary = new(); using var cancellation = new CancellationTokenSource();
        var hooks = new BackendMediaFeatureStoreHooks { BeforeCandidateValidation = cancellation.Cancel };
        var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"), hooks: hooks);
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MediaFeatureStoreFixture.PrepareAsync(store, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task ReadOnlyFullCodecGateRejectsACompleteButChangedCandidateBeforePublish()
    {
        using TemporaryDirectory temporary = new(); string state = temporary.FilePath("state");
        var hooks = new BackendMediaFeatureStoreHooks
        {
            BeforeCandidateValidation = () =>
            {
                string path = Directory.EnumerateFiles(Path.Combine(state, "media-feature-artifacts", ".staging"), "candidate.mft", SearchOption.AllDirectories).Single();
                byte[] data = File.ReadAllBytes(path); data[^2] ^= 1; File.WriteAllBytes(path, data);
            },
        };
        var store = new BackendMediaFeatureArtifactStore(state, hooks: hooks);
        MediaAbException failure = await Assert.ThrowsAsync<MediaAbException>(async () => await MediaFeatureStoreFixture.PrepareAsync(store));
        Assert.Equal(MediaAbFailure.ArtifactInvalid, failure.Failure); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task FixedTotalQuotaIncludesEveryLiveReservationAndActualCommittedTrack()
    {
        using TemporaryDirectory temporary = new(); MediaFeatureFileSource source = MediaFeatureStoreFixture.Source();
        long size = MediaTrackLayoutV1.GetScratchAndCandidateBytes(source.Kind, 1);
        var store = new BackendMediaFeatureArtifactStore(temporary.FilePath("state"), size);
        await using BackendMediaFeatureArtifactStore.Reservation first = store.TryReserve(MediaFeatureStoreFixture.Owner,
            MediaFeatureStoreFixture.Job(), source, 1)!;
        Assert.Equal(size, store.UsedBytes);
        Assert.Null(store.TryReserve(MediaFeatureStoreFixture.Owner, MediaFeatureStoreFixture.Job(), source, 1));
        await first.DisposeAsync(); Assert.Equal(0, store.UsedBytes);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store, source);
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        Assert.Equal(reference.Identity.ByteLength, store.UsedBytes);
        Assert.Null(store.TryReserve(MediaFeatureStoreFixture.Owner, MediaFeatureStoreFixture.Job(), source, 1));
        store.Prune([reference]); Assert.Equal(reference.Identity.ByteLength, store.UsedBytes);
    }

    private static string Track(string state, BackendMediaFeatureArtifactReference reference) =>
        Path.Combine(state, "media-feature-artifacts", reference.ArtifactId, "track.mft");
}
