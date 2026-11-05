using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;
using Xunit;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendMediaFeatureWorkerStageTests
{
    [Theory]
    [InlineData(MediaSourceKind.Still)]
    [InlineData(MediaSourceKind.Video)]
    public async Task RealFilesUseOneQuotaAndFullReadOnlyCodecGateBeforeExistingPublication(MediaSourceKind kind)
    {
        using TemporaryDirectory temp = new();
        var source = MediaFeatureStoreFixture.Source(kind);
        long quota = MediaTrackLayoutV1.GetScratchAndCandidateBytes(kind, 1);
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"), quota);
        await using var work = new BackendMediaFeatureWorkerStageFixture(store, source);
        Assert.Equal(quota, store.UsedBytes);
        Assert.Equal(work.Reservation.JobId, work.Grant.JobId); Assert.Equal(work.Reservation.ArtifactId, work.Grant.ArtifactId);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync();
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.ImportAsync(identity));
        work.Scope.AttestExecutorDrained(); work.Scope.AttestExecutorDrained();
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await work.ImportAsync(identity);
        Assert.Equal(quota, store.UsedBytes);
        Assert.Throws<MediaAbException>(() => work.Grant.OpenLocalWriter());
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.ImportAsync(identity));
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        Assert.Equal(identity.ByteLength, store.UsedBytes);
        await using (BackendMediaFeatureArtifactStore.ReadLease read = await store.AcquireReadAsync(MediaFeatureStoreFixture.Owner,
            reference, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None))
        {
            var grid = new MediaMomentValues[576];
            read.Reader.ReadTemporalGrid(new(500, 0, BackgroundPlaybackMode.Loop), grid, CancellationToken.None);
            Assert.Equal(32768 / 65535d, grid[0].Alpha);
            Assert.Equal(identity, read.Reader.Artifact);
        }
        Assert.Single(Directory.EnumerateFiles(Path.Combine(temp.FilePath("state"), "media-feature-artifacts", reference.ArtifactId)));
        Assert.Throws<MediaAbException>(() => store.Publish(prepared));
        store.Prune([]); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task WorkerAndInProcessProducersAreMutuallyExclusiveEvenAfterProducerScopeEnds()
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var worker = new BackendMediaFeatureWorkerStageFixture(store);
        Assert.Throws<MediaAbException>(() => worker.Reservation.AcquireProducer());
        Assert.Throws<MediaAbException>(() => worker.Reservation.BeginWorkerWrite());
        await Assert.ThrowsAsync<MediaAbException>(async () => await worker.Reservation.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None));
        await using BackendMediaFeatureArtifactStore.Reservation inProcess = store.TryReserve(MediaFeatureStoreFixture.Owner,
            MediaFeatureStoreFixture.Job(), MediaFeatureStoreFixture.Source(), 1)!;
        using (inProcess.AcquireProducer()) Assert.Throws<MediaAbException>(() => inProcess.BeginWorkerWrite());
        Assert.Throws<MediaAbException>(() => inProcess.BeginWorkerWrite());
    }

    [Fact]
    public async Task WrongScopeOrStoreCannotImportAndDoesNotConsumeTheRightScope()
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        var otherStore = new BackendMediaFeatureArtifactStore(temp.FilePath("other"));
        await using var first = new BackendMediaFeatureWorkerStageFixture(store);
        await using var second = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await first.WriteAndDrainFilesAsync(); first.Scope.AttestExecutorDrained();
        await Assert.ThrowsAsync<MediaAbException>(async () => await store.PrepareWorkerCandidateAsync(second.Reservation, first.Scope,
            identity, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        await Assert.ThrowsAsync<MediaAbException>(async () => await otherStore.PrepareWorkerCandidateAsync(first.Reservation, first.Scope,
            identity, MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None));
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await first.ImportAsync(identity);
        Assert.Equal(first.Reservation.JobId, store.Publish(prepared).JobId);
    }

    [Fact]
    public async Task AllFourFilesRejectGrowthBeforeDiskAndKeepCombinedHighWaterAfterShrinkOrFailedWrite()
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        Assert.Throws<MediaAbException>(() => work.Writer.CreateCandidate());
        Assert.Throws<MediaAbException>(() => work.Grant.OpenLocalWriter());
        Stream intervals = await work.Writer.OpenSectionAsync(MediaTrackSection.Intervals, work.Grant.IntervalMaximumBytes, CancellationToken.None);
        Stream grids = await work.Writer.OpenSectionAsync(MediaTrackSection.Grids, work.Grant.GridMaximumBytes, CancellationToken.None);
        Stream prefixes = await work.Writer.OpenSectionAsync(MediaTrackSection.Prefixes, work.Grant.PrefixMaximumBytes, CancellationToken.None);
        BackendMediaFeatureBoundedStream candidate = work.Writer.CreateCandidate();
        (Stream Stream, long Limit)[] files = [(intervals, work.Grant.IntervalMaximumBytes), (grids, work.Grant.GridMaximumBytes),
            (prefixes, work.Grant.PrefixMaximumBytes), (candidate, work.Grant.CandidateMaximumBytes)];
        foreach (var (file, limit) in files)
        {
            file.SetLength(limit); Assert.Equal(limit, file.Length);
            Assert.Throws<MediaAbException>(() => file.SetLength(limit + 1));
            Assert.Throws<MediaAbException>(() => file.Seek(limit + 1, SeekOrigin.Begin));
            file.Position = limit;
            await Assert.ThrowsAsync<MediaAbException>(async () => await file.WriteAsync(new byte[1]));
            Assert.Equal(limit, file.Length);
        }
        Assert.Equal(work.Grant.ReservedByteBudget, work.Writer.AdmittedHighWaterBytes);
        candidate.SetLength(0); Assert.Equal(work.Grant.ReservedByteBudget, work.Writer.AdmittedHighWaterBytes);
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.Writer.OpenSectionAsync(MediaTrackSection.Intervals, 1, CancellationToken.None));
        Assert.Throws<MediaAbException>(() => work.Writer.CreateCandidate());
        await work.DisposeAsync(); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task OversizedRequestedSectionAndUnknownSectionAreRejectedBeforeCreatingFiles()
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.Writer.OpenSectionAsync(MediaTrackSection.Grids,
            work.Grant.GridMaximumBytes + 1, CancellationToken.None));
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.Writer.OpenSectionAsync((MediaTrackSection)99, 1, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(work.Grant.StageDirectory));
    }

    [Fact]
    public async Task ActualWriteGateAndExecutorDrainHoldStageAndChargeDuringConcurrentReservationDispose()
    {
        using TemporaryDirectory temp = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        BackendMediaFeatureArtifactStore.Reservation reservation = store.TryReserve(MediaFeatureStoreFixture.Owner,
            MediaFeatureStoreFixture.Job(), MediaFeatureStoreFixture.Source(), 1)!;
        var scope = reservation.BeginWorkerWrite();
        var writer = scope.Grant.OpenLocalWriter(new BackendMediaFeatureStoreHooks
        { BeforeStreamWriteAsync = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } });
        try
        {
            Stream section = await writer.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None);
            Task writing = section.WriteAsync(new byte[64]).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task closingWriter = writer.DisposeAsync().AsTask();
            Task closingReservation = reservation.DisposeAsync().AsTask();
            Assert.False(closingWriter.IsCompleted); Assert.False(closingReservation.IsCompleted);
            Assert.Throws<MediaAbException>(scope.AttestExecutorDrained);
            Assert.True(Directory.Exists(scope.Grant.StageDirectory)); Assert.True(store.UsedBytes > 0);
            release.TrySetResult(); await writing; await closingWriter.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closingReservation.IsCompleted); Assert.True(store.UsedBytes > 0);
            scope.AttestExecutorDrained(); await closingReservation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, store.UsedBytes); Assert.False(Directory.Exists(scope.Grant.StageDirectory));
        }
        finally
        {
            release.TrySetResult(); await writer.DisposeAsync(); scope.AttestExecutorDrained(); await reservation.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedAdmittedWriteKeepsHighWaterAndCancellationTokenUntilRealDrain()
    {
        using TemporaryDirectory temp = new(); using var cancel = new CancellationTokenSource();
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        var reservation = store.TryReserve(MediaFeatureStoreFixture.Owner, MediaFeatureStoreFixture.Job(), MediaFeatureStoreFixture.Source(), 1)!;
        var scope = reservation.BeginWorkerWrite();
        var writer = scope.Grant.OpenLocalWriter(new BackendMediaFeatureStoreHooks
        { BeforeStreamWriteAsync = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; } });
        try
        {
            Stream section = await writer.OpenSectionAsync(MediaTrackSection.Intervals, 64, CancellationToken.None);
            OperationCanceledException primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await section.WriteAsync(new byte[64], cancel.Token));
            Assert.Equal(cancel.Token, primary.CancellationToken); Assert.Equal(64, writer.AdmittedHighWaterBytes);
            Assert.Equal(0, section.Length); Assert.Equal(reservation.ReservedByteBudget, store.UsedBytes);
            reservation.RecordFailure(primary);
        }
        finally { await writer.DisposeAsync(); scope.AttestExecutorDrained(); await reservation.DisposeAsync(); }
        Assert.Equal(0, store.UsedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCodecRejectsChangedCandidateEvenWhenWorkerRecomputesItsExternalHash(bool recomputeHash)
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        byte[] bytes = File.ReadAllBytes(work.Candidate); bytes[^1] = 1; File.WriteAllBytes(work.Candidate, bytes); // Canonical cell padding.
        if (recomputeHash) identity = BackendMediaFeatureWorkerStageFixture.WithHash(identity, bytes);
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.ImportAsync(identity));
        await work.DisposeAsync(); Assert.Equal(0, store.UsedBytes);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temp.FilePath("state"), "media-feature-artifacts"), "media-*"));
    }

    [Theory]
    [InlineData("intervals.tmp")]
    [InlineData("grids.tmp")]
    [InlineData("prefixes.tmp")]
    [InlineData("candidate.mft")]
    public async Task ImportRejectsPostDrainOvergrowthAgainstEachOriginalGrantCeiling(string name)
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        string path = Path.Combine(work.Grant.StageDirectory, name);
        using (FileStream file = new(path, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length + 1);
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.ImportAsync(identity));
        await work.DisposeAsync(); Assert.Equal(0, store.UsedBytes);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("link")]
    public async Task WorkerFileSetRejectsExtraMissingDirectoriesAndLinksWithoutReadingOutsideTheStage(string shape)
    {
        if (shape == "link" && OperatingSystem.IsWindows()) return;
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        string outside = temp.FilePath("outside"); File.WriteAllText(outside, "unchanged");
        string bad = Path.Combine(work.Grant.StageDirectory, shape == "extra" ? "unexpected" : "prefixes.tmp");
        if (shape != "extra") File.Delete(bad);
        switch (shape)
        {
            case "extra": File.WriteAllText(bad, "x"); break;
            case "directory": Directory.CreateDirectory(bad); break;
            case "link": File.CreateSymbolicLink(bad, outside); break;
        }
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.ImportAsync(identity));
        await work.DisposeAsync(); Assert.Equal("unchanged", File.ReadAllText(outside));
        if (shape != "missing")
        {
            Assert.True(store.UsedBytes > 0); // Rejected unknown/link/directory content remains conservatively charged.
            if (shape == "directory") Directory.Delete(bad); else File.Delete(bad);
            store.Prune([]);
        }
        Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task CancelledHostValidationPreservesTokenAndCleansCandidateAfterExecutorDrain()
    {
        using TemporaryDirectory temp = new(); using var cancel = new CancellationTokenSource();
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"), hooks: new() { BeforeCandidateValidation = cancel.Cancel });
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        OperationCanceledException primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work.ImportAsync(identity, cancel.Token));
        Assert.Equal(cancel.Token, primary.CancellationToken);
        await work.DisposeAsync(); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task FailedValidationPreservesPrimaryAndKeepsResidualQuotaUntilDeletionSucceeds()
    {
        using TemporaryDirectory temp = new(); bool denyDelete = true;
        var primary = new IOException("controlled validation failure");
        var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"), hooks: new()
        {
            BeforeCandidateValidation = () => throw primary,
            BeforeDeleteFile = _ => { if (denyDelete) throw new IOException("controlled cleanup failure"); },
        });
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await work.ImportAsync(identity)));
        await work.DisposeAsync(); Assert.NotNull(primary.Data["MediaReservationCleanupFailure"]);
        Assert.True(store.UsedBytes > 0); store.Prune([]); Assert.True(store.UsedBytes > 0);
        denyDelete = false; store.Prune([]); Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task ReservationClosingRejectsImportAndWaitsForExecutorAttestationBeforeDeletingFiles()
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync();
        Task closing = work.Reservation.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        await Assert.ThrowsAsync<MediaAbException>(async () => await work.ImportAsync(identity));
        Assert.True(File.Exists(work.Candidate)); Assert.True(store.UsedBytes > 0);
        work.Scope.AttestExecutorDrained(); await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task ReservationDisposeCannotRemoveFilesUnderAnInFlightFullCodecValidation()
    {
        using TemporaryDirectory temp = new(); var store = new BackendMediaFeatureArtifactStore(temp.FilePath("state"));
        await using var work = new BackendMediaFeatureWorkerStageFixture(store);
        MediaArtifactIdentity identity = await work.WriteAndDrainFilesAsync(); work.Scope.AttestExecutorDrained();
        var codec = new GatedValidationCodec();
        Task<BackendMediaFeatureArtifactStore.Prepared> preparing = store.PrepareWorkerCandidateAsync(work.Reservation, work.Scope,
            identity, codec, MediaFeatureStoreFixture.ReaderBudget(), CancellationToken.None).AsTask();
        try
        {
            await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task closing = work.Reservation.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted); Assert.True(File.Exists(work.Candidate)); Assert.True(store.UsedBytes > 0);
            codec.Release.TrySetResult();
            BackendMediaFeatureArtifactStore.Prepared prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(5));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, store.UsedBytes); Assert.True(codec.SawReadOnly);
            Assert.Throws<MediaAbException>(() => store.Publish(prepared));
        }
        finally { codec.Release.TrySetResult(); await preparing; }
    }

    /// <summary>Only delays the lifetime boundary; acceptance and bytes are still validated by the real codec.</summary>
    private sealed class GatedValidationCodec : IMediaFeatureTrackCodec
    {
        private readonly MediaFeatureTrackCodec _inner = MediaFeatureStoreFixture.Codec();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SawReadOnly { get; private set; }
        public IMediaFeatureTrackBuilder CreateBuilder(MediaFeatureFileSource source, IMediaTrackScratchReservation scratch,
            IMediaWorkingSetBudget budget) => _inner.CreateBuilder(source, scratch, budget);
        public ValueTask<MediaArtifactIdentity> WriteCanonicalAsync(IMediaFeatureTrackWriteSource source, Stream target,
            IMediaWorkingSetBudget budget, CancellationToken token) => _inner.WriteCanonicalAsync(source, target, budget, token);
        public async ValueTask<IMediaFeatureTrackReader> OpenValidatedAsync(Stream input, MediaFeatureFileSource source,
            MediaArtifactIdentity identity, IMediaWorkingSetBudget budget, CancellationToken token)
        {
            SawReadOnly = input.CanRead && input.CanSeek && !input.CanWrite;
            Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return await _inner.OpenValidatedAsync(input, source, identity, budget, token);
        }
    }
}
