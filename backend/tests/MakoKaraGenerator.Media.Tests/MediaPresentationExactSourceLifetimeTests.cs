using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;
using static MakoKaraGenerator.Media.Tests.MediaPresentationDecodePumpTests;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Actual decoder, exact sink, MOV authority, wire, converter, borrow and owner ledgers;
/// only native execution is replaced by controlled in-memory pipes and a runner drain gate.</summary>
public sealed class MediaPresentationExactSourceLifetimeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposalClosesAdmissionAndWaitsForBorrowThenWholeDecodeDrainWithOneSharedTask()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IMediaPresentationFrameSource reader = await fixture.OpenAsync();
        int readerHold = fixture.Source.LastHold;
        var enteredBorrow = Signal(); var finishBorrow = Signal();
        fixture.Runner.PauseDrain = true;
        int runsBefore = fixture.Runner.Calls;
        Task read = reader.ReadFrameAsync(new(1), async (frame, token) =>
        {
            Assert.Equal(1, frame.Observation.Id.Index);
            Assert.Equal(4, frame.LinearPremulRgba.Length);
            float pixel = frame.LinearPremulRgba.Span[0];
            enteredBorrow.TrySetResult();
            await finishBorrow.Task.WaitAsync(token);
            Assert.Equal(pixel, frame.LinearPremulRgba.Span[0]);
        }, CancellationToken.None).AsTask();
        Task? dispose = null;
        try
        {
            await enteredBorrow.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, fixture.Runner.LastOperation!.ActiveBorrows);
            MediaAbException parallel = await Assert.ThrowsAsync<MediaAbException>(() =>
                reader.ReadFrameAsync(new(0), (_, _) => ValueTask.CompletedTask, default).AsTask());
            Assert.Equal(MediaAbFailure.PresentationInvalid, parallel.Failure);
            Assert.Equal(runsBefore + 1, fixture.Runner.Calls);

            dispose = reader.DisposeAsync().AsTask();
            Task repeated = reader.DisposeAsync().AsTask();
            Assert.Same(dispose, repeated);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                reader.ReadFrameAsync(new(2), (_, _) => ValueTask.CompletedTask, default).AsTask());
            Assert.Equal(runsBefore + 1, fixture.Runner.Calls);

            finishBorrow.TrySetResult();
            await fixture.Runner.DrainEntered.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, fixture.Runner.LastOperation.ActiveBorrows);
            Assert.Equal(0, fixture.Runner.LastOperation.LiveManagedBytes);
            Assert.False(read.IsCompleted);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
            Assert.True(fixture.Budget.LiveBytes > 0);

            fixture.Runner.FinishDrain.TrySetResult();
            await read.WaitAsync(TestTimeout);
            await dispose.WaitAsync(TestTimeout);
            Assert.Same(dispose, reader.DisposeAsync().AsTask());
            Assert.Equal(1, fixture.Source.ReleaseCalls(readerHold));
            Assert.Equal(0, fixture.Source.LiveHolds);
            Assert.Equal(0, fixture.Budget.LiveBytes);
        }
        finally
        {
            finishBorrow.TrySetResult(); fixture.Runner.FinishDrain.TrySetResult();
            await IgnoreFailureAsync(read);
            await IgnoreFailureAsync(dispose ?? reader.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailureOrCancellationKeepsReadFailureAndStillDrainsOneReaderHold(bool cancel)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IMediaPresentationFrameSource reader = await fixture.OpenAsync();
        int readerHold = fixture.Source.LastHold;
        using var caller = new CancellationTokenSource();
        var enteredBorrow = Signal(); var finishBorrow = Signal();
        var callbackFailure = new IOException("controlled exact callback failure");
        fixture.Runner.PauseDrain = true;
        Task read = reader.ReadFrameAsync(new(0), async (_, token) =>
        {
            enteredBorrow.TrySetResult();
            await finishBorrow.Task;
            if (cancel) token.ThrowIfCancellationRequested();
            throw callbackFailure;
        }, caller.Token).AsTask();
        Task? dispose = null;
        try
        {
            await enteredBorrow.Task.WaitAsync(TestTimeout);
            dispose = reader.DisposeAsync().AsTask();
            Assert.False(dispose.IsCompleted);
            if (cancel) caller.Cancel();
            finishBorrow.TrySetResult();
            await fixture.Runner.DrainEntered.Task.WaitAsync(TestTimeout);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, fixture.Runner.LastOperation!.ActiveBorrows);
            Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
            fixture.Runner.FinishDrain.TrySetResult();
            if (cancel)
            {
                OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
                Assert.Equal(caller.Token, actual.CancellationToken);
            }
            else Assert.Same(callbackFailure, await Assert.ThrowsAsync<IOException>(() => read));
            await dispose.WaitAsync(TestTimeout);
            Assert.Same(dispose, reader.DisposeAsync().AsTask());
            Assert.Equal(1, fixture.Source.ReleaseCalls(readerHold));
            Assert.Equal(0, fixture.Source.LiveHolds);
            Assert.Equal(0, fixture.Budget.LiveBytes);
        }
        finally
        {
            finishBorrow.TrySetResult(); fixture.Runner.FinishDrain.TrySetResult();
            await IgnoreFailureAsync(read);
            await IgnoreFailureAsync(dispose ?? reader.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDisposalSharesAsynchronousFinalHoldReleaseIncludingItsFailure(bool failRelease)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IMediaPresentationFrameSource reader = await fixture.OpenAsync();
        int readerHold = fixture.Source.LastHold;
        var enteredRelease = Signal(); var finishRelease = Signal();
        var releaseFailure = new IOException("controlled reader hold release failure");
        fixture.Source.OnRelease = async id =>
        {
            Assert.Equal(readerHold, id);
            enteredRelease.TrySetResult();
            await finishRelease.Task;
            if (failRelease) throw releaseFailure;
        };
        Task first = reader.DisposeAsync().AsTask();
        try
        {
            await enteredRelease.Task.WaitAsync(TestTimeout);
            Task second = reader.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.Equal(1, fixture.Source.ReleaseCalls(readerHold));
            int runs = fixture.Runner.Calls;
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                reader.ReadFrameAsync(new(0), (_, _) => ValueTask.CompletedTask, default).AsTask());
            Assert.Equal(runs, fixture.Runner.Calls);
            finishRelease.TrySetResult();
            if (failRelease)
            {
                Assert.Same(releaseFailure, await Assert.ThrowsAsync<IOException>(() => first));
                Assert.Same(releaseFailure, await Assert.ThrowsAsync<IOException>(() => second));
            }
            else await first.WaitAsync(TestTimeout);
            Assert.Same(first, reader.DisposeAsync().AsTask());
            Assert.Equal(1, fixture.Source.ReleaseCalls(readerHold));
        }
        finally { finishRelease.TrySetResult(); await IgnoreFailureAsync(first); }
    }

    [Fact]
    public async Task AdmissionRaceAlwaysRejectsAfterStopOrRetainsHoldUntilActualAdmittedReadCompletes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        // This is stress coverage of the actual public entrypoints, in addition
        // to the deterministic borrow/drain cases above; no claim of scheduling
        // every machine interleaving is made.
        for (int attempt = 0; attempt < 24; attempt++)
        {
            IMediaPresentationFrameSource reader = await fixture.OpenAsync();
            int readerHold = fixture.Source.LastHold;
            var start = Signal(); var finishBorrow = Signal();
            Task read = Task.Run(async () =>
            {
                await start.Task;
                await reader.ReadFrameAsync(new(0), async (_, _) =>
                {
                    Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
                    await finishBorrow.Task;
                    Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
                }, default);
            });
            Task dispose = Task.Run(async () => { await start.Task; await reader.DisposeAsync(); });
            start.TrySetResult();
            finishBorrow.TrySetResult();
            try { await read.WaitAsync(TestTimeout); }
            catch (ObjectDisposedException) { }
            await dispose.WaitAsync(TestTimeout);
            Assert.Equal(1, fixture.Source.ReleaseCalls(readerHold));
            Assert.Equal(0, fixture.Source.LiveHolds);
        }
        Assert.Equal(0, fixture.Budget.LiveBytes);
    }

    [Fact]
    public async Task FailedReadCanBeFollowedByAnotherCompleteExactDecodeBeforeDisposal()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IMediaPresentationFrameSource reader = await fixture.OpenAsync();
        int readerHold = fixture.Source.LastHold;
        int callsBefore = fixture.Runner.Calls;
        var expected = new IOException("controlled first read failure");
        try
        {
            Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() =>
                reader.ReadFrameAsync(new(0), (_, _) => throw expected, default).AsTask()));
            Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
            int callbacks = 0;
            await reader.ReadFrameAsync(new(2), (frame, _) =>
            {
                Assert.Equal(2, frame.Observation.Id.Index);
                callbacks++;
                return ValueTask.CompletedTask;
            }, default);
            Assert.Equal(1, callbacks);
            Assert.Equal(callsBefore + 2, fixture.Runner.Calls);
            Assert.Equal(0, fixture.Source.ReleaseCalls(readerHold));
            Assert.Equal(1, fixture.Source.LiveHolds);
        }
        finally { await reader.DisposeAsync(); }
        Assert.Equal(1, fixture.Source.ReleaseCalls(readerHold));
        Assert.Equal(0, fixture.Source.LiveHolds);
        Assert.Equal(0, fixture.Budget.LiveBytes);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task IgnoreFailureAsync(Task task)
    { try { await task.WaitAsync(TestTimeout); } catch { } }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mkg-exact-lifetime-" + Guid.NewGuid().ToString("N"));
        internal TrackedSource Source { get; }
        internal MediaWorkingSetBudget Budget { get; } = new(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
        internal ControlledRunner Runner { get; }
        internal MediaPresentationDecoder Decoder { get; }
        private IMediaPresentationIndex? _index;
        private Fixture()
        {
            Directory.CreateDirectory(_directory);
            byte[] movie = Movie(out long offset, composition: true);
            byte[] gateBytes = [1, 2, 3], helperBytes = [4, 5, 6];
            string gatePath = Path.Combine(_directory, "gate.bin"), helperPath = Path.Combine(_directory, "helper.bin"), sourcePath = Path.Combine(_directory, "movie.mov");
            File.WriteAllBytes(gatePath, gateBytes); File.WriteAllBytes(helperPath, helperBytes); File.WriteAllBytes(sourcePath, movie);
            MediaLoadedToolFile gate = new(gatePath, new("gate", gateBytes.Length, Hash(gateBytes)));
            MediaLoadedToolFile helper = new(helperPath, new("helper", helperBytes.Length, Hash(helperBytes)));
            var deployment = new MediaPresentationDeployment(gatePath, helperPath, [gate, helper], []);
            MediaNativePresentationMetadata first = Metadata(0, 0, offset, 0, Hash(movie.AsSpan((int)offset, 3)));
            MediaFeatureFileSource accepted = MediaPresentationInterpretation.Create(new(Hash(movie), movie.Length),
                new(MediaSourceKind.Video, 0, null), first, null, deployment.Tool);
            Source = new(sourcePath, accepted);
            Runner = new(() => PipeSession.Create(movie, offset, helper));
            Decoder = new(deployment, Runner);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                var collect = new IndexSink();
                PresentationIndexSummary summary = await fixture.Decoder.DecodeAsync(fixture.Source, collect, fixture.Budget, default);
                fixture._index = new ExactIndex(summary, collect.Intervals.ToArray());
                Assert.Equal(3, summary.FrameCount);
                Assert.Equal(0, fixture.Source.LiveHolds);
                Assert.Equal(0, fixture.Budget.LiveBytes);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal ValueTask<IMediaPresentationFrameSource> OpenAsync() => Decoder.OpenIndexedAsync(Source, _index!, Budget, default);
        public ValueTask DisposeAsync()
        {
            Runner.FinishDrain.TrySetResult();
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IndexSink : IMediaPresentationSink
    {
        internal List<PresentationInterval> Intervals { get; } = [];
        public ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken token)
        { Intervals.Add(interval); return ValueTask.CompletedTask; }
    }
    private sealed class ExactIndex(PresentationIndexSummary summary, PresentationInterval[] intervals) : IMediaPresentationIndex
    {
        public PresentationIndexSummary Summary => summary;
        public ValueTask<PresentationInterval> ReadIntervalAsync(PresentationFrameId id, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(intervals[checked((int)id.Index)]); }
        public ValueTask<PresentationFrameId> SelectAsync(long sourceTimeUs, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class TrackedSource(string path, MediaFeatureFileSource accepted) : IMediaSourceLease, IMediaRetainableFileLease
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, int> _releaseCalls = [];
        private int _nextHold, _live;
        internal Func<int, Task>? OnRelease;
        public MediaFeatureFileSource Source => accepted;
        public MediaFileContent Content => accepted.Content;
        public string TrustedLocalPath => path;
        internal int LastHold { get { lock (_gate) return _nextHold; } }
        internal int LiveHolds { get { lock (_gate) return _live; } }
        internal int ReleaseCalls(int id) { lock (_gate) return _releaseCalls[id]; }
        public IAsyncDisposable AcquireHold()
        {
            lock (_gate) { int id = ++_nextHold; _live++; _releaseCalls.Add(id, 0); return new Hold(this, id); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private async Task ReleaseAsync(int id)
        {
            if (OnRelease is not null) await OnRelease(id);
            lock (_gate) _live--;
        }
        private sealed class Hold(TrackedSource owner, int id) : IAsyncDisposable
        {
            private readonly object _gate = new();
            private Task? _release;
            public ValueTask DisposeAsync()
            {
                lock (owner._gate) owner._releaseCalls[id]++;
                lock (_gate) return new(_release ??= owner.ReleaseAsync(id));
            }
        }
    }

    private sealed class ControlledRunner(Func<PipeSession> createSession) : IMediaPresentationNativeRunner
    {
        internal bool PauseDrain;
        internal TaskCompletionSource DrainEntered { get; } = Signal();
        internal TaskCompletionSource FinishDrain { get; } = Signal();
        internal DecodeOperationBudget? LastOperation;
        internal int Calls;
        public long NativeLimitBytes => 1024 * 1024;
        public async Task RunAsync(IMediaRetainableFileLease source, IMediaWorkingSetBudget ownerBudget,
            EnvelopeCommand command, long managedPeakBytes,
            Func<IEnvelopeSession, DecodeOperationBudget, CancellationToken, Task> consume, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            IMediaMemoryReservation ownerCharge = ownerBudget.Reserve(checked(managedPeakBytes + NativeLimitBytes), MediaWorkingSetPurpose.DecoderNative);
            var sourceHold = source.AcquireHold();
            var operation = new DecodeOperationBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes, managedPeakBytes,
                [NativeLimitBytes], 2, new SourceAndCharge(sourceHold, ownerCharge));
            LastOperation = operation;
            await using PipeSession session = createSession();
            Exception? failure = null;
            try { await consume(session, operation, cancellationToken); }
            catch (Exception error) { failure = error; }
            // The callback is joined, but the runner has not returned or released
            // its source hold. Disposal must wait beyond this point as well.
            if (PauseDrain) { DrainEntered.TrySetResult(); await FinishDrain.Task; }
            await operation.ReleaseSourceAsync();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private sealed class SourceAndCharge(IAsyncDisposable sourceHold, IMediaMemoryReservation charge) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync() { await sourceHold.DisposeAsync(); charge.Dispose(); }
        }
    }

    private sealed class PipeSession(byte[] metadata, byte[] pixels) : IEnvelopeSession
    {
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream(pixels);
        public Stream Metadata { get; } = new MemoryStream(metadata);
        public Stream Diagnostics => Stream.Null;
        public long PipeBackingBytes => DecodeOperationBudget.RequiredPipeBackingPerProducer;
        public long ProgressVersion => 0;
        public bool StartAttempted => false;
        internal static PipeSession Create(byte[] movie, long offset, MediaLoadedToolFile helper)
        {
            using var metadata = new MemoryStream();
            for (int ordinal = 0; ordinal < 3; ordinal++)
            {
                int packet = ordinal switch { 0 => 0, 1 => 2, _ => 1 };
                Loaded(metadata, helper); Record(metadata, 5, []);
                Record(metadata, 2, FrameBytes(MediaPresentationDecodePumpTests.Metadata(ordinal, packet, offset + 3 * packet, ordinal,
                    Hash(movie.AsSpan((int)offset + 3 * packet, 3)))));
            }
            Loaded(metadata, helper); Record(metadata, 5, []);
            byte[] complete = new byte[24];
            BinaryPrimitives.WriteInt64LittleEndian(complete, 3);
            BinaryPrimitives.WriteInt64LittleEndian(complete.AsSpan(8), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(complete.AsSpan(16), 1);
            Record(metadata, 3, complete);
            return new(metadata.ToArray(), [10, 20, 30, 70, 80, 90, 40, 50, 60]);
        }
        public ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask ReleaseExecGateAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken token) => throw new NotSupportedException();
        public ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        { Input.Dispose(); Output.Dispose(); Metadata.Dispose(); return ValueTask.CompletedTask; }
    }
}
