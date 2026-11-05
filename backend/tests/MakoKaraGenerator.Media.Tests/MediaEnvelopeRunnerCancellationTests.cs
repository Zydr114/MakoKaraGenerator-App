using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Real MediaEnvelopeRunner and OwnedNativeEnvelope, fake manager/pipes only; no native process starts.</summary>
public sealed class MediaEnvelopeRunnerCancellationTests
{
    private static readonly EnvelopeCommand Command = new("/internal/gate", "/internal/tool", []);

    [Fact]
    public async Task CallerCancellationCrossesBothLinkedTokensAfterSuccessfulDrain()
    {
        await using var fixture = new Fixture(); using var caller = new CancellationTokenSource();
        CancellationToken observed = default;
        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Runner.RunAsync(
            fixture.Source, fixture.Budget, Command, 1024 * 1024, (_, _, linked) =>
            {
                fixture.Clock.BeginConsumer(); observed = linked; Assert.NotEqual(caller.Token, linked); caller.Cancel();
                fixture.Session.Complete(); linked.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, caller.Token));
        Assert.NotEqual(caller.Token, observed); Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.True(fixture.Session.Unreferenced); Assert.True(fixture.Session.Disposed);
        Assert.Equal(1, fixture.Source.HoldReleases); Assert.Equal(0, fixture.Budget.Live);
        Assert.Empty(fixture.Quarantine.Records); Assert.Equal(0, fixture.Runner.RetainedOperationCount);
    }

