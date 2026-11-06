using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit;

namespace MakoKaraGenerator.Processes.Tests;

/// <summary>Controlled native-boundary tests; no DSO, bus, service, native helper or real process.</summary>
public sealed class EnvelopeObserverTests
{
    private const ulong Limit = 64 * 1024 * 1024;
    private static readonly string Nonce = new('1', 32);
    private static readonly string Unit = "mako-media-" + Nonce + ".service";
    private static readonly EnvelopeIdentity Identity = new(Nonce, Unit, "/org/freedesktop/systemd1/unit/owned",
        new string('a', 32), "/user.slice/" + Unit, 424242, 991, 31, 47);

    [Fact]
    public async Task SuccessfulAttachUsesBoundIdentityAndExplicitOwnUnref()
    {
        var native = new FakeNative();
        var observer = NewObserver(native);
        EnvelopeObserverAttachResult result = await observer.AttachOwnedAsync(CancellationToken.None);
        Assert.Same(observer, result.Observer);
        Assert.True(result.Attached);
        Assert.Null(result.Failure);
        Assert.Equal(Identity, observer.ExpectedIdentity);
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.DisposeAsync().AsTask());
        Assert.False(native.Closed);
        Assert.Equal(0, native.UnrefCalls);
        native.Snapshot = Drained();
        await observer.UnrefOwnReferenceAsync(CancellationToken.None);
        await observer.DisposeAsync();
        Assert.Equal(1, native.UnrefCalls);
        Assert.True(native.Closed);
        Assert.Equal(1, native.WorkerReferences);
    }

    [Fact]
    public async Task RefTimeoutReturnsSameLiveObserverAndNeverImplicitlyClosesIt()
    {
        var native = new FakeNative { AmbiguousRef = true };
        var observer = NewObserver(native);
        EnvelopeObserverAttachResult result = await observer.AttachOwnedAsync(CancellationToken.None);
        Assert.Same(observer, result.Observer);
        Assert.False(result.Attached);
        Assert.Equal("controlled-ref-timeout", result.Failure?.SafeCode);
        Assert.False(native.Closed);
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.DisposeAsync().AsTask());
        native.Snapshot = Drained();
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
        Assert.Equal(0, native.UnrefRequests);
        Assert.Equal(1, native.WorkerReferences);
        Assert.Equal(Identity.CgroupInode, (await observer.ObserveAsync(CancellationToken.None)).CgroupInode);
    }

    [Fact]
    public async Task RefSuccessFollowedByIdentityFailureKeepsReferenceForCleanup()
    {
        var native = new FakeNative { Snapshot = Live() with { InvocationId = new string('b', 32) } };
        var observer = NewObserver(native);
        EnvelopeObserverAttachResult result = await observer.AttachOwnedAsync(CancellationToken.None);
        Assert.False(result.Attached);
        Assert.Equal("native-envelope-owner-changed", result.Failure?.SafeCode);
        Assert.True(native.OwnReference);
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.DisposeAsync().AsTask());
        native.Snapshot = Drained();
        await observer.UnrefOwnReferenceAsync(CancellationToken.None);
        await observer.DisposeAsync();
        Assert.True(native.Closed);
    }

    [Fact]
    public async Task CancellationJoinsLateAcceptedRefAndReturnsItsOwner()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var canceled = new CancellationTokenSource();
        var native = new FakeNative { OnAttach = () => { entered.Set(); finish.Wait(); } };
        var observer = NewObserver(native);
        Task<EnvelopeObserverAttachResult> pending = observer.AttachOwnedAsync(canceled.Token).AsTask();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            canceled.Cancel();
            Assert.False(pending.IsCompleted);
        }
        finally { finish.Set(); }
        EnvelopeObserverAttachResult result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(observer, result.Observer);
        Assert.False(result.Attached);
        Assert.True(observer.AttachVerified);
        Assert.Equal("native-observer-attach-canceled", result.Failure?.SafeCode);
        Assert.True(native.OwnReference);
        native.Snapshot = Drained();
        await observer.UnrefOwnReferenceAsync(CancellationToken.None);
        await observer.DisposeAsync();
    }

    [Fact]
    public async Task CancellationBeforeAttachReturnsUnreferencedContextWhichCanClose()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var native = new FakeNative();
        var observer = NewObserver(native);
        EnvelopeObserverAttachResult result = await observer.AttachOwnedAsync(canceled.Token);
        Assert.Same(observer, result.Observer);
        Assert.False(result.Attached);
        Assert.Equal(0, native.AttachCalls);
        await observer.DisposeAsync();
        Assert.True(native.Closed);
        Assert.Equal(0, native.UnrefRequests);
    }

    [Fact]
    public async Task DisposeWaitsForInflightRefThenRetainsSameRecoverableContext()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var native = new FakeNative { OnAttach = () => { entered.Set(); finish.Wait(); } };
        var observer = NewObserver(native);
        Task<EnvelopeObserverAttachResult> attach = observer.AttachOwnedAsync(CancellationToken.None).AsTask();
        Task disposing;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            disposing = observer.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
        }
        finally { finish.Set(); }
        Assert.True((await attach).Attached);
        await Assert.ThrowsAsync<EnvelopeException>(() => disposing);
        Assert.Equal(Identity, observer.ExpectedIdentity);
        Assert.Equal(Identity.CgroupInode, (await observer.ObserveAsync(CancellationToken.None)).CgroupInode);
        native.Snapshot = Drained();
        await observer.UnrefOwnReferenceAsync(CancellationToken.None);
        await observer.DisposeAsync();
    }

    [Fact]
    public async Task UnrefReplyTimeoutDoesNotPermitBlindRetryOrDisposal()
    {
        var native = new FakeNative { AmbiguousUnref = true };
        var observer = NewObserver(native);
        Assert.True((await observer.AttachOwnedAsync(CancellationToken.None)).Attached);
        native.Snapshot = Drained();
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.DisposeAsync().AsTask());
        Assert.Equal(1, native.UnrefRequests);
        Assert.False(native.Closed);
        Assert.Equal(1, native.WorkerReferences);
        Assert.Equal(Identity.CgroupInode, (await observer.ObserveAsync(CancellationToken.None)).CgroupInode);
    }

    [Fact]
    public async Task ManagerOrPermissionFailureNeverBecomesAbsenceAndOwnerRemainsLive()
    {
        foreach (string failureCode in new[] { "native-observer-unit-unavailable", "controlled-permission-denied", "controlled-bus-failed" })
        {
            var native = new FakeNative();
            var observer = NewObserver(native);
            Assert.True((await observer.AttachOwnedAsync(CancellationToken.None)).Attached);
            native.ObserveFailure = new EnvelopeException(EnvelopeFailure.OwnershipLost, failureCode);
            await Assert.ThrowsAsync<EnvelopeException>(() => observer.ObserveAsync(CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<EnvelopeException>(() => observer.KillVerifiedCgroupAsync(CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<EnvelopeException>(() => observer.DisposeAsync().AsTask());
            Assert.Equal(0, native.Kills);
            Assert.Equal(0, native.UnrefRequests);
            Assert.False(native.Closed);
            native.ObserveFailure = null;
            native.Snapshot = Drained();
            await observer.UnrefOwnReferenceAsync(CancellationToken.None);
            await observer.DisposeAsync();
        }
    }

    [Fact]
    public async Task EachFixedOwnerReplacementBlocksKillAndUnref()
    {
        foreach (EnvelopeSnapshot changed in ChangedOwners())
        {
            var native = new FakeNative();
            var observer = NewObserver(native);
            Assert.True((await observer.AttachOwnedAsync(CancellationToken.None)).Attached);
            native.Snapshot = changed;
            await Assert.ThrowsAsync<EnvelopeException>(() => observer.KillVerifiedCgroupAsync(CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
            Assert.Equal(0, native.Kills);
            Assert.Equal(0, native.UnrefRequests);
            native.Snapshot = Drained();
            await observer.UnrefOwnReferenceAsync(CancellationToken.None);
            await observer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExitedPidAndRemovedPinStillRequireOriginalManagerTerminal()
    {
        var native = new FakeNative();
        var observer = NewObserver(native);
        Assert.True((await observer.AttachOwnedAsync(CancellationToken.None)).Attached);
        native.Snapshot = Drained() with { ActiveState = "active", CgroupExists = false, CgroupRemoved = true };
        Assert.Equal("active", (await observer.ObserveAsync(CancellationToken.None)).ActiveState);
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
        Assert.Equal(0, native.UnrefRequests);
        native.Snapshot = native.Snapshot with { ActiveState = "failed" };
        await observer.UnrefOwnReferenceAsync(CancellationToken.None);
        await observer.DisposeAsync();
    }

    [Fact]
    public async Task EmptyTerminalWithoutOriginalPinEvidenceCannotUnref()
    {
        var native = new FakeNative();
        var observer = NewObserver(native);
        Assert.True((await observer.AttachOwnedAsync(CancellationToken.None)).Attached);
        native.Snapshot = Drained() with { CgroupExists = false, CgroupRemoved = false };
        await Assert.ThrowsAsync<EnvelopeException>(() => observer.UnrefOwnReferenceAsync(CancellationToken.None).AsTask());
        Assert.Equal(0, native.UnrefRequests);
        native.Snapshot = Drained();
        await observer.UnrefOwnReferenceAsync(CancellationToken.None);
        await observer.DisposeAsync();
    }

    [Fact]
    public void EveryRequiredControlRemainsRequiredBySharedValidator()
    {
        EnvelopeSnapshot value = Live();
        EnvelopeSnapshotValidator.ValidateControls(value, Limit);
        EnvelopeSnapshot[] rejected = [
            value with { ControlsComplete = false }, value with { KernelKillAvailable = false },
            value with { MainPid = 0 }, value with { MainStartTicks = 0 }, value with { MainInControlGroup = false },
            value with { CgroupExists = false }, value with { CgroupInode = 0 },
            value with { MemoryMax = Limit + 4096 }, value with { KernelMemoryMax = Limit - 4096 },
            value with { EffectiveMemoryMax = 0 }, value with { EffectiveMemoryMax = Limit + 4096 },
            value with { MemorySwapMax = 1 }, value with { KernelSwapMax = 1 },
            value with { KernelOomGroup = false }, value with { MemoryAccounting = false }, value with { Delegate = true },
            value with { Type = "simple" }, value with { KillMode = "process" },
            value with { Restart = "always" }, value with { OomPolicy = "continue" },
        ];
        foreach (EnvelopeSnapshot invalid in rejected)
            Assert.Equal("native-envelope-controls-unverified", Assert.Throws<EnvelopeException>(
                () => EnvelopeSnapshotValidator.ValidateControls(invalid, Limit)).SafeCode);
        EnvelopeSnapshotValidator.ValidateControls(value with { EffectiveMemoryMax = Limit - 4096 }, Limit);
    }

    [Fact]
    public void LocalOwnerCaptureKeepsFailedGateAllowanceAndZeroInvocationRejection()
    {
        EnvelopeSnapshot failed = Live() with { ActiveState = "failed", MainPid = 0, MainStartTicks = 0,
            ControlGroup = "", CgroupDevice = 0, CgroupInode = 0, CgroupExists = false,
            CgroupPopulated = false, CgroupRemoved = true };
        EnvelopeIdentity captured = EnvelopeSnapshotValidator.CaptureOwner(failed, Nonce, Unit);
        Assert.Equal("", captured.ControlGroup);
        Assert.Equal(0UL, captured.CgroupInode);
        Assert.Throws<EnvelopeException>(() => EnvelopeSnapshotValidator.CaptureOwner(failed with { ActiveState = "active" }, Nonce, Unit));
        Assert.Throws<EnvelopeException>(() => EnvelopeSnapshotValidator.CaptureOwner(Live() with { InvocationId = new string('0', 32) }, Nonce, Unit));
        Assert.Equal(Identity, EnvelopeSnapshotValidator.CaptureOwner(Live(), Nonce, Unit));
    }

    [Fact]
    public void LocalVerifyForwarderKeepsExitRaceAndEveryFixedIdentityCheck()
    {
        EnvelopeSnapshot goneBetweenReads = Live() with { MainPid = 0, MainStartTicks = 0, MainInControlGroup = false };
        OwnedNativeEnvelope.VerifyIdentity(goneBetweenReads, Identity);
        EnvelopeSnapshotValidator.VerifyIdentity(goneBetweenReads, Identity);
        foreach (EnvelopeSnapshot changed in ChangedOwners())
        {
            Assert.Equal("native-envelope-owner-changed", Assert.Throws<EnvelopeException>(
                () => OwnedNativeEnvelope.VerifyIdentity(changed, Identity)).SafeCode);
            Assert.Equal("native-envelope-owner-changed", Assert.Throws<EnvelopeException>(
                () => EnvelopeSnapshotValidator.VerifyIdentity(changed, Identity)).SafeCode);
        }
    }

    [Fact]
    public void ExpectedIdentityIsStrictBeforeNativeOpen()
    {
        EnvelopeSnapshotValidator.ValidateObserverExpected(Identity, Limit);
        foreach (EnvelopeIdentity invalid in new[] {
            Identity with { Nonce = new string('A', 32) }, Identity with { UnitName = "other.service" },
            Identity with { UnitPath = "/invalid//path" }, Identity with { InvocationId = new string('0', 32) },
            Identity with { ControlGroup = "/user.slice/../" + Unit }, Identity with { MainPid = 0 },
            Identity with { StartTicks = 0 }, Identity with { CgroupInode = 0 },
        })
            Assert.Throws<EnvelopeException>(() => EnvelopeSnapshotValidator.ValidateObserverExpected(invalid, Limit));
        Assert.Throws<EnvelopeException>(() => EnvelopeSnapshotValidator.ValidateObserverExpected(Identity, Limit + 1));
    }

    private static IEnumerable<EnvelopeSnapshot> ChangedOwners()
    {
        EnvelopeSnapshot value = Live();
        return [ value with { UnitName = "other.service" }, value with { UnitPath = "/other" },
            value with { InvocationId = new string('b', 32) }, value with { Description = "other owner" },
            value with { ControlGroup = "/other/" + Unit }, value with { CgroupDevice = 32 },
            value with { CgroupInode = 48 }, value with { MainPid = Identity.MainPid + 1 },
            value with { MainStartTicks = 992 }, value with { MainInControlGroup = false } ];
    }
    private static EnvelopeObserver NewObserver(FakeNative native) => new(Identity, Limit, native);
    private static EnvelopeSnapshot Live() => new(Unit, Identity.UnitPath, Identity.InvocationId, "Mako media envelope " + Nonce,
        Identity.ControlGroup, Identity.MainPid, Identity.StartTicks, Identity.CgroupDevice, Identity.CgroupInode,
        true, true, true, false, Limit, Limit, 0, Limit, 0, true, true, false,
        "exec", "control-group", "no", "kill", "active", "success", 1, 0, 0, true, true);
    private static EnvelopeSnapshot Drained() => Live() with { ActiveState = "inactive", MainPid = 0,
        MainStartTicks = 0, MainInControlGroup = false, CgroupPopulated = false };

    private sealed class FakeNative : IEnvelopeObserverNativeContext
    {
        internal EnvelopeSnapshot Snapshot = Live();
        internal Action? OnAttach;
        internal EnvelopeException? ObserveFailure;
        internal bool AmbiguousRef, AmbiguousUnref, OwnReference, Closed;
        internal int AttachCalls, Kills, UnrefCalls, UnrefRequests;
        internal int WorkerReferences = 1;
        private bool _uncertainRef, _uncertainUnref;
        public EnvelopeSnapshot Attach()
        {
            AttachCalls++;
            OnAttach?.Invoke();
            OwnReference = true;
            if (AmbiguousRef)
            {
                _uncertainRef = true;
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "controlled-ref-timeout");
            }
            return Snapshot;
        }
        public EnvelopeSnapshot Observe()
        {
            if (ObserveFailure is not null) throw ObserveFailure;
            return Snapshot;
        }
        public void KillVerifiedCgroup() => Kills++;
        public void UnrefOwnReference()
        {
            if (_uncertainRef || _uncertainUnref)
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "native-observer-reference-unresolved");
            UnrefRequests++;
            if (AmbiguousUnref)
            {
                _uncertainUnref = true;
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "controlled-unref-timeout");
            }
            UnrefCalls++;
            OwnReference = false;
        }
        public void Close()
        {
            if (OwnReference || _uncertainRef || _uncertainUnref)
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "native-observer-reference-unresolved");
            Closed = true;
        }
    }
}
