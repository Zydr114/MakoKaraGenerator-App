using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit;

namespace MakoKaraGenerator.Processes.Tests;

/// <summary>Pure fake-backend tests. No manager, cgroup, native helper or process is accessed.</summary>
public sealed class OwnedNativeEnvelopeTests
{
    private static readonly EnvelopeCommand Command = new("/internal/gate", "/internal/tool", []);
    private static readonly EnvelopeExecutionPolicy Policy = new(TimeSpan.FromSeconds(1), TimeSpan.FromDays(1),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));

    /// <summary>A concurrent duplicate cannot drain or dispose the first run's live native/source ownership.</summary>
    [Fact]
    public async Task ConcurrentDuplicateLeaseRunIsRejectedWithoutTouchingFirstRun()
    {
        await using var fixture = new Fixture();
        DecodeOperationBudget.NativeLease lease = fixture.Budget.BeginNative(0);
        var entered = NewSignal();
        var finish = NewSignal();
        Task<EnvelopeCompletion> first = fixture.Owner.RunAsync(lease, Command, async (_, _) =>
        {
            entered.TrySetResult();
            await finish.Task;
            fixture.Session.Complete();
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        long liveBeforeDuplicate = fixture.Budget.LiveManagedBytes;
        EnvelopeException duplicate = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            lease, Command, (_, _) => throw new InvalidOperationException("duplicate consumer must not run"), CancellationToken.None));
        Assert.Equal("native-partition-already-run", duplicate.SafeCode);
        Assert.Equal(1, fixture.Backend.OpenCount);
        Assert.False(first.IsCompleted);
        Assert.False(fixture.Session.Disposed);
        Assert.False(fixture.Session.Unreferenced);
        Assert.Equal(0, fixture.Session.Kills);
        Assert.Equal(liveBeforeDuplicate, fixture.Budget.LiveManagedBytes);
        await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Budget.ReleaseSourceAsync().AsTask());
        Assert.Equal(0, fixture.Source.Releases);
        finish.TrySetResult();
        EnvelopeCompletion completed = await first;
        Assert.Equal("success", completed.Result);
        Assert.True(fixture.Session.Disposed);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
        Assert.Equal(1, fixture.Source.Releases);
    }

    /// <summary>A completed lease remains consumed and cannot start a second native owner after source release.</summary>
    [Fact]
    public async Task SuccessfullyCompletedLeaseCannotRunAgain()
    {
        await using var fixture = new Fixture();
        DecodeOperationBudget.NativeLease lease = fixture.Budget.BeginNative(0);
        await fixture.Owner.RunAsync(lease, Command, (_, _) =>
        { fixture.Session.Complete(); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, fixture.Budget.LiveManagedBytes);
        Assert.Equal(0, fixture.Source.Releases);
        EnvelopeException duplicateBeforeRelease = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            lease, Command, (_, _) => throw new InvalidOperationException("duplicate consumer must not run"), CancellationToken.None));
        Assert.Equal("native-partition-already-run", duplicateBeforeRelease.SafeCode);
        await fixture.Budget.ReleaseSourceAsync();
        EnvelopeException duplicateAfterRelease = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            lease, Command, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("native-partition-already-run", duplicateAfterRelease.SafeCode);
        Assert.Equal(1, fixture.Backend.OpenCount);
        Assert.Equal(1, fixture.Source.Releases);
        Assert.Equal(0, fixture.Budget.LiveManagedBytes);
        Assert.Equal(0, fixture.Session.Kills);
        Assert.Empty(fixture.Quarantine.Records);
    }

    [Fact]
    /// <summary>Checks GateIsReleasedOnlyAfterExactManagerAndKernelControls.</summary>
    public async Task GateIsReleasedOnlyAfterExactManagerAndKernelControls()
    {
        await using var fixture = new Fixture();
        EnvelopeCompletion result = await fixture.Owner.RunAsync(fixture.Budget.BeginNative(0), Command, (s, _) =>
        {
            Assert.True(fixture.Session.GateReleased);
            Assert.Equal(0, fixture.Session.Kills);
            fixture.Session.Complete();
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal("success", result.Result);
        Assert.Equal(0, fixture.Session.Kills);
        Assert.True(fixture.Session.Unreferenced);
        Assert.True(fixture.Session.Disposed);
        await fixture.Budget.ReleaseSourceAsync();
        Assert.Equal(1, fixture.Source.Releases);
        Assert.Empty(fixture.Quarantine.Records);
    }

    [Fact]
    public async Task ActivatingManagerChildIsNotAcceptedAsExecedGate()
    {
        await using var fixture = new Fixture();
        int observations = 0;
        fixture.Session.Transform = value => ++observations < 3 ? value with { ActiveState = "activating" } : value;
        await fixture.Owner.RunAsync(fixture.Budget.BeginNative(0), Command, (_, _) =>
        {
            Assert.True(observations >= 3);
            fixture.Session.Complete();
            return Task.CompletedTask;
        }, CancellationToken.None);
        await fixture.Budget.ReleaseSourceAsync();
        Assert.Equal(1, fixture.Source.Releases);
    }

    [Fact]
    /// <summary>Checks UnsupportedKernelControlDoesNotReleaseGateButStillDrainsHeldOwner.</summary>
    public async Task UnsupportedKernelControlDoesNotReleaseGateButStillDrainsHeldOwner()
    {
        await using var fixture = new Fixture();
        fixture.Session.Transform = value => value with { KernelMemoryMax = value.MemoryMax + 4096 };
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) => throw new InvalidOperationException("must not consume"), CancellationToken.None));
        Assert.Equal(EnvelopeFailure.CapacityUnsupported, error.Failure);
        Assert.False(fixture.Session.GateReleased);
        Assert.Equal(1, fixture.Session.Kills);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
    }

    [Fact]
    /// <summary>Checks PipeCapacityMustEqualVerifiedFour4096BytePipes.</summary>
    public async Task PipeCapacityMustEqualVerifiedFour4096BytePipes()
    {
        await using var fixture = new Fixture();
        fixture.Session.Backing = 32768;
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(EnvelopeFailure.CapacityUnsupported, error.Failure);
        Assert.False(fixture.Session.StartAttempted);
        Assert.True(fixture.Session.Disposed);
        await fixture.Budget.ReleaseSourceAsync();
    }

    [Fact]
    /// <summary>Checks NaturalExitGetsFiniteGraceInsteadOfImmediateKill.</summary>
    public async Task NaturalExitGetsFiniteGraceInsteadOfImmediateKill()
    {
        await using var fixture = new Fixture();
        fixture.Session.CompleteAfterObservations = 3;
        await fixture.Owner.RunAsync(fixture.Budget.BeginNative(0), Command, (_, _) =>
        { fixture.Session.ConsumerDone = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, fixture.Session.Kills);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task ExitedPidBeforeManagerTerminalStateRetainsPinnedOwner(int exitStatus)
    {
        await using var fixture = new Fixture();
        int transitionalObservations = 0;
        Task<EnvelopeCompletion> run = fixture.Owner.RunAsync(fixture.Budget.BeginNative(0), Command, (_, _) =>
        {
            fixture.Session.Transform = snapshot =>
            {
                if (Interlocked.Increment(ref transitionalObservations) == 1)
                    return snapshot with { MainPid = 0, MainStartTicks = 0, MainInControlGroup = false };
                fixture.Session.Transform = null;
                fixture.Session.Complete(exitStatus == 0 ? "success" : "exit-code", exitStatus);
                return snapshot with { MainPid = 0, MainStartTicks = 0, MainInControlGroup = false,
                    ActiveState = exitStatus == 0 ? "inactive" : "failed", CgroupPopulated = false,
                    Result = exitStatus == 0 ? "success" : "exit-code", ExecMainStatus = exitStatus };
            };
            return Task.CompletedTask;
        }, CancellationToken.None);
        if (exitStatus == 0) Assert.Equal(0, (await run).ExitCode);
        else
        {
            EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => run);
            Assert.Equal(EnvelopeFailure.DecodeFailed, error.Failure);
            Assert.Equal("native-decoder-unsuccessful", error.SafeCode);
        }
        Assert.Equal(2, transitionalObservations);
        Assert.Equal(0, fixture.Session.Kills);
        Assert.True(fixture.Session.Unreferenced); Assert.True(fixture.Session.Disposed);
        Assert.Empty(fixture.Quarantine.Records);
        await fixture.Budget.ReleaseSourceAsync(); Assert.Equal(1, fixture.Source.Releases);
    }

    [Fact]
    public async Task ZeroMainPidDoesNotCountAsTerminalOrDrained()
    {
        await using var fixture = new Fixture();
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) =>
            {
                fixture.Session.Transform = snapshot => snapshot.Terminal ? snapshot
                    : snapshot with { MainPid = 0, MainStartTicks = 0, MainInControlGroup = false };
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.Equal(EnvelopeFailure.DecodeFailed, error.Failure);
        Assert.Equal("native-natural-exit-timeout", error.SafeCode);
        Assert.Equal(1, fixture.Session.Kills);
        Assert.True(fixture.Session.Unreferenced); Assert.True(fixture.Session.Disposed);
        Assert.Empty(fixture.Quarantine.Records);
        await fixture.Budget.ReleaseSourceAsync(); Assert.Equal(1, fixture.Source.Releases);
    }

    [Fact]
    /// <summary>Checks ConsumerNamedExceptionIsRethrownAfterOwnedDrain.</summary>
    public async Task ConsumerNamedExceptionIsRethrownAfterOwnedDrain()
    {
        await using var fixture = new Fixture();
        var expected = new NamedPresentationException();
        NamedPresentationException actual = await Assert.ThrowsAsync<NamedPresentationException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) => throw expected, CancellationToken.None));
        Assert.Same(expected, actual);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
    }

    [Fact]
    /// <summary>Checks ConsumerIndependentCancellationPreservesExactExceptionAndToken.</summary>
    public async Task ConsumerIndependentCancellationPreservesExactExceptionAndToken()
    {
        await using var fixture = new Fixture();
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        var expected = new OperationCanceledException(independent.Token);
        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) => throw expected, CancellationToken.None));
        Assert.Same(expected, actual);
        Assert.Equal(independent.Token, actual.CancellationToken);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldStartCancellationDistinguishesActualCallerFromOwnedDeadlineAfterDrain(bool callerCanceled)
    {
        await using var fixture = new Fixture(); using var caller = new CancellationTokenSource();
        CancellationToken observed = default;
        fixture.Session.StartCallback = async startToken =>
        {
            observed = startToken; Assert.NotEqual(caller.Token, startToken);
            if (callerCanceled) caller.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, startToken);
        };
        var policy = Policy with { StartTimeout = TimeSpan.FromMilliseconds(20) };
        var owner = new OwnedNativeEnvelope(fixture.Backend, fixture.Quarantine, policy, new FakeClock());
        Task<EnvelopeCompletion> run = owner.RunAsync(fixture.Budget.BeginNative(0), Command,
            (_, _) => throw new InvalidOperationException("held start must not reach consumer"), caller.Token);
        if (callerCanceled)
        {
            OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(caller.Token, actual.CancellationToken);
        }
        else
        {
            EnvelopeException actual = await Assert.ThrowsAsync<EnvelopeException>(() => run);
            Assert.Equal(EnvelopeFailure.CapacityUnsupported, actual.Failure); Assert.Equal("native-gate-start-timeout", actual.SafeCode);
            Assert.False(caller.IsCancellationRequested);
        }
        Assert.True(observed.IsCancellationRequested); Assert.False(fixture.Session.GateReleased);
        Assert.True(fixture.Session.Disposed); Assert.True(fixture.Session.Unreferenced); Assert.Equal(1, fixture.Session.Kills);
        Assert.Empty(fixture.Quarantine.Records); await fixture.Budget.ReleaseSourceAsync(); Assert.Equal(1, fixture.Source.Releases);
    }

    [Fact]
    public async Task CallerLinkedCancellationReturnsOriginalCallerTokenAfterSuccessfulOwnedDrain()
    {
        await using var fixture = new Fixture(); using var caller = new CancellationTokenSource();
        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, linked) =>
            {
                Assert.NotEqual(caller.Token, linked); caller.Cancel();
                fixture.Session.Complete(); linked.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, caller.Token));
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.True(fixture.Session.Disposed); Assert.True(fixture.Session.Unreferenced);
        Assert.Empty(fixture.Quarantine.Records); await fixture.Budget.ReleaseSourceAsync(); Assert.Equal(1, fixture.Source.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstablishedSupervisionFailureBeatsLateCallerCancellation(bool ownershipLost)
    {
        await using var fixture = new Fixture(); using var caller = new CancellationTokenSource();
        int replaceOnce = 0;
        fixture.Session.Transform = snapshot =>
        {
            return Interlocked.Exchange(ref replaceOnce, 0) == 0 ? snapshot : snapshot with { InvocationId = new string('b', 32) };
        };
        var policy = Policy with { IdleTimeout = ownershipLost ? TimeSpan.FromDays(1) : TimeSpan.FromMilliseconds(1) };
        var clock = new ConsumerStartedClock();
        var owner = new OwnedNativeEnvelope(fixture.Backend, fixture.Quarantine, policy, clock);
        EnvelopeException actual = await Assert.ThrowsAsync<EnvelopeException>(() => owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, async (_, linked) =>
            {
                Interlocked.Exchange(ref replaceOnce, ownershipLost ? 1 : 0); clock.BeginConsumer();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, linked); }
                catch (OperationCanceledException)
                {
                    // Supervision already selected the named cause before this
                    // late caller cancellation; cleanup sees a stable owner.
                    caller.Cancel(); fixture.Session.Complete(); throw;
                }
            }, caller.Token));
        Assert.Equal(ownershipLost ? EnvelopeFailure.OwnershipLost : EnvelopeFailure.DecodeFailed, actual.Failure);
        Assert.Equal(ownershipLost ? "native-envelope-owner-changed" : "native-pipe-progress-timeout", actual.SafeCode);
        Assert.True(fixture.Session.Disposed); Assert.True(fixture.Session.Unreferenced);
        Assert.Empty(fixture.Quarantine.Records); await fixture.Budget.ReleaseSourceAsync(); Assert.Equal(1, fixture.Source.Releases);
    }

    [Fact]
    /// <summary>Checks CallerCancellationKillsWhileBorrowStillLivesThenWaitsForCallback.</summary>
    public async Task CallerCancellationKillsWhileBorrowStillLivesThenWaitsForCallback()
    {
        await using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var entered = NewSignal();
        var releaseBorrow = NewSignal();
        Task run = fixture.Owner.RunAsync(fixture.Budget.BeginNative(0), Command, async (_, _) =>
        {
            using IDisposable borrow = fixture.Budget.BeginBorrow();
            entered.TrySetResult();
            await releaseBorrow.Task;
        }, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        await fixture.Session.Killed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        Assert.Equal(1, fixture.Budget.ActiveBorrows);
        await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Budget.ReleaseSourceAsync().AsTask());
        Assert.Equal(0, fixture.Source.Releases);
        releaseBorrow.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
        Assert.Equal(1, fixture.Source.Releases);
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("pid")]
    [InlineData("ticks")]
    [InlineData("cgroup")]
    [InlineData("invocation-no-main")]
    [InlineData("cgroup-no-main")]
    /// <summary>Checks ReplacedOwnerIsNeverKilledAndRemainsQuarantined.</summary>
    public async Task ReplacedOwnerIsNeverKilledAndRemainsQuarantined(string replacement)
    {
        await using var fixture = new Fixture();
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) =>
            {
                fixture.Session.Transform = value => replacement switch
                {
                    "invocation" => value with { InvocationId = new string('b', 32) },
                    "pid" => value with { MainPid = value.MainPid + 1 },
                    "ticks" => value with { MainStartTicks = value.MainStartTicks + 1 },
                    "invocation-no-main" => value with { MainPid = 0, MainStartTicks = 0, MainInControlGroup = false,
                        InvocationId = new string('b', 32) },
                    "cgroup-no-main" => value with { MainPid = 0, MainStartTicks = 0, MainInControlGroup = false,
                        CgroupInode = value.CgroupInode + 1 },
                    _ => value with { CgroupInode = value.CgroupInode + 1 },
                };
                throw new NamedPresentationException();
            }, CancellationToken.None));
        Assert.Equal(EnvelopeFailure.DrainFailed, error.Failure);
        Assert.Equal(0, fixture.Session.Kills);
        Assert.False(fixture.Session.Disposed);
        Assert.Single(fixture.Quarantine.Records);
        await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Budget.ReleaseSourceAsync().AsTask());
        Assert.Equal(0, fixture.Source.Releases);
    }

    [Fact]
    /// <summary>Checks OomMapsToCapacityExceededAfterDrain.</summary>
    public async Task OomMapsToCapacityExceededAfterDrain()
    {
        await using var fixture = new Fixture();
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) =>
            { fixture.Session.Complete("oom-kill", 9, 1); throw new NamedPresentationException(); }, CancellationToken.None));
        Assert.Equal(EnvelopeFailure.CapacityExceeded, error.Failure);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
    }

    [Fact]
    /// <summary>Checks FailedDrainHasSafeCauseAndRetainsNativeManagedAndSource.</summary>
    public async Task FailedDrainHasSafeCauseAndRetainsNativeManagedAndSource()
    {
        await using var fixture = new Fixture();
        fixture.Session.IgnoreKill = true;
        var expected = new NamedPresentationException();
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) => throw expected, CancellationToken.None));
        Assert.Equal(EnvelopeFailure.DrainFailed, error.Failure);
        Assert.Equal("consumer-or-cleanup-failed", error.SafeCauseCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
        Assert.Single(fixture.Quarantine.Records);
        Assert.True(fixture.Budget.LiveManagedBytes > 0);
        await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Budget.ReleaseSourceAsync().AsTask());
        Assert.Equal(0, fixture.Source.Releases);
    }

    [Fact]
    /// <summary>Checks AmbiguousStartIsReconciledAndOwnedGateDrained.</summary>
    public async Task AmbiguousStartIsReconciledAndOwnedGateDrained()
    {
        await using var fixture = new Fixture();
        fixture.Session.FailAfterAcceptedStart = true;
        EnvelopeException error = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.Owner.RunAsync(
            fixture.Budget.BeginNative(0), Command, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(EnvelopeFailure.CapacityUnsupported, error.Failure);
        Assert.Equal(1, fixture.Session.Kills);
        Assert.True(fixture.Session.Unreferenced);
        await fixture.Budget.ReleaseSourceAsync();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NamedPresentationException : Exception
    { internal NamedPresentationException() : base("presentation-invalid /private/source-path") { } }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeSource Source = new();
        internal readonly FakeSession Session = new();
        internal readonly FakeQuarantine Quarantine = new();
        internal DecodeOperationBudget Budget { get; }
        internal OwnedNativeEnvelope Owner { get; }
        internal FakeBackend Backend { get; }
        internal Fixture()
        {
            Budget = new(268435456, 1048576, [134217728], 2, Source);
            Backend = new FakeBackend(Session);
            Owner = new(Backend, Quarantine, Policy, new FakeClock());
        }
        public async ValueTask DisposeAsync()
        {
            // Test-only backing cleanup after assertions, including intentional fake quarantine.
            await Session.DisposeAsync();
            if (Source.Releases == 0) await Source.DisposeAsync();
        }
    }

    private sealed class FakeBackend(FakeSession session) : IEnvelopeBackend
    {
        internal int OpenCount;
        public ValueTask<IEnvelopeSession> OpenAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); OpenCount++; return ValueTask.FromResult<IEnvelopeSession>(session); }
    }

    private sealed class ConsumerStartedClock : IEnvelopeClock
    {
        private readonly TaskCompletionSource _started = NewSignal();
        private long _milliseconds;
        internal void BeginConsumer() => _started.TrySetResult();
        public long Timestamp => Interlocked.Read(ref _milliseconds);
        public TimeSpan Elapsed(long before) => TimeSpan.FromMilliseconds(Timestamp - before);
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken token)
        {
            await _started.Task.WaitAsync(token); token.ThrowIfCancellationRequested();
            Interlocked.Add(ref _milliseconds, (long)delay.TotalMilliseconds); await Task.Yield(); token.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeClock : IEnvelopeClock
    {
        private long _milliseconds;
        public long Timestamp => Interlocked.Read(ref _milliseconds);
        public TimeSpan Elapsed(long before) => TimeSpan.FromMilliseconds(Timestamp - before);
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Add(ref _milliseconds, (long)delay.TotalMilliseconds);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeSource : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeQuarantine : IEnvelopeQuarantine
    {
        internal readonly System.Collections.Generic.List<EnvelopeQuarantine> Records = [];
        public void Retain(EnvelopeQuarantine record) => Records.Add(record);
    }

    private sealed class FakeSession : IEnvelopeSession
    {
        private EnvelopeSnapshot? _snapshot;
        private int _afterConsumer;
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public Stream Metadata { get; } = new MemoryStream();
        public Stream Diagnostics { get; } = new MemoryStream();
        internal long Backing = DecodeOperationBudget.RequiredPipeBackingPerProducer;
        public long PipeBackingBytes => Backing;
        public long ProgressVersion => 0;
        public bool StartAttempted { get; private set; }
        internal bool GateReleased, Unreferenced, Disposed, IgnoreKill, FailAfterAcceptedStart, ConsumerDone;
        internal int Kills, CompleteAfterObservations;
        internal Func<EnvelopeSnapshot, EnvelopeSnapshot>? Transform;
        internal Func<CancellationToken, Task>? StartCallback;
        internal readonly TaskCompletionSource Killed = NewSignal();

        public ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken cancellationToken)
        {
            StartAttempted = true;
            _snapshot = new(start.UnitName, "/owned/unit", new string('a', 32), "Mako media envelope " + start.Nonce,
                "/user.slice/" + start.UnitName, 424242, 991, 31, 47, true, true, true, false,
                start.NativeLimitBytes, start.NativeLimitBytes, 0, start.NativeLimitBytes, 0, true, true, false,
                "exec", "control-group", "no", "kill", "active", "success", 1, 0, 0, true, true);
            if (StartCallback is not null) return new ValueTask(StartCallback(cancellationToken));
            if (FailAfterAcceptedStart) throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "fake-start-timeout");
            return ValueTask.CompletedTask;
        }

        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConsumerDone && ++_afterConsumer >= CompleteAfterObservations) Complete();
            EnvelopeSnapshot value = _snapshot ?? throw new InvalidOperationException();
            return ValueTask.FromResult(Transform is null ? value : Transform(value));
        }
        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask ReleaseExecGateAsync(CancellationToken cancellationToken)
        { GateReleased = true; return ValueTask.CompletedTask; }
        public ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
        {
            Kills++;
            if (!IgnoreKill) Complete("signal", 9);
            Killed.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
        { Unreferenced = true; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Input.Dispose(); Output.Dispose(); Metadata.Dispose(); Diagnostics.Dispose();
            return ValueTask.CompletedTask;
        }
        internal void Complete(string result = "success", int status = 0, ulong oom = 0)
        {
            if (_snapshot is null) throw new InvalidOperationException();
            _snapshot = _snapshot with { ActiveState = result == "success" ? "inactive" : "failed", Result = result,
                ExecMainStatus = status, OomKills = oom, MainPid = 0, MainStartTicks = 0, CgroupPopulated = false };
        }
    }
}