    [Fact]
    public async Task IndependentConsumerCancellationPreservesExactExceptionAndToken()
    {
        await using var fixture = new Fixture(); using var independent = new CancellationTokenSource(); independent.Cancel();
        var expected = new OperationCanceledException(independent.Token);
        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Runner.RunAsync(
            fixture.Source, fixture.Budget, Command, 1024 * 1024, (_, _, _) =>
            { fixture.Clock.BeginConsumer(); fixture.Session.Complete(); throw expected; }, CancellationToken.None));
        Assert.Same(expected, actual); Assert.Equal(independent.Token, actual.CancellationToken);
        Assert.Equal(1, fixture.Source.HoldReleases); Assert.Equal(0, fixture.Budget.Live);
        Assert.Empty(fixture.Quarantine.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedSupervisionFailureSurvivesLateCallerCancellationAcrossRunner(bool ownershipLost)
    {
        await using var fixture = new Fixture(advanceClock: true); using var caller = new CancellationTokenSource();
        MediaAbException actual = await Assert.ThrowsAsync<MediaAbException>(() => fixture.Runner.RunAsync(
            fixture.Source, fixture.Budget, Command, 1024 * 1024, async (_, _, linked) =>
            {
                fixture.Session.ReplaceOwnerOnce = ownershipLost; fixture.Clock.BeginConsumer();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, linked); }
                catch (OperationCanceledException) { caller.Cancel(); fixture.Session.Complete(); throw; }
            }, caller.Token));
        Assert.Equal(MediaAbFailure.DecodeFailed, actual.Failure);
        Assert.Equal(ownershipLost ? "native-envelope-owner-changed" : "native-pipe-progress-timeout", actual.Message);
        Assert.True(fixture.Session.Unreferenced); Assert.True(fixture.Session.Disposed);
        Assert.Equal(1, fixture.Source.HoldReleases); Assert.Equal(0, fixture.Budget.Live);
        Assert.Empty(fixture.Quarantine.Records); Assert.Equal(0, fixture.Runner.RetainedOperationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOrSourceDrainFailureOverridesActualCallerCancellation(bool failSourceRelease)
    {
        await using var fixture = new Fixture(); using var caller = new CancellationTokenSource();
        fixture.Source.RefuseHoldRelease = failSourceRelease; fixture.Session.FailUnref = !failSourceRelease;
        MediaAbException actual = await Assert.ThrowsAsync<MediaAbException>(() => fixture.Runner.RunAsync(
            fixture.Source, fixture.Budget, Command, 1024 * 1024, (_, _, linked) =>
            {
                fixture.Clock.BeginConsumer(); caller.Cancel(); fixture.Session.Complete(); linked.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, caller.Token));
        Assert.Equal(MediaAbFailure.DecodeFailed, actual.Failure); Assert.Equal("owned-drain-failed", actual.Message);
        Assert.Equal(0, fixture.Source.HoldReleases); Assert.True(fixture.Budget.Live > 0);
        Assert.Equal(1, fixture.Runner.RetainedOperationCount);
        if (failSourceRelease) Assert.Empty(fixture.Quarantine.Records); else Assert.Single(fixture.Quarantine.Records);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mkg-runner-cancel-" + Guid.NewGuid().ToString("N"));
        internal Source Source { get; }
        internal Budget Budget { get; } = new();
        internal Session Session { get; } = new();
        internal Quarantine Quarantine { get; } = new();
        internal MediaEnvelopeRunner Runner { get; }
        internal FastClock Clock { get; } = new();
        internal Fixture(bool advanceClock = false)
        {
            Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "source.bin");
            byte[] bytes = [10, 20, 30]; File.WriteAllBytes(path, bytes); Source = new(path, bytes);
            // Accelerate only the cases that intentionally trigger a supervisor deadline.
            // Ordinary synchronous cancellation must not race a synthetic two-minute jump.
            Runner = new(new Backend(Session), Quarantine, 1024 * 1024, advanceClock ? Clock : new EnvelopeClock());
        }
        public async ValueTask DisposeAsync()
        {
            // Only fake backing cleanup; these tests intentionally retain some
            // fake failed-terminal charges and never claim they drained.
            await Session.DisposeAsync(); Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Budget : IMediaWorkingSetBudget
    {
        internal long Live;
        public long LimitBytes => MediaAbCapacityV1.MaximumDecodeWorkingBytes;
        public IMediaMemoryReservation Reserve(long bytes, MediaWorkingSetPurpose purpose)
        {
            Assert.True(bytes > 0 && bytes <= LimitBytes - Live); Live += bytes; return new Charge(this, bytes);
        }
        private sealed class Charge(Budget owner, long bytes) : IMediaMemoryReservation
        {
            private int _done; public long Bytes => bytes;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.Live -= bytes; }
        }
    }

    private sealed class Source(string path, byte[] bytes) : IMediaRetainableFileLease
    {
        public MediaFileContent Content { get; } = new(new(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.Length);
        public string TrustedLocalPath => path;
        internal bool RefuseHoldRelease;
        internal int HoldReleases;
        public IAsyncDisposable AcquireHold() => new Hold(this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Hold(Source owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                if (owner.RefuseHoldRelease) throw new IOException("controlled source release failure");
                owner.HoldReleases++; return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Backend(Session session) : IEnvelopeBackend
    {
        public ValueTask<IEnvelopeSession> OpenAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IEnvelopeSession>(session); }
    }

    private sealed class FastClock : IEnvelopeClock
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _milliseconds;
        internal void BeginConsumer() => _started.TrySetResult();
        public long Timestamp => Interlocked.Read(ref _milliseconds);
        public TimeSpan Elapsed(long before) => TimeSpan.FromMilliseconds(Timestamp - before);
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken token)
        { await _started.Task.WaitAsync(token); token.ThrowIfCancellationRequested(); Interlocked.Add(ref _milliseconds, 60_000); await Task.Yield(); token.ThrowIfCancellationRequested(); }
    }

    private sealed class Quarantine : IEnvelopeQuarantine
    {
        internal List<EnvelopeQuarantine> Records { get; } = [];
        public void Retain(EnvelopeQuarantine record) => Records.Add(record);
    }

    private sealed class Session : IEnvelopeSession
    {
        private EnvelopeSnapshot? _snapshot;
        private int _replaceOwnerOnce;
        internal bool ReplaceOwnerOnce { set => Volatile.Write(ref _replaceOwnerOnce, value ? 1 : 0); }
        internal bool FailUnref, Unreferenced, Disposed;
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public Stream Metadata { get; } = new MemoryStream();
        public Stream Diagnostics { get; } = new MemoryStream();
        public long PipeBackingBytes => DecodeOperationBudget.RequiredPipeBackingPerProducer;
        public long ProgressVersion => 0;
        public bool StartAttempted { get; private set; }
        public ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StartAttempted = true;
            _snapshot = new(start.UnitName, "/owned/unit", new string('a', 32), "Mako media envelope " + start.Nonce,
                "/user.slice/" + start.UnitName, 424242, 991, 31, 47, true, true, true, false,
                start.NativeLimitBytes, start.NativeLimitBytes, 0, start.NativeLimitBytes, 0, true, true, false,
                "exec", "control-group", "no", "kill", "active", "success", 1, 0, 0, true, true);
            return ValueTask.CompletedTask;
        }
        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); EnvelopeSnapshot value = _snapshot ?? throw new InvalidOperationException();
            return ValueTask.FromResult(Interlocked.Exchange(ref _replaceOwnerOnce, 0) == 0 ? value : value with { InvocationId = new string('b', 32) });
        }
        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask ReleaseExecGateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken token) { token.ThrowIfCancellationRequested(); Complete(); return ValueTask.CompletedTask; }
        public ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailUnref) throw new IOException("controlled unref failure");
            Unreferenced = true; return ValueTask.CompletedTask;
        }
        internal void Complete()
        {
            EnvelopeSnapshot value = _snapshot ?? throw new InvalidOperationException();
            _snapshot = value with { ActiveState = "inactive", MainPid = 0, MainStartTicks = 0, CgroupPopulated = false };
        }
        public ValueTask DisposeAsync()
        { Disposed = true; Input.Dispose(); Output.Dispose(); Metadata.Dispose(); Diagnostics.Dispose(); return ValueTask.CompletedTask; }
    }
}
