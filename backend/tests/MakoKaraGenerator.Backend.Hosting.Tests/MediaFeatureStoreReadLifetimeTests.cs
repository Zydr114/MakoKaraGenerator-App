using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class MediaFeatureStoreReadLifetimeTests
{
    [Fact]
    public async Task RecoveryPrunedDuringFullValidationCannotReportAvailable()
    {
        using TemporaryDirectory temporary = new();
        string state = temporary.FilePath("state");
        var original = new BackendMediaFeatureArtifactStore(state);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(original);
        BackendMediaFeatureArtifactReference reference = original.Publish(prepared);
        var restarted = new BackendMediaFeatureArtifactStore(state);
        var codec = new PausingCodec(pauseValidation: true);
        MediaWorkingSetBudget budget = MediaFeatureStoreFixture.ReaderBudget();
        Task<bool> recovering = restarted.RecoverAsync(reference, codec, budget, CancellationToken.None).AsTask();
        try
        {
            await codec.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            restarted.Prune([]);
            Assert.Equal(reference.Identity.ByteLength, restarted.UsedBytes);
            codec.Release.TrySetResult();
            Assert.False(await recovering.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, restarted.UsedBytes);
            Assert.Equal(0, budget.LiveBytes);
            await Assert.ThrowsAsync<MediaAbException>(async () => await restarted.AcquireReadAsync(
                MediaFeatureStoreFixture.Owner, reference, MediaFeatureStoreFixture.Codec(), budget, CancellationToken.None));
        }
        finally
        {
            codec.Release.TrySetResult();
            await recovering.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LeaseDisposalAndPruneWaitForActualCodecQueryBeforeReleasingFileAndBudget()
    {
        using TemporaryDirectory temporary = new();
        string state = temporary.FilePath("state");
        var store = new BackendMediaFeatureArtifactStore(state);
        await using BackendMediaFeatureArtifactStore.Prepared prepared = await MediaFeatureStoreFixture.PrepareAsync(store);
        BackendMediaFeatureArtifactReference reference = store.Publish(prepared);
        var codec = new PausingCodec();
        MediaWorkingSetBudget budget = MediaFeatureStoreFixture.ReaderBudget();
        await using BackendMediaFeatureArtifactStore.ReadLease lease = await store.AcquireReadAsync(
            MediaFeatureStoreFixture.Owner, reference, codec, budget, CancellationToken.None);
        string track = Path.Combine(state, "media-feature-artifacts", reference.ArtifactId, "track.mft");
        var grid = new MediaMomentValues[MediaTrackV1.Cells];
        Task query = lease.Reader.ReadTemporalGridAsync(new(0, 0, BackgroundPlaybackMode.Loop), grid, CancellationToken.None).AsTask();
        try
        {
            await codec.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Prune([]);
            Task disposing = lease.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.False(query.IsCompleted);
            Assert.True(File.Exists(track));
            Assert.Equal(reference.Identity.ByteLength, store.UsedBytes);
            Assert.True(budget.LiveBytes > 0);
            await Assert.ThrowsAsync<MediaAbException>(async () => await store.AcquireReadAsync(
                MediaFeatureStoreFixture.Owner, reference, MediaFeatureStoreFixture.Codec(), budget, CancellationToken.None));
            codec.Release.TrySetResult();
            await Task.WhenAll(query, disposing).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(32768 / 65535d, grid[0].Alpha);
            Assert.False(File.Exists(track));
            Assert.Equal(0, store.UsedBytes);
            Assert.Equal(0, budget.LiveBytes);
        }
        finally
        {
            codec.Release.TrySetResult();
            await query.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // The production codec still validates every byte and supplies its real reader/gate.
    // Only a stream read is paused deterministically after the full opening validation.
    private sealed class PausingCodec(bool pauseValidation = false) : IMediaFeatureTrackCodec
    {
        private readonly MediaFeatureTrackCodec _inner = MediaFeatureStoreFixture.Codec();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IMediaFeatureTrackBuilder CreateBuilder(MediaFeatureFileSource source, IMediaTrackScratchReservation scratch,
            IMediaWorkingSetBudget builderBudget) => _inner.CreateBuilder(source, scratch, builderBudget);

        public ValueTask<MediaArtifactIdentity> WriteCanonicalAsync(IMediaFeatureTrackWriteSource source, Stream target,
            IMediaWorkingSetBudget codecBudget, CancellationToken cancellationToken) =>
            _inner.WriteCanonicalAsync(source, target, codecBudget, cancellationToken);

        public async ValueTask<IMediaFeatureTrackReader> OpenValidatedAsync(Stream seekableSource, MediaFeatureFileSource source,
            MediaArtifactIdentity expected, IMediaWorkingSetBudget readerBudget, CancellationToken cancellationToken)
        {
            var stream = new PausingStream(seekableSource, Started, Release) { Armed = pauseValidation };
            IMediaFeatureTrackReader reader = await _inner.OpenValidatedAsync(stream, source, expected, readerBudget, cancellationToken);
            stream.Armed = true;
            return reader;
        }
    }

    private sealed class PausingStream(Stream inner, TaskCompletionSource started, TaskCompletionSource release) : Stream
    {
        internal bool Armed { get; set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
