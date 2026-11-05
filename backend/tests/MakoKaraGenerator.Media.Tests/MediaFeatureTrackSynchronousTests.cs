using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaFeatureTrackSynchronousTests
{
    private readonly MediaFeatureTrackCodec _codec = new(new MediaMomentKernel(), new MediaAbValueValidator());

    [Fact]
    public async Task SynchronousQueriesUseOnlyDirectReadsAndMatchTheValidatedAsynchronousPath()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 512);
        var input = new ControlledInput(fixture.Bytes);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None);
        MediaTemporalQuery[] queries = [new(450_000, 400_000, BackgroundPlaybackMode.FreezeLastFrame),
            new(long.MinValue, 2_000_000, BackgroundPlaybackMode.Loop), new(long.MaxValue, 2_000_000, BackgroundPlaybackMode.Loop),
            new(long.MaxValue, 2_000_000, BackgroundPlaybackMode.FreezeLastFrame), new(512_000, 0, BackgroundPlaybackMode.Loop)];
        var expected = new MediaMomentValues[queries.Length][];
        for (int i = 0; i < queries.Length; i++)
        {
            expected[i] = new MediaMomentValues[576];
            await reader.ReadTemporalGridAsync(queries[i], expected[i], CancellationToken.None);
        }
        input.SynchronousOnly = true;
        int[] order = [3, 0, 4, 2, 1, 0, 3];
        foreach (int index in order)
        {
            input.SyncBytes = 0;
            var actual = new MediaMomentValues[576];
            reader.ReadTemporalGrid(queries[index], actual, CancellationToken.None);
            Assert.Equal(expected[index], actual);
            if (index == 0) Assert.InRange(input.SyncBytes, 1, 1_400_000);
        }
        Assert.True(budget.LiveBytes > 0);
        await reader.DisposeAsync();
        Assert.Equal(0, budget.LiveBytes);
        Assert.True(input.Closed);
    }

    [Fact]
    public async Task CancelledPartialDirectReadPreservesTokenAndNextQueryRestartsCleanly()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 130);
        var input = new ControlledInput(fixture.Bytes);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        await using IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None);
        input.SynchronousOnly = true;
        input.MaximumSyncRead = 31;
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        input.BeforeRead = () => { if (++reads == 4) cancellation.Cancel(); };
        var sentinel = new MediaMomentValues(.1, .2, .3, .4, .5, .6, .7);
        var actual = Enumerable.Repeat(sentinel, 576).ToArray();
        OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(() =>
            reader.ReadTemporalGrid(new(130_000, 128_000, BackgroundPlaybackMode.Loop), actual, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.All(actual, cell => Assert.Equal(sentinel, cell));
        Assert.True(budget.LiveBytes > 0);
        Assert.False(input.Closed);
        input.BeforeRead = null;
        reader.ReadTemporalGrid(new(64_000, 0, BackgroundPlaybackMode.FreezeLastFrame), actual, CancellationToken.None);
        Assert.All(actual, cell => Assert.Equal(Mft1IndependentFixture.Q(64, 0) / 65535d, cell.Rp));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reader.ReadTemporalGrid(new(0, 2_000_001, BackgroundPlaybackMode.Loop), actual, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeWaitsForAnInFlightSynchronousReadBeforeReleasingItsStreamAndBudget()
    {
        var fixture = new Mft1IndependentFixture(MediaSourceKind.Video, 65);
        var input = new ControlledInput(fixture.Bytes);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        IMediaFeatureTrackReader reader = await _codec.OpenValidatedAsync(input, fixture.Source, fixture.Identity, budget, CancellationToken.None);
        input.SynchronousOnly = true;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        input.BeforeRead = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release its bounded read.");
        };
        var destination = new MediaMomentValues[576];
        Task query = Task.Run(() => reader.ReadTemporalGrid(new(64_000, 0, BackgroundPlaybackMode.Loop), destination, CancellationToken.None));
        Task? dispose = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            dispose = reader.DisposeAsync().AsTask();
            Assert.False(dispose.IsCompleted);
            Assert.False(input.Closed);
            Assert.True(budget.LiveBytes > 0);
        }
        finally
        {
            release.Set();
            await query;
            if (dispose is not null) await dispose;
            else await reader.DisposeAsync();
        }
        Assert.True(input.Closed);
        Assert.Equal(0, budget.LiveBytes);
        Assert.All(destination, cell => Assert.Equal(Mft1IndependentFixture.Q(64, 0) / 65535d, cell.Rp));
        Assert.Throws<ObjectDisposedException>(() =>
            reader.ReadTemporalGrid(new(0, 0, BackgroundPlaybackMode.Loop), destination, CancellationToken.None));
        await reader.DisposeAsync();
    }

    private sealed class ControlledInput(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal bool SynchronousOnly { get; set; }
        internal int MaximumSyncRead { get; set; } = int.MaxValue;
        internal long SyncBytes { get; set; }
        internal Action? BeforeRead { get; set; }
        internal bool Closed { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            BeforeRead?.Invoke();
            int count = base.Read(buffer[..Math.Min(buffer.Length, MaximumSyncRead)]);
            SyncBytes += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (SynchronousOnly) throw new InvalidOperationException("Synchronous query called asynchronous I/O.");
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
